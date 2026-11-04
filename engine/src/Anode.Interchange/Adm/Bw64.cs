using System.Buffers.Binary;
using System.Text;

namespace Anode.Interchange.Adm;

/// <summary>
///     A RIFF/WAVE, RF64 or BW64 file being read (ITU-R BS.2088): its format, its chunks (sizes above 4 GB from
///     <c>ds64</c>) and its samples as floats. PCM of 16, 24 or 32 bits and 32-bit float, plain or extensible. Anything
///     malformed throws <see cref="FormatException" />.
/// </summary>
sealed class Bw64Reader : IDisposable {
    const int Block = 4096;
    readonly FileStream stream;
    readonly Dictionary<string, (long Offset, long Size)> chunks = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> sizes = new(StringComparer.Ordinal);
    byte[] buffer = [];
    long position;
    long dataSize64 = -1;

    Bw64Reader(FileStream stream, bool lenient) {
        this.stream = stream;
        try {
            ReadHeader(lenient);
        } catch {
            stream.Dispose();
            throw;
        }
    }

    public int Channels { get; private set; }
    public int SampleRate { get; private set; }
    public int BitsPerSample { get; private set; }
    public bool IsFloat { get; private set; }
    public long Frames { get; private set; }
    public int BytesPerFrame => Channels * BitsPerSample / 8;

