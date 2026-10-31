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
///     The current value of a target, normalised 0…1 over the target's own range (switches 0 or 1), as the server
///     resolves it (a null <c>TrackId</c> is the selected track); null when the target does not exist (no selected track,
///     a parameter that is gone). Pickup, relative modes and toggle read it.
/// </summary>
public interface ITargetValues {
    double? Value(ControlTarget target);
}

/// <summary>
///     A change an assignment makes: assignment <paramref name="AssignmentId" /> sets <paramref name="Target" /> (as the
///     assignment names it: a null <c>TrackId</c> is still the selected track) to <paramref name="Value" />, normalised
///     0…1 over the target's range. For track switches 1 is on and 0 off; transport functions, key commands and zone
///     modes act on 1 (a press) and ignore 0, except that momentary transport functions (rewind, forward) stop on 0.
/// </summary>
public sealed record TargetChange(string AssignmentId, ControlTarget Target, double Value);

/// <summary>
///     Controller assignments at run time (docs/09-phase-8.md, workstream D): input matching (7-bit, 14-bit pairs, NRPN,
///     notes, pitch bend, program change), the value modes (absolute, the three relative kinds, toggle, momentary), the
///     range, pickup, and zones with their active modes. Stateful (14-bit and NRPN halves, pickup, toggles), one instance
///     per assignment set, used from one thread. Pure: no I/O, no clock.
/// </summary>
public sealed class AssignmentEngine {
    /// <summary>An engine for <paramref name="assignments" /> (checked: ids unique, references valid).</summary>
    public AssignmentEngine(ControllerAssignments assignments) {
        throw Pending.WorkstreamD();
    }

    /// <summary>The assignments, with each zone's active mode as zoneMode targets last switched it.</summary>
    public ControllerAssignments Assignments => throw Pending.WorkstreamD();

    /// <summary>
    ///     The matcher table these assignments imply (every assignment of every mode, so a mode switch needs no new
    ///     table): what the host must consume rather than play.
    /// </summary>
    public IReadOnlyList<ControlMatcher> Matchers => throw Pending.WorkstreamD();

    /// <summary>
    ///     A message from port <paramref name="port" /> (a <c>MidiPort.id</c>): the changes the active modes'
    ///     assignments make, in order (none for half a 14-bit pair or NRPN, for a message no assignment takes, or while
    ///     pickup holds). A zoneMode change is applied here too (see <see cref="Assignments" />) and reported.
    /// </summary>
    public IReadOnlyList<TargetChange> Receive(string port, ReadOnlySpan<byte> message, ITargetValues values) =>
        throw Pending.WorkstreamD();

    /// <summary>
    ///     The input a message from <paramref name="port" /> reads as, for Learn: a control change is 7-bit unless its
    ///     pair's other half (controller ± 32) arrived just before it, CC 99/98 then 6/38 is an NRPN; null for a half
    ///     still incomplete or a message that cannot be assigned. Keeps its own state, apart from
    ///     <see cref="Receive" />'s.
    /// </summary>
    public ControlInput? Learn(string port, ReadOnlySpan<byte> message) => throw Pending.WorkstreamD();

    /// <summary>Forgets pickup and partial messages (after a project change, or when the selected track changes).</summary>
    public void Reset() => throw Pending.WorkstreamD();
}
