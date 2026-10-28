using System.Buffers.Binary;

namespace Anode.Engine.Rendering;

/// <summary>
///     Writes a 32-bit float WAV (<c>WAVE_FORMAT_IEEE_FLOAT</c>: <c>fmt </c>, <c>fact</c>, then the data), the format
///     renders produce (docs/05-phase-4.md, "Bounce": the server converts it). The sizes are written on completion.
/// </summary>
public sealed class FloatWavWriter : IDisposable {
    const int HeaderBytes = 12 + 8 + 18 + 12 + 8;
    readonly FileStream stream;
    readonly byte[] buffer = new byte[64 * 1024];
    bool completed;

    public FloatWavWriter(string path, int channels, int sampleRate) {
        Path = path;
        Channels = channels;
        SampleRate = sampleRate;
        stream = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
        Span<byte> header = stackalloc byte[HeaderBytes];
        WriteHeader(header, 0);
        stream.Write(header);
    }

    public string Path { get; }

    public int Channels { get; }

    public int SampleRate { get; }

    /// <summary>Frames written so far.</summary>
    public long Frames => Samples / Channels;

    long Samples { get; set; }

    /// <summary>Appends interleaved samples.</summary>
    public void Write(ReadOnlySpan<float> interleaved) {
        var done = 0;
        while (done < interleaved.Length) {
            var count = Math.Min(interleaved.Length - done, buffer.Length / 4);
            for (var i = 0; i < count; i++) {
                BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(4 * i), interleaved[done + i]);
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
        Span<byte> header = stackalloc byte[HeaderBytes];
        WriteHeader(header, Samples * 4);
        stream.Flush();
        stream.Position = 0;
        stream.Write(header);
        stream.Flush(true);
        stream.Dispose();
    }

    public void Dispose() => Complete();

    void WriteHeader(Span<byte> h, long dataBytes) {
        var data = (uint)Math.Min(uint.MaxValue - HeaderBytes, dataBytes);
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(HeaderBytes - 8) + data);
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 18);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 3); // IEEE float
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], (ushort)Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)(SampleRate * Channels * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)(Channels * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(h[36..], 0); // cbSize
        "fact"u8.CopyTo(h[38..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[42..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(h[46..], (uint)Math.Min(uint.MaxValue, dataBytes / 4 / Channels));
        "data"u8.CopyTo(h[50..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[54..], data);
    }
}
