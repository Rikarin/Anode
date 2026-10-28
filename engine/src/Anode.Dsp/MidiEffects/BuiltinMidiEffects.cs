namespace Anode.Dsp.MidiEffects;

/// <summary>
///     Anode's own MIDI effects (docs/07-phase-6.md, workstream E: Arpeggiator, Chord Trigger, Modifier, Modulator,
///     Note Repeater, Randomizer, Transposer, Velocity Processor). The engine lists <see cref="All" /> in its plugin
///     catalogue, category <c>MIDI Effect</c>.
/// </summary>
public static class BuiltinMidiEffects {
    /// <summary>The MIDI effect the Smart Controls arpeggiator button puts in a track's first MIDI FX slot.</summary>
    public const string ArpeggiatorId = "anode.arpeggiator";

    public static IReadOnlyList<MidiEffectDescriptor> All { get; } = [];

    /// <summary>A new, unprepared instance of MIDI effect <paramref name="id" />, or null when there is none.</summary>
    public static IMidiEffect? Create(string id) =>
        id switch {
            _ => null
        };
}
