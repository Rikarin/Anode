using Anode.Dsp;
using Anode.Dsp.Spatial;
using System.Buffers.Binary;

namespace Anode.Engine.Rendering;

/// <summary>
///     Writes a 32-bit float WAV (<c>WAVE_FORMAT_IEEE_FLOAT</c>: <c>fmt </c>, <c>fact</c>, then the data), the format
///     renders produce (docs/05-phase-4.md, "Bounce": the server converts it). The sizes are written on completion.
///     <para>
///         Phase 10 (docs/11-phase-10.md, "Bounce"): given a speaker layout of more than two channels, the file is
///         <c>WAVE_FORMAT_EXTENSIBLE</c> with the layout's channel mask (<see cref="WaveChannels" />): the frames it is
///         handed in the layout's (ADM) order are stored in the mask's order (7.x: the rears before the sides). Such a
///         file reserves a <c>JUNK</c> chunk that becomes <c>ds64</c> when it outgrows 4 GB (RF64, EBU Tech 3306 /
///         ITU-R BS.2088's BW64 rules). Mono and stereo files are as before (and stay RIFF, sizes clamped past 4 GB).
///     </para>
/// </summary>
public sealed class FloatWavWriter : IDisposable {
    const int HeaderBytes = 12 + 8 + 18 + 12 + 8;

    // RIFF, JUNK/ds64 (28), fmt (40, extensible), fact, data.
    const int ExtensibleHeaderBytes = 12 + 8 + 28 + 8 + 40 + 12 + 8;

    static readonly byte[] FloatSubFormat =
        [0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    readonly FileStream stream;
    readonly byte[] buffer = new byte[64 * 1024];
    readonly int[]? order;
    readonly uint mask;
    bool completed;

    public FloatWavWriter(string path, int channels, int sampleRate, SpeakerLayout? layout = null) {
        Path = path;
        Channels = channels;
        SampleRate = sampleRate;
        if (layout is not null && channels > 2 && layout.ChannelCount == channels) {
            Extensible = true;
            (mask, var wave) = WaveChannels.Of(layout);
            order = wave.Select(static (c, k) => (c, k)).All(static x => x.c == x.k) ? null : wave;
        }

        stream = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
        Span<byte> header = stackalloc byte[Extensible ? ExtensibleHeaderBytes : HeaderBytes];
        WriteHeader(header, 0);
        stream.Write(header);
    }

    public string Path { get; }

    public int Channels { get; }

    public int SampleRate { get; }

    /// <summary>A WAVE_FORMAT_EXTENSIBLE file (more than two channels of a known layout).</summary>
    public bool Extensible { get; }

    /// <summary>The file's channel mask (0 unless <see cref="Extensible" />).</summary>
    public uint ChannelMask => mask;

    /// <summary>
    ///     The data size past which an extensible file becomes RF64 (tests lower it; the RIFF limit otherwise). Set before
    ///     <see cref="Complete" />.
    /// </summary>
    public long Rf64Threshold { get; init; } = uint.MaxValue - ExtensibleHeaderBytes;

    /// <summary>Frames written so far.</summary>
    public long Frames => Samples / Channels;

    long Samples { get; set; }

    /// <summary>Appends interleaved samples (whole frames, in the layout's order).</summary>
    public void Write(ReadOnlySpan<float> interleaved) {
        var done = 0;
        var frameFloats = Channels;
        while (done < interleaved.Length) {
            var count = Math.Min(interleaved.Length - done, buffer.Length / 4 / frameFloats * frameFloats);
            if (order is null) {
                for (var i = 0; i < count; i++) {
                    BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(4 * i), interleaved[done + i]);
                }
            } else {
                for (var f = 0; f < count; f += frameFloats) {
                    for (var k = 0; k < frameFloats; k++) {
                        BinaryPrimitives.WriteSingleLittleEndian(
                            buffer.AsSpan(4 * (f + k)),
                            interleaved[done + f + order[k]]
                        );
                    }
                }
            }

            stream.Write(buffer, 0, count * 4);
            done += count;
        }

        Samples += interleaved.Length;
    }

    /// <summary>Writes the sizes and closes the file.</summary>
    public void Complete() {
        if (completed) {
            return;
        }

        completed = true;
        Span<byte> header = stackalloc byte[Extensible ? ExtensibleHeaderBytes : HeaderBytes];
        WriteHeader(header, Samples * 4);
        stream.Flush();
        stream.Position = 0;
        stream.Write(header);
        stream.Flush(true);
        stream.Dispose();
    }

    public void Dispose() => Complete();

