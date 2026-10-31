using Anode.Contracts;

namespace Anode.Control.Assignments;

/// <summary>
///     Which messages a <see cref="ControlMatcher" /> consumes besides the one it names (IPC's
///     <c>EngineMatcherFamily</c>).
/// </summary>
public enum MatcherFamily {
    /// <summary>The message alone; a note-on matcher (0x90) also takes its key's note offs (0x80).</summary>
    Single,

    /// <summary>Controllers <c>Data1</c> (0…31) and <c>Data1</c> + 32.</summary>
    ControlChange14,

    /// <summary>Controllers 99, 98, 6, 38, 96 and 97 on the channel (<c>Data1</c> −1).</summary>
    Nrpn
}

/// <summary>
///     One row of the matcher table the host routes by (docs/09-phase-8.md, "Routing"; IPC's
///     <c>EngineControlMatcher</c>): port (null any), status nibble 0x80…0xE0, channel 0…15 (−1 any), first data byte
///     0…127 (−1 any), family.
/// </summary>
public readonly record struct ControlMatcher(string? Port, int Status, int Channel, int Data1, MatcherFamily Family);

/// <summary>
///     The current value of a target, normalised 0…1 over the target's own range (switches 0 or 1; a volume is its
///     fader position), as the server resolves it (a null <c>TrackId</c> is the selected track); null when the target
///     does not exist (no selected track, a parameter that is gone). Pickup, relative modes and toggle read it.
/// </summary>
public interface ITargetValues {
    double? Value(ControlTarget target);
}

/// <summary>
///     A change an assignment makes: assignment <paramref name="AssignmentId" /> sets <paramref name="Target" /> (as the
///     assignment names it: a null <c>TrackId</c> is still the selected track) to <paramref name="Value" />, normalised
///     0…1 over the target's range. For track switches 1 is on and 0 off. Functions (transport, key commands, zone
///     modes, select next and previous) get 1 on a press and 0 on a release whatever the mode and range: they act on 1
///     and ignore 0, except that the momentary transport functions (rewind, forward) stop on 0.
/// </summary>
public sealed record TargetChange(string AssignmentId, ControlTarget Target, double Value);

/// <summary>
///     Controller assignments at run time (docs/09-phase-8.md, workstream D): input matching (7-bit, 14-bit pairs, NRPN,
///     notes, pitch bend, program change), the value modes (absolute, the three relative kinds, toggle, momentary), the
///     range, pickup, and zones with their active modes. Stateful (14-bit and NRPN halves, pickup, toggles), one instance
///     per assignment set, used from one thread. Pure: no I/O, no clock. Documented in <c>docs/control/README.md</c>.
/// </summary>
public sealed class AssignmentEngine {
    readonly List<ZoneState> _zones = [];
    readonly Dictionary<(string Port, int Channel), Assembly> _receive = [];
    readonly Dictionary<(string Port, int Channel), Assembly> _learn = [];
    readonly Dictionary<string, Memory> _memory = [];
    readonly List<ControlMatcher> _matchers = [];

    /// <summary>
    ///     An engine for <paramref name="assignments" />, checked: ids unique across zones, modes and assignments; each
    ///     zone's active mode one of its own (or any id while it has none); zoneMode targets naming a zone and its mode;
    ///     channels 1…16; numbers in their kind's range; <c>min</c> and <c>max</c> within 0…1. An
    ///     <see cref="ArgumentException" /> otherwise.
    /// </summary>
    public AssignmentEngine(ControllerAssignments assignments) {
        ArgumentNullException.ThrowIfNull(assignments);
        Validate(assignments);
        foreach (var zone in assignments.Zones) {
            _zones.Add(new(zone, zone.ActiveModeId));
            foreach (var mode in zone.Modes) {
                foreach (var assignment in mode.Assignments) {
                    var matcher = MatcherFor(assignment.Input);
                    if (!_matchers.Contains(matcher)) {
                        _matchers.Add(matcher);
                    }
                }
            }
        }
    }

