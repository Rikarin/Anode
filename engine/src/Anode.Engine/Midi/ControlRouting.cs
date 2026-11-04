using Anode.Ipc;

namespace Anode.Engine.Midi;

/// <summary>
///     The server's control routing (<see cref="SetControlRoutingMessage" />, docs/09-phase-8.md, "Routing") as the MIDI
///     thread consults it for every message: the control ports (control surfaces' inputs), the matchers whose messages
///     are consumed, and Learn. Immutable; built on the control thread and swapped in whole. <see cref="Decide" /> does
///     not allocate.
/// </summary>
public sealed class ControlRouting(
    IEnumerable<string> controlPorts,
    IEnumerable<EngineControlMatcher> matchers,
    bool learn) {
    readonly HashSet<string> controlPorts =
        new(controlPorts.Where(static p => !string.IsNullOrEmpty(p)), StringComparer.Ordinal);

    readonly EngineControlMatcher[] matchers = [..matchers];

    /// <summary>No control ports, no matchers, not learning: everything plays.</summary>
    public static ControlRouting None { get; } = new([], [], false);

    public bool Learn { get; } = learn;

    public bool HasControlPorts => controlPorts.Count > 0;

    public IReadOnlyCollection<string> ControlPorts => controlPorts;

    public IReadOnlyList<EngineControlMatcher> Matchers => matchers;

    /// <summary>Whether input <paramref name="port" /> belongs to a control surface.</summary>
    public bool IsControlPort(string? port) => port is not null && controlPorts.Contains(port);

    /// <summary>
    ///     What becomes of a message (status, first data byte) from input <paramref name="port" />: whether it goes to
    ///     the server, and whether it still plays the tracks. A control port's messages and matched ones go to the server
    ///     only; while learning every channel message also goes to the server, and plays unless consumed.
    /// </summary>
    public (bool Forward, bool Play) Decide(string? port, byte status, byte data1) {
        if (IsControlPort(port)) {
            return (true, false);
        }

        if (status is < 0x80 or >= 0xF0) {
            return (false, true); // system messages: the hub's other paths (sync) take them
        }

        if (Matches(port, status, data1)) {
            return (true, false);
        }

        return (Learn, true);
    }

    /// <summary>Whether some matcher consumes the channel message.</summary>
    public bool Matches(string? port, byte status, byte data1) {
        foreach (var matcher in matchers) {
            if (Matches(matcher, port, status, data1)) {
                return true;
            }
        }

        return false;
    }

    static bool Matches(EngineControlMatcher matcher, string? port, byte status, byte data1) {
        if (matcher.Port is not null && matcher.Port != port) {
            return false;
        }

        var kind = status & 0xF0;
        if (matcher.Channel >= 0 && matcher.Channel != (status & 0x0F)) {
            return false;
        }

        switch (matcher.Family) {
            case EngineMatcherFamily.ControlChange14:
                return kind == 0xB0 && (data1 == matcher.Data1 || data1 == matcher.Data1 + 32);
            case EngineMatcherFamily.Nrpn:
                return kind == 0xB0 && data1 is 99 or 98 or 6 or 38 or 96 or 97;
            default:
                var sameKind = kind == matcher.Status || (matcher.Status == 0x90 && kind == 0x80);
                return sameKind && (matcher.Data1 < 0 || matcher.Data1 == data1);
        }
    }
}
