using Anode.Dsp.Instruments.Drums;
using Anode.Dsp.Instruments.Fm;
using Anode.Dsp.Instruments.Organ;
using Anode.Dsp.Instruments.Piano;
using Anode.Dsp.Instruments.Quick;
using Anode.Dsp.Instruments.Sampler;
using Anode.Dsp.Instruments.Synth;
using Anode.Dsp.Instruments.Wavetable;

namespace Anode.Dsp.Instruments;

/// <summary>
///     Anode's own instruments. T1 (docs/03-phase-2.md, workstream D): <c>anode.synth</c> (subtractive) and
///     <c>anode.sampler</c> (SFZ and single samples). T2 (docs/07-phase-6.md, workstream E; docs/instruments/T2.md):
///     <c>anode.drum-synth</c>, <c>anode.fm</c>, <c>anode.organ</c>, <c>anode.electric-piano</c>,
///     <c>anode.wavetable</c> and <c>anode.quick-sampler</c>. The engine lists <see cref="All" /> in its plugin
///     catalogue.
/// </summary>
public static class BuiltinInstruments {
    /// <summary>The instrument a new instrument track gets.</summary>
    public const string DefaultId = AnodeSynth.Id;

    public static IReadOnlyList<InstrumentDescriptor> All { get; } = [
        AnodeSynth.Info, AnodeSampler.Info, DrumSynth.Info, FmSynth.Info, DrawbarOrgan.Info, ElectricPiano.Info,
        WavetableSynth.Info, QuickSampler.Info
    ];

    /// <summary>A new, unprepared instance of instrument <paramref name="id" />, or null when there is none.</summary>
    public static IInstrument? Create(string id) =>
        id switch {
            AnodeSynth.Id => new AnodeSynth(),
            AnodeSampler.Id => new AnodeSampler(),
            DrumSynth.Id => new DrumSynth(),
            FmSynth.Id => new FmSynth(),
            DrawbarOrgan.Id => new DrawbarOrgan(),
            ElectricPiano.Id => new ElectricPiano(),
            WavetableSynth.Id => new WavetableSynth(),
            QuickSampler.Id => new QuickSampler(),
            _ => null
        };
}