    /// <summary>The assignments, with each zone's active mode as zoneMode targets last switched it.</summary>
    public ControllerAssignments Assignments {
        get {
            var zones = new List<ControlZone>(_zones.Count);
            foreach (var zone in _zones) {
                zones.Add(zone.Zone with { ActiveModeId = zone.ActiveModeId });
            }

            return new(zones);
        }
    }

    /// <summary>
    ///     The matcher table these assignments imply (every assignment of every mode, so a mode switch needs no new
    ///     table), without duplicates, in assignment order: what the host must consume rather than play.
    /// </summary>
    public IReadOnlyList<ControlMatcher> Matchers => _matchers;

    /// <summary>
    ///     A message from port <paramref name="port" /> (a <c>MidiPort.id</c>): the changes the active modes'
    ///     assignments make, in order (none for half a 14-bit pair or NRPN, for a message no assignment takes, or while
    ///     pickup holds). The first assignment (zone, mode and list order) whose input the message belongs to takes it,
    ///     even when it makes no change. A zoneMode change is applied here too (see <see cref="Assignments" />) and
    ///     reported. A 14-bit pair's value arrives with its LSB (n + 32), after the MSB (n); an NRPN's with CC 38, after
    ///     99, 98 and 6; CC 96 and 97 step the selected NRPN by one.
    /// </summary>
    public IReadOnlyList<TargetChange> Receive(string port, ReadOnlySpan<byte> message, ITargetValues values) {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(values);
        if (!ChannelMessage.TryRead(message, out var msg)) {
            return [];
        }

        var assembly = AssemblyFor(_receive, port, msg.Channel);
        var readings = Read(msg, assembly);
        if (FirstClaim(port, msg, assembly, readings) is not var (assignment, reading)
            || reading is not { } r
            || Apply(assignment, r, values) is not { } v) {
            return [];
        }

        if (assignment.Target.Kind == ControlTargetKind.ZoneMode && v >= 0.5) {
            SwitchMode(assignment.Target.ZoneId!, assignment.Target.ModeId!);
        }

        return [new(assignment.Id, assignment.Target, v)];
    }

