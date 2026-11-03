using Anode.Dsp.Spatial;
using Anode.Dsp.Tests.Support;

namespace Anode.Dsp.Tests.Spatial;

/// <summary>
///     The HRTF set Anode ships (SADIE II D2) and the binaural renderer: interaural level and time differences at
///     known angles (the exit criterion: 90° left at least 6 dB louder in the left ear, the right ear 0.5–0.8 ms later).
/// </summary>
public sealed class BinauralTests {
    static readonly Lazy<HrtfSet> Shipped = new(() => HrtfSet.Load(HrtfSet.Find(Ear.RepositoryRoot())!));

    /// <summary>A 7.1.4 render of an impulse in one speaker, or an object at a direction: the two ears' responses.</summary>
    static (float[] Left, float[] Right) Impulse(float rate, Action<BinauralRenderer, bool, float[], float[], float[]> render) {
        const int block = 128;
        var renderer = new BinauralRenderer();
        renderer.Prepare(Shipped.Value, SpeakerLayout.Of(SpeakerFormat.Surround714), rate, block, 1);
        const int length = 1024;
        var left = new float[length];
        var right = new float[length];
        var speakers = new float[12 * block];
        var l = new float[block];
        var r = new float[block];
        for (var start = 0; start < length; start += block) {
            Array.Clear(speakers);
            render(renderer, start == 0, speakers, l, r);
            l.CopyTo(left, start);
            r.CopyTo(right, start);
        }

        return (left, right);
    }

    static (float[] Left, float[] Right) Speaker(int channel, float rate = 48_000) =>
        Impulse(
            rate,
            (renderer, first, speakers, l, r) => {
                speakers[channel * 128] = first ? 1 : 0;
                renderer.Process(new(speakers, 12, 128), l, r);
            }
        );

    static (float[] Left, float[] Right) Object(float azimuth, float elevation, float rate = 48_000) {
        var input = new float[128];
        return Impulse(
            rate,
            (renderer, first, speakers, l, r) => {
                input[0] = first ? 1 : 0;
                renderer.Process(new(speakers, 12, 128), l, r);
                renderer.ProcessObject(0, input, azimuth, elevation, l, r);
            }
        );
    }

    static double IldDb((float[] Left, float[] Right) ears) =>
        Support.Analysis.ToDb(Support.Analysis.Rms(ears.Left) / Support.Analysis.Rms(ears.Right));

    /// <summary>
    ///     How much later the right ear hears than the left, in ms: the lag of the peak of the cross-correlation of the
    ///     two responses low-passed at 1.5 kHz (4th-order Butterworth), the usual measure of the interaural time difference.
    /// </summary>
    static double ItdMs((float[] Left, float[] Right) ears, float rate) {
        var left = LowPass(ears.Left, rate);
        var right = LowPass(ears.Right, rate);
        var reach = (int)(0.0015 * rate);
        var best = 0;
        var most = double.NegativeInfinity;
        for (var lag = -reach; lag <= reach; lag++) {
            var sum = 0.0;
            for (var n = Math.Max(0, -lag); n < left.Length && n + lag < right.Length; n++) {
                sum += left[n] * right[n + lag];
            }

            if (sum > most) {
                (most, best) = (sum, lag);
            }
        }

        return best * 1000.0 / rate;
    }

    static double[] LowPass(float[] signal, float rate) {
        var y = signal.Select(static s => (double)s).Concat(new double[signal.Length]).ToArray();
        foreach (var q in new[] { 0.54119610, 1.3065630 }) {
            var w = 2 * Math.PI * 1500 / rate;
            var alpha = Math.Sin(w) / (2 * q);
            var a0 = 1 + alpha;
            var b0 = (1 - Math.Cos(w)) / 2 / a0;
            var b1 = (1 - Math.Cos(w)) / a0;
            var a1 = -2 * Math.Cos(w) / a0;
            var a2 = (1 - alpha) / a0;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (var n = 0; n < y.Length; n++) {
                var x = y[n];
                var o = b0 * x + b1 * x1 + b0 * x2 - a1 * y1 - a2 * y2;
                (x2, x1, y2, y1) = (x1, x, y1, o);
                y[n] = o;
            }
        }

        return y;
    }

