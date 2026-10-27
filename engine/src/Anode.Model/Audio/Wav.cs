using System.Buffers.Binary;

namespace Anode.Model.Audio;

/// <summary>How a WAV file's samples are stored.</summary>
public enum WavEncoding {
    Pcm16,
    Pcm24,
    Pcm32,
    Float32
}

/// <summary>A WAV file's format and where its samples are.</summary>
public sealed record WavInfo(int Channels, int SampleRate, WavEncoding Encoding, long DataOffset, long Frames) {
    public int BitDepth =>
        Encoding switch {
            WavEncoding.Pcm16 => 16,
            WavEncoding.Pcm24 => 24,
            _ => 32
        };

    public int BytesPerFrame => Channels * (BitDepth / 8);
}

/// <summary>A file that is not a WAV Anode reads; the API answers 422.</summary>
public sealed class WavFormatException : Exception {
    public WavFormatException() { }

    public WavFormatException(string message) : base(message) { }

    public WavFormatException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
///     Reads RIFF/WAVE files: PCM of 16, 24 or 32 bits and 32-bit float, plain or <c>WAVE_FORMAT_EXTENSIBLE</c>, any
///     number of channels (the importer accepts one or two).
/// </summary>
public sealed class WavReader : IDisposable {
    const ushort FormatPcm = 1;
    const ushort FormatFloat = 3;
    const ushort FormatExtensible = 0xFFFE;

    readonly Stream stream;
    readonly bool ownsStream;
    readonly byte[] raw = new byte[64 * 1024];
    long framesLeft;

    public WavInfo Info { get; }

    WavReader(Stream stream, bool ownsStream) {
        this.stream = stream;
        this.ownsStream = ownsStream;
        Info = ReadHeader(stream);
        stream.Position = Info.DataOffset;
        framesLeft = Info.Frames;
    }

    public static WavReader Open(string path) {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1,
            FileOptions.SequentialScan
        );
        try {
            return new(stream, true);
        } catch {
            stream.Dispose();
            throw;
        }
    }

    public static WavReader Open(Stream stream) => new(stream, false);

    /// <summary>The format of the WAV file at <paramref name="path" />.</summary>
    public static WavInfo Probe(string path) {
        using var reader = Open(path);
        return reader.Info;
    }

    /// <summary>
    ///     Reads up to <c>interleaved.Length / Channels</c> frames as floats (full scale ±1) and returns how many it
    ///     read: 0 at the end of the data.
    /// </summary>
    public int Read(Span<float> interleaved) {
        var bytesPerSample = Info.BitDepth / 8;
        var frames = (int)Math.Min(
            Math.Min(interleaved.Length / Info.Channels, raw.Length / Info.BytesPerFrame),
            framesLeft
        );
        if (frames <= 0) {
            return 0;
        }

        var bytes = frames * Info.BytesPerFrame;
        var read = 0;
        while (read < bytes) {
            var n = stream.Read(raw, read, bytes - read);
            if (n == 0) {
                break;
            }

            read += n;
        }

        frames = read / Info.BytesPerFrame;
        framesLeft = frames == 0 ? 0 : framesLeft - frames;
        var samples = frames * Info.Channels;
        var source = raw.AsSpan();
        for (var i = 0; i < samples; i++) {
            var at = source[(i * bytesPerSample)..];
            interleaved[i] = Info.Encoding switch {
                WavEncoding.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(at) / 32768f,
                WavEncoding.Pcm24 => ((at[0] | (at[1] << 8) | (at[2] << 16)) << 8 >> 8) / 8388608f,
                WavEncoding.Pcm32 => (float)(BinaryPrimitives.ReadInt32LittleEndian(at) / 2147483648.0),
                _ => BinaryPrimitives.ReadSingleLittleEndian(at)
            };
        }

        return frames;
    }

    public void Dispose() {
        if (ownsStream) {
            stream.Dispose();
        }
    }

    static WavInfo ReadHeader(Stream stream) {
        Span<byte> head = stackalloc byte[12];
        if (!TryRead(stream, head)
            || !head[..4].SequenceEqual("RIFF"u8)
            || !head[8..12].SequenceEqual("WAVE"u8)) {
            throw new WavFormatException("Not a RIFF/WAVE file.");
        }

        (int Channels, int Rate, WavEncoding Encoding)? format = null;
        Span<byte> chunk = stackalloc byte[8];
        var position = 12L;
        while (TryRead(stream, chunk)) {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var body = position + 8;
            if (chunk[..4].SequenceEqual("fmt "u8)) {
                format = ReadFormat(stream, size);
            } else if (chunk[..4].SequenceEqual("data"u8)) {
                return DataInfo(stream, format, body, size);
            }

            position = body + size + (size & 1);
            if (position > stream.Length) {
                break;
            }

            stream.Position = position;
        }

        throw new WavFormatException(
            format is null ? "The WAV file has no format chunk." : "The WAV file has no data."
        );
    }

