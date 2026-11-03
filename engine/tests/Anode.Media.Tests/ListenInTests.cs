using Anode.Contracts;
using Anode.Media.Opus;
using System.Numerics;

namespace Anode.Media.Tests;

/// <summary>
///     Listen-in's Opus: a 1 kHz tone survives the encoder and decoder (frequency within 2 Hz, level within 1 dB), and
///     the framer's headers carry sequence, transport positions and flags across rates and discontinuities.
/// </summary>
public sealed class ListenInTests {
    const int Rate = ListenIn.SampleRate;
    const int Frame = ListenIn.FrameSamples;

    [Fact]
    public void A_listen_in_frame_fits_a_header_and_the_largest_packet() =>
        Assert.Equal(16 + 1275, ListenIn.HeaderBytes + OpusEncoder.MaxPacketBytes);

    [Fact]
    public void A_tone_keeps_its_frequency_and_level_through_opus() {
        using var encoder = new OpusEncoder();
        using var decoder = new OpusDecoder();
        var packet = new byte[OpusEncoder.MaxPacketBytes];
        var decoded = new List<float>();
        var output = new float[Frame * 2];
        var input = new float[Frame * 2];
        long bytes = 0;
        for (var f = 0; f < 100; f++) {
            Tone(input, f * Frame, 1000, 0.5, Rate);
            var length = encoder.Encode(input, packet);
            bytes += length;
            Assert.Equal(Frame, decoder.Decode(packet.AsSpan(0, length), output));
            decoded.AddRange(output);
        }

        // Two seconds; skip the first half (the codec's delay and settling), measure the left channel.
        var left = decoded.Where(static (_, i) => i % 2 == 0).Skip(Rate / 2).ToArray();
        var (frequency, level) = Peak(left, Rate);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{frequency:0.000} Hz, {20 * Math.Log10(level / 0.5):0.000} dB"
        );
        Assert.InRange(frequency, 998, 1002);
        Assert.InRange(20 * Math.Log10(level / 0.5), -1, 1);
        TestContext.Current.TestOutputHelper?.WriteLine($"{bytes * 8 / 2.0 / 1000:0.0} kbit/s");
        Assert.InRange(bytes * 8 / 2.0, 100_000, 160_000); // about 128 kbit/s
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    [InlineData(96_000)]
    public void Framed_listen_in_decodes_to_the_tone_at_any_project_rate(int projectRate) {
        using var framer = new ListenInFramer(projectRate);
        using var decoder = new OpusDecoder();
        var block = new float[512 * 2];
        var frame = new byte[ListenIn.HeaderBytes + OpusEncoder.MaxPacketBytes];
        var output = new float[Frame * 2];
        var decoded = new List<float>();
        for (long at = 0; at < projectRate * 2L; at += 512) {
            Tone(block, at, 1000, 0.5, projectRate);
            framer.Write(block, at, true, false);
            int length;
            while ((length = framer.Read(frame)) > 0) {
                Assert.True(ListenIn.TryReadHeader(frame.AsSpan(0, length), out _, out _, out _, out var packet));
                decoder.Decode(packet, output);
                decoded.AddRange(output);
            }
        }

        Assert.Equal(new(48_000, 2, 960, 128_000, projectRate), framer.Format);
        var left = decoded.Where(static (_, i) => i % 2 == 0).Skip(Rate / 2).ToArray();
        var (frequency, level) = Peak(left, Rate);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{frequency:0.000} Hz, {20 * Math.Log10(level / 0.5):0.000} dB"
        );
        Assert.InRange(frequency, 998, 1002);
        Assert.InRange(20 * Math.Log10(level / 0.5), -1, 1);
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public void Headers_carry_sequence_positions_and_flags_across_a_discontinuity(int projectRate) {
        using var framer = new ListenInFramer(projectRate);
        var frames = new List<(uint Sequence, long Transport, byte Flags)>();
        var frame = new byte[ListenIn.HeaderBytes + OpusEncoder.MaxPacketBytes];

        void Feed(long transport, int blocks, bool playing, bool discontinuity) {
            var block = new float[256 * 2];
            for (var b = 0; b < blocks; b++) {
                var at = transport < 0 ? -1 : transport + b * 256L;
                framer.Write(block, at, playing, discontinuity && b == 0);
                int length;
                while ((length = framer.Read(frame)) > 0) {
                    Assert.True(
                        ListenIn.TryReadHeader(frame.AsSpan(0, length), out var s, out var t, out var f, out var p)
                    );
                    Assert.Equal(length - ListenIn.HeaderBytes, p.Length);
                    frames.Add((s, t, f));
                }
            }
        }

        // Stopped, then playing from sample 100 000, then a locate to 1 000 000.
        Feed(-1, 40, false, false);
        var stopped = frames.Count;
        Feed(100_000, 400, true, false);
        var played = frames.Count;
        Feed(1_000_000, 400, true, true);

        Assert.Equal(
            Enumerable.Range(0, frames.Count).Select(static i => (uint)i),
            frames.Select(static f => f.Sequence)
        );
        Assert.Equal(ListenIn.FlagDiscontinuity, frames[0].Flags);
        Assert.All(frames.Take(stopped), static f => Assert.Equal(-1, f.Transport));
        Assert.All(frames.Skip(1).Take(stopped - 1), static f => Assert.Equal(0, f.Flags));

        // While playing, each frame's first sample is where 20 ms at 48 kHz lands at the project's rate.
        var firstPlaying = frames.FindIndex(static f => f.Transport >= 0);
        Assert.InRange(firstPlaying, stopped, stopped + 2);
        for (var i = firstPlaying + 1; i < played; i++) {
            var step = frames[i].Transport - frames[i - 1].Transport;
            Assert.InRange(step, Frame * (double)projectRate / Rate - 1, Frame * (double)projectRate / Rate + 1);
            Assert.Equal(ListenIn.FlagPlaying, frames[i].Flags);
        }

        // After the locate: the flag once, positions from the new place, exactly 20 ms apart in project samples.
        Assert.Equal(ListenIn.FlagPlaying | ListenIn.FlagDiscontinuity, frames[played].Flags);
        Assert.Equal(1_000_000, frames[played].Transport);
        for (var i = played + 1; i < frames.Count; i++) {
            var expected = 1_000_000 + (long)Math.Floor((i - played) * (double)Frame * projectRate / Rate + 1e-9);
            Assert.Equal(expected, frames[i].Transport);
            Assert.Equal(ListenIn.FlagPlaying, frames[i].Flags);
        }
    }