    [Fact]
    public void The_shipped_set_is_sadie_ii_d2_at_48_khz_under_apache_2() {
        var set = Shipped.Value;
        Assert.Equal(48_000, set.SampleRate);
        Assert.Equal(256, set.Length);
        Assert.Equal(1982, set.DirectionCount);
        Assert.Contains("SADIE II Database, subject D2 (KEMAR)", set.Metadata, StringComparison.Ordinal);
        Assert.Contains("Apache License, Version 2.0", set.Metadata, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(48_000f)]
    [InlineData(44_100f)]
    public void A_source_at_90_degrees_left_is_louder_and_earlier_in_the_left_ear(float rate) {
        // The left side speaker of 7.1.4 (M+090) and an object at 90°, 0°.
        foreach (var ears in new[] { Speaker(4, rate), Object(90, 0, rate) }) {
            Assert.True(IldDb(ears) >= 6, $"ILD {IldDb(ears):F1} dB");
            Assert.InRange(ItdMs(ears, rate), 0.5, 0.8);
        }

        var right = Object(-90, 0, rate);
        Assert.True(IldDb(right) <= -6);
        Assert.InRange(-ItdMs(right, rate), 0.5, 0.8);
    }

    [Fact]
    public void Sources_ahead_and_behind_reach_both_ears_alike() {
        foreach (var ears in new[] { Speaker(2), Object(0, 0), Object(180, 0), Object(0, 90) }) {
            Assert.InRange(IldDb(ears), -1.5, 1.5);
            Assert.InRange(ItdMs(ears, 48_000), -0.1, 0.1);
        }
    }

    [Fact]
    public void A_measured_direction_is_returned_as_measured_and_others_interpolate() {
        var set = Shipped.Value;
        var (az, el) = set.Direction(500);
        var left = new float[set.Length];
        var right = new float[set.Length];
        set.ImpulseResponses(az, el, left, right);
        var again = new float[set.Length];
        var right2 = new float[set.Length];
        set.ImpulseResponses(az + 0.001f, el, again, right2);
        Assert.Equal(left, again);

        // 61° lies between the measured 60° and 62°: its interaural delay lies between theirs.
        static double Itd(HrtfSet s, float azimuth) {
            var l = new float[s.Length];
            var r = new float[s.Length];
            s.ImpulseResponses(azimuth, 0, l, r);
            return ItdMs((l, r), s.SampleRate);
        }

        var between = Itd(set, 61);
        Assert.InRange(between, Math.Min(Itd(set, 60), Itd(set, 62)) - 0.021, Math.Max(Itd(set, 60), Itd(set, 62)) + 0.021);
        var halfway = Itd(set, 45);
        Assert.InRange(halfway, Itd(set, 30), Itd(set, 60));
    }

    [Fact]
    public void The_lfe_reaches_both_ears_unfiltered() {
        var ears = Speaker(3);
        Assert.Equal(1, ears.Left[0]);
        Assert.Equal(1, ears.Right[0]);
        Assert.Equal(0, Support.Analysis.Peak(ears.Left.AsSpan(1)));
    }

    [Fact]
    public void A_file_that_is_not_an_hrtf_set_is_refused() {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllBytes(path, [.."ANHR"u8, 1, 0, 0, 0, 0x80, 0xBB, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0]);
            Assert.Throws<InvalidDataException>(() => HrtfSet.Load(path));
            File.WriteAllText(path, "RIFF....WAVE");
            Assert.Throws<InvalidDataException>(() => HrtfSet.Load(path));
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rendering_allocates_nothing() {
        var layout = SpeakerLayout.Of(SpeakerFormat.Surround714);
        var renderer = new BinauralRenderer();
        renderer.Prepare(Shipped.Value, layout, 48_000, 256, 2);
        var speakers = new float[12 * 256];
        var input = new float[256];
        var left = new float[256];
        var right = new float[256];
        var azimuth = 0f;
        Assert.Equal(
            0,
            AllocationProbe.Measure(() => {
                renderer.Process(new(speakers, 12, 256), left, right);
                renderer.ProcessObject(0, input, azimuth += 5, 10, left, right);
                renderer.ProcessObject(1, input, -azimuth, 0, left, right);
            })
        );
        Assert.Equal(0, renderer.LatencySamples);
    }
}