    /// <summary>
    ///     Opens <paramref name="path" />; <paramref name="lenient" /> takes a data chunk that claims more than the
    ///     file holds (a WAV writer that never finished, or a RIFF over 4 GB) as what is there.
    /// </summary>
    public static Bw64Reader Open(string path, bool lenient = false) =>
        new(new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16), lenient);

    public bool Has(string id) => chunks.ContainsKey(id);

    /// <summary>A chunk's bytes (a metadata chunk; at most 256 MB).</summary>
    public byte[] Chunk(string id) {
        var (offset, size) = chunks[id];
        if (size > 256 << 20) {
            throw new FormatException($"The '{id}' chunk is too large ({size} bytes).");
        }

        var bytes = new byte[size];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        stream.Position = chunks["data"].Offset + position * BytesPerFrame;
        return bytes;
    }

    void ReadHeader(bool lenient) {
        var large = ReadKind();
        var length = stream.Length;
        var at = 12L;
        Span<byte> header = stackalloc byte[8];
        var first = true;
        while (at + 8 <= length) {
            stream.Position = at;
            stream.ReadExactly(header);
            var id = Encoding.ASCII.GetString(header[..4]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var body = at + 8;
            if (first && large) {
                ReadDs64(id, size);
            }

            first = false;
            size = Resolve(id, size, body, large, lenient);
            chunks.TryAdd(id, (body, size));
            if (id == "fmt ") {
                ReadFormat(size);
            }

            at = body + size + (size & 1);
        }

        if (!chunks.ContainsKey("fmt ")) {
            throw new FormatException("The file has no format chunk.");
        }

        if (!chunks.TryGetValue("data", out var data)) {
            throw new FormatException("The file has no data chunk.");
        }

        Frames = data.Size / BytesPerFrame;
        stream.Position = data.Offset;
    }

    /// <summary>The RIFF header: whether the file is RF64 or BW64 (sizes in ds64).</summary>
    bool ReadKind() {
        Span<byte> head = stackalloc byte[12];
        if (stream.Read(head) < 12) {
            throw new FormatException("The file is too short for a RIFF, RF64 or BW64 header.");
        }

        var kind = Encoding.ASCII.GetString(head[..4]);
        if (kind is not ("RIFF" or "RF64" or "BW64") || !head[8..12].SequenceEqual("WAVE"u8)) {
            throw new FormatException("Not a RIFF/WAVE, RF64 or BW64 file.");
        }

        return kind != "RIFF";
    }

    void ReadDs64(string id, long size) {
        if (id != "ds64" || size < 28) {
            throw new FormatException("An RF64 or BW64 file must start with a ds64 chunk.");
        }

        Span<byte> ds64 = stackalloc byte[28];
        stream.ReadExactly(ds64);
        dataSize64 = BinaryPrimitives.ReadInt64LittleEndian(ds64[8..]);
        var entries = BinaryPrimitives.ReadUInt32LittleEndian(ds64[24..]);
        if (entries > 1000 || 28 + entries * 12L > size) {
            throw new FormatException("The ds64 chunk's table is malformed.");
        }

        Span<byte> entry = stackalloc byte[12];
        for (var e = 0; e < entries; e++) {
            stream.ReadExactly(entry);
            sizes[Encoding.ASCII.GetString(entry[..4])] = BinaryPrimitives.ReadInt64LittleEndian(entry[4..]);
        }
    }

    /// <summary>A chunk's real size: from ds64 when it says −1 in a large file, checked against the file's end.</summary>
    long Resolve(string id, long size, long body, bool large, bool lenient) {
        if (large && size == uint.MaxValue) {
            size = id == "data" ? dataSize64 : sizes.GetValueOrDefault(id, -1);
            if (size < 0) {
                throw new FormatException($"The '{id}' chunk's size is not in the ds64 chunk.");
            }
        }

        if (body + size <= stream.Length) {
            return size;
        }

        return lenient && id == "data"
            ? stream.Length - body
            : throw new FormatException($"The '{id}' chunk runs past the end of the file.");
    }

    void ReadFormat(long size) {
        if (size is < 16 or > 1024) {
            throw new FormatException("The format chunk is malformed.");
        }

        Span<byte> fmt = stackalloc byte[(int)size];
        stream.ReadExactly(fmt);
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        Channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
        BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        if (tag == 0xFFFE) {
            if (size < 40) {
                throw new FormatException("The extensible format chunk is malformed.");
            }

            tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
        }

        if (Channels is 0 or > 1024 || SampleRate is < 1000 or > 768_000) {
            throw new FormatException($"The file has {Channels} channels at {SampleRate} Hz.");
        }

        IsFloat = tag == 3;
        if (!(tag == 1 && BitsPerSample is 16 or 24 or 32 || tag == 3 && BitsPerSample == 32)) {
            throw new FormatException(
                $"Format {tag} with {BitsPerSample} bits is not supported (PCM 16/24/32, float 32)."
            );
        }
    }

    /// <summary>Reads up to <c>interleaved.Length / Channels</c> frames; how many it read (0 at the end).</summary>
    public int Read(Span<float> interleaved) {
        var frames = (int)Math.Min(interleaved.Length / Channels, Frames - position);
        if (frames <= 0) {
            return 0;
        }

        var bytes = frames * BytesPerFrame;
        if (buffer.Length < bytes) {
            buffer = new byte[Math.Max(bytes, Block * BytesPerFrame)];
        }

        stream.ReadExactly(buffer, 0, bytes);
        var span = buffer.AsSpan(0, bytes);
        var count = frames * Channels;
        switch (BitsPerSample, IsFloat) {
            case (16, _):
                for (var i = 0; i < count; i++) {
                    interleaved[i] = BinaryPrimitives.ReadInt16LittleEndian(span[(2 * i)..]) / 32768f;
                }

                break;
            case (24, _):
                for (var i = 0; i < count; i++) {
                    var v = span[3 * i] | (span[3 * i + 1] << 8) | ((sbyte)span[3 * i + 2] << 16);
                    interleaved[i] = v / 8388608f;
                }

                break;
            case (32, true):
                for (var i = 0; i < count; i++) {
                    interleaved[i] = BinaryPrimitives.ReadSingleLittleEndian(span[(4 * i)..]);
                }

                break;
            default:
                for (var i = 0; i < count; i++) {
                    interleaved[i] = (float)(BinaryPrimitives.ReadInt32LittleEndian(span[(4 * i)..]) / 2147483648.0);
                }

                break;
        }

        position += frames;
        return frames;
    }

    public void Dispose() => stream.Dispose();
}

