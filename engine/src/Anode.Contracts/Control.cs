using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>
///     Anode's virtual MIDI ports (docs/09-phase-8.md): published through CoreMIDI on macOS (absent on Windows) and
///     listed by <c>GET /midi/devices</c> with the others under these fixed ids. Other apps send to "Anode In", which
///     Anode receives as an input; Anode sends to "Anode Out", which other apps receive from.
/// </summary>
public static class VirtualMidiPorts {
    public const string InputId = "anode:in";
    public const string InputName = "Anode In";
    public const string OutputId = "anode:out";
    public const string OutputName = "Anode Out";
}

/// <summary>The kind of MIDI message a controller assignment listens to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControlInputKind>))]
public enum ControlInputKind {
    /// <summary>A 7-bit control change: <c>number</c> is the controller 0…127, the value 0…127.</summary>
    [JsonStringEnumMemberName("controlChange")]
    ControlChange,

    /// <summary>
    ///     A 14-bit control change pair: <c>number</c> is the MSB's controller 0…31, its LSB is <c>number</c> + 32; the
    ///     value 0…16383.
    /// </summary>
    [JsonStringEnumMemberName("controlChange14")]
    ControlChange14,

    /// <summary>
    ///     A non-registered parameter number (CC 99 and 98 select it, 6 and 38 carry the value, 96 and 97 step it):
    ///     <c>number</c> is the parameter 0…16383, the value 0…16383.
    /// </summary>
    [JsonStringEnumMemberName("nrpn")]
    Nrpn,

    /// <summary>A note: <c>number</c> is the key 0…127; note on is its velocity, note off (or velocity 0) is 0.</summary>
    [JsonStringEnumMemberName("note")]
    Note,

    /// <summary>Pitch bend, 0…16383 (8192 the centre); <c>number</c> is 0.</summary>
    [JsonStringEnumMemberName("pitchBend")]
    PitchBend,

    /// <summary>A program change: <c>number</c> is the program 0…127; it presses (a trigger), with no value.</summary>
    [JsonStringEnumMemberName("programChange")]
    ProgramChange
}

/// <summary>
///     What a controller assignment listens to: a port (a <c>MidiPort.id</c>; null: any port that is not a control
///     surface's), a kind, a channel (1…16; null: any) and a number (its meaning depends on <c>kind</c>).
/// </summary>
public sealed record ControlInput(string? Port, ControlInputKind Kind, int? Channel, int Number);

/// <summary>What a controller assignment changes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControlTargetKind>))]
public enum ControlTargetKind {
    /// <summary>
    ///     A track parameter by its automation address (<c>address</c>: <c>volume</c>, <c>pan</c>, <c>mute</c>,
    ///     <c>send:{sendId}</c>, <c>insert:{insertId}:{parameterId}</c>, <c>instrument:{parameterId}</c>,
    ///     <c>midiEffect:{slotId}:{parameterId}</c>; see <see cref="AutomationLane" />).
    /// </summary>
    [JsonStringEnumMemberName("parameter")]
    Parameter,

    /// <summary>A Smart Control: <c>address</c> is its id (<c>knob1</c>…<c>knob12</c>, <c>switch1</c>, …).</summary>
    [JsonStringEnumMemberName("smartControl")]
    SmartControl,

    /// <summary>A track switch (<c>switch</c>).</summary>
    [JsonStringEnumMemberName("trackSwitch")]
    TrackSwitch,

    /// <summary>A transport function (<c>transport</c>).</summary>
    [JsonStringEnumMemberName("transport")]
    Transport,

    /// <summary>A key command by its id (<c>keyCommandId</c>, as <c>KeyCommandSet</c> names them), run on press.</summary>
    [JsonStringEnumMemberName("keyCommand")]
    KeyCommand,

    /// <summary>Makes mode <c>modeId</c> of zone <c>zoneId</c> its zone's active mode, on press.</summary>
    [JsonStringEnumMemberName("zoneMode")]
    ZoneMode
}

/// <summary>A track switch a controller assignment can work.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrackSwitchAction>))]
public enum TrackSwitchAction {
    [JsonStringEnumMemberName("mute")]
    Mute,

    [JsonStringEnumMemberName("solo")]
    Solo,

    [JsonStringEnumMemberName("recordArm")]
    RecordArm,

    /// <summary>Selects the track below the selected one (its <c>trackId</c> is ignored).</summary>
    [JsonStringEnumMemberName("selectNext")]
    SelectNext,

