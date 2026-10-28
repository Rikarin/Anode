using Anode.Dsp.Flex;
using Anode.Dsp.Tests.Support;
using System.Numerics;

namespace Anode.Dsp.Tests.Flex;

/// <summary>A file in memory (interleaved), zero outside it.</summary>
public sealed class ArraySource(float[] data, int channels) : IFlexSource {
    public int Channels => channels;
    public long Frames => data.Length / channels;
    public float[] Data => data;

    public int Read(long fileFrame, Span<float> interleaved) {
        interleaved.Clear();
        var frames = interleaved.Length / channels;
        var first = Math.Max(fileFrame, 0);
        var end = Math.Min(fileFrame + frames, Frames);
        if (first >= end) {
            return 0;
        }

        data.AsSpan((int)first * channels, (int)(end - first) * channels)
            .CopyTo(interleaved[((int)(first - fileFrame) * channels)..]);
        return (int)(end - first);
    }

    /// <summary>A stereo source with <paramref name="mono" /> in both channels.</summary>
    public static ArraySource Stereo(float[] mono) {
        var data = new float[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++) {
            data[i * 2] = mono[i];
            data[i * 2 + 1] = mono[i];
        }

        return new(data, 2);
    }
}

/// <summary>Test material, warp maps, rendering and measurement for the flex algorithms.</summary>
public static class FlexKit {
    public const int Rate = 48_000;

    public static IReadOnlyList<FlexKind> Algorithms { get; } = [
        FlexKind.Polyphonic, FlexKind.Monophonic, FlexKind.Slicing, FlexKind.Rhythmic, FlexKind.Speed,
        FlexKind.Tempophone, FlexKind.Pitch
    ];

    public static IReadOnlyList<FlexKind> Stretch { get; } = [FlexKind.Polyphonic, FlexKind.Monophonic, FlexKind.Pitch];

    /// <summary>
    ///     Renders <paramref name="frames" /> frames from <paramref name="offset" /> in calls of
    ///     <paramref name="block" />.
    /// </summary>
    public static float[] Render(
        IFlexRenderer renderer,
        IFlexSource source,
        FlexWarpPoint[] warp,
        FlexPitchPoint[] pitch,
        long length,
        long offset,
        int frames,
        int block,
        FlexKind kind = FlexKind.Polyphonic
    ) {
        var channels = source.Channels;
        var output = new float[frames * channels];
        for (var done = 0; done < frames; done += block) {
            var count = Math.Min(block, frames - done);
            renderer.Render(
                new(kind, warp, pitch, length),
                offset + done,
                output.AsSpan(done * channels, count * channels),
                channels,
                source
            );
        }

        return output;
    }

    /// <summary>The whole entry, from 0, in 512-frame calls, by a fresh renderer of <paramref name="kind" />.</summary>
    public static float[] RenderAll(
        FlexKind kind,
        IFlexSource source,
        FlexWarpPoint[] warp,
        FlexPitchPoint[]? pitch = null,
        int block = 512
    ) {
        using var renderer = FlexRenderers.Create(kind, source.Channels, Rate, 4_096);
        var length = warp[^1].Offset;
        return Render(renderer, source, warp, pitch ?? [], length, 0, (int)length, block, kind);
    }

    /// <summary>Channel <paramref name="channel" /> of interleaved audio.</summary>
    public static float[] Channel(float[] interleaved, int channels, int channel = 0) {
        var result = new float[interleaved.Length / channels];
        for (var i = 0; i < result.Length; i++) {
            result[i] = interleaved[i * channels + channel];
        }

        return result;
    }

    /// <summary>A warp map through (offset, file frame) points.</summary>
    public static FlexWarpPoint[] Warp(params (long Offset, double FileFrame)[] points) =>
        [..points.Select(static p => new FlexWarpPoint { Offset = p.Offset, FileFrame = p.FileFrame })];

    /// <summary>A constant rate (file frames per output frame) over <paramref name="length" /> output frames.</summary>
    public static FlexWarpPoint[] Constant(long length, double rate, double start = 0) =>
        Warp((0, start), (length, start + length * rate));

    /// <summary>The entry offset that plays <paramref name="fileFrame" /> (the inverse of a monotonic map).</summary>
    public static double OffsetOf(FlexWarpPoint[] warp, double fileFrame) {
        for (var i = 0; i < warp.Length - 1; i++) {
            var a = warp[i];
            var b = warp[i + 1];
            if (fileFrame >= a.FileFrame && fileFrame <= b.FileFrame) {
                return a.Offset + (fileFrame - a.FileFrame) * (b.Offset - a.Offset) / (b.FileFrame - a.FileFrame);
            }
        }

        return double.NaN;
    }

