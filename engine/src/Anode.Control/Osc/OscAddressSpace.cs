using Anode.Contracts;
using Anode.Control.Surfaces;
using System.Globalization;

namespace Anode.Control.Osc;

/// <summary>What an OSC message asks of Anode.</summary>
public enum OscRequestKind {
    /// <summary>
    ///     Set <c>Target</c> to <c>Value</c>, normalised 0…1 (a track switch: 1 on, 0 off; a function or key command acts
    ///     on 1, and rewind and forward stop on 0), as an assignment's <c>TargetChange</c> would.
    /// </summary>
    Set,

    /// <summary>Set <c>Target</c> (a volume or send) to <c>Value</c> dB (addresses ending <c>/db</c>).</summary>
    SetDb,

    /// <summary>Rename track <c>Target.TrackId</c> to <c>Text</c> (addresses ending <c>/name</c>).</summary>
    SetName,

    /// <summary>Move the playhead to marker <c>Number</c> (1-based, in project order).</summary>
    GoToMarker,

    /// <summary>Move the playhead to <c>Value</c> quarter notes from the project start (<c>/anode/transport/position</c>).</summary>
    Locate,

    /// <summary>Send feedback to the sender's host on UDP port <c>Number</c> (<c>/anode/subscribe port</c>).</summary>
    Subscribe,

    /// <summary>Stop sending feedback to the sender's host on port <c>Number</c> (0: every port of that host).</summary>
    Unsubscribe
}

/// <summary>
///     A resolved OSC request: its kind and what it applies to (a target with its track id resolved from an index or
///     id; the selected track's addresses keep a null track id, which follows the selection).
/// </summary>
public sealed record OscRequest(
    OscRequestKind Kind,
    ControlTarget? Target = null,
    double Value = 0,
    string? Text = null,
    int Number = 0
);

/// <summary>
///     Anode's OSC address space (documented in <c>docs/control/osc.md</c>), as a table of addresses to targets:
///     transport, tracks by index (1-based) and id (volume, pan, mute, solo, arm, sends, names), the selected track,
///     markers, Smart Controls and key commands, under <see cref="Root" />. Values are normalised 0…1 unless the address
///     says otherwise (<c>…/db</c>, <c>…/name</c>). A value is an <c>i</c>, <c>f</c>, <c>T</c> or <c>F</c> argument.
/// </summary>
public static class OscAddressSpace {
    public const string Root = "/anode";

    static readonly Dictionary<string, ControlTransportAction> Transport = new(StringComparer.Ordinal) {
        ["play"] = ControlTransportAction.Play,
        ["stop"] = ControlTransportAction.Stop,
        ["record"] = ControlTransportAction.Record,
        ["cycle"] = ControlTransportAction.Cycle,
        ["rewind"] = ControlTransportAction.Rewind,
        ["forward"] = ControlTransportAction.Forward,
        ["goToStart"] = ControlTransportAction.GoToStart
    };

    /// <summary>
    ///     What <paramref name="message" /> asks, with tracks resolved in <paramref name="view" />; null for an address
    ///     outside the space, a track index or id that does not exist (or no selected track for <c>/anode/selected</c>),
    ///     or arguments of the wrong type.
    /// </summary>
    public static OscRequest? Resolve(OscMessage message, ProjectView view) {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(view);
        if (!message.Address.StartsWith(Root + "/", StringComparison.Ordinal)) {
            return null;
        }

        var path = message.Address[(Root.Length + 1)..].Split('/');
        var args = message.Arguments;
        return path[0] switch {
            "transport" when path.Length == 2 => TransportRequest(path[1], args),
            "track" when path.Length >= 3 => TrackRequest(path, args, view),
            "selected" when path.Length >= 2 => SelectedRequest(path, args, view),
            "marker" when path.Length == 2 => MarkerRequest(path[1], args, view),
            "smart" when path.Length == 2 => SmartRequest(path[1], args, view),
            "key" when path.Length >= 2 && path[1].Length > 0 => Pressed(
                new(ControlTargetKind.KeyCommand, KeyCommandId: string.Join('/', path[1..])),
                args
            ),
            "subscribe" when path.Length == 1 => Port(args) is { } port and > 0
                ? new(OscRequestKind.Subscribe, Number: port)
                : null,
            "unsubscribe" when path.Length == 1 => UnsubscribeRequest(args),
            _ => null
        };
    }

    static OscRequest? TransportRequest(string function, IReadOnlyList<OscArgument> args) {
        if (function == "position") {
            return Number(args) is { } quarters && quarters >= 0 ? new(OscRequestKind.Locate, Value: quarters) : null;
        }

        return Transport.TryGetValue(function, out var action)
            ? Pressed(new(ControlTargetKind.Transport, Transport: action), args)
            : null;
    }

