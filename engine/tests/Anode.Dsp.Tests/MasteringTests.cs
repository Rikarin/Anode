using Anode.Dsp.Mastering;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>
///     The Mastering Assistant (docs/12-phase-11.md, exit criterion step 5): the analysis measures what it says, and on
///     varied material (quiet, loud, bass-heavy, bright) every character's master lands on −14 LUFS within 0.5 LU with
///     its true peak at most −1 dBTP; Valve's even harmonics exceed Clean's by 6 dB; the level-matched bypass is within
///     1 LU of the processed loudness.
/// </summary>
public sealed class MasteringTests(ITestOutputHelper output) {
    const float Rate = Masters.Rate;

    static float[] Sine(double hz, double amplitude, int frames) =>
        [..Enumerable.Range(0, frames).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate)))];

    static MasteringAssistant Assistant(
        float[] left,
        float[] right,
        MasteringCharacterKind character,
        params (int Index, float Value)[] parameters
    ) {
        var assistant = (MasteringAssistant)T3Kit.Effect(
            MasteringAssistant.Id,
            [(MasteringAssistant.Character, (int)character), ..parameters]
        );
        assistant.SetChain(MasteringChainDesigner.Design(MasteringAnalyzer.Analyse(left, right, Rate), character));
        return assistant;
    }

    [Fact]
    public void The_analysis_measures_loudness_peak_width_and_spectrum() {
        // A 1 kHz sine at −20 dBFS in both channels: −20.69 LUFS (K-weighting is about +0.7 dB at 1 kHz: −20.0).
        var sine = Sine(1_000, 0.1, 10 * Masters.Rate);
        var measured = MasteringAnalyzer.Analyse(sine, sine, Rate);
        Assert.InRange(measured.IntegratedLufs, -20.2, -19.8);
        Assert.InRange(measured.MaxShortTermLufs, -20.2, -19.8);
        Assert.InRange(measured.LoudnessRange, 0, 0.1);
        Assert.InRange(measured.TruePeakDbtp, -20.05, -19.95);
        Assert.InRange(measured.CrestFactorDb, 2.95, 3.06); // a sine's peak over its RMS
        Assert.Equal(0, measured.Width, 6);
        Assert.Equal(MasteringBands.Count, measured.SpectrumDb.Length);
        Assert.Equal(0, measured.SpectrumDb.Average(), 4);
        Assert.Equal(16, Array.IndexOf(measured.SpectrumDb, measured.SpectrumDb.Max())); // the 1 kHz band

        // Opposite channels are all side.
        var inverted = sine.Select(static s => -s).ToArray();
        Assert.Equal(1, MasteringAnalyzer.Analyse(sine, inverted, Rate).Width, 6);

        // A quarter-rate sine at 45° peaks 3 dB over its samples, as a true-peak meter reads it.
        var over = Enumerable.Range(0, Masters.Rate)
            .Select(static i => (float)(0.5 * Math.Sin(Math.PI / 2 * i + Math.PI / 4)))
            .ToArray();
        Assert.InRange(MasteringAnalyzer.Analyse(over, over, Rate).TruePeakDbtp, -6.1, -5.9);

        // Silence measures as silence.
        var silence = new float[Masters.Rate];
        var nothing = MasteringAnalyzer.Analyse(silence, silence, Rate);
        Assert.True(nothing.IntegratedLufs <= -70);
        Assert.All(nothing.SpectrumDb, static s => Assert.Equal(0, s));
        Assert.All(
            MasteringChainDesigner.Design(nothing, MasteringCharacterKind.Clean).EqGainsDb,
            static g => Assert.Equal(0, g)
        );
    }

    [Fact]
    public void The_curves_and_the_design_follow_the_character() {
        foreach (var character in Enum.GetValues<MasteringCharacterKind>()) {
            var curve = MasteringAnalyzer.Curve(character);
            Assert.Equal(MasteringBands.Count, curve.Length);
            Assert.Equal(0, curve.Average(), 4);
        }

        var clean = MasteringAnalyzer.Curve(MasteringCharacterKind.Clean);
        var punch = MasteringAnalyzer.Curve(MasteringCharacterKind.Punch);
        var valve = MasteringAnalyzer.Curve(MasteringCharacterKind.Valve);
        Assert.True(punch[5] - punch[20] > clean[5] - clean[20]); // fuller at 80 Hz against 2.5 kHz
        Assert.True(valve[9] - valve[27] > clean[9] - clean[27]); // warmer at 200 Hz against 12.5 kHz
        Assert.Equal(clean, MasteringAnalyzer.Curve(MasteringCharacterKind.Transparent));

        var (left, right) = Masters.Make(Masters.Kind.Bright);
        var measured = MasteringAnalyzer.Analyse(left, right, Rate);
        var full = MasteringChainDesigner.Design(measured, MasteringCharacterKind.Clean);
        var half = MasteringChainDesigner.Design(measured, MasteringCharacterKind.Transparent);
        Assert.All(
            full.EqGainsDb,
            static g => Assert.InRange(g, -MasteringChainDesigner.MaxEqDb, MasteringChainDesigner.MaxEqDb)
        );
        Assert.True(full.EqGainsDb[^3] < -1, "a bright mix gets its top taken down");
        for (var b = 0; b < MasteringBands.Count; b++) {
            Assert.Equal(full.EqGainsDb[b] / 2, half.EqGainsDb[b], 3);
        }

        Assert.True(half.CompressorRatio < full.CompressorRatio);
        Assert.Equal((float)measured.IntegratedLufs, full.LoudnessLufs);
    }

    public static TheoryData<Masters.Kind, MasteringCharacterKind> Material {
        get {
            var data = new TheoryData<Masters.Kind, MasteringCharacterKind>();
            foreach (var kind in Enum.GetValues<Masters.Kind>()) {
                foreach (var character in Enum.GetValues<MasteringCharacterKind>()) {
                    data.Add(kind, character);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Material))]
    public void The_master_reaches_its_loudness_under_its_ceiling(Masters.Kind kind, MasteringCharacterKind character) {
        var (left, right) = Masters.Make(kind);
        using var assistant = Assistant(left, right, character);
        var (l, r) = Fx.Run(assistant, left, right);
        var loudness = Masters.Integrated(l, r);
        var truePeak = ToDb(Math.Max(Fx.TruePeak(l), Fx.TruePeak(r)));
        output.WriteLine(
            $"{kind} ({Masters.Integrated(left, right):0.0} LUFS) {character}: {loudness:0.00} LUFS, {truePeak:0.00} dBTP, "
            + $"gain {assistant.TotalGainDb:0.00} dB; the assistant metered {assistant.IntegratedLufs:0.00} LUFS, "
            + $"{assistant.TruePeakDbtp:0.00} dBTP"
        );
        Assert.InRange(loudness, -14.5, -13.5);
        Assert.True(truePeak <= -1, $"true peak {truePeak:0.00} dBTP");
        Assert.InRange(assistant.IntegratedLufs, loudness - 0.1, loudness + 0.1);
    }

    [Fact]
    public void Other_targets_and_ceilings_are_reached_too() {
        var (left, right) = Masters.Make(Masters.Kind.Loud);
        foreach (var (target, ceiling) in new[] { (-9f, -1f), (-18f, -2f), (-23f, -1f) }) {
            using var assistant = Assistant(
                left,
                right,
                MasteringCharacterKind.Clean,
                (MasteringAssistant.TargetLufs, target),
                (MasteringAssistant.CeilingDbtp, ceiling)
            );
            var (l, r) = Fx.Run(assistant, left, right);
            var loudness = Masters.Integrated(l, r);
            var truePeak = ToDb(Math.Max(Fx.TruePeak(l), Fx.TruePeak(r)));
            output.WriteLine($"target {target}, ceiling {ceiling}: {loudness:0.00} LUFS, {truePeak:0.00} dBTP");
            Assert.InRange(loudness, target - 0.5, target + 0.5);
            Assert.True(truePeak <= ceiling);
        }
    }

    [Fact]
    public void Valve_adds_even_harmonics_that_clean_does_not() {
        var tone = Sine(1_000, 0.1, 4 * Masters.Rate);

        double Even(MasteringCharacterKind character) {
            using var assistant = Assistant(tone, tone, character);
            var (l, _) = Fx.Run(assistant, tone, tone);
            const int size = 32_768;
            var spectrum = Spectrum(l, l.Length - size, size);
            return 10 * Math.Log10((Band(2) + Band(4) + Band(6)) / Band(1));

            double Band(int k) {
                var centre = (int)Math.Round(1_000.0 * k * size / Rate);
                return Enumerable.Range(centre - 3, 7).Sum(b => spectrum[b].Magnitude * spectrum[b].Magnitude);
            }
        }

        var clean = Even(MasteringCharacterKind.Clean);
        var valve = Even(MasteringCharacterKind.Valve);
        output.WriteLine($"even harmonics: Clean {clean:0.0} dB, Valve {valve:0.0} dB");
        Assert.True(valve >= clean + 6);
    }

    [Fact]
    public void The_matched_bypass_plays_at_the_processed_loudness() {
        foreach (var kind in new[] { Masters.Kind.Quiet, Masters.Kind.Loud }) {
            var (left, right) = Masters.Make(kind);
            using var processed = Assistant(left, right, MasteringCharacterKind.Valve);
            using var bypassed = Assistant(
                left,
                right,
                MasteringCharacterKind.Valve,
                (MasteringAssistant.MatchedBypass, 1)
            );
            var wet = Fx.Run(processed, left, right);
            var dry = Fx.Run(bypassed, left, right);
            var difference = Masters.Integrated(dry.Left, dry.Right) - Masters.Integrated(wet.Left, wet.Right);
            output.WriteLine($"{kind}: the bypass is {difference:0.00} LU from the master");
            Assert.InRange(difference, -1, 1);

            // The bypass is the input at the target over its analysed loudness, delayed by the latency.
            var latency = bypassed.LatencySamples;
            var gain = Math.Pow(10, (-14 - MasteringAnalyzer.Analyse(left, right, Rate).IntegratedLufs) / 20);
            for (var i = 10_000; i < 10_100; i++) {
                Assert.Equal(left[i] * gain, dry.Left[i + latency], 4);
            }
        }
    }

    [Fact]
    public void Strengths_width_and_a_missing_chain_do_what_they_say() {
        var (left, right) = Masters.Make(Masters.Kind.BassHeavy);

        // Without a chain: the limiter alone, at unit gain.
        using var unanalysed = (MasteringAssistant)T3Kit.Effect(MasteringAssistant.Id);
        var (l, _) = Fx.Run(unanalysed, left, right);
        Assert.InRange(Masters.Integrated(l, l) - Masters.Integrated(left, left), -0.1, 0.1);

        // No EQ and no dynamics: the chain is then gain, the band limit and the limiter; the target still holds.
        using var plain = Assistant(
            left,
            right,
            MasteringCharacterKind.Clean,
            (MasteringAssistant.Eq, 0),
            (MasteringAssistant.Dynamics, 0)
        );
        var flat = Fx.Run(plain, left, right);
        Assert.InRange(Masters.Integrated(flat.Left, flat.Right), -14.5, -13.5);
        Assert.Equal(0, plain.CompressorReductionDb, 6);

        // Width 0 makes mono, 2 widens.
        var (wide, wideRight) = Masters.Make(Masters.Kind.Bright);
        using var mono = Assistant(wide, wideRight, MasteringCharacterKind.Clean, (MasteringAssistant.Width, 0));
        var folded = Fx.Run(mono, wide, wideRight);
        Assert.True(MasteringAnalyzer.Analyse(folded.Left, folded.Right, Rate).Width < 1e-4);
        using var wider = Assistant(wide, wideRight, MasteringCharacterKind.Clean, (MasteringAssistant.Width, 2));
        var spread = Fx.Run(wider, wide, wideRight);
        Assert.True(
            MasteringAnalyzer.Analyse(spread.Left, spread.Right, Rate).Width
            > 2 * MasteringAnalyzer.Analyse(wide, wideRight, Rate).Width
        );

        Assert.Throws<ArgumentException>(() => unanalysed.SetChain(new([1, 2], -10, 2, -14, 0)));
    }
}