    /// <summary>A constant pitch shift at every file frame.</summary>
    public static FlexPitchPoint[] Shift(double cents, double formantCents = 0, double gainDb = 0) => [
        new() { FileFrame = 0, Cents = (float)cents, FormantCents = (float)formantCents, GainDb = (float)gainDb }
    ];

    /// <summary>1 ms decaying noise clicks (deterministic) at <paramref name="positions" />.</summary>
    public static float[] Clicks(int frames, IEnumerable<long> positions, float level = 0.8f) {
        var audio = new float[frames];
        var random = new Random(7);
        foreach (var p in positions) {
            for (var i = 0; i < Rate / 1_000 && p + i < frames; i++) {
                audio[p + i] += level * (float)((random.NextDouble() * 2 - 1) * Math.Exp(-i / 10.0));
            }
        }

        return audio;
    }

    /// <summary>A sum of sines at <paramref name="frequencies" />, each at <paramref name="level" />.</summary>
    public static float[] Tones(int frames, double level, params double[] frequencies) {
        var audio = new float[frames];
        foreach (var f in frequencies) {
            for (var i = 0; i < frames; i++) {
                audio[i] += (float)(level * Math.Sin(2 * Math.PI * f * i / Rate));
            }
        }

        return audio;
    }

    /// <summary>
    ///     A synthesized vowel ("ah"): a band-limited glottal pulse train at <paramref name="f0" /> through resonators at
    ///     700, 1,220 and 2,600 Hz.
    /// </summary>
    public static float[] Vowel(int frames, double f0) {
        var source = new double[frames];
        for (var h = 1; h * f0 < 8_000; h++) {
            var amplitude = 1.0 / h;
            for (var i = 0; i < frames; i++) {
                source[i] += amplitude * Math.Sin(2 * Math.PI * h * f0 * i / Rate);
            }
        }

        var output = new double[frames];
        foreach (var (frequency, bandwidth, gain) in new[] {
                     (700.0, 110.0, 1.0), (1_220.0, 120.0, 0.6), (2_600.0, 160.0, 0.3)
                 }) {
            var r = Math.Exp(-Math.PI * bandwidth / Rate);
            var a1 = 2 * r * Math.Cos(2 * Math.PI * frequency / Rate);
            var a2 = -r * r;
            double y1 = 0, y2 = 0;
            for (var i = 0; i < frames; i++) {
                var y = (1 - r) * source[i] + a1 * y1 + a2 * y2;
                y2 = y1;
                y1 = y;
                output[i] += gain * y;
            }
        }

        var peak = output.Max(Math.Abs);
        return [..output.Select(s => (float)(0.5 * s / peak))];
    }

    /// <summary>A 100 bpm loop of synthesized kicks (on beats) and hats (on eighths), <paramref name="beats" /> long.</summary>
    public static float[] DrumLoop(int beats, double bpm = 100) {
        var beat = Rate * 60 / bpm;
        var frames = (int)(beats * beat);
        var audio = new float[frames];
        var random = new Random(3);
        for (var e = 0; e < beats * 2; e++) {
            var start = (int)Math.Round(e * beat / 2);
            if (e % 2 == 0) {
                double phase = 0;
                for (var i = 0; i < Rate / 4 && start + i < frames; i++) {
                    var f = 50 + 90 * Math.Exp(-i / 1_500.0);
                    phase += 2 * Math.PI * f / Rate;
                    audio[start + i] += (float)(0.8 * Math.Sin(phase) * Math.Exp(-i / 6_000.0));
                }
            }

            double previous = 0;
            for (var i = 0; i < Rate / 20 && start + i < frames; i++) {
                var noise = random.NextDouble() * 2 - 1;
                var high = noise - previous; // a crude high-pass
                previous = noise;
                audio[start + i] += (float)(0.25 * high * Math.Exp(-i / 600.0));
            }
        }

        return audio;
    }