    [Fact]
    public void Nothing_is_framed_before_twenty_milliseconds() {
        using var framer = new ListenInFramer(48_000);
        var frame = new byte[ListenIn.HeaderBytes + OpusEncoder.MaxPacketBytes];
        framer.Write(new float[959 * 2], 0, true, false);
        Assert.Equal(0, framer.Read(frame));
        framer.Write(new float[2], 959, true, false);
        Assert.True(framer.Read(frame) > ListenIn.HeaderBytes);
        Assert.Equal(0, framer.Read(frame));
    }

    /// <summary>A stereo tone into <paramref name="interleaved" />, starting at sample <paramref name="start" />.</summary>
    static void Tone(float[] interleaved, long start, double hz, double amplitude, int rate) {
        for (var n = 0; n < interleaved.Length / 2; n++) {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * hz * (start + n) / rate));
            interleaved[2 * n] = interleaved[2 * n + 1] = value;
        }
    }

    /// <summary>
    ///     The strongest frequency (a Hann-windowed DFT around 1 kHz, interpolated quadratically in dB) and the RMS level as
    ///     a sine's amplitude (√2 × RMS).
    /// </summary>
    static (double Frequency, double Amplitude) Peak(float[] samples, int rate) {
        var n = samples.Length;

        double Magnitude(double hz) {
            var sum = Complex.Zero;
            for (var i = 0; i < n; i++) {
                var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
                sum += samples[i] * window * Complex.FromPolarCoordinates(1, -2 * Math.PI * hz * i / rate);
            }

            return Math.Log(sum.Magnitude + 1e-12);
        }

        var best = 900.0;
        for (var hz = 900.0; hz <= 1100; hz += 1) {
            if (Magnitude(hz) > Magnitude(best)) {
                best = hz;
            }
        }

        var (a, b, c) = (Magnitude(best - 1), Magnitude(best), Magnitude(best + 1));
        var frequency = best + 0.5 * (a - c) / (a - 2 * b + c);
        var rms = Math.Sqrt(samples.Average(static s => s * (double)s));
        return (frequency, rms * Math.Sqrt(2));
    }
}
