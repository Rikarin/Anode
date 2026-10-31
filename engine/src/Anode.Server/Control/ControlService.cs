using Anode.Contracts;
using Anode.Control.Assignments;
using Anode.Control.Surfaces;
using Anode.Ipc;
using Anode.Model;
using Anode.Server.Engine;
using Anode.Server.Library;
using Anode.Server.Projects;
using Anode.Server.Realtime;

namespace Anode.Server.Control;

/// <summary>
///     Hardware control (docs/09-phase-8.md, workstream B): the controller assignments, control surfaces and OSC server
///     (user settings), Learn, and the editor's selected track. It keeps the host's control routing in step
///     (<see cref="SetControlRoutingMessage" /> on every change and on each host connection), runs the host's
///     <see cref="ControlMidiMessage" />s through the assignment engine and the surfaces' protocols
///     (<see cref="IControlProtocols" />, workstream D's classes), and carries out what they ask as the editor would:
///     parameter moves as the same commands (coalesced per gesture, recorded as automation by the session), track
///     switches, transport calls and key commands. Every <see cref="TickInterval" /> it brings the surfaces and OSC
///     clients up to date from a <see cref="ProjectView" />. All control state lives under one lock, so the protocol
///     objects are used from one thread at a time.
/// </summary>
public sealed partial class ControlService(
    ProjectSession session,
    EngineConnection engine,
    EngineCatalog catalog,
    UserSettings settings,
    IControlProtocols protocols,
    EventHub hub,
    TimeProvider time,
    ILogger<ControlService> logger
) : BackgroundService {
    /// <summary>
    ///     How often the service ticks: feedback (faders, LEDs, OSC values) at most 30 times a second, meters and polls
    ///     at most 10 (<see cref="MinFeedbackInterval" />, <see cref="MinMeterInterval" />).
    /// </summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(34);

    /// <summary>A gesture ends this long after its control's last move (unless the control is touched).</summary>
    public static readonly TimeSpan GestureTimeout = TimeSpan.FromMilliseconds(500);

    readonly Lock gate = new();
    readonly List<SurfaceRuntime> surfaces = [];
    readonly List<GroupRuntime> groups = [];
    ControllerAssignments assignments = ControllerAssignments.Empty;
    IAssignmentRuntime? runtime;
    IAssignmentRuntime? learner;
    LearnState learn = new(false, null, null, null, null);
    string? learned;
    (long Session, string? TrackId) selection;
    long seenSession = -1;
    IReadOnlyList<SurfaceStatus> published = [];

    /// <summary>The latest meters by track id (post-fader peaks), from the host's telemetry.</summary>
    Dictionary<string, double> meters = new(StringComparer.Ordinal);

    public override async Task StartAsync(CancellationToken cancellationToken) {
        lock (gate) {
            assignments = Load(settings.ControllerAssignments());
            Rebuild();
            foreach (var surface in settings.ControlSurfaces().OrderBy(static s => s.Order)) {
                surfaces.Add(Start(surface));
            }

            Regroup();
            StartOsc(settings.OscSettings());
        }

        engine.HostConnected += OnHostConnected;
        engine.ControlMidi += OnControlMidi;
        engine.MetersRead += OnMeters;
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken) {
        engine.HostConnected -= OnHostConnected;
        engine.ControlMidi -= OnControlMidi;
        engine.MetersRead -= OnMeters;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        lock (gate) {
            StopOsc();
        }
    }

    public override void Dispose() {
        lock (gate) {
            StopOsc();
        }

        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(TickInterval, time);
        try {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                lock (gate) {
                    try {
                        Tick();
                    } catch (Exception e) when (e is not OutOfMemoryException) {
                        ControlLog.TickFailed(logger, e);
                    }
                }
            }
        } catch (OperationCanceledException) {
            // stopping
        }
    }

    /// <summary>Stored assignments that no longer check (a newer Anode's, damaged) are dropped, logged.</summary>
    ControllerAssignments Load(ControllerAssignments stored) {
        try {
            return ControlValidation.Check(stored);
        } catch (ProjectException e) {
            ControlLog.SettingsDropped(logger, "controller assignments", e.Message);
            return ControllerAssignments.Empty;
        }
    }

    // ---- Assignments ----

    public ControllerAssignments Assignments() {
        lock (gate) {
            return assignments;
        }
    }

    /// <summary>Replaces every assignment (checked: 422), saves them and gives the host the new matcher table.</summary>
    public ControllerAssignments SetAssignments(ControllerAssignments? replacement) {
        var checkedAssignments = ControlValidation.Check(replacement);
        lock (gate) {
            if (Engine(checkedAssignments) is var (made, error) && error is not null) {
                throw ProjectException.Unprocessable(error);
            }

            Replace(checkedAssignments, made);
            return assignments;
        }
    }

    /// <summary>
    ///     An engine for <paramref name="next" />, or null without assignments; the error when the engine refuses them
    ///     (or the protocols are not available yet).
    /// </summary>
    (IAssignmentRuntime? Runtime, string? Error) Engine(ControllerAssignments next) {
        if (!next.Zones.Any(static z => z.Modes.Any(static m => m.Assignments.Count > 0))) {
            return (null, null);
        }

        try {
            return (protocols.Assignments(next), null);
        } catch (ArgumentException e) {
            return (null, e.Message);
        } catch (NotImplementedException e) {
            return (null, $"Controller assignments are not available yet: {e.Message}.");
        }
    }

    /// <summary>Keeps <paramref name="next" /> (saved) with its engine, and gives the host the new table.</summary>
    void Replace(ControllerAssignments next, IAssignmentRuntime? made) {
        settings.SaveControllerAssignments(next);
        assignments = next;
        runtime = made;
        PushRouting();
    }

    /// <summary>The engine for the stored assignments (none when it cannot be made: logged).</summary>
    void Rebuild() {
        var (made, error) = Engine(assignments);
        if (error is not null) {
            ControlLog.SettingsDropped(logger, "controller assignments", error);
        }

        runtime = made;
    }

    // ---- Learn ----

    public LearnState Learn() {
        lock (gate) {
            return learn;
        }
    }

    /// <summary>Turns Learn on, or changes its target (checked: a target's fields, its zone and mode; 422).</summary>
    public LearnState StartLearn(LearnRequest? request) {
        request ??= new();
        if (request.Target is { } target) {
            ControlValidation.Check(target);
        }

        lock (gate) {
            CheckLearn(request, ControlValidation.Modes(assignments));
            if (learner is null) {
                try {
                    learner = protocols.Assignments(ControllerAssignments.Empty);
                } catch (NotImplementedException e) {
                    throw ProjectException.Unprocessable($"Learn is not available yet: {e.Message}.");
                }
            }

            var wasOn = learn.On;
            if (!wasOn
                || learn.Target != request.Target
                || learn.ZoneId != request.ZoneId
                || learn.ModeId != request.ModeId) {
                learned = null;
            }

            learn = new(true, request.Target, request.ZoneId, request.ModeId, wasOn ? learn.Last : null);
            if (!wasOn) {
                learner.Reset();
                PushRouting();
            }

            return learn;
        }
    }

    /// <summary>A Learn request's zone and mode (and a zoneMode target's), against the zones there are.</summary>
    static void CheckLearn(LearnRequest request, Dictionary<string, HashSet<string>> modes) {
        if (request.Target is { Kind: ControlTargetKind.ZoneMode } zoneMode) {
            ControlValidation.CheckMode(zoneMode.ZoneId, zoneMode.ModeId, modes);
        }

        if (request.ZoneId is null) {
            if (request.ModeId is not null) {
                throw ProjectException.Unprocessable("Name the zone of the mode to learn into.");
            }

            return;
        }

        if (!modes.TryGetValue(request.ZoneId, out var mine)) {
            throw ProjectException.Unprocessable($"There is no zone '{request.ZoneId}'.");
        }

        if (request.ModeId is not null && !mine.Contains(request.ModeId)) {
            throw ProjectException.Unprocessable($"There is no mode '{request.ModeId}' in zone '{request.ZoneId}'.");
        }
    }

    public LearnState StopLearn() {
        lock (gate) {
            if (learn.On) {
                learn = learn with { On = false };
                learned = null;
                PushRouting();
            }

            return learn;
        }
    }

    /// <summary>A message while learning: offered as the last one and, with a target, assigned to it.</summary>
    void Learn(EngineControlEvent e) {
        if (learner?.Learn(e.Port, e.Data) is not { } input) {
            return;
        }

        var message = new ControlMessage(
            e.Port,
            e.Data.Length > 0 ? e.Data[0] : 0,
            e.Data.Length > 1 ? e.Data[1] : 0,
            e.Data.Length > 2 ? e.Data[2] : 0,
            input
        );
        learn = learn with { Last = message };
        string? assignmentId = null;
        if (learn.Target is { } target) {
            try {
                assignmentId = Assign(target, input);
            } catch (ProjectException ex) {
                ControlLog.LearnFailed(logger, ex.Message);
            }
        }

        hub.Publish(new ControlLearnEvent(message, assignmentId));
    }

    /// <summary>
    ///     Assigns <paramref name="input" /> to <paramref name="target" /> in Learn's zone and mode through
    ///     <see cref="AssignmentEngine.Assign" /> (workstream D: the first zone's active mode without one, made when there
    ///     is none; an assignment of the same input there, or the 7-bit halves a 14-bit or NRPN input superseded, is
    ///     replaced; toggle for switches, absolute otherwise). The assignment this Learn made before for the same target
    ///     gives way to the new one, so moving another control re-learns rather than adding a second assignment.
    /// </summary>
    string Assign(ControlTarget target, ControlInput input) {
        var current = assignments;
        if (learned is not null) {
            var mine = current.Zones.SelectMany(static z => z.Modes)
                .SelectMany(static m => m.Assignments)
                .FirstOrDefault(a => a.Id == learned);
            if (mine is not null && mine.Input == input && mine.Target == target) {
                return mine.Id; // the same control moving on
            }

            current = new(
                [
                    ..current.Zones.Select(z => z with {
                            Modes = [
                                ..z.Modes.Select(m => m with {
                                        Assignments = [..m.Assignments.Where(a => a.Id != learned)]
                                    }
                                )
                            ]
                        }
                    )
                ]
            );
        }

        ControllerAssignments next;
        ControlAssignment made;
        try {
            (next, made) = AssignmentEngine.Assign(
                current,
                input,
                target,
                learn.ZoneId,
                learn.ModeId,
                static () => RandomIds.Instance.Next("ctl")
            );
        } catch (ArgumentException e) {
            throw ProjectException.Unprocessable(e.Message);
        }

        if (learned is not null) {
            // Re-learnt, the assignment keeps its id.
            var keep = learned;
            var fresh = made.Id;
            next = new(
                [
                    ..next.Zones.Select(z => z with {
                            Modes = [
                                ..z.Modes.Select(m => m with {
                                        Assignments =
                                            [..m.Assignments.Select(a => a.Id == fresh ? a with { Id = keep } : a)]
                                    }
                                )
                            ]
                        }
                    )
                ]
            );
            made = made with { Id = keep };
        }

        next = ControlValidation.Check(next);
        var (engineMade, error) = Engine(next);
        if (error is not null) {
            throw ProjectException.Unprocessable(error);
        }

        Replace(next, engineMade);
        learned = made.Id;
        return made.Id;
    }

    // ---- Selection ----

    /// <summary>The editor's selected track (not part of the project): 404 without a project or for an unknown track.</summary>
    public void SetSelection(SelectionRequest? request) {
        var view = session.View ?? throw ProjectException.NoProject();
        if (request?.TrackId is { } id && view.State.FindTrack(id) is null) {
            throw ProjectException.NotFound($"No track '{id}'.");
        }

        lock (gate) {
            Select(view.Session, request?.TrackId, false);
        }
    }

    /// <summary>
    ///     Selects a track (null none); when hardware or OSC did it (<paramref name="publish" />), the editor hears of it
    ///     (<see cref="SelectionEvent" />) and follows.
    /// </summary>
    void Select(long sessionId, string? trackId, bool publish = true) {
        if (selection == (sessionId, trackId)) {
            return;
        }

        selection = (sessionId, trackId);
        ResetEngines();
        if (publish) {
            hub.Publish(new SelectionEvent(trackId));
        }
    }

    /// <summary>The selected track of the open project, if it is still there.</summary>
    string? Selected(OpenProjectView? view) =>
        view is not null
        && selection.Session == view.Session
        && selection.TrackId is { } id
        && view.State.FindTrack(id) is not null
            ? id
            : null;

    void ResetEngines() {
        try {
            runtime?.Reset();
        } catch (NotImplementedException) {
            // the stubs, until workstream D
        }
    }

    // ---- Routing ----

    /// <summary>Gives the host the control routing: the surfaces' input ports, the assignments' matchers, Learn.</summary>
    void PushRouting() => engine.Post(Routing());

    SetControlRoutingMessage Routing() =>
        new(
            [..surfaces.Select(static s => s.Config.InputPort).Distinct(StringComparer.Ordinal)],
            [..(runtime?.Matchers ?? []).Select(static m => Matcher(m))],
            learn.On
        );

    static EngineControlMatcher Matcher(ControlMatcher m) =>
        new(
            m.Port,
            m.Status,
            m.Channel,
            m.Data1,
            m.Family switch {
                MatcherFamily.ControlChange14 => EngineMatcherFamily.ControlChange14,
                MatcherFamily.Nrpn => EngineMatcherFamily.Nrpn,
                _ => EngineMatcherFamily.Single
            }
        );

    /// <summary>A host connected: it gets the routing, and the surfaces everything again.</summary>
    void OnHostConnected() {
        lock (gate) {
            try {
                PushRouting();
                foreach (var surface in surfaces) {
                    surface.Invalidate(logger);
                }
            } catch (Exception e) when (e is not OutOfMemoryException) {
                ControlLog.TickFailed(logger, e);
            }
        }
    }

    void OnMeters(MetersEvent e) {
        var latest = new Dictionary<string, double>(e.Tracks.Count, StringComparer.Ordinal);
        foreach (var meter in e.Tracks) {
            latest[meter.TrackId] = Math.Max(meter.PeakLeft, meter.PeakRight);
        }

        Volatile.Write(ref meters, latest);
    }

    // ---- Control MIDI ----

    /// <summary>The host's consumed messages, in order: a surface's, Learn's, or the assignments'.</summary>
    void OnControlMidi(ControlMidiMessage message) {
        lock (gate) {
            foreach (var e in message.Events) {
                if (e?.Port is null || e.Data is not { Length: > 0 }) {
                    continue;
                }

                try {
                    Receive(e);
                } catch (Exception ex) when (ex is not OutOfMemoryException) {
                    ControlLog.MessageFailed(logger, e.Port, ex);
                }
            }
        }
    }

    void Receive(EngineControlEvent e) {
        var surfaced = false;
        foreach (var surface in surfaces.Where(s => s.Config.InputPort == e.Port).ToArray()) {
            surfaced = true;
            if (surface.Group is not { } group) {
                continue;
            }

            foreach (var action in group.Decode(surface, e.Data, e.TimeNs, logger)) {
                Act(surface, action);
            }

            // Handshake and ping replies go out at once (docs/control/README.md, "Input").
            SendAll(group, group.Poll(NowNs, logger));
        }

        if (surfaced) {
            return;
        }

        if (learn.On) {
            Learn(e);
            return;
        }

        if (runtime is null) {
            return;
        }

        var view = session.View;
        var before = assignments;
        foreach (var change in runtime.Receive(e.Port, e.Data, new TargetValues(this, view))) {
            Apply(change, view);
            view = session.View;
        }

        // A zoneMode target switched a mode: the active modes are kept with the assignments.
        var after = runtime.Assignments;
        if (!ReferenceEquals(after, before)
            && !after.Zones.Select(static z => z.ActiveModeId)
                .SequenceEqual(before.Zones.Select(static z => z.ActiveModeId))) {
            assignments = after;
            settings.SaveControllerAssignments(after);
        }
    }

    // ---- Surfaces ----

    public IReadOnlyList<ControlSurface> Surfaces() {
        lock (gate) {
            return [..surfaces.Select(static s => s.Config)];
        }
    }

    public IReadOnlyList<SurfaceStatus> SurfaceStatuses() {
        lock (gate) {
            return Statuses();
        }
    }

    IReadOnlyList<SurfaceStatus> Statuses() => [..surfaces.Select(static s => s.Status())];

    /// <summary>Adds a surface after the others (422 for an unknown kind, a missing port or one another surface uses).</summary>
    public ControlSurface AddSurface(AddSurfaceRequest? request) {
        if (request is null || !Enum.IsDefined(request.Kind)) {
            throw ProjectException.Unprocessable("Name a control surface kind.");
        }

        if (!ControlValidation.IsPort(request.InputPort) || !ControlValidation.IsPort(request.OutputPort)) {
            throw ProjectException.Unprocessable("A control surface has an input port and an output port.");
        }

        lock (gate) {
            if (surfaces.Count >= ControlValidation.MaxSurfaces) {
                throw ProjectException.Unprocessable($"Anode works at most {ControlValidation.MaxSurfaces} surfaces.");
            }

            var used = surfaces.SelectMany(static s => new[] { s.Config.InputPort, s.Config.OutputPort }).ToHashSet();
            if (used.Contains(request.InputPort) || used.Contains(request.OutputPort)) {
                throw ProjectException.Unprocessable("Another control surface uses that port.");
            }

            var surface = new ControlSurface(
                RandomIds.Instance.Next("srf"),
                request.Kind,
                request.InputPort,
                request.OutputPort,
                surfaces.Count
            );
            surfaces.Add(Start(surface));
            SurfacesChanged();
            return surface;
        }
    }

    /// <summary>Removes a surface; false when there is none.</summary>
    public bool RemoveSurface(string id) {
        lock (gate) {
            var index = surfaces.FindIndex(s => s.Config.Id == id);
            if (index < 0) {
                return false;
            }

            surfaces.RemoveAt(index);
            Renumber();
            SurfacesChanged();
            return true;
        }
    }

    /// <summary>Puts the surfaces in a new order (every id once: 422 otherwise).</summary>
    public IReadOnlyList<ControlSurface> ReorderSurfaces(ReorderSurfacesRequest? request) {
        lock (gate) {
            var ids = request?.Ids ?? [];
            if (ids.Count != surfaces.Count
                || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count
                || ids.Any(id => surfaces.TrueForAll(s => s.Config.Id != id))) {
                throw ProjectException.Unprocessable("Name every control surface once, in the new order.");
            }

            var ordered = ids.Select(id => surfaces.Single(s => s.Config.Id == id)).ToList();
            surfaces.Clear();
            surfaces.AddRange(ordered);
            Renumber();
            SurfacesChanged();
            return [..surfaces.Select(static s => s.Config)];
        }
    }

    void Renumber() {
        for (var i = 0; i < surfaces.Count; i++) {
            surfaces[i].Config = surfaces[i].Config with { Order = i };
        }
    }

    void SurfacesChanged() {
        settings.SaveControlSurfaces([..surfaces.Select(static s => s.Config)]);
        Regroup();
        PushRouting();
        PublishStatuses();
    }

    SurfaceRuntime Start(ControlSurface config) {
        IControlSurface? protocol = null;
        try {
            protocol = protocols.Surface(config.Kind);
        } catch (Exception e) when (e is NotImplementedException or ArgumentException) {
            ControlLog.SurfaceFailed(logger, config.Id, e.Message);
        }

        return new(config, protocol);
    }

    /// <summary>
    ///     Joins the surfaces into consoles (<see cref="SurfaceGroup" />, docs/control/README.md, "Grouping"), left to
    ///     right in <c>order</c>: a Mackie Control XT joins the Mackie Control (or XT) before it, so extenders follow the
    ///     main unit's bank; anything else starts a group of its own (a HUI is always alone). A group keeps the bank its
    ///     first unit showed.
    /// </summary>
    void Regroup() {
        groups.Clear();
        List<SurfaceRuntime>? current = null;
        foreach (var surface in surfaces) {
            surface.Group = null;
            if (surface.Protocol is null) {
                continue;
            }

            var joins = surface.Config.Kind == ControlSurfaceKind.MackieControlXt
                && current is [
                    .., { Config.Kind: ControlSurfaceKind.MackieControl or ControlSurfaceKind.MackieControlXt }
                ];
            if (!joins || current is null) {
                current = [];
                groups.Add(new(current));
            }

            current.Add(surface);
        }

        foreach (var group in groups) {
            group.Build(logger);
        }
    }

    void PublishStatuses() {
        var now = Statuses();
        if (!now.SequenceEqual(published)) {
            published = now;
            hub.Publish(new SurfacesEvent(now));
        }
    }

    /// <summary>Sends each unit of a group its messages (<see cref="SurfaceGroup" /> answers per unit, in unit order).</summary>
    void SendAll(GroupRuntime group, IReadOnlyList<IReadOnlyList<byte[]>> perUnit) {
        for (var i = 0; i < perUnit.Count && i < group.Members.Count; i++) {
            Send(group.Members[i], perUnit[i]);
        }
    }

    /// <summary>Sends a surface's messages to its output port, as one message.</summary>
    void Send(SurfaceRuntime surface, IReadOnlyList<byte[]> messages) {
        if (messages.Count == 0) {
            return;
        }

        var data = messages.Count == 1 ? messages[0] : [..messages.SelectMany(static m => m)];
        engine.Post(new SendMidiMessage(surface.Config.OutputPort, data));
    }

    /// <summary>A surface: its settings, its protocol object (null when it could not be made), its group and Zoom.</summary>
    sealed class SurfaceRuntime(ControlSurface config, IControlSurface? protocol) {
        public ControlSurface Config { get; set; } = config;

        public IControlSurface? Protocol { get; } = protocol;

        /// <summary>The console it is part of; null when its protocol could not be made.</summary>
        public GroupRuntime? Group { get; set; }

        public bool Zoom { get; set; }

        public SurfaceStatus Status() =>
            Group is { Failed: false } && Protocol is { } p
                ? new(Config.Id, p.Connected, p.BankOffset)
                : new(Config.Id, false, 0);

        public void Invalidate(ILogger logger) => Group?.Use(_ => Protocol?.Invalidate(), Config.Id, logger);
    }

    /// <summary>
    ///     A <see cref="SurfaceGroup" /> and its surfaces, unit for unit (<c>Members[i]</c> is unit i). A protocol that
    ///     throws stops the whole group (logged once).
    /// </summary>
    sealed class GroupRuntime(List<SurfaceRuntime> members) {
        SurfaceGroup? group;

        public List<SurfaceRuntime> Members => members;

        public bool Failed { get; private set; }

        /// <summary>Makes the group (it takes over its units' bank offsets, from the first unit's).</summary>
        public void Build(ILogger logger) {
            foreach (var member in members) {
                member.Group = this;
            }

            group = null;
            Use(static _ => { }, members[0].Config.Id, logger);
        }

        public IReadOnlyList<SurfaceAction> Decode(SurfaceRuntime surface, byte[] data, long timeNs, ILogger logger) {
            var unit = members.IndexOf(surface);
            return Call(g => g.Decode(unit, data, timeNs), [], surface.Config.Id, logger);
        }

        public IReadOnlyList<IReadOnlyList<byte[]>> Encode(ProjectView view, ILogger logger) =>
            Call(g => g.Encode(view), [], members[0].Config.Id, logger);

        public IReadOnlyList<IReadOnlyList<byte[]>> EncodeMeters(ProjectView view, ILogger logger) =>
            Call(g => g.EncodeMeters(view), [], members[0].Config.Id, logger);

        public IReadOnlyList<IReadOnlyList<byte[]>> Poll(long timeNs, ILogger logger) =>
            Call(g => g.Poll(timeNs), [], members[0].Config.Id, logger);

        public void Use(Action<SurfaceGroup> call, string id, ILogger logger) =>
            Call(
                g => {
                    call(g);
                    return 0;
                },
                0,
                id,
                logger
            );

        /// <summary>Runs <paramref name="call" /> on the group (made on first use); a protocol that throws stops it.</summary>
        T Call<T>(Func<SurfaceGroup, T> call, T fallback, string id, ILogger logger) {
            if (Failed) {
                return fallback;
            }

            try {
                group ??= new([..members.Select(static m => m.Protocol!)]);
                return call(group);
            } catch (Exception e) when (e is NotImplementedException
                                            or InvalidOperationException
                                            or ArgumentException
                                            or IndexOutOfRangeException) {
                Failed = true;
                ControlLog.SurfaceFailed(logger, id, e.Message);
                return fallback;
            }
        }
    }
}

static partial class ControlLog {
    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped the stored {What}: {Message}")]
    public static partial void SettingsDropped(ILogger logger, string what, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Control feedback failed")]
    public static partial void TickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A control message from {Port} failed")]
    public static partial void MessageFailed(ILogger logger, string port, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Learn did not assign: {Message}")]
    public static partial void LearnFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Control surface {Id} stopped: {Message}")]
    public static partial void SurfaceFailed(ILogger logger, string id, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "A control move did not apply: {Message}")]
    public static partial void MoveIgnored(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Key command {Id} is the editor's: relayed")]
    public static partial void KeyCommandRelayed(ILogger logger, string id);
}
