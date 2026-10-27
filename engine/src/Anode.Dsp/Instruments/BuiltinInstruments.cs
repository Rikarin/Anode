using Anode.Dsp.Instruments.Sampler;
using Anode.Dsp.Instruments.Synth;

namespace Anode.Dsp.Instruments;

/// <summary>
///     Anode's own instruments (docs/03-phase-2.md, workstream D): <c>anode.synth</c> (subtractive) and
///     <c>anode.sampler</c> (SFZ and single samples). The engine lists <see cref="All" /> in its plugin catalogue.
/// </summary>
public static class BuiltinInstruments {
    /// <summary>The instrument a new instrument track gets.</summary>
    public const string DefaultId = AnodeSynth.Id;

    public static IReadOnlyList<InstrumentDescriptor> All { get; } = [AnodeSynth.Info, AnodeSampler.Info];

    /// <summary>A new, unprepared instance of instrument <paramref name="id" />, or null when there is none.</summary>
    public static IInstrument? Create(string id) =>
        id switch {
            AnodeSynth.Id => new AnodeSynth(),
            AnodeSampler.Id => new AnodeSampler(),
            _ => null
        };
}