    void WriteHeader(Span<byte> h, long dataBytes) {
        if (Extensible) {
            WriteExtensibleHeader(h, dataBytes);
            return;
        }

        var data = (uint)Math.Min(uint.MaxValue - HeaderBytes, dataBytes);
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(HeaderBytes - 8) + data);
        WavFormatChunk.Write(h, 18, WavFormatChunk.IeeeFloat, Channels, SampleRate, 32);
        BinaryPrimitives.WriteUInt16LittleEndian(h[36..], 0); // cbSize
        "fact"u8.CopyTo(h[38..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[42..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(h[46..], (uint)Math.Min(uint.MaxValue, dataBytes / 4 / Channels));
        "data"u8.CopyTo(h[50..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[54..], data);
    }

    void WriteExtensibleHeader(Span<byte> h, long dataBytes) {
        var frames = dataBytes / 4 / Channels;
        var rf64 = dataBytes > Rf64Threshold;
        var riff = ExtensibleHeaderBytes - 8 + dataBytes;
        h.Clear();
        (rf64 ? "RF64"u8 : "RIFF"u8).CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], rf64 ? uint.MaxValue : (uint)riff);
        "WAVE"u8.CopyTo(h[8..]);
        (rf64 ? "ds64"u8 : "JUNK"u8).CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 28);
        if (rf64) {
            BinaryPrimitives.WriteInt64LittleEndian(h[20..], riff);
            BinaryPrimitives.WriteInt64LittleEndian(h[28..], dataBytes);
            BinaryPrimitives.WriteInt64LittleEndian(h[36..], frames);
            BinaryPrimitives.WriteUInt32LittleEndian(h[44..], 0); // no table
        }

        var f = h[48..];
        "fmt "u8.CopyTo(f);
        BinaryPrimitives.WriteUInt32LittleEndian(f[4..], 40);
        BinaryPrimitives.WriteUInt16LittleEndian(f[8..], 0xFFFE); // WAVE_FORMAT_EXTENSIBLE
        BinaryPrimitives.WriteUInt16LittleEndian(f[10..], (ushort)Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(f[12..], (uint)SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(f[16..], (uint)(SampleRate * Channels * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(f[20..], (ushort)(Channels * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(f[22..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(f[24..], 22); // cbSize
        BinaryPrimitives.WriteUInt16LittleEndian(f[26..], 32); // valid bits
        BinaryPrimitives.WriteUInt32LittleEndian(f[28..], mask);
        FloatSubFormat.CopyTo(f[32..]);
        var t = f[48..];
        "fact"u8.CopyTo(t);
        BinaryPrimitives.WriteUInt32LittleEndian(t[4..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(t[8..], rf64 ? uint.MaxValue : (uint)Math.Min(uint.MaxValue, frames));
        "data"u8.CopyTo(t[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(t[16..], rf64 ? uint.MaxValue : (uint)dataBytes);
    }
}

/// <summary>
///     A render's output file (Phase 10): one WAV (extensible for a surround layout), or one mono file per channel
///     (<c>{name}.{ADM label}.wav</c>) when the render splits its channels.
/// </summary>
public sealed class RenderFile : IDisposable {
    readonly FloatWavWriter? single;
    readonly FloatWavWriter[] split = [];
    readonly float[] scratch = [];

    public RenderFile(string path, int channels, int sampleRate, SpeakerLayout? layout, bool splitChannels) {
        if (!splitChannels) {
            single = new(path, channels, sampleRate, layout);
            Paths = [path];
            return;
        }

        var labels = layout?.ChannelCount == channels
            ? Enumerable.Range(0, channels).Select(layout.Label).ToArray()
            : channels == 2
                ? ["M+030", "M-030"]
                : channels == 1
                    ? ["M+000"]
                    : Enumerable.Range(0, channels)
                        .Select(static c => (c + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ToArray();
        var stem = Path.Combine(
            Path.GetDirectoryName(path)!,
            Path.GetFileNameWithoutExtension(path)
        );
        var writers = new List<FloatWavWriter>();
        try {
            foreach (var label in labels) {
                writers.Add(new($"{stem}.{label}.wav", 1, sampleRate));
            }
        } catch {
            foreach (var writer in writers) {
                writer.Dispose();
            }

            throw;
        }

        split = [..writers];
        scratch = new float[16 * 1024];
        Paths = [..split.Select(static w => w.Path)];
    }

    /// <summary>The files written.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>Appends interleaved frames (the layout's order).</summary>
    public void Write(ReadOnlySpan<float> interleaved) {
        if (single is not null) {
            single.Write(interleaved);
            return;
        }

        var channels = split.Length;
        var frames = interleaved.Length / channels;
        for (var c = 0; c < channels; c++) {
            for (var done = 0; done < frames; done += scratch.Length) {
                var n = Math.Min(scratch.Length, frames - done);
                for (var i = 0; i < n; i++) {
                    scratch[i] = interleaved[(done + i) * channels + c];
                }

                split[c].Write(scratch.AsSpan(0, n));
            }
        }
    }

    public void Complete() {
        single?.Complete();
        foreach (var writer in split) {
            writer.Complete();
        }
    }

    public void Dispose() => Complete();
}
