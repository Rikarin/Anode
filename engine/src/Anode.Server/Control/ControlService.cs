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

            Rebank();
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
    ///     Assigns <paramref name="input" /> to <paramref name="target" /> in Learn's mode (the first zone's active mode
    ///     without one; a zone and mode are made when there is none): the assignment this Learn made for the target
    ///     takes the new input, else a new one; another assignment of the same input there is replaced.
    /// </summary>
    string Assign(ControlTarget target, ControlInput input) {
        var zones = assignments.Zones.ToList();
        if (zones.Count == 0) {
            var modeId = RandomIds.Instance.Next("ctm");
            zones.Add(new(RandomIds.Instance.Next("ctz"), "Zone 1", [new(modeId, "Mode 1", [])], modeId));
        }

        var zoneIndex = learn.ZoneId is { } zoneId ? zones.FindIndex(z => z.Id == zoneId) : 0;
        var zone = zones[zoneIndex];
        var modeIndex = zone.Modes.ToList().FindIndex(m => m.Id == (learn.ModeId ?? zone.ActiveModeId));
        var mode = zone.Modes[modeIndex];
        var list = mode.Assignments.Where(a => a.Id == learned || a.Input != input).ToList();
        var index = learned is null ? -1 : list.FindIndex(a => a.Id == learned);
        string id;
        if (index >= 0) {
            list[index] = list[index] with { Input = input, Mode = DefaultMode(target, input) };
            id = list[index].Id;
        } else {
            id = RandomIds.Instance.Next("cta");
            list.Add(new(id, null, input, target, DefaultMode(target, input), 0, 1, false));
        }

        var modesList = zone.Modes.ToList();
        modesList[modeIndex] = mode with { Assignments = list };
        zones[zoneIndex] = zone with { Modes = modesList };
        var next = ControlValidation.Check(new ControllerAssignments(zones));
        var (made, error) = Engine(next);
        if (error is not null) {
            throw ProjectException.Unprocessable(error);
        }

        Replace(next, made);
        learned = id;
        return id;
    }

    /// <summary>
    ///     A learnt assignment's value mode: a button (a note or program change) toggles a switch and presses a function;
    ///     a continuous control is absolute.
    /// </summary>
    static ControlValueMode DefaultMode(ControlTarget target, ControlInput input) {
        if (input.Kind is not (ControlInputKind.Note or ControlInputKind.ProgramChange)) {
            return ControlValueMode.Absolute;
        }

        return target switch {
            {
                    Kind: ControlTargetKind.TrackSwitch,
                    Switch: TrackSwitchAction.SelectNext or TrackSwitchAction.SelectPrevious
                }
                => ControlValueMode.Momentary,
            { Kind: ControlTargetKind.TrackSwitch or ControlTargetKind.Parameter or ControlTargetKind.SmartControl } =>
                ControlValueMode.Toggle,
            _ => ControlValueMode.Momentary
        };
    }

    // ---- Selection ----

    /// <summary>The editor's selected track (not part of the project): 404 without a project or for an unknown track.</summary>
    public void SetSelection(SelectionRequest? request) {
        var view = session.View ?? throw ProjectException.NoProject();
        if (request?.TrackId is { } id && view.State.FindTrack(id) is null) {
            throw ProjectException.NotFound($"No track '{id}'.");
        }

        lock (gate) {
            Select(view.Session, request?.TrackId);
        }
    }

    void Select(long sessionId, string? trackId) {
        if (selection == (sessionId, trackId)) {
            return;
        }

        selection = (sessionId, trackId);
        ResetEngines();
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
            foreach (var action in surface.Decode(e.Data, e.TimeNs, logger)) {
                Act(surface, action);
            }
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
        Rebank();
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
    ///     Extenders follow the surface before them (docs/09-phase-8.md, "Surfaces"): each shows the strips after its
    ///     neighbour's.
    /// </summary>
    void Rebank() {
        for (var i = 1; i < surfaces.Count; i++) {
            if (surfaces[i].Config.Kind == ControlSurfaceKind.MackieControlXt) {
                surfaces[i].SetBank(surfaces[i - 1].Bank + surfaces[i - 1].Strips, logger);
            }
        }
    }

    /// <summary>
    ///     A surface banked to <paramref name="offset" />: the first surface of its chain (it and the extenders after it)
    ///     moves so that this one shows <paramref name="offset" />, and the extenders follow.
    /// </summary>
    void Bank(SurfaceRuntime surface, int offset) {
        var index = surfaces.IndexOf(surface);
        var head = index;
        var before = 0;
        while (head > 0 && surfaces[head].Config.Kind == ControlSurfaceKind.MackieControlXt) {
            head--;
            before += surfaces[head].Strips;
        }

        var tracks = session.View?.State.Tracks.Count ?? 0;
        surfaces[head].SetBank(Math.Clamp(offset - before, 0, Math.Max(0, tracks - 1)), logger);
        Rebank();
        PublishStatuses();
    }

    void PublishStatuses() {
        var now = Statuses();
        if (!now.SequenceEqual(published)) {
            published = now;
            hub.Publish(new SurfacesEvent(now));
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

    /// <summary>A surface: its settings, its protocol object (null when it could not be made) and Zoom.</summary>
    sealed class SurfaceRuntime(ControlSurface config, IControlSurface? protocol) {
        bool failed;

        public ControlSurface Config { get; set; } = config;

        public bool Zoom { get; set; }

        public int Bank => Use(static p => p.BankOffset, 0);

        public int Strips => Use(static p => p.Strips, 8);

        public SurfaceStatus Status() =>
            new(Config.Id, Use(static p => p.Connected, false), Use(static p => p.BankOffset, 0));

        public void SetBank(int offset, ILogger logger) =>
            Use(
                p => {
                    p.BankOffset = offset;
                    return 0;
                },
                0,
                logger
            );

        public void Invalidate(ILogger logger) =>
            Use(
                static p => {
                    p.Invalidate();
                    return 0;
                },
                0,
                logger
            );

        public IReadOnlyList<SurfaceAction> Decode(byte[] data, long timeNs, ILogger logger) =>
            Use(p => p.Decode(data, timeNs), [], logger);

        public IReadOnlyList<byte[]> Encode(ProjectView view, ILogger logger) => Use(p => p.Encode(view), [], logger);

        public IReadOnlyList<byte[]> EncodeMeters(ProjectView view, ILogger logger) =>
            Use(p => p.EncodeMeters(view), [], logger);

        public IReadOnlyList<byte[]> Poll(long timeNs, ILogger logger) => Use(p => p.Poll(timeNs), [], logger);

        /// <summary>Runs <paramref name="call" /> on the protocol; a protocol that throws is stopped (logged once).</summary>
        T Use<T>(Func<IControlSurface, T> call, T fallback, ILogger? logger = null) {
            if (protocol is null || failed) {
                return fallback;
            }

            try {
                return call(protocol);
            } catch (Exception e) when (e is NotImplementedException
                                            or InvalidOperationException
                                            or ArgumentException
                                            or IndexOutOfRangeException) {
                failed = true;
                if (logger is not null) {
                    ControlLog.SurfaceFailed(logger, Config.Id, e.Message);
                }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Key command {Id} is not one the server runs")]
    public static partial void KeyCommandIgnored(ILogger logger, string id);
}