    /// <summary>
    ///     A melody of sine-like notes (fundamental plus two soft harmonics), each a few cents off: MIDI notes and
    ///     lengths in beats at 100 bpm, with 30 ms attacks and releases.
    /// </summary>
    public static float[] Melody() {
        var notes = new (double Note, double Beats)[] {
            (64.05, 1), (67.0, 0.5), (69.0, 0.5), (71.95, 1), (69.1, 1), (67.0, 0.5), (64.0, 0.5), (62.03, 2)
        };
        var beat = Rate * 60 / 100.0;
        var frames = (int)(notes.Sum(static n => n.Beats) * beat);
        var audio = new float[frames];
        var at = 0;
        foreach (var (note, beats) in notes) {
            var length = (int)(beats * beat);
            var f = Support.Analysis.NoteFrequency(note);
            var edge = Rate * 3 / 100;
            for (var i = 0; i < length && at + i < frames; i++) {
                var envelope = Math.Min(1, Math.Min(i / (double)edge, (length - i) / (double)edge));
                var x = 2 * Math.PI * f * i / Rate;
                audio[at + i] = (float)(0.4 * envelope * (Math.Sin(x) + 0.2 * Math.Sin(2 * x) + 0.1 * Math.Sin(3 * x)));
            }

            at += length;
        }

        return audio;
    }

    /// <summary>
    ///     The onset of the click expected near <paramref name="expected" />: the first frame within ±20 ms whose
    ///     magnitude reaches a tenth of the peak there.
    /// </summary>
    public static double Onset(ReadOnlySpan<float> audio, double expected) {
        var from = Math.Max(0, (int)expected - Rate / 50);
        var to = Math.Min(audio.Length, (int)expected + Rate / 50);
        float peak = 0;
        for (var i = from; i < to; i++) {
            peak = Math.Max(peak, Math.Abs(audio[i]));
        }

        for (var i = from; i < to; i++) {
            if (Math.Abs(audio[i]) >= peak / 10) {
                return i;
            }
        }

        return double.NaN;
    }

    /// <summary>The median and worst of absolute values.</summary>
    public static (double Median, double Worst) Stats(IEnumerable<double> values) {
        var sorted = values.Select(Math.Abs).Order().ToArray();
        return (sorted[sorted.Length / 2], sorted[^1]);
    }

    public static double Rms(ReadOnlySpan<float> audio) => Support.Analysis.Rms(audio);

    /// <summary>
    ///     The cepstrally smoothed log-magnitude envelope (dB) of <paramref name="audio" />'s long-term spectrum, per bin
    ///     of a 4,096-point FFT.
    /// </summary>
    public static double[] Envelope(ReadOnlySpan<float> audio) {
        const int size = 4_096;
        var power = new double[size];
        var frames = 0;
        for (var start = 0; start + size <= audio.Length; start += size / 2, frames++) {
            var spectrum = Support.Analysis.Spectrum(audio, start, size);
            for (var k = 0; k < size; k++) {
                power[k] += spectrum[k].Magnitude * spectrum[k].Magnitude;
            }
        }

        var cepstrum = new Complex[size];
        for (var k = 0; k < size; k++) {
            cepstrum[k] = Math.Log(power[k] / Math.Max(frames, 1) + 1e-20);
        }

        Fft.Forward(cepstrum);
        const int lifter = 40; // quefrencies under ~0.8 ms: the envelope, not the harmonics
        for (var q = lifter; q <= size - lifter; q++) {
            cepstrum[q] = 0;
        }

        for (var q = 0; q < size; q++) {
            cepstrum[q] = Complex.Conjugate(cepstrum[q]);
        }

        Fft.Forward(cepstrum);
        var envelope = new double[size / 2];
        for (var k = 0; k < envelope.Length; k++) {
            envelope[k] = 10 / Math.Log(10) * cepstrum[k].Real / size;
        }

        return envelope;
    }

    /// <summary>
    ///     The shift in semitones that best realigns <paramref name="shifted" />'s envelope with
    ///     <paramref name="original" />'s over 300–3,500 Hz (least squares after removing the level difference).
    /// </summary>
    public static double FormantShift(double[] original, double[] shifted) {
        var binHz = Rate / 4_096.0;

        double Sample(double[] envelope, double hz) {
            var x = hz / binHz;
            var k = (int)x;
            return envelope[k] + (envelope[k + 1] - envelope[k]) * (x - k);
        }

        var best = 0.0;
        var bestError = double.MaxValue;
        for (var semitones = -12.0; semitones <= 12; semitones += 0.1) {
            var factor = Math.Pow(2, semitones / 12);
            var diffs = new List<double>();
            for (var hz = 300.0; hz <= 3_500; hz *= 1.01) {
                diffs.Add(Sample(shifted, hz * factor) - Sample(original, hz));
            }

            var mean = diffs.Average();
            var error = diffs.Sum(d => (d - mean) * (d - mean));
            if (error < bestError) {
                bestError = error;
                best = semitones;
            }
        }

        return best;
    }
}
