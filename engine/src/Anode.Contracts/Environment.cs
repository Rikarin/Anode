using System.Text.Json.Serialization;

namespace Anode.Contracts;

// The Environment (docs/12-phase-11.md, docs/adr/0015-environment.md): a MIDI object graph on the input path, stored in
// the project (Project.environment) and run by the host. Events from MIDI devices, live MIDI and control surfaces enter
// at the physical input object, at their sample, and leave through sequencer input (the record-enabled or selected
// track, as before), track inputs, MIDI ports or control targets. An empty Environment (every project before schema
// 11) is the default path exactly: physical input to sequencer input; the editor makes that pair when its window first
// opens. The server validates the graph (known kinds, inlets and outlets that exist, no cycles) and compiles it to the
// host as a flat, ordered list.

/// <summary>
///     The Environment's object kinds (ADR-0015's fixed set). Their inlets and outlets:
///     <see cref="EnvironmentObjectKinds" />.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EnvironmentObjectKind>))]
public enum EnvironmentObjectKind {
    /// <summary>
    ///     Every MIDI input (devices, live MIDI, control surfaces), or one device (<c>port</c>). At most one per
    ///     project without a <c>port</c>.
    /// </summary>
    [JsonStringEnumMemberName("physicalInput")]
    PhysicalInput,

    /// <summary>The sequencer's input: the record-enabled or selected track, as the default path. At most one.</summary>
    [JsonStringEnumMemberName("sequencerInput")]
    SequencerInput,

    /// <summary>An on-screen keyboard: what the editor plays into it (<c>/project/environment/objects/{id}/input</c>).</summary>
    [JsonStringEnumMemberName("keyboard")]
    Keyboard,

    /// <summary>Passes everything through and shows the last events (telemetry, the <c>environmentMonitor</c> event).</summary>
    [JsonStringEnumMemberName("monitor")]
    Monitor,

    /// <summary>
    ///     A MIDI value: an event of its kind (<c>message</c>, <c>channel</c>, <c>number</c>) moves it; it sends its value
    ///     as that event, scaled to <c>min</c>…<c>max</c>, when moved (by an event or on screen). Reported through
    ///     telemetry (the <c>environmentFader</c> event).
    /// </summary>
    [JsonStringEnumMemberName("fader")]
    Fader,

    /// <summary>
    ///     Logic's transformer: events matching every one of <c>conditions</c> are changed by <c>operations</c> and leave
    ///     by outlet 0; the others leave unchanged by outlet 1. Phase 7's MIDI Transform conditions and operations on
    ///     <c>status</c> (conditions only), <c>channel</c>, <c>pitch</c> (data 1) and <c>velocity</c> (data 2); not on
    ///     <c>position</c>, <c>length</c> or <c>subposition</c>, nor the time-based operations (<c>crescendo</c>,
    ///     <c>relativeCrescendo</c>).
    /// </summary>
    [JsonStringEnumMemberName("transformer")]
    Transformer,

    /// <summary>
    ///     Repeats each event at its <c>taps</c> (delay in ticks, transpose, velocity offset; at most 16); the original
    ///     passes when <c>parameters.dry</c> is 1.
    /// </summary>
    [JsonStringEnumMemberName("delayLine")]
    DelayLine,

    /// <summary>Phase 6's Arpeggiator engine; its parameters are the MIDI FX's (<c>anode.arpeggiator</c>'s ids).</summary>
    [JsonStringEnumMemberName("arpeggiator")]
    Arpeggiator,

    /// <summary>
    ///     Phase 6's Chord Trigger engine; its parameters are the MIDI FX's (<c>anode.chord-trigger</c>'s ids), and
    ///     <c>chord</c>, when set, is the chord every key plays (semitones from the key; the MIDI FX's learned chord).
    /// </summary>
    [JsonStringEnumMemberName("chordMemorizer")]
    ChordMemorizer,

    /// <summary>Sends channel messages of channel n (1…16) out of outlet n − 1; everything else out of outlet 16.</summary>
    [JsonStringEnumMemberName("channelSplitter")]
    ChannelSplitter,

    /// <summary>Sends to the external MIDI output <c>port</c>.</summary>
    [JsonStringEnumMemberName("midiPort")]
    MidiPort,

    /// <summary>Plays on track <c>trackId</c> (an instrument or external MIDI track), as live MIDI on it.</summary>
    [JsonStringEnumMemberName("trackInput")]
    TrackInput,

    /// <summary>
    ///     Drives a strip parameter: <c>address</c> on track <c>trackId</c> (null: the master), an automation address
    ///     (<c>volume</c>, <c>pan</c>, <c>insert:{id}:{param}</c>, …), from a CC (or a fader's) value 0…127 over the
    ///     parameter's range.
    /// </summary>
    [JsonStringEnumMemberName("controlTarget")]
    ControlTarget
}

/// <summary>A layer of the Environment: one canvas of the window (Logic's layers). Objects belong to one layer.</summary>
public sealed record EnvironmentLayer(string Id, string Name);

/// <summary>A delay line's tap: <c>delayTicks</c> after the event (1…3840), transposed, its velocity offset (−127…127).</summary>
public sealed record DelayTap(long DelayTicks, int Transpose, int Velocity);

