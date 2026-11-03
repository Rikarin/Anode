using Anode.Dsp.Instruments.Alchemy;
using Anode.Dsp.Instruments.Drums;
using Anode.Dsp.Instruments.Fm;
using Anode.Dsp.Instruments.Organ;
using Anode.Dsp.Instruments.Physical;
using Anode.Dsp.Instruments.Piano;
using Anode.Dsp.Instruments.Quick;
using Anode.Dsp.Instruments.Sampler;
using Anode.Dsp.Instruments.Synth;
using Anode.Dsp.Instruments.Tape;
using Anode.Dsp.Instruments.Wavetable;

namespace Anode.Dsp.Instruments;

/// <summary>
///     Anode's own instruments. T1 (docs/03-phase-2.md, workstream D): <c>anode.synth</c> (subtractive) and
///     <c>anode.sampler</c> (SFZ and single samples). T2 (docs/07-phase-6.md, workstream E; docs/instruments/T2.md):
///     <c>anode.drum-synth</c>, <c>anode.fm</c>, <c>anode.organ</c>, <c>anode.electric-piano</c>,
///     <c>anode.wavetable</c> and <c>anode.quick-sampler</c>. T3 (docs/12-phase-11.md, workstream E):
///     <c>anode.physical-model</c>, <c>anode.tape-replay</c> and <c>anode.sample-alchemy</c>, while
///     <see cref="T3.Ready" />. The engine lists <see cref="All" /> in its plugin catalogue.
/// </summary>
public static class BuiltinInstruments {
    /// <summary>The instrument a new instrument track gets.</summary>
    public const string DefaultId = AnodeSynth.Id;

    /// <summary>The T3 instruments (registered while <see cref="T3.Ready" />).</summary>
    public static IReadOnlyList<InstrumentDescriptor> Tier3 { get; } = [
        PhysicalModel.Info, TapeReplay.Info, SampleAlchemy.Info
    ];

    public static IReadOnlyList<InstrumentDescriptor> All { get; } = [
        AnodeSynth.Info, AnodeSampler.Info, DrumSynth.Info, FmSynth.Info, DrawbarOrgan.Info, ElectricPiano.Info,
        WavetableSynth.Info, QuickSampler.Info, ..T3.Ready ? Tier3 : []
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
            PhysicalModel.Id when T3.Ready => new PhysicalModel(),
            TapeReplay.Id when T3.Ready => new TapeReplay(),
            SampleAlchemy.Id when T3.Ready => new SampleAlchemy(),
            _ => null
        };
}