    static OscRequest? TrackRequest(string[] path, IReadOnlyList<OscArgument> args, ProjectView view) {
        if (path[1] == "id") {
            var track = view.Tracks.FirstOrDefault(t => t.Id == path[2]);
            return track is null ? null : TrackLeaf(track, track.Id, path[3..], args);
        }

        return Index(path[1], view.Tracks.Count) is { } index
            ? TrackLeaf(view.Tracks[index], view.Tracks[index].Id, path[2..], args)
            : null;
    }

    static OscRequest? SelectedRequest(string[] path, IReadOnlyList<OscArgument> args, ProjectView view) {
        if (path is [_, "next" or "previous"]) {
            var action = path[1] == "next" ? TrackSwitchAction.SelectNext : TrackSwitchAction.SelectPrevious;
            return Pressed(new(ControlTargetKind.TrackSwitch, Switch: action), args);
        }

        var selected = view.Tracks.FirstOrDefault(static t => t.Selected);
        return selected is null ? null : TrackLeaf(selected, null, path[1..], args);
    }

    static OscRequest? MarkerRequest(string segment, IReadOnlyList<OscArgument> args, ProjectView view) {
        if (segment == "goto") {
            return Number(args) is { } n && n == Math.Floor(n) && n >= 1 && n <= view.Markers.Count
                ? new(OscRequestKind.GoToMarker, Number: (int)n)
                : null;
        }

        return Index(segment, view.Markers.Count) is { } marker && Press(args) is >= 0.5
            ? new(OscRequestKind.GoToMarker, Number: marker + 1)
            : null;
    }

    static OscRequest? SmartRequest(string segment, IReadOnlyList<OscArgument> args, ProjectView view) {
        var smart = Index(segment, view.SmartControls.Count) is { } index
            ? view.SmartControls[index]
            : view.SmartControls.FirstOrDefault(s => s.Address == segment);
        return smart is not null && Unit(args) is { } value
            ? new(OscRequestKind.Set, new(ControlTargetKind.SmartControl, Address: smart.Address), value)
            : null;
    }

    static OscRequest? UnsubscribeRequest(IReadOnlyList<OscArgument> args) {
        if (args.Count == 0) {
            return new(OscRequestKind.Unsubscribe);
        }

        return Port(args) is { } port ? new(OscRequestKind.Unsubscribe, Number: port) : null;
    }

    /// <summary>A press of <paramref name="target" />: 1 without an argument, else the argument (0…1).</summary>
    static OscRequest? Pressed(ControlTarget target, IReadOnlyList<OscArgument> args) =>
        Press(args) is { } value ? new(OscRequestKind.Set, target, value) : null;

    /// <summary>
    ///     Every feedback message for <paramref name="view" />: each address a client can subscribe to with its current
    ///     value (switches as <c>i</c> 0 or 1, values as <c>f</c>, text as <c>s</c>; the list is in
    ///     <c>docs/control/osc.md</c>). The server sends those whose value changed, at most 30 a second per address.
    /// </summary>
    public static IReadOnlyList<OscMessage> Feedback(ProjectView view) {
        ArgumentNullException.ThrowIfNull(view);
        var output = new List<OscMessage>();
        void Add(string address, OscArgument argument) => output.Add(new(Root + address, [argument]));

        Add("/transport/play", Switch(view.Transport != TransportState.Stopped));
        Add("/transport/stop", Switch(view.Transport == TransportState.Stopped));
        Add("/transport/record", Switch(view.Transport == TransportState.Recording));
        Add("/transport/cycle", Switch(view.Cycle));
        Add("/transport/tempo", new OscFloat((float)view.Tempo));
        var b = view.Beats;
        Add("/transport/beats", new OscString(Invariant($"{b.Bar}.{b.Beat}.{b.Division}.{b.Ticks}")));
        var tc = view.Timecode;
        Add(
            "/transport/timecode",
            new OscString(Invariant($"{tc.Hours:D2}:{tc.Minutes:D2}:{tc.Seconds:D2}:{tc.Frames:D2}"))
        );
        Add("/tracks", new OscInt(view.Tracks.Count));
        var selected = 0;
        for (var i = 0; i < view.Tracks.Count; i++) {
            var track = view.Tracks[i];
            TrackFeedback(Invariant($"/track/{i + 1}"), track, true, Add);
            if (track.Selected && selected == 0) {
                selected = i + 1;
            }
        }

        Add("/selected/index", new OscInt(selected));
        if (selected > 0) {
            TrackFeedback("/selected", view.Tracks[selected - 1], false, Add);
        }

        Add("/markers", new OscInt(view.Markers.Count));
        for (var i = 0; i < view.Markers.Count; i++) {
            Add(Invariant($"/marker/{i + 1}/name"), new OscString(view.Markers[i].Name));
        }

        foreach (var smart in view.SmartControls) {
            Add($"/smart/{smart.Address}", new OscFloat((float)smart.Value));
            Add($"/smart/{smart.Address}/name", new OscString(smart.Name));
            Add($"/smart/{smart.Address}/display", new OscString(smart.Display));
        }

        return output;
    }