/// <summary>
///     An Environment object: its kind, name, layer and position on the layer's canvas (pixels at zoom 1). Its numeric
///     settings are <c>parameters</c> (ids by kind, <see cref="EnvironmentObjectKinds" />; missing ones at their
///     defaults). <c>port</c> (physical input: a device; MIDI port: an output), <c>trackId</c> (track input; control
///     target, null for the master), <c>address</c> (control target), <c>conditions</c> and <c>operations</c>
///     (transformer), <c>taps</c> (delay line) and <c>chord</c> (chord memoriser) are used by the kinds that say so and
///     null on the others.
/// </summary>
public sealed record EnvironmentObject(
    string Id,
    string LayerId,
    EnvironmentObjectKind Kind,
    string Name,
    double X,
    double Y,
    IReadOnlyDictionary<string, double> Parameters,
    string? Port = null,
    string? TrackId = null,
    string? Address = null,
    IReadOnlyList<TransformCondition>? Conditions = null,
    IReadOnlyList<TransformOperation>? Operations = null,
    IReadOnlyList<DelayTap>? Taps = null,
    IReadOnlyList<int>? Chord = null
);

/// <summary>
///     A cable from outlet <c>outlet</c> of an object to inlet <c>inlet</c> of another (0-based, as the kind lists
///     them). An outlet may feed several cables (each gets every event) and an inlet take several; a cable may cross
///     layers. The graph has no cycles.
/// </summary>
public sealed record EnvironmentCable(string Id, string FromObjectId, int Outlet, string ToObjectId, int Inlet);

/// <summary>
///     The project's Environment (<c>Project.environment</c>): its layers, objects and cables. <see cref="Empty" />
///     (no objects) is the default input path, physical input to sequencer input.
/// </summary>
public sealed record MidiEnvironment(
    IReadOnlyList<EnvironmentLayer> Layers,
    IReadOnlyList<EnvironmentObject> Objects,
    IReadOnlyList<EnvironmentCable> Cables
) {
    public static MidiEnvironment Empty { get; } = new([], [], []);
}

/// <summary>A numeric parameter of an object kind: its id, name, range and default.</summary>
public sealed record EnvironmentParameter(string Id, string Name, double Min, double Max, double Default);

/// <summary>
///     An object kind: its name, its inlets and outlets by name (in index order) and its numeric parameters (the
///     arpeggiator's and chord memoriser's are their MIDI FX's, listed by the plugin catalogue rather than here).
/// </summary>
public sealed record EnvironmentObjectKindInfo(
    EnvironmentObjectKind Kind,
    string Name,
    IReadOnlyList<string> Inlets,
    IReadOnlyList<string> Outlets,
    IReadOnlyList<EnvironmentParameter> Parameters
);

/// <summary>
///     The object kinds' inlets, outlets and parameters (the editor mirrors them in models.ts; the server validates
///     cables against them). Fader <c>message</c>: 0 control change, 1 note, 2 program change, 3 pitch bend;
///     <c>channel</c> 1…16; <c>number</c> the controller or note.
/// </summary>
public static class EnvironmentObjectKinds {
    const string In = "In";
    const string Out = "Out";

    static readonly EnvironmentParameter Channel = new("channel", "Channel", 1, 16, 1);

    public static IReadOnlyList<EnvironmentObjectKindInfo> All { get; } = [
        new(EnvironmentObjectKind.PhysicalInput, "Physical Input", [], [Out], []),
        new(EnvironmentObjectKind.SequencerInput, "Sequencer Input", [In], [], []),
        new(EnvironmentObjectKind.Keyboard, "Keyboard", [], [Out], [Channel, new("velocity", "Velocity", 1, 127, 100)]),
        new(EnvironmentObjectKind.Monitor, "Monitor", [In], [Out], []),
        new(
            EnvironmentObjectKind.Fader,
            "Fader",
            [In],
            [Out],
            [
                new("message", "Message", 0, 3, 0), Channel, new("number", "Number", 0, 127, 7),
                new("min", "Minimum", 0, 127, 0), new("max", "Maximum", 0, 127, 127), new("value", "Value", 0, 127, 0)
            ]
        ),
        new(EnvironmentObjectKind.Transformer, "Transformer", [In], ["Matched", "Unmatched"], []),
        new(EnvironmentObjectKind.DelayLine, "Delay Line", [In], [Out], [new("dry", "Original", 0, 1, 1)]),
        new(EnvironmentObjectKind.Arpeggiator, "Arpeggiator", [In], [Out], []),
        new(EnvironmentObjectKind.ChordMemorizer, "Chord Memorizer", [In], [Out], []),
        new(
            EnvironmentObjectKind.ChannelSplitter,
            "Channel Splitter",
            [In],
            [..Enumerable.Range(1, 16).Select(static c => $"Channel {c}"), "Other"],
            []
        ),
        new(EnvironmentObjectKind.MidiPort, "MIDI Port", [In], [], []),
        new(EnvironmentObjectKind.TrackInput, "Track Input", [In], [], []),
        new(EnvironmentObjectKind.ControlTarget, "Control Target", [In], [], [])
    ];

    public static EnvironmentObjectKindInfo Of(EnvironmentObjectKind kind) => All[(int)kind];
}

/// <summary>
///     Live MIDI into an on-screen object (<c>POST /project/environment/objects/{id}/input</c>): a keyboard plays it, a
///     fader takes it as a move (the editor sends a control change of the fader's value).
/// </summary>
public sealed record EnvironmentInputRequest(IReadOnlyList<LiveMidiEvent> Events);

/// <summary>An event a monitor saw: its MIDI bytes, and its project sample (−1 while stopped).</summary>
public sealed record MonitoredEvent(int Status, int Data1, int Data2, long Sample);
