using System.Buffers.Binary;

namespace Anode.Engine.Recording;

/// <summary>
///     Writes a 24-bit PCM WAV file (a canonical 44-byte header, then the data). The sizes in the header are written
///     when it completes; a writer that dies leaves them at zero, which readers (and <c>WavFormat</c>) take as "to the
///     end of the file".
/// </summary>
public sealed class WavWriter : IDisposable {
    const int HeaderBytes = 44;
    readonly FileStream stream;
    readonly byte[] buffer;
    bool completed;

    public WavWriter(string path, int channels, int sampleRate, int bufferFrames = 16_384) {
        Path = path;
        Channels = channels;
        SampleRate = sampleRate;
        stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
        buffer = new byte[bufferFrames * channels * 3];
        Span<byte> header = stackalloc byte[HeaderBytes];
        WriteHeader(header, 0);
        stream.Write(header);
    }

    public string Path { get; }

    public int Channels { get; }

    public int SampleRate { get; }

    /// <summary>Frames written so far.</summary>
    public long Frames { get; private set; }

    /// <summary>A float sample as signed 24-bit PCM: clipped to [−1, 1), rounded to nearest.</summary>
    public static int ToInt24(float sample) {
        var scaled = MathF.Round(sample * 8_388_608f);
        return (int)Math.Clamp(scaled, -8_388_608f, 8_388_607f);
    }

    /// <summary>Appends whole interleaved frames.</summary>
    public void Write(ReadOnlySpan<float> interleaved) {
        var samples = interleaved.Length - interleaved.Length % Channels;
        var done = 0;
        while (done < samples) {
            var count = Math.Min(samples - done, buffer.Length / 3);
            for (var i = 0; i < count; i++) {
                var v = ToInt24(interleaved[done + i]);
                buffer[3 * i] = (byte)v;
                buffer[3 * i + 1] = (byte)(v >> 8);
                buffer[3 * i + 2] = (byte)(v >> 16);
            }

            stream.Write(buffer, 0, count * 3);
            done += count;
        }

        Frames += samples / Channels;
    }

    /// <summary>Writes the sizes into the header and closes the file.</summary>
    public void Complete() {
        if (completed) {
            return;
        }

        completed = true;
        Span<byte> header = stackalloc byte[HeaderBytes];
        WriteHeader(header, Frames * Channels * 3);
        stream.Flush();
        stream.Position = 0;
        stream.Write(header);
        stream.Flush(true);
        stream.Dispose();
    }

    public void Dispose() => Complete();

    void WriteHeader(Span<byte> h, long dataBytes) {
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)Math.Min(uint.MaxValue, 36 + dataBytes));
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], (ushort)Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)(SampleRate * Channels * 3));
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)(Channels * 3));
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 24);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)Math.Min(uint.MaxValue - 36, dataBytes));
    }
}
