using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>
///     A MIDI region: it starts at <c>startTick</c> and lasts <c>lengthTicks</c>; its notes and controller events are
///     positioned relative to its start (ticks at 960 PPQ). Events past the end do not play; a note that crosses the end
///     is cut there. <c>playback</c> holds the non-destructive region parameters.
/// </summary>
public sealed record MidiRegion(
    string Id,
    string TrackId,
    string Name,
    long StartTick,
    long LengthTicks,
    RegionPlayback Playback,
    IReadOnlyList<Note> Notes,
    IReadOnlyList<ControlEvent> Controls,
    bool Muted
);

/// <summary>
///     How a MIDI region plays, without changing its notes (Logic's Region inspector): transpose in semitones,
///     velocity offset (−127…127), quantise grid in ticks (0: off) with strength (0…1, 1 = all the way) and swing
///     (0…1, 0 = straight; delays every second grid step by up to half a step). <c>grooveId</c> (Phase 7): it
///     quantises to that groove template of the project instead of the grid (<c>quantizeTicks</c> and <c>swing</c>
///     then unused; null: the grid).
/// </summary>
public sealed record RegionPlayback(
    int Transpose,
    int Velocity,
    int QuantizeTicks,
    double QuantizeStrength,
    double Swing,
    string? GrooveId
);

/// <summary>
///     A note. <c>id</c> is unique in the project; <c>channel</c> is 1…16. <c>articulationId</c> (Phase 7) is its
///     articulation in its track's articulation set (1…254; 0: none): the articulation's outputs play just before the
///     note, at its tick, on its channel (a channel output changes the note's channel instead).
/// </summary>
public sealed record Note(
    long Id,
    int Pitch,
    int Velocity,
    int Channel,
    long StartTick,
    long LengthTicks,
    int ArticulationId
);

[JsonConverter(typeof(JsonStringEnumConverter<ControlKind>))]
public enum ControlKind {
    /// <summary><c>number</c> is the controller (0…127), <c>value</c> 0…127.</summary>
    [JsonStringEnumMemberName("controlChange")]
    ControlChange,

    /// <summary><c>value</c> is −8192…8191.</summary>
    [JsonStringEnumMemberName("pitchBend")]
    PitchBend,

    /// <summary><c>value</c> is 0…127.</summary>
    [JsonStringEnumMemberName("channelPressure")]
    ChannelPressure,

    /// <summary><c>value</c> is the program (0…127).</summary>
    [JsonStringEnumMemberName("programChange")]
    ProgramChange,

    /// <summary>Phase 7: polyphonic aftertouch; <c>number</c> is the key (0…127), <c>value</c> 0…127.</summary>
    [JsonStringEnumMemberName("polyPressure")]
    PolyPressure
}

/// <summary>
///     A controller event of a MIDI region; <c>tick</c> is relative to the region's start. <c>id</c> is unique in the
///     project. <c>number</c> is the controller of a control change and the key of a poly pressure event, 0 for the
///     other kinds. Together with the notes these are every event the Event List shows (Phase 7).
/// </summary>
public sealed record ControlEvent(long Id, ControlKind Kind, int Channel, int Number, int Value, long Tick);

/// <summary>Where a track takes live MIDI from: a device id (null: every device) and channel (0: all, else 1…16).</summary>
public sealed record MidiInputSettings(string? Device, int Channel);

/// <summary>Where an external MIDI track sends its regions: a device id and channel 1…16.</summary>
public sealed record MidiOutputSettings(string Device, int Channel);

/// <summary>A MIDI port of the machine the engine runs on.</summary>
public sealed record MidiPort(string Id, string Name);

public sealed record MidiDevices(IReadOnlyList<MidiPort> Inputs, IReadOnlyList<MidiPort> Outputs);

/// <summary>One raw MIDI message (status 0x80…0xEF, data bytes 0…127).</summary>
public sealed record LiveMidiEvent(int Status, int Data1, int Data2);

/// <summary>Plays MIDI on a track now, as if it came from a keyboard (the on-screen keyboard, musical typing).</summary>
public sealed record LiveMidiRequest(string TrackId, IReadOnlyList<LiveMidiEvent> Events);

/// <summary>
///     Which track hears live MIDI when no instrument track is record-armed (Logic sends input to the selected
///     track); null: none.
/// </summary>
public sealed record MidiThruRequest(string? TrackId);

/// <summary>A note to add; positions relative to the region's start; <c>articulationId</c> left out: 0 (none).</summary>
public sealed record NewNote(
    int Pitch,
    int Velocity,
    int Channel,
    long StartTick,
    long LengthTicks,
    int? ArticulationId = null
);

/// <summary>Changes to one note; left-out properties stay. <c>articulationId</c> 0 clears its articulation.</summary>
public sealed record NoteChange(
    long Id,
    int? Pitch = null,
    int? Velocity = null,
    int? Channel = null,
    long? StartTick = null,
    long? LengthTicks = null,
    int? ArticulationId = null
);

/// <summary>A controller event to add; <c>tick</c> relative to the region's start.</summary>
public sealed record NewControlEvent(ControlKind Kind, int Channel, int Number, int Value, long Tick);

/// <summary>
///     Changes to one controller event (Phase 7, the Event List; <c>setControls</c>); left-out properties stay. Its kind
///     stays: to change it, delete the event and add another. Values are checked against the kind's range.
/// </summary>
public sealed record ControlEventChange(
    long Id,
    long? Tick = null,
    int? Channel = null,
    int? Number = null,
    int? Value = null
);