    /// <summary>The first live assignment that takes the message, with the reading it acts on.</summary>
    (ControlAssignment Assignment, Reading? Reading)? FirstClaim(
        string port,
        ChannelMessage msg,
        Assembly assembly,
        List<Reading> readings
    ) {
        foreach (var zone in _zones) {
            foreach (var assignment in zone.ActiveMode?.Assignments ?? []) {
                if (Claims(assignment.Input, port, msg, assembly, readings, out var reading)) {
                    return (assignment, reading);
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     The input a message from <paramref name="port" /> reads as, for Learn: a control change is 7-bit unless its
    ///     pair's other half (controller ± 32) arrived just before it on the same port and channel (then it is the 14-bit
    ///     pair: an MSB alone reads as 7-bit, and the LSB that follows reads as the pair); CC 6, 38, 96 or 97 after
    ///     99 and 98 is that NRPN; a note on is its note (a note off is null); null for CC 99, 98, 101 and 100 (a
    ///     half still incomplete) and for a message that cannot be assigned. Keeps its own state, apart from
    ///     <see cref="Receive" />'s.
    /// </summary>
    public ControlInput? Learn(string port, ReadOnlySpan<byte> message) {
        ArgumentNullException.ThrowIfNull(port);
        if (!ChannelMessage.TryRead(message, out var msg)) {
            return null;
        }

        var channel = msg.Channel + 1;
        switch (msg.Type) {
            case 0x90 when msg.Data2 > 0:
                return new(port, ControlInputKind.Note, channel, msg.Data1);
            case 0xE0:
                return new(port, ControlInputKind.PitchBend, channel, 0);
            case 0xC0:
                return new(port, ControlInputKind.ProgramChange, channel, msg.Data1);
            case 0xB0:
                break;
            default:
                return null;
        }

        var state = AssemblyFor(_learn, port, msg.Channel);
        var c = msg.Data1;
        var previous = state.LastController;
        state.LastController = c;
        switch (c) {
            case 99:
                state.ParamMsb = msg.Data2;
                state.ParamLsb = -1;
                return null;
            case 98:
                state.ParamLsb = state.ParamMsb >= 0 ? msg.Data2 : -1;
                return null;
            case 101 or 100:
                state.ParamMsb = -1;
                state.ParamLsb = -1;
                return null;
            case 6 or 38 or 96 or 97 when state.Parameter >= 0:
                return new(port, ControlInputKind.Nrpn, channel, state.Parameter);
            case < 32 when previous == c + 32:
                return new(port, ControlInputKind.ControlChange14, channel, c);
            case >= 32 and < 64 when previous == c - 32:
                return new(port, ControlInputKind.ControlChange14, channel, c - 32);
            default:
                return new(port, ControlInputKind.ControlChange, channel, c);
        }
    }

    /// <summary>Forgets pickup, toggles and partial messages (after a project change, or when the selected track changes).</summary>
    public void Reset() {
        _receive.Clear();
        _learn.Clear();
        _memory.Clear();
    }

    /// <summary>
    ///     Learn's result (docs/09-phase-8.md, "Learn"): <paramref name="assignments" /> with a new assignment of
    ///     <paramref name="input" /> to <paramref name="target" /> in mode <paramref name="modeId" /> of zone
    ///     <paramref name="zoneId" /> (null zone: the first zone, made when there is none; null mode: the zone's active
    ///     mode, made when it has none). It replaces any assignment of the same input in that mode, and, for a 14-bit or
    ///     NRPN input, the 7-bit assignments of the same target that its halves made a moment before (controllers n and
    ///     n + 32; 99, 98, 6 and 38). Its mode is toggle for switches (track switches, the <c>mute</c> parameter) and
    ///     absolute otherwise; its range 0…1; no pickup. <paramref name="newId" /> makes the ids (assignment, and zone
    ///     and mode when made). An unknown zone or mode is an <see cref="ArgumentException" />.
    /// </summary>
    public static (ControllerAssignments Assignments, ControlAssignment Assignment) Assign(
        ControllerAssignments assignments,
        ControlInput input,
        ControlTarget target,
        string? zoneId,
        string? modeId,
        Func<string> newId
    ) {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(newId);
        var zones = new List<ControlZone>(assignments.Zones);
        int zoneIndex;
        if (zoneId is not null) {
            zoneIndex = zones.FindIndex(z => z.Id == zoneId);
            if (zoneIndex < 0) {
                throw new ArgumentException($"Unknown zone {zoneId}.", nameof(zoneId));
            }
        } else if (zones.Count > 0) {
            zoneIndex = 0;
        } else {
            zones.Add(new(newId(), "Zone 1", [], string.Empty));
            zoneIndex = 0;
        }

        var zone = zones[zoneIndex];
        var modes = new List<ControlMode>(zone.Modes);
        var wanted = modeId ?? zone.ActiveModeId;
        var modeIndex = modes.FindIndex(m => m.Id == wanted);
        if (modeIndex < 0) {
            if (modeId is not null) {
                throw new ArgumentException($"Unknown mode {modeId}.", nameof(modeId));
            }

            if (modes.Count > 0) {
                modeIndex = 0;
            } else {
                modes.Add(new(newId(), "Mode 1", []));
                modeIndex = 0;
                zone = zone with { ActiveModeId = modes[0].Id };
            }
        }

        var mode = modes[modeIndex];
        var list = new List<ControlAssignment>();
        foreach (var existing in mode.Assignments) {
            if (!Supersedes(input, target, existing)) {
                list.Add(existing);
            }
        }

        var assignment = new ControlAssignment(newId(), null, input, target, DefaultMode(target), 0, 1, false);
        list.Add(assignment);
        modes[modeIndex] = mode with { Assignments = list };
        zones[zoneIndex] = zone with { Modes = modes };
        return (new(zones), assignment);
    }

    static bool Supersedes(ControlInput input, ControlTarget target, ControlAssignment existing) {
        var other = existing.Input;
        if (other == input) {
            return true;
        }

        if (other.Kind != ControlInputKind.ControlChange
            || other.Port != input.Port
            || other.Channel != input.Channel
            || existing.Target != target) {
            return false;
        }

        return input.Kind switch {
            ControlInputKind.ControlChange14 => other.Number == input.Number || other.Number == input.Number + 32,
            ControlInputKind.Nrpn => other.Number is 99 or 98 or 6 or 38,
            _ => false
        };
    }

    static ControlValueMode DefaultMode(ControlTarget target) =>
        target.Kind switch {
            ControlTargetKind.TrackSwitch when target.Switch is TrackSwitchAction.Mute
                or TrackSwitchAction.Solo
                or TrackSwitchAction.RecordArm => ControlValueMode.Toggle,
            ControlTargetKind.Parameter when target.Address == "mute" => ControlValueMode.Toggle,
            _ => ControlValueMode.Absolute
        };

    static bool IsFunction(ControlTarget target) =>
        target.Kind switch {
            ControlTargetKind.Transport or ControlTargetKind.KeyCommand or ControlTargetKind.ZoneMode => true,
            ControlTargetKind.TrackSwitch => target.Switch is TrackSwitchAction.SelectNext
                or TrackSwitchAction.SelectPrevious,
            _ => false
        };

    static ControlMatcher MatcherFor(ControlInput input) {
        var channel = input.Channel is { } c ? c - 1 : -1;
        return input.Kind switch {
            ControlInputKind.ControlChange => new(input.Port, 0xB0, channel, input.Number, MatcherFamily.Single),
            ControlInputKind.ControlChange14 => new(
                input.Port,
                0xB0,
                channel,
                input.Number,
                MatcherFamily.ControlChange14
            ),
            ControlInputKind.Nrpn => new(input.Port, 0xB0, channel, -1, MatcherFamily.Nrpn),
            ControlInputKind.Note => new(input.Port, 0x90, channel, input.Number, MatcherFamily.Single),
            ControlInputKind.PitchBend => new(input.Port, 0xE0, channel, -1, MatcherFamily.Single),
            ControlInputKind.ProgramChange => new(input.Port, 0xC0, channel, input.Number, MatcherFamily.Single),
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.Kind, "Unknown input kind.")
        };
    }

    static void Validate(ControllerAssignments assignments) {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var all = assignments.Zones.SelectMany(static z => z.Modes).SelectMany(static m => m.Assignments).ToList();
        var everyId = assignments.Zones.Select(static z => z.Id)
            .Concat(assignments.Zones.SelectMany(static z => z.Modes).Select(static m => m.Id))
            .Concat(all.Select(static a => a.Id));
        foreach (var id in everyId) {
            if (string.IsNullOrEmpty(id) || !ids.Add(id)) {
                throw new ArgumentException($"Controller assignment ids must be unique and non-empty ({id}).");
            }
        }

        foreach (var zone in assignments.Zones) {
            if (zone.Modes.Count > 0 && !HasMode(zone, zone.ActiveModeId)) {
                throw new ArgumentException(
                    $"Zone {zone.Id}'s active mode {zone.ActiveModeId} is not one of its modes."
                );
            }
        }

        foreach (var a in all) {
            ValidateAssignment(a);
            var target = a.Target;
            if (target.Kind == ControlTargetKind.ZoneMode
                && !assignments.Zones.Any(z => z.Id == target.ZoneId && HasMode(z, target.ModeId))) {
                throw new ArgumentException($"Assignment {a.Id} switches to a mode that does not exist.");
            }
        }
    }

    static bool HasMode(ControlZone zone, string? modeId) => zone.Modes.Any(m => m.Id == modeId);

    static void ValidateAssignment(ControlAssignment a) {
        var input = a.Input;
        if (input.Channel is < 1 or > 16) {
            throw new ArgumentException($"Assignment {a.Id}: channel {input.Channel} is not 1…16.");
        }

        var max = input.Kind switch {
            ControlInputKind.ControlChange or ControlInputKind.Note or ControlInputKind.ProgramChange => 127,
            ControlInputKind.ControlChange14 => 31,
            ControlInputKind.Nrpn => 16383,
            _ => 0
        };
        if (input.Number < 0 || input.Number > max) {
            throw new ArgumentException($"Assignment {a.Id}: number {input.Number} is not 0…{max}.");
        }

        if (!(a.Min is >= 0 and <= 1) || !(a.Max is >= 0 and <= 1)) {
            throw new ArgumentException($"Assignment {a.Id}: min and max must be within 0…1.");
        }

        switch (a.Target.Kind) {
            case ControlTargetKind.Parameter or ControlTargetKind.SmartControl when string.IsNullOrEmpty(
                a.Target.Address
            ):
            case ControlTargetKind.TrackSwitch when a.Target.Switch is null:
            case ControlTargetKind.Transport when a.Target.Transport is null:
            case ControlTargetKind.KeyCommand when string.IsNullOrEmpty(a.Target.KeyCommandId):
            case ControlTargetKind.ZoneMode when a.Target.ZoneId is null || a.Target.ModeId is null:
                throw new ArgumentException($"Assignment {a.Id}: its target lacks the field its kind needs.");
        }
    }

    static Assembly AssemblyFor(Dictionary<(string, int), Assembly> table, string port, int channel) {
        if (!table.TryGetValue((port, channel), out var assembly)) {
            assembly = new();
            table[(port, channel)] = assembly;
        }

        return assembly;
    }

    /// <summary>Updates the 14-bit and NRPN assembly with <paramref name="msg" /> and lists what it reads as.</summary>
    static List<Reading> Read(ChannelMessage msg, Assembly state) {
        var readings = new List<Reading>(2);
        switch (msg.Type) {
            case 0x90 when msg.Data2 > 0:
                readings.Add(new(ControlInputKind.Note, msg.Data1, msg.Data2, 127, true));
                break;
            case 0x80 or 0x90:
                readings.Add(new(ControlInputKind.Note, msg.Data1, 0, 127, false));
                break;
            case 0xE0: {
                var raw = (msg.Data2 << 7) | msg.Data1;
                readings.Add(new(ControlInputKind.PitchBend, 0, raw, 16383, raw >= 8192));
                break;
            }
            case 0xC0:
                readings.Add(new(ControlInputKind.ProgramChange, msg.Data1, 127, 127, true, true));
                break;
            case 0xB0:
                ReadControlChange(msg.Data1, msg.Data2, state, readings);
                break;
        }

        return readings;
    }

    static void ReadControlChange(int c, int v, Assembly state, List<Reading> readings) {
        readings.Add(new(ControlInputKind.ControlChange, c, v, 127, v >= 64));
        switch (c) {
            case 99:
                state.ParamMsb = v;
                state.ParamLsb = -1;
                state.DataMsb = -1;
                break;
            case 98:
                state.ParamLsb = state.ParamMsb >= 0 ? v : -1;
                state.DataMsb = -1;
                break;
            case 101 or 100:
                state.ParamMsb = -1;
                state.ParamLsb = -1;
                break;
            case 6 when state.Parameter >= 0:
                state.DataMsb = v;
                break;
            case 38 when state.Parameter >= 0 && state.DataMsb >= 0:
                readings.Add(
                    new(
                        ControlInputKind.Nrpn,
                        state.Parameter,
                        (state.DataMsb << 7) | v,
                        16383,
                        state.DataMsb >= 64
                    )
                );
                break;
            case 96 or 97 when state.Parameter >= 0:
                readings.Add(new(ControlInputKind.Nrpn, state.Parameter, 0, 16383, false, Step: c == 96 ? 1 : -1));
                break;
        }

        if (c < 32) {
            state.Msb[c] = v;
        } else if (c < 64 && state.Msb[c - 32] >= 0) {
            var msb = state.Msb[c - 32];
            readings.Add(new(ControlInputKind.ControlChange14, c - 32, (msb << 7) | v, 16383, msb >= 64));
        }
    }

    /// <summary>
    ///     Whether <paramref name="input" /> takes <paramref name="msg" /> (its family), and the reading it acts on (null:
    ///     taken without a change, as an MSB alone).
    /// </summary>
    static bool Claims(
        ControlInput input,
        string port,
        ChannelMessage msg,
        Assembly state,
        List<Reading> readings,
        out Reading? reading
    ) {
        reading = null;
        if ((input.Port is not null && input.Port != port) || (input.Channel is { } ch && ch - 1 != msg.Channel)) {
            return false;
        }

        var claimed = input.Kind switch {
            ControlInputKind.Note => msg.Type is 0x80 or 0x90 && msg.Data1 == input.Number,
            ControlInputKind.PitchBend => msg.Type == 0xE0,
            ControlInputKind.ProgramChange => msg.Type == 0xC0 && msg.Data1 == input.Number,
            ControlInputKind.ControlChange => msg.Type == 0xB0 && msg.Data1 == input.Number,
            ControlInputKind.ControlChange14 => msg.Type == 0xB0
                && (msg.Data1 == input.Number || msg.Data1 == input.Number + 32),
            ControlInputKind.Nrpn => msg.Type == 0xB0 && ClaimsNrpn(msg.Data1, state.Parameter, input.Number),
            _ => false
        };

        // A family's other messages (a 14-bit MSB, CC 99, 98 and 6) find no reading: taken without a change.
        if (claimed) {
            reading = Find(readings, input.Kind, input.Number);
        }

        return claimed;
    }

    /// <summary>CC 99 and 98 for any NRPN; 6, 38, 96 and 97 for the one selected.</summary>
    static bool ClaimsNrpn(int controller, int selected, int number) =>
        controller is 99 or 98 || (controller is 6 or 38 or 96 or 97 && selected == number);

    static Reading? Find(List<Reading> readings, ControlInputKind kind, int number) {
        foreach (var r in readings) {
            if (r.Kind == kind && r.Number == number) {
                return r;
            }
        }

        return null;
    }

    double? Apply(ControlAssignment a, Reading r, ITargetValues values) {
        if (IsFunction(a.Target)) {
            return r.Step != 0 || IsRelative(a.Mode) ? null : r.Pressed ? 1 : 0;
        }

        return a.Mode switch {
            ControlValueMode.Absolute when r.Step != 0 => Step(a, r.Step, values),
            ControlValueMode.Absolute => Absolute(a, r, values, MemoryFor(a.Id)),
            ControlValueMode.Toggle => Toggle(a, r, values, MemoryFor(a.Id)),
            ControlValueMode.Momentary => Momentary(a, r),
            _ => RelativeValue(a, r, values)
        };
    }

    static bool IsRelative(ControlValueMode mode) =>
        mode is ControlValueMode.RelativeBinaryOffset
            or ControlValueMode.RelativeSignedBit
            or ControlValueMode.RelativeTwosComplement;

    static double? Toggle(ControlAssignment a, Reading r, ITargetValues values, Memory memory) {
        if (r.Step != 0 || !r.Pressed) {
            return null;
        }

        var current = values.Value(a.Target);
        var on = current is { } c ? Math.Abs(c - a.Max) < Math.Abs(c - a.Min) : memory.ToggleOn;
        memory.ToggleOn = !on;
        return on ? a.Min : a.Max;
    }

    static double? Momentary(ControlAssignment a, Reading r) {
        if (r.Step != 0) {
            return null;
        }

        return r.Pressed ? a.Max : a.Min;
    }

    static double? RelativeValue(ControlAssignment a, Reading r, ITargetValues values) {
        if (r.Step != 0) {
            return Step(a, r.Step, values);
        }

        if (r.Kind is ControlInputKind.Note or ControlInputKind.ProgramChange || a.Max == a.Min) {
            return null;
        }

        var steps = Relative(a.Mode, r.Raw, r.Max == 127 ? 7 : 14);
        return steps == 0 ? null : Step(a, steps, values);
    }

    static double? Step(ControlAssignment a, int steps, ITargetValues values) {
        if (values.Value(a.Target) is not { } current) {
            return null;
        }

        var value = current + steps * (a.Max - a.Min) / 128;
        return Math.Clamp(value, Math.Min(a.Min, a.Max), Math.Max(a.Min, a.Max));
    }

    /// <summary>A relative encoder's steps from its value of <paramref name="bits" /> bits.</summary>
    internal static int Relative(ControlValueMode mode, int raw, int bits) {
        var full = 1 << bits;
        var half = full >> 1;
        return mode switch {
            ControlValueMode.RelativeTwosComplement => raw < half ? raw : raw - full,
            ControlValueMode.RelativeSignedBit => (raw & half) != 0 ? -(raw & (half - 1)) : raw,
            ControlValueMode.RelativeBinaryOffset => raw - half,
            _ => 0
        };
    }

    static double? Absolute(ControlAssignment a, Reading r, ITargetValues values, Memory memory) {
        var span = a.Max - a.Min;
        var value = r.Trigger ? a.Max : a.Min + (double)r.Raw / r.Max * span;
        if (!a.Pickup || values.Value(a.Target) is not { } current) {
            return value;
        }

        // Within a step of the target, or crossing it since the last value, picks the target up; a target that moved
        // elsewhere since the last value sent drops it again.
        var tolerance = Math.Max(Math.Abs(span) / r.Max, 1.0 / 256);
        if (memory.PickedUp && memory.Sent is { } sent && Math.Abs(current - sent) > tolerance) {
            memory.PickedUp = false;
        }

        if (!memory.PickedUp
            && (Math.Abs(value - current) <= tolerance
                || (memory.LastValue is { } last && (last - current) * (value - current) < 0))) {
            memory.PickedUp = true;
        }

        memory.LastValue = value;
        if (!memory.PickedUp) {
            return null;
        }

        memory.Sent = value;
        return value;
    }

    Memory MemoryFor(string id) {
        if (!_memory.TryGetValue(id, out var memory)) {
            memory = new();
            _memory[id] = memory;
        }

        return memory;
    }

    void SwitchMode(string zoneId, string modeId) {
        foreach (var zone in _zones) {
            if (zone.Zone.Id == zoneId) {
                zone.ActiveModeId = modeId;
            }
        }
    }

    sealed class ZoneState(ControlZone zone, string activeModeId) {
        public ControlZone Zone { get; } = zone;

        public string ActiveModeId { get; set; } = activeModeId;

        public ControlMode? ActiveMode {
            get {
                foreach (var mode in Zone.Modes) {
                    if (mode.Id == ActiveModeId) {
                        return mode;
                    }
                }

                return null;
            }
        }
    }

    /// <summary>A port and channel's partial messages: 14-bit MSBs, the NRPN selected and its data MSB.</summary>
    sealed class Assembly {
        public int[] Msb { get; } = [..Enumerable.Repeat(-1, 32)];

        public int ParamMsb { get; set; } = -1;

        public int ParamLsb { get; set; } = -1;

        public int DataMsb { get; set; } = -1;

        public int LastController { get; set; } = -1;

        public int Parameter => ParamMsb >= 0 && ParamLsb >= 0 ? (ParamMsb << 7) | ParamLsb : -1;
    }

    /// <summary>An assignment's state: pickup, the toggle's state when the target's is unknown.</summary>
    sealed class Memory {
        public bool PickedUp { get; set; }

        public double? LastValue { get; set; }

        public double? Sent { get; set; }

        public bool ToggleOn { get; set; }
    }

    /// <summary>
    ///     What a message reads as for one input kind: the raw value of <c>Max</c> (127 or 16383), whether it is a press
    ///     (a note on, a program change, a value at or above half), a trigger (program change: no value) or a step.
    /// </summary>
    readonly record struct Reading(
        ControlInputKind Kind,
        int Number,
        int Raw,
        int Max,
        bool Pressed,
        bool Trigger = false,
        int Step = 0
    );
}

/// <summary>A channel voice message: its type nibble, channel 0…15 and data bytes (0 when absent).</summary>
readonly record struct ChannelMessage(int Type, int Channel, int Data1, int Data2) {
    public static bool TryRead(ReadOnlySpan<byte> message, out ChannelMessage msg) {
        msg = default;
        if (message.Length < 2 || message[0] < 0x80 || message[0] >= 0xF0) {
            return false;
        }

        var type = message[0] & 0xF0;
        var length = type is 0xC0 or 0xD0 ? 2 : 3;
        if (message.Length < length || message[1] > 0x7F || (length == 3 && message[2] > 0x7F)) {
            return false;
        }

        msg = new(type, message[0] & 0x0F, message[1], length == 3 ? message[2] : 0);
        return true;
    }
}