/// <summary>
///     Writes a BW64 file (ITU-R BS.2088) as samples arrive: <c>RIFF</c> with a <c>JUNK</c> chunk reserving room for
///     <c>ds64</c>, <c>fmt </c>, the metadata chunks, then <c>data</c>; on <see cref="Dispose" />, a file above 4 GB (or
///     any, when asked) becomes <c>BW64</c> with the <c>ds64</c> chunk in place of the <c>JUNK</c>. 24-bit PCM or
///     32-bit float.
/// </summary>
sealed class Bw64Writer : IDisposable {
    readonly FileStream stream;
    readonly int channels;
    readonly bool isFloat;
    readonly bool forceDs64;
    readonly long dataSizeAt;
    byte[] buffer = [];
    long dataBytes;

    public Bw64Writer(
        string path,
        int channels,
        int sampleRate,
        int bitDepth,
        IReadOnlyList<(string Id, byte[] Body)> metadata,
        bool forceDs64 = false
    ) {
        if (bitDepth is not (24 or 32)) {
            throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth, "24 (PCM) or 32 (float).");
        }

        this.channels = channels;
        isFloat = bitDepth == 32;
        this.forceDs64 = forceDs64;
        stream = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        var bytesPerSample = bitDepth / 8;
        using var header = new MemoryStream();
        Write(header, "RIFF"u8, 0);
        header.Write("WAVE"u8);
        Write(header, "JUNK"u8, 28);
        header.Write(new byte[28]);
        var fmt = new byte[isFloat ? 18 : 16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, (ushort)(isFloat ? 3 : 1));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), (uint)(sampleRate * channels * bytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)(channels * bytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)bitDepth);
        Write(header, "fmt "u8, (uint)fmt.Length);
        header.Write(fmt);
        foreach (var (id, body) in metadata) {
            Write(header, Encoding.ASCII.GetBytes(id), (uint)body.Length);
            header.Write(body);
            if ((body.Length & 1) != 0) {
                header.WriteByte(0);
            }
        }

        dataSizeAt = header.Length + 4;
        Write(header, "data"u8, 0);
        stream.Write(header.GetBuffer(), 0, (int)header.Length);
    }

    static void Write(Stream stream, ReadOnlySpan<byte> id, uint size) {
        stream.Write(id);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, size);
        stream.Write(bytes);
    }

    /// <summary>Appends interleaved frames (full scale ±1; 24-bit clips).</summary>
    public void Write(ReadOnlySpan<float> interleaved) {
        var size = isFloat ? 4 : 3;
        var bytes = interleaved.Length * size;
        if (buffer.Length < bytes) {
            buffer = new byte[bytes];
        }

        var span = buffer.AsSpan(0, bytes);
        for (var i = 0; i < interleaved.Length; i++) {
            if (isFloat) {
                BinaryPrimitives.WriteSingleLittleEndian(span[(4 * i)..], interleaved[i]);
            } else {
                var v = (int)Math.Round(Math.Clamp(interleaved[i], -1f, 1f) * 8388607.0);
                span[3 * i] = (byte)v;
                span[3 * i + 1] = (byte)(v >> 8);
                span[3 * i + 2] = (byte)(v >> 16);
            }
        }

        stream.Write(span);
        dataBytes += bytes;
    }

    public void Dispose() {
        if ((dataBytes & 1) != 0) {
            stream.WriteByte(0);
        }

        var riffSize = stream.Length - 8;
        Span<byte> bytes = stackalloc byte[8];
        if (forceDs64 || riffSize > uint.MaxValue || dataBytes > uint.MaxValue) {
            stream.Position = 0;
            stream.Write("BW64"u8);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, uint.MaxValue);
            stream.Write(bytes[..4]);
            stream.Position = 12;
            Write(stream, "ds64"u8, 28);
            Span<byte> ds64 = stackalloc byte[28];
            BinaryPrimitives.WriteInt64LittleEndian(ds64, riffSize);
            BinaryPrimitives.WriteInt64LittleEndian(ds64[8..], dataBytes);
            BinaryPrimitives.WriteInt64LittleEndian(ds64[16..], dataBytes / (channels * (isFloat ? 4 : 3)));
            stream.Write(ds64);
            stream.Position = dataSizeAt;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, uint.MaxValue);
        } else {
            stream.Position = 4;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)riffSize);
            stream.Write(bytes[..4]);
            stream.Position = dataSizeAt;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)dataBytes);
        }

        stream.Write(bytes[..4]);
        stream.Dispose();
    }
}
