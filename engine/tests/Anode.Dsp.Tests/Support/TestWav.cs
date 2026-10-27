using System.Buffers.Binary;

namespace Anode.Dsp.Tests.Support;

/// <summary>WAV files for the tests: written as 24-bit PCM or 32-bit float, with an optional <c>smpl</c> chunk.</summary>
public static class TestWav {
    /// <summary>Writes <paramref name="channels" /> (equal lengths) to <paramref name="path" />.</summary>
    public static void Write(
        string path,
        int sampleRate,
        float[][] channels,
        bool asFloat = false,
        int rootNote = -1,
        double rootCents = 0,
        long loopStart = -1,
        long loopEnd = -1
    ) {
        var frames = channels[0].Length;
        var bytesPerSample = asFloat ? 4 : 3;
        var dataBytes = frames * channels.Length * bytesPerSample;
        var smpl = rootNote >= 0 || loopStart >= 0;
        var smplBytes = smpl ? 36 + (loopStart >= 0 ? 24 : 0) : 0;
        var total = 4 + (8 + 16) + (8 + dataBytes) + (smpl ? 8 + smplBytes : 0);
        using var stream = new MemoryStream();
        var b = new byte[4];

        void U32(uint v) {
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            stream.Write(b);
        }

        void U16(ushort v) {
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            stream.Write(b.AsSpan(0, 2));
        }

        stream.Write("RIFF"u8);
        U32((uint)total);
        stream.Write("WAVE"u8);
        stream.Write("fmt "u8);
        U32(16);
        U16((ushort)(asFloat ? 3 : 1));
        U16((ushort)channels.Length);
        U32((uint)sampleRate);
        U32((uint)(sampleRate * channels.Length * bytesPerSample));
        U16((ushort)(channels.Length * bytesPerSample));
        U16((ushort)(bytesPerSample * 8));
        if (smpl) {
            stream.Write("smpl"u8);
            U32((uint)smplBytes);
            U32(0); // manufacturer
            U32(0); // product
            U32((uint)(1e9 / sampleRate));
            U32((uint)(rootNote >= 0 ? rootNote : 60));
            U32((uint)(rootCents / 100 * 4_294_967_296.0));
            U32(0);
            U32(0);
            U32(loopStart >= 0 ? 1u : 0u);
            U32(0);
            if (loopStart >= 0) {
                U32(0);
                U32(0);
                U32((uint)loopStart);
                U32((uint)loopEnd);
                U32(0);
                U32(0);
            }
        }

        stream.Write("data"u8);
        U32((uint)dataBytes);
        for (var f = 0; f < frames; f++) {
            foreach (var channel in channels) {
                if (asFloat) {
                    BinaryPrimitives.WriteSingleLittleEndian(b, channel[f]);
                    stream.Write(b);
                } else {
                    var v = (int)Math.Clamp(MathF.Round(channel[f] * 8_388_608f), -8_388_608f, 8_388_607f);
                    b[0] = (byte)v;
                    b[1] = (byte)(v >> 8);
                    b[2] = (byte)(v >> 16);
                    stream.Write(b.AsSpan(0, 3));
                }
            }
        }

        File.WriteAllBytes(path, stream.ToArray());
    }

    /// <summary>A sine of <paramref name="frequency" /> Hz, <paramref name="seconds" /> long.</summary>
    public static float[] Sine(double frequency, int sampleRate, double seconds, float amplitude = 0.5f) {
        var samples = new float[(int)(seconds * sampleRate)];
        for (var i = 0; i < samples.Length; i++) {
            samples[i] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / sampleRate);
        }

        return samples;
    }

    /// <summary>A constant level (a "DC" sample whose output level is easy to read).</summary>
    public static float[] Constant(float level, int frames) {
        var samples = new float[frames];
        Array.Fill(samples, level);
        return samples;
    }

    /// <summary>
    ///     A plucked-string tone at <paramref name="frequency" /> Hz (Karplus-Strong, deterministic), for the demos and
    ///     the golden sampler render.
    /// </summary>
    public static float[] Pluck(double frequency, int sampleRate, double seconds, int seed) {
        var period = Math.Max(2, (int)Math.Round(sampleRate / frequency));
        var line = new float[period];
        var random = (uint)(seed * 2_654_435_761u) | 1;
        for (var i = 0; i < period; i++) {
            random ^= random << 13;
            random ^= random >> 17;
            random ^= random << 5;
            line[i] = random * (2f / uint.MaxValue) - 1;
        }

        var samples = new float[(int)(seconds * sampleRate)];
        for (var i = 0; i < samples.Length; i++) {
            var at = i % period;
            var next = (at + 1) % period;
            samples[i] = 0.5f * line[at];
            line[at] = 0.996f * 0.5f * (line[at] + line[next]);
        }

        return samples;
    }
}