    static WavInfo DataInfo(
        Stream stream,
        (int Channels, int Rate, WavEncoding Encoding)? format,
        long body,
        uint size
    ) {
        if (format is not { } f) {
            throw new WavFormatException("The WAV file's data comes before its format.");
        }

        // A writer that never finished (or streams) leaves the size 0 or 0xFFFFFFFF: take what is there.
        var available = stream.Length - body;
        var length = size is 0 or uint.MaxValue || size > available ? available : size;
        var info = new WavInfo(f.Channels, f.Rate, f.Encoding, body, 0);
        return info with { Frames = length / info.BytesPerFrame };
    }

    static (int, int, WavEncoding) ReadFormat(Stream stream, uint size) {
        if (size is < 16 or > 1024) {
            throw new WavFormatException("The WAV file's format chunk is malformed.");
        }

        Span<byte> fmt = stackalloc byte[(int)size];
        if (!TryRead(stream, fmt)) {
            throw new WavFormatException("The WAV file's format chunk is cut short.");
        }

        var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        if (tag == FormatExtensible) {
            if (size < 40) {
                throw new WavFormatException("The WAV file's extensible format chunk is malformed.");
            }

            tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
        }

        if (channels is 0 or > 64 || rate is 0 or > 768_000) {
            throw new WavFormatException($"The WAV file has {channels} channels at {rate} Hz.");
        }

        var encoding = (tag, bits) switch {
            (FormatPcm, 16) => WavEncoding.Pcm16,
            (FormatPcm, 24) => WavEncoding.Pcm24,
            (FormatPcm, 32) => WavEncoding.Pcm32,
            (FormatFloat, 32) => WavEncoding.Float32,
            (FormatPcm, _) => throw new WavFormatException($"{bits}-bit PCM is not supported (16, 24 or 32 bits)."),
            (FormatFloat, _) => throw new WavFormatException($"{bits}-bit float is not supported (32 bits)."),
            _ => throw new WavFormatException($"WAV format {tag} is not supported (PCM or float).")
        };
        return (channels, (int)rate, encoding);
    }

    static bool TryRead(Stream stream, Span<byte> buffer) {
        var read = 0;
        while (read < buffer.Length) {
            var n = stream.Read(buffer[read..]);
            if (n == 0) {
                return false;
            }

            read += n;
        }

        return true;
    }
}

/// <summary>Writes a WAV file as samples arrive; the header's sizes are filled in on <see cref="Dispose" />.</summary>
public sealed class WavWriter : IDisposable {
    readonly Stream stream;
    readonly int channels;
    readonly WavEncoding encoding;
    long dataBytes;

    public WavWriter(string path, int channels, int sampleRate, WavEncoding encoding)
        : this(
            new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None),
            channels,
            sampleRate,
            encoding
        ) { }

    public WavWriter(Stream stream, int channels, int sampleRate, WavEncoding encoding) {
        this.stream = stream;
        this.channels = channels;
        this.encoding = encoding;
        var bits = encoding switch {
            WavEncoding.Pcm16 => 16,
            WavEncoding.Pcm24 => 24,
            _ => 32
        };
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], (ushort)(encoding == WavEncoding.Float32 ? 3 : 1));
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(sampleRate * channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)(channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], (ushort)bits);
        "data"u8.CopyTo(header[36..]);
        stream.Write(header);
    }

    /// <summary>Appends interleaved samples (full scale ±1; clipped for the integer encodings).</summary>
    public void Write(ReadOnlySpan<float> interleaved) {
        if (interleaved.Length % channels != 0) {
            throw new ArgumentException("Write whole frames.", nameof(interleaved));
        }

        var size = encoding switch {
            WavEncoding.Pcm16 => 2,
            WavEncoding.Pcm24 => 3,
            _ => 4
        };
        var bytes = new byte[interleaved.Length * size];
        for (var i = 0; i < interleaved.Length; i++) {
            var at = bytes.AsSpan(i * size);
            var x = Math.Clamp(interleaved[i], -1f, 1f);
            switch (encoding) {
                case WavEncoding.Pcm16:
                    BinaryPrimitives.WriteInt16LittleEndian(at, (short)Math.Round(x * 32767));
                    break;
                case WavEncoding.Pcm24:
                    var v = (int)Math.Round(x * 8388607);
                    at[0] = (byte)v;
                    at[1] = (byte)(v >> 8);
                    at[2] = (byte)(v >> 16);
                    break;
                case WavEncoding.Pcm32:
                    BinaryPrimitives.WriteInt32LittleEndian(at, (int)Math.Round(x * 2147483647.0));
                    break;
                default:
                    BinaryPrimitives.WriteSingleLittleEndian(at, interleaved[i]);
                    break;
            }
        }

        stream.Write(bytes);
        dataBytes += bytes.Length;
    }

    public void Dispose() {
        if ((dataBytes & 1) != 0) {
            stream.WriteByte(0);
        }

        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)Math.Min(uint.MaxValue, 36 + dataBytes + (dataBytes & 1)));
        stream.Position = 4;
        stream.Write(size);
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)Math.Min(uint.MaxValue, dataBytes));
        stream.Position = 40;
        stream.Write(size);
        stream.Dispose();
    }
}