    /// <summary>Selects the track above the selected one (its <c>trackId</c> is ignored).</summary>
    [JsonStringEnumMemberName("selectPrevious")]
    SelectPrevious
}

/// <summary>A transport function a controller assignment can work.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControlTransportAction>))]
public enum ControlTransportAction {
    [JsonStringEnumMemberName("play")]
    Play,

    [JsonStringEnumMemberName("stop")]
    Stop,

    [JsonStringEnumMemberName("record")]
    Record,

    /// <summary>Turns the cycle on or off.</summary>
    [JsonStringEnumMemberName("cycle")]
    Cycle,

    /// <summary>Moves the playhead back a bar per press (held: repeatedly).</summary>
    [JsonStringEnumMemberName("rewind")]
    Rewind,

    /// <summary>Moves the playhead forward a bar per press (held: repeatedly).</summary>
    [JsonStringEnumMemberName("forward")]
    Forward,

    [JsonStringEnumMemberName("goToStart")]
    GoToStart
}

/// <summary>
///     What a controller assignment changes: <c>kind</c>, and the fields that kind uses (the others are null).
///     <c>trackId</c> (parameter, smartControl, trackSwitch) is a fixed track; null follows the selected track (which
///     the editor reports with <c>PUT /project/selection</c>). <c>address</c> is the automation address (parameter) or
///     the Smart Control id (smartControl); <c>switch</c> the track switch; <c>transport</c> the transport function;
///     <c>keyCommandId</c> the key command; <c>zoneId</c> and <c>modeId</c> the mode a zoneMode target activates.
/// </summary>
public sealed record ControlTarget(
    ControlTargetKind Kind,
    string? TrackId = null,
    string? Address = null,
    TrackSwitchAction? Switch = null,
    ControlTransportAction? Transport = null,
    string? KeyCommandId = null,
    string? ZoneId = null,
    string? ModeId = null
);

/// <summary>How a controller assignment turns the control's values into the target's.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControlValueMode>))]
public enum ControlValueMode {
    /// <summary>The control's position is the target's value: its range maps linearly onto <c>min</c>…<c>max</c>.</summary>
    [JsonStringEnumMemberName("absolute")]
    Absolute,

    /// <summary>An endless encoder sending steps as two's complement (1…63 up, 127…65 down, as 7-bit −1…−63).</summary>
    [JsonStringEnumMemberName("relativeTwosComplement")]
    RelativeTwosComplement,

    /// <summary>An endless encoder sending steps as sign and magnitude (1…63 up, 65…127 down by 1…63).</summary>
    [JsonStringEnumMemberName("relativeSignedBit")]
    RelativeSignedBit,

    /// <summary>An endless encoder sending steps offset by 64 (65… up, …63 down).</summary>
    [JsonStringEnumMemberName("relativeBinaryOffset")]
    RelativeBinaryOffset,

    /// <summary>Each press (a value above half) flips the target between <c>min</c> and <c>max</c>.</summary>
    [JsonStringEnumMemberName("toggle")]
    Toggle,

    /// <summary>The target is <c>max</c> while the control is pressed, <c>min</c> when released.</summary>
    [JsonStringEnumMemberName("momentary")]
    Momentary
}

/// <summary>
///     A controller assignment (Logic's, simplified): <c>input</c> drives <c>target</c> in <c>mode</c>. <c>min</c> and
///     <c>max</c> are the target's range, normalised 0…1 over the target's own range (a parameter's, a Smart
///     Control's; switches and functions are 0 off, 1 on); <c>min</c> above <c>max</c> inverts. With <c>pickup</c>, an
///     absolute control has no effect until its value crosses the target's current value. Relative steps move the
///     target by 1/128 of <c>min</c>…<c>max</c> each.
/// </summary>
public sealed record ControlAssignment(
    string Id,
    string? Name,
    ControlInput Input,
    ControlTarget Target,
    ControlValueMode Mode,
    double Min,
    double Max,
    bool Pickup
);

/// <summary>A mode of a zone: a set of assignments that are live while it is the zone's active mode.</summary>
public sealed record ControlMode(string Id, string Name, IReadOnlyList<ControlAssignment> Assignments);

/// <summary>
///     A zone (usually one controller): its modes, of which <c>activeModeId</c> is live (only its assignments match
///     input); a zoneMode target switches it.
/// </summary>
public sealed record ControlZone(string Id, string Name, IReadOnlyList<ControlMode> Modes, string ActiveModeId);

