using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Drive;
using Anode.Dsp.Instruments;
using Anode.Dsp.Instruments.Alchemy;
using Anode.Dsp.Instruments.Physical;
using Anode.Dsp.Instruments.Tape;
using Anode.Dsp.Mastering;

namespace Anode.Dsp.Tests.Support;

/// <summary>
///     The T3 instruments and effects (docs/12-phase-11.md, workstream E), created as <see cref="BuiltinInstruments" />
///     and <see cref="BuiltinEffects" /> create the registered ones: the engine registers them at integration, and these
///     tests hold them to the same contract until then.
/// </summary>
public static class T3Kit {
    public static IReadOnlyList<InstrumentDescriptor> Instruments { get; } =
        [PhysicalModel.Info, TapeReplay.Info, SampleAlchemy.Info];

    public static IReadOnlyList<EffectDescriptor> Effects { get; } = [Saturation.Info, MasteringAssistant.Info];

    /// <summary>A new, unprepared T3 instrument (or a registered one), or null.</summary>
    public static IInstrument? CreateInstrument(string id) =>
        BuiltinInstruments.Create(id)
        ?? id switch {
            PhysicalModel.Id => new PhysicalModel(),
            TapeReplay.Id => new TapeReplay(),
            SampleAlchemy.Id => new SampleAlchemy(),
            _ => null
        };

    /// <summary>A new, unprepared T3 effect (or a registered one), or null.</summary>
    public static IAudioEffect? CreateEffect(string id) =>
        BuiltinEffects.Create(id)
        ?? id switch {
            Saturation.Id => new Saturation(),
            MasteringAssistant.Id => new MasteringAssistant(),
            _ => null
        };

    /// <summary>A prepared T3 instrument with <paramref name="parameters" /> set.</summary>
    public static IInstrument Instrument(string id, params (int Index, float Value)[] parameters) {
        var instrument = CreateInstrument(id) ?? throw new InvalidOperationException(id);
        instrument.Prepare(Play.Rate, 1_024);
        foreach (var (index, value) in parameters) {
            instrument.SetParameter(index, value);
        }

        return instrument;
    }

    /// <summary>A prepared T3 effect with <paramref name="parameters" /> set.</summary>
    public static IAudioEffect Effect(string id, params (int Index, float Value)[] parameters) {
        var effect = CreateEffect(id) ?? throw new InvalidOperationException(id);
        effect.Prepare(Fx.Rate, 1_024);
        foreach (var (index, value) in parameters) {
            effect.SetParameter(index, value);
        }

        return effect;
    }

    /// <summary>
    ///     The frequency of the partial nearest <paramref name="expected" /> (within a semitone), refined by the phase
    ///     advance between two Hann-windowed frames a quarter frame apart, as <see cref="Analysis.PeakFrequency" />.
    /// </summary>
    public static double PartialFrequency(
        ReadOnlySpan<float> signal,
        double sampleRate,
        double expected,
        int start = 0,
        int length = 32_768
    ) {
        var hop = length / 4;
        var a = Analysis.Spectrum(signal, start, length);
        var b = Analysis.Spectrum(signal, start + hop, length);
        var low = (int)Math.Floor(expected / 1.06 * length / sampleRate);
        var high = (int)Math.Ceiling(expected * 1.06 * length / sampleRate);
        var peak = low;
        for (var k = low; k <= high; k++) {
            if (a[k].Magnitude > a[peak].Magnitude) {
                peak = k;
            }
        }

        var expectedPhase = 2 * Math.PI * peak * hop / length;
        var deviation = b[peak].Phase - a[peak].Phase - expectedPhase;
        deviation -= 2 * Math.PI * Math.Round(deviation / (2 * Math.PI));
        return (peak + deviation * length / (2 * Math.PI * hop)) * sampleRate / length;
    }

    /// <summary>
    ///     The frequency of the strongest component within a semitone of <paramref name="expected" /> in the power
    ///     spectrum averaged over Hann frames of 16,384 samples a quarter apart (Welch), refined by a parabola through the
    ///     log powers: for sounds whose phase does not run on (grains), where the phase advance says nothing.
    /// </summary>
    public static double AveragedPeakFrequency(
        ReadOnlySpan<float> signal,
        double sampleRate,
        double expected,
        int start,
        int length
    ) {
        const int size = 16_384;
        var power = new double[size / 2];
        for (var at = start; at + size <= start + length; at += size / 4) {
            var spectrum = Analysis.Spectrum(signal, at, size);
            for (var k = 0; k < power.Length; k++) {
                power[k] += spectrum[k].Magnitude * spectrum[k].Magnitude;
            }
        }

        var low = (int)Math.Floor(expected / 1.06 * size / sampleRate);
        var high = (int)Math.Ceiling(expected * 1.06 * size / sampleRate);
        var peak = low;
        for (var k = low; k <= high; k++) {
            if (power[k] > power[peak]) {
                peak = k;
            }
        }

        var a = Math.Log(power[peak - 1]);
        var b = Math.Log(power[peak]);
        var c = Math.Log(power[peak + 1]);
        var offset = 0.5 * (a - c) / (a - 2 * b + c);
        return (peak + offset) * sampleRate / size;
    }

    /// <summary>
    ///     The time (s) from the loudest 10 ms window of <paramref name="signal" /> until its RMS stays 60 dB below that
    ///     (extrapolated linearly in dB from the fall to −40 dB when the signal ends sooner).
    /// </summary>
    public static double DecaySeconds(ReadOnlySpan<float> signal, double sampleRate) {
        var window = (int)(0.01 * sampleRate);
        var levels = new double[signal.Length / window];
        for (var w = 0; w < levels.Length; w++) {
            levels[w] = Analysis.ToDb(Analysis.Rms(signal.Slice(w * window, window)));
        }

        var loudest = 0;
        for (var w = 1; w < levels.Length; w++) {
            if (levels[w] > levels[loudest]) {
                loudest = w;
            }
        }

        int Below(double db) {
            for (var w = levels.Length - 1; w > loudest; w--) {
                if (levels[w] > levels[loudest] - db) {
                    return w + 1;
                }
            }

            return loudest + 1;
        }

        var sixty = Below(60);
        if (sixty < levels.Length) {
            return (sixty - loudest) * window / sampleRate;
        }

        var forty = Below(40);
        return (forty - loudest) * 1.5 * window / sampleRate;
    }
}