    static void TrackFeedback(string prefix, TrackView track, bool sends, Action<string, OscArgument> add) {
        add(prefix + "/name", new OscString(track.Name));
        add(prefix + "/volume", new OscFloat((float)track.Fader));
        add(prefix + "/volume/db", new OscFloat((float)track.VolumeDb));
        add(prefix + "/pan", new OscFloat((float)((track.Pan + 1) / 2)));
        add(prefix + "/mute", Switch(track.Mute));
        add(prefix + "/solo", Switch(track.Solo));
        add(prefix + "/arm", Switch(track.RecordArm));
        if (!sends) {
            return;
        }

        for (var k = 0; k < track.Sends.Count; k++) {
            add(Invariant($"{prefix}/send/{k + 1}"), new OscFloat((float)track.Sends[k].Value));
            add(Invariant($"{prefix}/send/{k + 1}/name"), new OscString(track.Sends[k].Name));
        }
    }

    static OscRequest? TrackLeaf(TrackView track, string? trackId, string[] leaf, IReadOnlyList<OscArgument> args) {
        if (leaf is ["name"]) {
            return args is [OscString name]
                ? new(OscRequestKind.SetName, new(ControlTargetKind.Parameter, trackId, "name"), Text: name.Value)
                : null;
        }

        var (kind, target) = LeafTarget(track, trackId, leaf);
        if (target is null) {
            return null;
        }

        var value = kind == OscRequestKind.SetDb ? Number(args) : Unit(args);
        return value is { } v ? new(kind, target, v) : null;
    }

    static (OscRequestKind Kind, ControlTarget? Target) LeafTarget(TrackView track, string? trackId, string[] leaf) {
        return leaf switch {
            ["volume"] => (OscRequestKind.Set, Parameter("volume")),
            ["volume", "db"] => (OscRequestKind.SetDb, Parameter("volume")),
            ["pan"] => (OscRequestKind.Set, Parameter("pan")),
            ["mute"] => (OscRequestKind.Set, Switch(TrackSwitchAction.Mute)),
            ["solo"] => (OscRequestKind.Set, Switch(TrackSwitchAction.Solo)),
            ["arm"] => (OscRequestKind.Set, Switch(TrackSwitchAction.RecordArm)),
            ["send", var k] when Send(k) is { } send => (OscRequestKind.Set, Parameter(send)),
            ["send", var k, "db"] when Send(k) is { } send => (OscRequestKind.SetDb, Parameter(send)),
            _ => (OscRequestKind.Set, null)
        };
        ControlTarget Parameter(string address) => new(ControlTargetKind.Parameter, trackId, address);
        ControlTarget Switch(TrackSwitchAction action) => new(ControlTargetKind.TrackSwitch, trackId, Switch: action);
        string? Send(string k) => Index(k, track.Sends.Count) is { } i ? track.Sends[i].Address : null;
    }

    /// <summary>A 1-based index segment as a 0-based index below <paramref name="count" />.</summary>
    static int? Index(string segment, int count) =>
        int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= count
            ? n - 1
            : null;

    /// <summary>The one numeric argument (<c>i</c>, <c>f</c>, <c>T</c>, <c>F</c>), finite.</summary>
    static double? Number(IReadOnlyList<OscArgument> args) {
        double? value = args switch {
            [OscInt i] => i.Value,
            [OscFloat f] => f.Value,
            [OscBool b] => b.Value ? 1 : 0,
            _ => null
        };
        return value is { } v && double.IsFinite(v) ? v : null;
    }

    /// <summary>The numeric argument clamped to 0…1.</summary>
    static double? Unit(IReadOnlyList<OscArgument> args) => Number(args) is { } v ? Math.Clamp(v, 0, 1) : null;

    /// <summary>A press: no argument is 1, else the numeric argument clamped to 0…1.</summary>
    static double? Press(IReadOnlyList<OscArgument> args) => args.Count == 0 ? 1 : Unit(args);

    static int? Port(IReadOnlyList<OscArgument> args) =>
        Number(args) is { } v && v == Math.Floor(v) && v is >= 0 and <= 65535 ? (int)v : null;

    static OscInt Switch(bool on) => new(on ? 1 : 0);

    static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
