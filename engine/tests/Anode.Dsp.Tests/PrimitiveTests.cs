using Anode.Dsp.Envelopes;
using Anode.Dsp.Filters;
using Anode.Dsp.IO;
using Anode.Dsp.Modulation;
using Anode.Dsp.Oscillators;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

public sealed class PrimitiveTests : IDisposable {
    const double Rate = 48_000;
    readonly string folder = Play.TempFolder();

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void Adsr_stages_take_their_times() {
        var settings = AdsrSettings.From(0.01, 0.005, 0.1, 0.5, 0.2, Rate);
        var envelope = new Adsr();
        envelope.Trigger();
        var levels = new List<float>();
        for (var i = 0; i < 480 + 240 + 4_800; i++) {
            levels.Add(envelope.Next(settings));
        }

        Assert.Equal(1f, levels[479]); // a linear 10 ms attack ends on sample 480
        Assert.InRange(levels[239], 0.49f, 0.51f);
        Assert.Equal(1f, levels[480 + 238]); // holding for 5 ms
        Assert.InRange(levels[^1], 0.5f + 0.5f * 0.00099f, 0.5f + 0.5f * 0.00101f); // decay: a T60 to the sustain
        Assert.Equal(AdsrStage.Decay, envelope.Stage);

        envelope.Release();
        var start = envelope.Level;
        for (var i = 0; i < 9_600; i++) {
            envelope.Next(settings);
        }

        Assert.InRange(ToDb(envelope.Level / start), -60.1, -59.9); // release: a T60
        while (!envelope.IsIdle) {
            envelope.Next(settings);
        }

        Assert.Equal(0, envelope.Level);
    }

    [Fact]
    public void Adsr_retriggers_from_its_level_and_ends_without_sustain() {
        var settings = AdsrSettings.From(0.01, 0, 0.01, 0, 0.01, Rate);
        var envelope = new Adsr();
        envelope.Trigger();
        for (var i = 0; i < 3_000; i++) {
            envelope.Next(settings);
        }

        Assert.True(envelope.IsIdle); // decayed to nothing with no sustain

        settings = AdsrSettings.From(0.01, 0, 1, 1, 1, Rate);
        envelope.Trigger();
        for (var i = 0; i < 240; i++) {
            envelope.Next(settings);
        }

        envelope.Release();
        envelope.Next(settings);
        var level = envelope.Level;
        envelope.Trigger();
        Assert.InRange(envelope.Next(settings) - level, 0, 1 / 480f + 1e-6f); // no jump to 0
    }

    [Theory]
    [InlineData(FilterMode.LowPass24, 1_000, -3.01)]
    [InlineData(FilterMode.LowPass24, 2_000, -24.08)] // 4th-order Butterworth an octave up
    [InlineData(FilterMode.LowPass12, 1_000, -3.01)]
    [InlineData(FilterMode.LowPass12, 2_000, -12.30)]
    [InlineData(FilterMode.HighPass12, 1_000, -3.01)]
    [InlineData(FilterMode.HighPass12, 500, -12.30)]
    [InlineData(FilterMode.BandPass12, 1_000, 0)]
    public void Filter_responses_without_resonance(FilterMode mode, double frequency, double expectedDb) {
        // The bilinear transform warps frequencies away from the cutoff a little: compare at 1 kHz of 48 kHz.
        var response = ImpulseResponse(mode, 1_000, 0);
        var tolerance = frequency == 1_000 ? 0.05 : 0.3;
        Assert.InRange(ToDb(MagnitudeAt(response, frequency, Rate)), expectedDb - tolerance, expectedDb + tolerance);
    }

    [Fact]
    public void Resonance_raises_a_peak_at_the_cutoff() {
        foreach (var mode in new[] { FilterMode.LowPass24, FilterMode.LowPass12, FilterMode.HighPass12 }) {
            var flat = MagnitudeAt(ImpulseResponse(mode, 1_000, 0), 1_000, Rate);
            var resonant = MagnitudeAt(ImpulseResponse(mode, 1_000, 0.7), 1_000, Rate);
            Assert.True(ToDb(resonant / flat) > 12, $"{mode}: {ToDb(resonant / flat):0.0} dB");
            Assert.InRange(
                MultimodeCoefficients.Quality(mode, 1),
                MultimodeCoefficients.MaxQ - 1e-9,
                MultimodeCoefficients.MaxQ + 1e-9
            );
        }
    }