/// <summary>
///     Every controller assignment, by zone and mode, stored in the user settings (not the project, as in Logic).
///     Ids are unique across all zones, modes and assignments. An input matches at most one assignment among the active
///     modes (the first in zone, mode and list order).
/// </summary>
public sealed record ControllerAssignments(IReadOnlyList<ControlZone> Zones) {
    /// <summary>A new user's: no zones.</summary>
    public static ControllerAssignments Empty { get; } = new([]);
}

/// <summary>
///     A MIDI message the server received for control (learning, or a matched or control-port message): the port, the
///     raw bytes (status and data; for a 14-bit pair or an NRPN, the message that completed it) and the input it reads
///     as (what Learn would assign).
/// </summary>
public sealed record ControlMessage(string Port, int Status, int Data1, int Data2, ControlInput Input);

/// <summary>
///     Learn mode: while <c>on</c>, the next control moved is assigned to <c>target</c> (when set) in mode
///     <c>modeId</c> of zone <c>zoneId</c> (null: the first zone's active mode, made when there is none), replacing an
///     assignment of the same input there. Messages still play their tracks while learning. <c>last</c> is the last
///     message received while learning.
/// </summary>
public sealed record LearnState(bool On, ControlTarget? Target, string? ZoneId, string? ModeId, ControlMessage? Last);

/// <summary>
///     Turns Learn on, or changes its target while on (<c>POST /control/learn/start</c>): the target the editor's
///     selection names (or a control's "Learn Assignment for …"); null waits for one.
/// </summary>
public sealed record LearnRequest(ControlTarget? Target = null, string? ZoneId = null, string? ModeId = null);

/// <summary>The protocol a control surface speaks.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControlSurfaceKind>))]
public enum ControlSurfaceKind {
    /// <summary>Mackie Control (Universal): eight strips with the master section.</summary>
    [JsonStringEnumMemberName("mackieControl")]
    MackieControl,

    /// <summary>Mackie Control XT: eight more strips, following a Mackie Control's bank.</summary>
    [JsonStringEnumMemberName("mackieControlXt")]
    MackieControlXt,

    /// <summary>HUI: eight strips over HUI's zone/port scheme.</summary>
    [JsonStringEnumMemberName("hui")]
    Hui
}

/// <summary>
///     A control surface, stored in the user settings: its protocol and port pair (<c>MidiPort.id</c>s; the ports become
///     control ports, whose messages never reach tracks). <c>order</c> (0, 1, …) places it left to right: extenders show
///     the strips after the surface before them.
/// </summary>
public sealed record ControlSurface(
    string Id,
    ControlSurfaceKind Kind,
    string InputPort,
    string OutputPort,
    int Order
);

/// <summary>Adds a control surface (<c>POST /control/surfaces</c>), after the others.</summary>
public sealed record AddSurfaceRequest(ControlSurfaceKind Kind, string InputPort, string OutputPort);

/// <summary>The surfaces' new order (<c>PUT /control/surfaces/order</c>): every surface id, once, left to right.</summary>
public sealed record ReorderSurfacesRequest(IReadOnlyList<string> Ids);

/// <summary>
///     A surface's live state: whether it answers (Mackie Control's handshake or any input; HUI's ping) and the index of
///     the track on its first strip.
/// </summary>
public sealed record SurfaceStatus(string Id, bool Connected, int BankOffset);

/// <summary>An OSC client that receives feedback: a host (name or address) and UDP port.</summary>
public sealed record OscClient(string Host, int Port);

/// <summary>
///     The OSC server (docs/control/osc.md), stored in the user settings: on or off, its UDP port (default 8000), and
///     clients that always receive feedback (others subscribe with <c>/anode/subscribe port</c> for the session).
/// </summary>
public sealed record OscSettings(bool Enabled, int Port, IReadOnlyList<OscClient> Clients) {
    /// <summary>A new user's: off, on port 8000, without clients.</summary>
    public static OscSettings Default { get; } = new(false, 8000, []);
}

/// <summary>
///     The OSC server's state (<c>GET /control/osc</c>): its settings, whether it is listening (the port may be taken),
///     this machine's addresses (for the user to type into a client) and the clients subscribed this session.
/// </summary>
public sealed record OscStatus(
    OscSettings Settings,
    bool Listening,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<OscClient> Subscribed
);

/// <summary>
///     The editor's selection as far as the server needs it (<c>PUT /project/selection</c>): the selected track, which
///     selected-track controller assignments, surfaces and OSC follow; null: none.
/// </summary>
public sealed record SelectionRequest(string? TrackId);
