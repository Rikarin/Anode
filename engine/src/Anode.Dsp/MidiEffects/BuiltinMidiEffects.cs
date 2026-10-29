using Anode.Dsp.MidiEffects.Scripter;

namespace Anode.Dsp.MidiEffects;

/// <summary>
///     Anode's own MIDI effects (docs/07-phase-6.md, workstream E: Arpeggiator, Chord Trigger, Modifier, Modulator,
///     Note Repeater, Randomizer, Transposer, Velocity Processor; docs/instruments/T2.md): <c>anode.arpeggiator</c>,
///     <c>anode.chord-trigger</c>, <c>anode.modifier</c>, <c>anode.modulator</c>, <c>anode.note-repeater</c>,
///     <c>anode.randomizer</c>, <c>anode.transposer</c> and <c>anode.velocity-processor</c>; and Phase 7's Scripter,
///     <c>anode.scripter</c> (<see cref="ScripterEffect" />, docs/adr/0009-scripter.md), whose parameters its script
///     defines at run time. The engine lists <see cref="All" /> in its plugin catalogue, category <c>MIDI Effect</c>.
/// </summary>
public static class BuiltinMidiEffects {
    /// <summary>The MIDI effect the Smart Controls arpeggiator button puts in a track's first MIDI FX slot.</summary>
    public const string ArpeggiatorId = "anode.arpeggiator";

    public static IReadOnlyList<MidiEffectDescriptor> All { get; } = [
        Arpeggiator.Info, ChordTrigger.Info, Modifier.Info, Modulator.Info, NoteRepeater.Info, Randomizer.Info,
        Transposer.Info, VelocityProcessor.Info, ScripterEffect.Info
    ];

    /// <summary>A new, unprepared instance of MIDI effect <paramref name="id" />, or null when there is none.</summary>
    public static IMidiEffect? Create(string id) =>
        id switch {
            ArpeggiatorId => new Arpeggiator(),
            ChordTrigger.Id => new ChordTrigger(),
            Modifier.Id => new Modifier(),
            Modulator.Id => new Modulator(),
            NoteRepeater.Id => new NoteRepeater(),
            Randomizer.Id => new Randomizer(),
            Transposer.Id => new Transposer(),
            VelocityProcessor.Id => new VelocityProcessor(),
            ScripterEffect.Id => new ScripterEffect(),
            _ => null
        };
}