    static float[] ImpulseResponse(FilterMode mode, double cutoff, double resonance) {
        var c = MultimodeCoefficients.Design(mode, Rate, cutoff, MultimodeCoefficients.Quality(mode, resonance));
        var filter = new MultimodeFilter();
        var response = new float[16_384];
        for (var i = 0; i < response.Length; i++) {
            response[i] = (float)filter.Tick(c, i == 0 ? 1 : 0);
        }

        return response;
    }

    [Theory]
    [InlineData(Waveform.Saw)]
    [InlineData(Waveform.Pulse)]
    [InlineData(Waveform.Triangle)]
    [InlineData(Waveform.Sine)]
    public void Waveforms_span_minus_one_to_one_without_dc(Waveform waveform) {
        const double increment = 100.0 / Rate; // 100 Hz: 480 samples a cycle
        uint noise = 1;
        double sum = 0, peak = 0;
        for (var i = 0; i < 480; i++) {
            var v = PolyBlep.Render(waveform, i * increment % 1, increment, 0.3, ref noise);
            sum += v;
            peak = Math.Max(peak, Math.Abs(v));
        }

        Assert.InRange(sum / 480, -0.01, 0.01);
        Assert.InRange(peak, 0.95, 1.71); // a pulse of width 0.3 swings 1.4 / 0.6 about its mean
    }

    [Fact]
    public void Triangle_corners_are_band_limited() {
        // PolyBLAMP: the corrected triangle is continuous and smoother than the naive one at the corner.
        const double increment = 0.0371;
        var phase = 0.5 - increment / 3;
        var v = PolyBlep.Triangle(phase, increment);
        var naive = 1 - 4 * Math.Abs(phase - 0.5);
        Assert.True(v < naive); // rounded off below the sharp peak
        Assert.InRange(PolyBlep.Ramp(0, 1), 1.0 / 6 - 1e-12, 1.0 / 6 + 1e-12);
        Assert.InRange(PolyBlep.Ramp(1 - 1e-12, 0.1), 1.0 / 6 - 1e-6, 1.0 / 6 + 1e-6);
        Assert.InRange(PolyBlep.Ramp(0.1 - 1e-12, 0.1), -1e-6, 1e-6);
    }

    [Theory]
    [InlineData(LfoShape.Sine)]
    [InlineData(LfoShape.Triangle)]
    [InlineData(LfoShape.Saw)]
    [InlineData(LfoShape.Square)]
    [InlineData(LfoShape.SampleAndHold)]
    public void Lfo_shapes_stay_in_range_and_repeat(LfoShape shape) {
        var lfo = new Lfo();
        lfo.Reset();
        var values = new HashSet<float>();
        for (var i = 0; i < 1_000; i++) {
            var v = lfo.Next(shape, 0.01);
            Assert.InRange(v, -1, 1);
            values.Add(v);
        }

        Assert.True(values.Count > (shape is LfoShape.Square ? 1 : 5));
    }

    [Fact]
    public void Wav_reader_reads_encodings_and_the_smpl_chunk() {
        var path = Path.Combine(folder, "a.wav");
        TestWav.Write(
            path,
            44_100,
            [[0.5f, -0.25f, 0], [0.125f, 1, -1]],
            rootNote: 57,
            rootCents: 25,
            loopStart: 1,
            loopEnd: 2
        );
        var wav = WavReader.Read(path);
        Assert.Equal((44_100, 2, 3, 57), (wav.SampleRate, wav.Channels.Length, wav.Frames, wav.RootNote));
        Assert.Equal(25, wav.RootCents, 3);
        Assert.Equal((1L, 2L), (wav.LoopStart, wav.LoopEnd));
        Assert.Equal(0.5f, wav.Channels[0][0], 5);
        Assert.Equal(-1f, wav.Channels[1][2], 5);

        TestWav.Write(path, 48_000, [[0.75f]], true);
        wav = WavReader.Read(path);
        Assert.Equal((-1, -1L, 0.75f), (wav.RootNote, wav.LoopStart, wav.Channels[0][0]));
        Assert.Throws<InvalidDataException>(static () => WavReader.Read("RIFF____WAVEjunk"u8));
    }
}
