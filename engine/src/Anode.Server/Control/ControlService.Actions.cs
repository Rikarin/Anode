using Anode.Contracts;
using Anode.Control.Assignments;
using Anode.Control.Surfaces;
using Anode.Ipc;
using Anode.Model;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Model.History;
using Anode.Server.Projects;

namespace Anode.Server.Control;

/// <summary>
///     What controls do (docs/09-phase-8.md, "Assignments"): a control move is the command the editor would send, under
///     a gesture's coalescing key (<see cref="EditHistory.GesturePrefix" />) so a gesture undoes in one step; the session
///     records it as automation when the track's mode writes and the transport plays, as it does an editor fader's.
/// </summary>
public sealed partial class ControlService {
    /// <summary>How often a held rewind or forward moves the playhead another bar.</summary>
    static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>A held rewind or forward starts repeating after this long.</summary>
    static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);

    readonly Dictionary<string, Gesture> gestures = new(StringComparer.Ordinal);

    TransportState TransportState => engine.Transport().State;

    PluginCatalog Plugins => catalog.ForEdits;
    long nextGesture;
    (int Direction, DateTimeOffset Next)? held;

    // ---- Assignment changes ----

    /// <summary>An assignment's change, against the project as it is now.</summary>
    void Apply(TargetChange change, OpenProjectView? view) {
        if (view is null) {
            return;
        }

        var target = change.Target;
        switch (target.Kind) {
            case ControlTargetKind.Parameter or ControlTargetKind.SmartControl:
                if (TrackOf(view, target.TrackId) is { } track) {
                    var address = target.Kind == ControlTargetKind.SmartControl
                        ? "smartControl:" + target.Address
                        : target.Address!;
                    SetParameter($"a:{change.AssignmentId}", view, track, address, change.Value);
                }

                break;
            case ControlTargetKind.TrackSwitch:
                Switch(view, target.TrackId, target.Switch!.Value, change.Value);
                break;
            case ControlTargetKind.Transport:
                Transport(view, target.Transport!.Value, change.Value);
                break;
            case ControlTargetKind.KeyCommand:
                if (change.Value >= 0.5) {
                    RunKeyCommand(view, target.KeyCommandId!);
                }

                break;
            case ControlTargetKind.ZoneMode:
                // The engine switched the mode itself (Receive keeps it).
                break;
        }
    }

    /// <summary>
    ///     The Environment's control targets moved strip parameters (Phase 11): the host has applied them; the model takes
    ///     each as a control's gesture, so the strips follow and an undo takes the move back whole.
    /// </summary>
    void OnEnvironmentControl(EnvironmentControlMessage message) {
        if (session.View is not { } view) {
            return;
        }

        foreach (var move in message.Moves ?? []) {
            var address = AutomationEvents.Address(move.Target);
            if (move.Target.TrackId == AutomationEvents.MasterTrack) {
                SetMaster(address, move.Value);
            } else if (view.State.FindTrack(view.Ids.TrackOf(move.Target.TrackId) ?? string.Empty) is { } track) {
                SetParameter("environment", view, track, address, move.Value);
            }
        }
    }

    /// <summary>The master's fader, pan or mute from a control target.</summary>
    void SetMaster(string address, double value) {
        EditCommand? command = address switch {
            "volume" => new SetStripCommand(null, Taper.Db(value)),
            "pan" => new SetStripCommand(null, Pan: value * 2 - 1),
            "mute" => new SetStripCommand(null, Mute: value >= 0.5),
            _ => null
        };
        if (command is null) {
            return;
        }

        try {
            session.Apply(new([command]), null, GestureKey($"environment|master|{address}"));
        } catch (ProjectException e) {
            ControlLog.MoveIgnored(logger, e.Message);
        }
    }

    /// <summary>A track a target names, or the selected one for null; null when it is not there.</summary>
    Track? TrackOf(OpenProjectView view, string? trackId) =>
        (trackId ?? Selected(view)) is { } id ? view.State.FindTrack(id) : null;

    /// <summary>
    ///     Moves <paramref name="address" /> (an automation address, or <c>smartControl:{id}</c>) of a track to
    ///     <paramref name="value" /> (normalised 0…1) as one gesture of <paramref name="source" />'s control.
    /// </summary>
    void SetParameter(string source, OpenProjectView view, Track track, string address, double value) {
        if (Command(view.State, track, address, Math.Clamp(value, 0, 1)) is not { } command) {
            return;
        }

        var key = GestureKey($"{source}|{track.Id}|{address}");
        try {
            session.Apply(new([command]), null, key);
        } catch (ProjectException e) {
            ControlLog.MoveIgnored(logger, e.Message);
        }
    }

    /// <summary>The command that sets a target to a normalised value; null for one that is not there.</summary>
    EditCommand? Command(ProjectState state, Track track, string address, double value) {
        switch (address) {
            case "volume":
                return new SetStripCommand(track.Id, Taper.Db(value));
            case "pan":
                return new SetStripCommand(track.Id, Pan: value * 2 - 1);
            case "mute":
                return new SetStripCommand(track.Id, Mute: value >= 0.5);
        }

        var parts = address.Split(':');
        if (parts is ["send", var sendId]) {
            return track.Strip.Sends.Any(s => s.Id == sendId)
                ? new SetSendCommand(track.Id, sendId, Taper.Db(value))
                : null;
        }

        if (parts is ["smartControl", var controlId]) {
            return track.SmartControls?.Controls.Any(c => c.Id == controlId) == true
                ? new SetSmartControlCommand(track.Id, controlId, value)
                : null;
        }

        if (Range(state, track, address) is not { } range) {
            return null;
        }

        var plain = range.Min + value * (range.Max - range.Min);
        return parts switch {
            ["insert", var insertId, var parameter] => new SetInsertParameterCommand(
                insertId,
                parameter,
                plain,
                track.Id
            ),
            ["instrument", var parameter] => new SetParameterCommand(track.Id, parameter, plain),
            ["midiEffect", var slotId, var parameter] => new SetInsertParameterCommand(
                slotId,
                parameter,
                plain,
                track.Id,
                InsertChain.MidiEffects
            ),
            _ => null
        };
    }

    /// <summary>A plugin parameter's range (null for one that is not there, or that has no finite range).</summary>
    Edits.LaneRange? Range(ProjectState state, Track track, string address) {
        try {
            var range = Edits.TargetRangeOf(state, track.Id, address, catalog.ForEdits);
            return double.IsFinite(range.Min)
                && double.IsFinite(range.Max)
                && range.Max > range.Min
                && range.Min > double.MinValue
                && range.Max < double.MaxValue
                    ? range
                    : null;
        } catch (EditException) {
            return null;
        }
    }

    /// <summary>
    ///     The coalescing key of the gesture a move of <paramref name="control" /> belongs to: the current one while it
    ///     is touched or moved within <see cref="GestureTimeout" />, else a new one.
    /// </summary>
    string GestureKey(string control) {
        var now = time.GetUtcNow();
        if (!gestures.TryGetValue(control, out var gesture)
            || (!gesture.Touched && now - gesture.Last > GestureTimeout)) {
            gesture = new(++nextGesture, now, false);
        }

        gestures[control] = gesture with { Last = now };
        return EditHistory.GesturePrefix + gesture.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A control was touched (a gesture starts, or goes on) or released (it ends).</summary>
    void Touch(string control, bool touched) {
        if (!touched) {
            gestures.Remove(control);
            return;
        }

        var now = time.GetUtcNow();
        gestures[control] = gestures.TryGetValue(control, out var gesture) && now - gesture.Last <= GestureTimeout
            ? gesture with { Touched = true, Last = now }
            : new(++nextGesture, now, true);
    }

    /// <summary>Gestures whose control has not moved for <see cref="GestureTimeout" /> (and is not touched) end.</summary>
    void EndGestures() {
        var now = time.GetUtcNow();
        foreach (var (control, gesture) in gestures.ToArray()) {
            if (!gesture.Touched && now - gesture.Last > GestureTimeout) {
                gestures.Remove(control);
            }
        }
    }

    /// <summary>A track switch: mute, solo and record arm set on (≥ 0.5) or off; select next or previous on a press.</summary>
    void Switch(OpenProjectView view, string? trackId, TrackSwitchAction action, double value) {
        var on = value >= 0.5;
        if (action is TrackSwitchAction.SelectNext or TrackSwitchAction.SelectPrevious) {
            if (on) {
                SelectNeighbour(view, action == TrackSwitchAction.SelectNext ? 1 : -1);
            }

            return;
        }

        if (TrackOf(view, trackId) is not { } track) {
            return;
        }

        EditCommand? command = action switch {
            TrackSwitchAction.Mute when track.Strip.Mute != on => new SetStripCommand(track.Id, Mute: on),
            TrackSwitchAction.Solo when track.Strip.Solo != on => new SetStripCommand(track.Id, Solo: on),
            TrackSwitchAction.RecordArm when track.RecordArmed != on => new SetTrackCommand(track.Id, RecordArmed: on),
            _ => null
        };
        Edit(command);
    }

    /// <summary>Applies an edit that is not a gesture (a switch, a key command); one that does not apply is logged.</summary>
    void Edit(EditCommand? command) {
        if (command is null) {
            return;
        }

        try {
            session.Apply(new([command]), null);
        } catch (ProjectException e) {
            ControlLog.MoveIgnored(logger, e.Message);
        }
    }

    /// <summary>Selects the track <paramref name="step" /> places from the selected one, among those surfaces show.</summary>
    void SelectNeighbour(OpenProjectView view, int step) {
        var tracks = Shown(view.State).ToList();
        if (tracks.Count == 0) {
            return;
        }

        var index = Selected(view) is { } id ? tracks.FindIndex(t => t.Id == id) : -1;
        // Nothing selected: the first track going down, the last going up.
        var from = index >= 0 ? index : step > 0 ? -1 : tracks.Count;
        Select(view.Session, tracks[Math.Clamp(from + step, 0, tracks.Count - 1)].Id);
    }

    /// <summary>A transport function: play, stop, record, go to start and cycle on a press; rewind and forward held.</summary>
    void Transport(OpenProjectView view, ControlTransportAction action, double value) {
        var pressed = value >= 0.5;
        switch (action) {
            case ControlTransportAction.Rewind:
                Hold(view, pressed ? -1 : 0);
                return;
            case ControlTransportAction.Forward:
                Hold(view, pressed ? 1 : 0);
                return;
            case ControlTransportAction.Play when pressed:
                Control(new(TransportCommand.Play));
                break;
            case ControlTransportAction.Stop when pressed:
                Control(new(TransportCommand.Stop));
                break;
            case ControlTransportAction.Record when pressed:
                Control(new(TransportCommand.Record));
                break;
            case ControlTransportAction.GoToStart when pressed:
                Control(new(TransportCommand.Locate, 0));
                break;
            case ControlTransportAction.Cycle when pressed:
                Edit(new SetCycleCommand(!view.State.Settings.Cycle.Enabled));
                break;
        }
    }

    void Control(TransportRequest request) {
        try {
            engine.Control(request);
        } catch (ProjectException e) {
            ControlLog.MoveIgnored(logger, e.Message);
        }
    }

    /// <summary>Rewind (−1) or forward (1) pressed: a bar now, and more while held; 0 releases.</summary>
    void Hold(OpenProjectView view, int direction) {
        if (direction == 0) {
            held = null;
            return;
        }

        StepBars(view, direction);
        held = (direction, time.GetUtcNow() + RepeatDelay);
    }

    void RepeatHeld() {
        if (held is not var (direction, next) || time.GetUtcNow() < next || session.View is not { } view) {
            return;
        }

        StepBars(view, direction);
        held = (direction, time.GetUtcNow() + RepeatInterval);
    }

    /// <summary>Moves the playhead to the bar line <paramref name="bars" /> away (back to this bar's start first).</summary>
    void StepBars(OpenProjectView view, int bars) {
        var map = view.State.SignatureMap;
        var tick = engine.Transport().PositionTick;
        var bar = map.Position(tick).Bar;
        var target = bars < 0 && !map.IsBarLine(tick) ? map.BarStart(bar) : map.BarStart(Math.Max(0, bar + bars));
        Control(new(TransportCommand.Locate, Math.Max(0, target)));
    }

    /// <summary>The jog wheel: a sixteenth note per step.</summary>
    void Jog(OpenProjectView view, double steps) {
        var tick = engine.Transport().PositionTick + (long)Math.Round(steps) * (Model.Time.TempoMap.Ppq / 4);
        Control(new(TransportCommand.Locate, Math.Clamp(tick, 0, Edits.MaxTick)));
    }

    /// <summary>
    ///     The key commands the server runs itself (transport, undo and redo, new tracks); the others are the editor's
    ///     (menus, windows, the editor's selection), relayed to it as a <see cref="Contracts.KeyCommandEvent" />.
    /// </summary>
    void RunKeyCommand(OpenProjectView view, string id) {
        var playing = engine.Transport().State != TransportState.Stopped;
        switch (id) {
            case "transport.play":
                Control(new(playing ? TransportCommand.Stop : TransportCommand.Play));
                break;
            case "transport.stop":
                Control(new(TransportCommand.Stop));
                break;
            case "transport.record":
                Control(new(TransportCommand.Record));
                break;
            case "transport.goToStart":
                Control(new(TransportCommand.Locate, 0));
                break;
            case "transport.cycle":
                Edit(new SetCycleCommand(!view.State.Settings.Cycle.Enabled));
                break;
            case "transport.metronome":
                Edit(new SetMetronomeCommand(!view.State.Settings.Metronome.Enabled));
                break;
            case "transport.flashback":
                Run(engine.Capture);
                break;
            case "edit.undo":
                Run(() => session.Undo(null));
                break;
            case "edit.redo":
                Run(() => session.Redo(null));
                break;
            case "track.newAudio":
                Edit(new AddTrackCommand());
                break;
            case "track.newInstrument":
                Edit(new AddTrackCommand(Kind: TrackKind.Instrument));
                break;
            default:
                ControlLog.KeyCommandRelayed(logger, id);
                hub.Publish(new KeyCommandEvent(id));
                break;
        }
    }

    void Run(Action action) {
        try {
            action();
        } catch (ProjectException e) {
            ControlLog.MoveIgnored(logger, e.Message);
        }
    }

    // ---- Surface actions ----

    /// <summary>
    ///     What a surface's control did. Buttons (switches, transport, select, cursor, cycle) act on each action, which
    ///     the protocols report on press; touch, rewind, forward and zoom carry 1 on press and 0 on release.
    /// </summary>
    void Act(SurfaceRuntime surface, SurfaceAction action) {
        if (session.View is not { } view) {
            if (action.Kind == SurfaceActionKind.Banked) {
                PublishStatuses(); // the group moved its units itself
            }

            return;
        }

        if (!ActOnStrip(surface, view, action)) {
            ActOnSection(surface, view, action);
        }
    }

    /// <summary>A strip's control (fader, VPot, touch, switches, select); false for any other action.</summary>
    bool ActOnStrip(SurfaceRuntime surface, OpenProjectView view, SurfaceAction action) {
        var source = "s:" + surface.Config.Id;
        var track = action.TrackId is { } id ? view.State.FindTrack(id) : null;
        switch (action.Kind) {
            case SurfaceActionKind.SetParameter or SurfaceActionKind.StepParameter or SurfaceActionKind.Touch
                when track is null || action.Address is null:
                return true;
            case SurfaceActionKind.SetParameter:
                SetParameter(source, view, track!, action.Address!, action.Value);
                return true;
            case SurfaceActionKind.StepParameter:
                var current = new TargetValues(this, view).Value(track!, action.Address!) ?? 0;
                SetParameter(source, view, track!, action.Address!, current + action.Value / 128);
                return true;
            case SurfaceActionKind.Touch:
                Touch($"{source}|{track!.Id}|{action.Address}", action.Value >= 0.5);
                return true;
            case SurfaceActionKind.Mute
                or SurfaceActionKind.Solo
                or SurfaceActionKind.RecordArm
                or SurfaceActionKind.Select when track is null:
                return true;
            case SurfaceActionKind.Mute:
                Switch(view, track!.Id, TrackSwitchAction.Mute, track.Strip.Mute ? 0 : 1);
                return true;
            case SurfaceActionKind.Solo:
                Switch(view, track!.Id, TrackSwitchAction.Solo, track.Strip.Solo ? 0 : 1);
                return true;
            case SurfaceActionKind.RecordArm:
                Switch(view, track!.Id, TrackSwitchAction.RecordArm, track.RecordArmed ? 0 : 1);
                return true;
            case SurfaceActionKind.Select:
                Select(view.Session, track!.Id);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The master section's controls: transport, jog, cursor, zoom, banking, automation modes.</summary>
    void ActOnSection(SurfaceRuntime surface, OpenProjectView view, SurfaceAction action) {
        switch (action.Kind) {
            case SurfaceActionKind.Play:
                Control(new(TransportCommand.Play));
                break;
            case SurfaceActionKind.Stop:
                Control(new(TransportCommand.Stop));
                break;
            case SurfaceActionKind.Record:
                Control(new(TransportCommand.Record));
                break;
            case SurfaceActionKind.Rewind:
                Hold(view, action.Value >= 0.5 ? -1 : 0);
                break;
            case SurfaceActionKind.Forward:
                Hold(view, action.Value >= 0.5 ? 1 : 0);
                break;
            case SurfaceActionKind.Cycle:
                Edit(new SetCycleCommand(!view.State.Settings.Cycle.Enabled));
                break;
            case SurfaceActionKind.Jog:
                Jog(view, action.Value);
                break;
            case SurfaceActionKind.CursorUp:
                SelectNeighbour(view, -1);
                break;
            case SurfaceActionKind.CursorDown:
                SelectNeighbour(view, 1);
                break;
            case SurfaceActionKind.CursorLeft when !surface.Zoom:
                StepBars(view, -1);
                break;
            case SurfaceActionKind.CursorRight when !surface.Zoom:
                StepBars(view, 1);
                break;
            case SurfaceActionKind.Zoom:
                surface.Zoom = action.Value >= 0.5;
                break;
            case SurfaceActionKind.Banked:
                PublishStatuses(); // the group moved its units itself
                break;
            case SurfaceActionKind.SetAutomationMode
                when TrackOf(view, action.TrackId) is { } automated && Enum.IsDefined(action.Automation):
                Edit(new SetAutomationModeCommand(action.Automation, automated.Id));
                break;
            default:
                // The strips' actions (ActOnStrip), cursor keys while zooming, an automation mode without a track.
                break;
        }
    }

    /// <summary>
    ///     The current values of targets, normalised 0…1 (<see cref="ITargetValues" />), from the project as it is: the
    ///     assignment engine reads them for pickup, relative steps and toggles.
    /// </summary>
    sealed class TargetValues(ControlService service, OpenProjectView? view) : ITargetValues {
        public double? Value(ControlTarget target) {
            if (view is null) {
                return null;
            }

            var track = service.TrackOf(view, target.TrackId);
            return target.Kind switch {
                ControlTargetKind.Parameter => track is null ? null : Value(track, target.Address ?? string.Empty),
                ControlTargetKind.SmartControl => track is null ? null : Value(track, "smartControl:" + target.Address),
                ControlTargetKind.TrackSwitch => SwitchValue(track, target.Switch),
                ControlTargetKind.Transport => TransportValue(view, target.Transport),
                _ => 0
            };
        }

        /// <summary>A track switch: on 1, off 0 (select next and previous are always off); null without the track.</summary>
        static double? SwitchValue(Track? track, TrackSwitchAction? action) {
            if (action is TrackSwitchAction.SelectNext or TrackSwitchAction.SelectPrevious) {
                return 0;
            }

            if (track is null) {
                return null;
            }

            var on = action switch {
                TrackSwitchAction.Mute => track.Strip.Mute,
                TrackSwitchAction.Solo => track.Strip.Solo,
                _ => track.RecordArmed
            };
            return on ? 1 : 0;
        }

        /// <summary>A transport function: playing, recording and cycle are on or off; the rest are presses (0).</summary>
        double TransportValue(OpenProjectView open, ControlTransportAction? action) {
            var on = action switch {
                ControlTransportAction.Play => service.TransportState != TransportState.Stopped,
                ControlTransportAction.Record => service.TransportState == TransportState.Recording,
                ControlTransportAction.Cycle => open.State.Settings.Cycle.Enabled,
                _ => false
            };
            return on ? 1 : 0;
        }

        /// <summary>A track's control by its address (or <c>smartControl:{id}</c>), normalised; null when not there.</summary>
        public double? Value(Track track, string address) {
            var strip = track.Strip;
            switch (address) {
                case "volume":
                    return Taper.Position(strip.VolumeDb);
                case "pan":
                    return (strip.Pan + 1) / 2;
                case "mute":
                    return strip.Mute ? 1 : 0;
            }

            var parts = address.Split(':');
            switch (parts) {
                case ["send", var sendId]:
                    return strip.Sends.FirstOrDefault(s => s.Id == sendId) is { } send
                        ? Taper.Position(send.LevelDb)
                        : null;
                case ["smartControl", var controlId]:
                    return track.SmartControls?.Controls.FirstOrDefault(c => c.Id == controlId)?.Value;
            }

            if (service.Range(view!.State, track, address) is not { } range) {
                return null;
            }

            var plain = parts switch {
                ["insert", var insertId, var parameter] => Plain(
                    strip.Inserts.FirstOrDefault(i => i.Id == insertId),
                    parameter
                ),
                ["instrument", var parameter] when track.Instrument is { } slot => slot.Parameters.TryGetValue(
                    parameter,
                    out var v
                )
                    ? v
                    : service.Plugins.Find(slot.PluginId)
                        ?.Parameters.FirstOrDefault(p => p.Id == parameter)
                        ?.Default,
                ["midiEffect", var slotId, var parameter] => Plain(
                    strip.MidiEffects.FirstOrDefault(i => i.Id == slotId),
                    parameter
                ),
                _ => null
            };
            return plain is { } p ? Math.Clamp((p - range.Min) / (range.Max - range.Min), 0, 1) : null;
        }

        double? Plain(Insert? insert, string parameter) {
            if (insert is null) {
                return null;
            }

            if (insert.Parameters.TryGetValue(parameter, out var value)) {
                return value;
            }

            return (insert.RuntimeParameters ?? service.Plugins.Find(insert.PluginId)?.Parameters)
                ?.FirstOrDefault(p => p.Id == parameter)
                ?.Default;
        }
    }

    /// <summary>A control's gesture: its number (the coalescing key), when it last moved, and whether it is touched.</summary>
    readonly record struct Gesture(long Id, DateTimeOffset Last, bool Touched);
}
