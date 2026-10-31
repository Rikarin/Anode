using Anode.Contracts;
using Anode.Control.Assignments;
using Anode.Control.Osc;
using Anode.Control.Surfaces;

namespace Anode.Server.Control;

/// <summary>
///     The controller assignments at run time, as <see cref="ControlService" /> uses them: exactly the members of
///     <c>Anode.Control</c>'s <see cref="AssignmentEngine" /> (workstream D), behind an interface so tests can run the
///     service with a stand-in.
/// </summary>
public interface IAssignmentRuntime {
    /// <summary>The assignments, with each zone's active mode as zoneMode targets last switched it.</summary>
    ControllerAssignments Assignments { get; }

    /// <summary>The matcher table the host routes by.</summary>
    IReadOnlyList<ControlMatcher> Matchers { get; }

    /// <summary>The changes a message makes (none for half a pair, an unassigned message, or while pickup holds).</summary>
    IReadOnlyList<TargetChange> Receive(string port, ReadOnlySpan<byte> message, ITargetValues values);

    /// <summary>The input a message reads as, for Learn; null for half a pair or a message that cannot be assigned.</summary>
    ControlInput? Learn(string port, ReadOnlySpan<byte> message);

    /// <summary>Forgets pickup and partial messages.</summary>
    void Reset();
}

/// <summary>
///     Everything <see cref="ControlService" /> needs of the control protocols (docs/09-phase-8.md, workstream D): the
///     assignment engine, the surface protocols, the OSC codec and Anode's OSC address space. The default,
///     <see cref="ControlProtocols" />, is <c>Anode.Control</c>'s; tests substitute their own. Each object it makes is
///     used under the service's lock, from one thread at a time.
/// </summary>
public interface IControlProtocols {
    /// <summary>An engine for <paramref name="assignments" /> (checked by the engine: <see cref="ArgumentException" />).</summary>
    IAssignmentRuntime Assignments(ControllerAssignments assignments);

    /// <summary>The protocol and model of a new surface of <paramref name="kind" />.</summary>
    IControlSurface Surface(ControlSurfaceKind kind);

    /// <summary>A datagram as an OSC packet; a malformed one is a <see cref="FormatException" />.</summary>
    OscPacket DecodeOsc(ReadOnlySpan<byte> data);

    byte[] EncodeOsc(OscPacket packet);

    /// <summary>What an OSC message asks, against the view; null outside the address space.</summary>
    OscRequest? ResolveOsc(OscMessage message, ProjectView view);

    /// <summary>Every OSC feedback message for the view (each address with its current value).</summary>
    IReadOnlyList<OscMessage> OscFeedback(ProjectView view);
}

/// <summary>The control protocols of <c>Anode.Control</c> (workstream D).</summary>
public sealed class ControlProtocols : IControlProtocols {
    public IAssignmentRuntime Assignments(ControllerAssignments assignments) => new Engine(new(assignments));

    public IControlSurface Surface(ControlSurfaceKind kind) => ControlSurfaces.Create(kind);

    public OscPacket DecodeOsc(ReadOnlySpan<byte> data) => OscCodec.Decode(data);

    public byte[] EncodeOsc(OscPacket packet) => OscCodec.Encode(packet);

    public OscRequest? ResolveOsc(OscMessage message, ProjectView view) => OscAddressSpace.Resolve(message, view);

    public IReadOnlyList<OscMessage> OscFeedback(ProjectView view) => OscAddressSpace.Feedback(view);

    sealed class Engine(AssignmentEngine engine) : IAssignmentRuntime {
        public ControllerAssignments Assignments => engine.Assignments;

        public IReadOnlyList<ControlMatcher> Matchers => engine.Matchers;

        public IReadOnlyList<TargetChange> Receive(string port, ReadOnlySpan<byte> message, ITargetValues values) =>
            engine.Receive(port, message, values);

        public ControlInput? Learn(string port, ReadOnlySpan<byte> message) => engine.Learn(port, message);

        public void Reset() => engine.Reset();
    }
}
