using Anode.Contracts;
using Anode.Model.Groove;
using Anode.Model.Time;

namespace Anode.Model.Editing;

/// <summary>
///     Phase 7 (docs/08-phase-7.md): the Event List's controller edits, MIDI Transform, groove templates and the groove
///     track, articulation sets, Step Editor lane sets, step input, the Audio File Editor's file versions and Scripter's
///     scripts. The Transform engine and groove templates come through <see cref="MidiAlgorithms.Current" />.
/// </summary>
public static partial class Edits {
    public const int MaxGrooveSteps = 1024;
    public const int MaxGrooveBars = 4;
    public const long DefaultGrooveStepTicks = 240;
    public const int MaxArticulationId = 254;
    public const int MaxArticulations = 254;
    public const int MaxArticulationRules = 1024;
    public const int MaxSymbolLength = 16;
    public const int MaxLanes = 64;
    public const int MaxStepPitches = 128;
    public const int DefaultStepVelocity = 100;
    public const int MaxScriptLength = 1 << 20;
    public const int MaxTransformRules = 64;

    /// <summary>The built-in MIDI effect that runs scripts (ADR-0009).</summary>
    public const string ScripterId = "anode.scripter";

    /// <summary>The id of the groove a matching track quantises to: the groove track's, made at quantise time.</summary>
    public const string LiveGrooveId = "grv_live";

    /// <summary>The operations of a Phase 7 command, or null when <paramref name="command" /> is not one.</summary>
    static IReadOnlyList<DeltaOp>? Phase7Ops(
        ProjectState state,
        EditCommand command,
        IIdSource ids,
        IAnalysisSource analysis
    ) =>
        command switch {
            SetControlsCommand c => SetControls(state, c),
            TransformCommand c => Transform(state, c),
            MakeGrooveCommand c => MakeGroove(state, c, ids, analysis),
            SetGrooveCommand c => [new GrooveUpserted(CheckGroove(c.Groove))],
            DeleteGrooveCommand c => DeleteGroove(state, c),
            SetGrooveTrackCommand c => SetGrooveTrack(state, c),
            SetMatchGrooveCommand c => SetMatchGroove(state, c),
            SetArticulationSetCommand c => [new ArticulationSetUpserted(CheckArticulationSet(c.Set))],
            DeleteArticulationSetCommand c => DeleteArticulationSet(state, c),
            AssignArticulationSetCommand c => AssignArticulationSet(state, c),
            SetLaneSetCommand c => [new LaneSetUpserted(CheckLaneSet(c.Set))],
            DeleteLaneSetCommand c => state.LaneSets.ContainsKey(c.SetId ?? string.Empty)
                ? [new LaneSetRemoved(c.SetId!)]
                : throw new EditException($"No lane set '{c.SetId}'."),
            StepNotesCommand c => StepNotes(state, c, ids),
            StepTieCommand c => StepTie(state, c),
            SetFileVersionCommand c => SetFileVersion(state, c),
            SetScriptCommand c => SetScript(state, c),
            _ => null
        };

    static DeltaOp[] SetControls(ProjectState state, SetControlsCommand c) {
        var region = MidiRegionOf(state, c.RegionId);
        if (c.Changes is not { Count: > 0 and <= MaxNotesPerCommand }) {
            throw new EditException($"Change 1…{MaxNotesPerCommand} controller events.");
        }

        var controls = region.Controls.ToArray();
        var index = new Dictionary<long, int>(controls.Length);
        for (var i = 0; i < controls.Length; i++) {
            index[controls[i].Id] = i;
        }

        foreach (var change in c.Changes) {
            if (change is null || !index.TryGetValue(change.Id, out var i)) {
                throw new EditException($"Region '{region.Id}' has no controller event {change?.Id}.");
            }

            var old = controls[i];
            var control = old with {
                Tick = change.Tick ?? old.Tick,
                Channel = change.Channel ?? old.Channel,
                Number = change.Number ?? old.Number,
                Value = change.Value ?? old.Value
            };
            CheckControl(region, control);
            controls[i] = control;
        }

        return [new MidiRegionUpserted(region with { Controls = Sorted(controls) })];
    }

    /// <summary>
    ///     MIDI Transform over the events of regions, in project ticks while it runs (so position conditions and
    ///     crescendos span the regions), then back in each region's.
    /// </summary>
    static List<DeltaOp> Transform(ProjectState state, TransformCommand c) {
        var set = CheckTransformSet(c.Set);
        if (set.Mode == TransformMode.SelectOnly) {
            throw new EditException("Select Only changes the editor's selection, not the project: the editor runs it.");
        }

        if (c.RegionIds is not { Count: > 0 and <= MaxBatch }) {
            throw new EditException($"Transform the events of 1…{MaxBatch} MIDI regions.");
        }

        var regions = c.RegionIds.Distinct(StringComparer.Ordinal).Select(id => MidiRegionOf(state, id)).ToArray();
        var (notes, controls, owner) = Gathered(regions, c);
        var result = MidiAlgorithms.Current.Transform(notes, controls, set, state.SignatureMap);
        var changedNotes = Placed(
            result.Notes,
            owner,
            static n => n.Id,
            static (n, r) => n with { StartTick = n.StartTick - r.StartTick },
            static (r, n) => CheckNote(r, n, true)
        );
        var changedControls = Placed(
            result.Controls,
            owner,
            static e => e.Id,
            static (e, r) => e with { Tick = e.Tick - r.StartTick },
            CheckControl
        );
        return [
            ..regions.Select(r => new MidiRegionUpserted(
                    r with {
                        Notes = Sorted(r.Notes.Select(n => changedNotes.GetValueOrDefault(n.Id, n))),
                        Controls = Sorted(r.Controls.Select(e => changedControls.GetValueOrDefault(e.Id, e)))
                    }
                )
            )
        ];
    }

    /// <summary>
    ///     The events a transform works on (the selection named, else every event of the regions) in project ticks,
    ///     and the region of each by id.
    /// </summary>
    static (List<Note> Notes, List<ControlEvent> Controls, Dictionary<long, MidiRegion> Owner) Gathered(
        MidiRegion[] regions,
        TransformCommand c
    ) {
        var all = c.NoteIds is null && c.ControlIds is null;
        var noteIds = Selected(regions.SelectMany(static r => r.Notes.Select(static n => n.Id)), c.NoteIds, "note");
        var controlIds = Selected(
            regions.SelectMany(static r => r.Controls.Select(static e => e.Id)),
            c.ControlIds,
            "controller event"
        );
        var owner = new Dictionary<long, MidiRegion>();
        var notes = new List<Note>();
        var controls = new List<ControlEvent>();
        foreach (var region in regions) {
            foreach (var note in region.Notes.Where(n => all || noteIds.Contains(n.Id))) {
                owner[note.Id] = region;
                notes.Add(note with { StartTick = region.StartTick + note.StartTick });
            }

            foreach (var control in region.Controls.Where(e => all || controlIds.Contains(e.Id))) {
                owner[control.Id] = region;
                controls.Add(control with { Tick = region.StartTick + control.Tick });
            }
        }

        return (notes, controls, owner);
    }

    /// <summary>Transformed events back in their regions' ticks, checked, by id.</summary>
    static Dictionary<long, T> Placed<T>(
        IReadOnlyList<T> events,
        Dictionary<long, MidiRegion> owner,
        Func<T, long> id,
        Func<T, MidiRegion, T> relative,
        Action<MidiRegion, T> check
    ) where T : class {
        var placed = new Dictionary<long, T>();
        foreach (var e in events) {
            if (e is not null && owner.TryGetValue(id(e), out var region)) {
                var moved = relative(e, region);
                check(region, moved);
                placed[id(e)] = moved;
            }
        }

        return placed;
    }

    /// <summary>The ids named (each one of <paramref name="existing" />), or none when none are named.</summary>
    static HashSet<long> Selected(IEnumerable<long> existing, IReadOnlyList<long>? named, string what) {
        if (named is null) {
            return [];
        }

        var have = existing.ToHashSet();
        foreach (var id in named) {
            if (!have.Contains(id)) {
                throw new EditException($"The regions have no {what} {id}.");
            }
        }

        return [..named];
    }

    /// <summary>A Transform set checked: its rules well formed, at most one operation per field, a map of 128 values.</summary>
    public static TransformSet CheckTransformSet(TransformSet? set) {
        if (set is null) {
            throw new EditException("Name a Transform set.");
        }

        if (!Enum.IsDefined(set.Mode)) {
            throw new EditException($"Unknown Transform mode {set.Mode}.");
        }

        if (set.Conditions is not { Count: <= MaxTransformRules } conditions
            || set.Operations is not { Count: <= MaxTransformRules } operations) {
            throw new EditException($"A Transform set has 0…{MaxTransformRules} conditions and operations.");
        }

        if (conditions.Any(static c => c is null
                || !Enum.IsDefined(c.Field)
                || !Enum.IsDefined(c.Op)
                || !double.IsFinite(c.A)
                || !double.IsFinite(c.B)
            )) {
            throw new EditException("A Transform condition names a field, a comparison and finite values.");
        }

        CheckOperations(operations);
        if (set.Map is { } map && (map.Count != 128 || map.Any(static v => v is < 0 or > 127))) {
            throw new EditException("A Transform map has 128 values, each 0…127.");
        }

        return set with { Name = set.Name ?? string.Empty, Id = set.Id ?? string.Empty };
    }

    /// <summary>Operations well formed, on the fields operations change, at most one per field.</summary>
    static void CheckOperations(IReadOnlyList<TransformOperation> operations) {
        var fields = new HashSet<TransformField>();
        foreach (var operation in operations) {
            if (operation is null
                || !Enum.IsDefined(operation.Op)
                || !double.IsFinite(operation.A)
                || !double.IsFinite(operation.B)) {
                throw new EditException("A Transform operation names a field, an operation and finite values.");
            }

            if (operation.Field is TransformField.Status or TransformField.Subposition
                || !Enum.IsDefined(operation.Field)) {
                throw new EditException($"A Transform operation cannot change the {operation.Field}.");
            }

            if (!fields.Add(operation.Field)) {
                throw new EditException($"A Transform set has one operation per field, not two on {operation.Field}.");
            }
        }
    }

    static DeltaOp[] MakeGroove(ProjectState state, MakeGrooveCommand c, IIdSource ids, IAnalysisSource analysis) {
        var bars = c.LengthBars ?? 1;
        if (bars is < 1 or > MaxGrooveBars) {
            throw new EditException($"A groove template is 1…{MaxGrooveBars} bars long, not {bars}.");
        }

        var step = c.StepTicks ?? DefaultGrooveStepTicks;
        var id = ids.Next(IdPrefix.Groove);
        GrooveTemplate groove;
        if (state.MidiRegions.TryGetValue(c.RegionId ?? string.Empty, out var midi)) {
            var length = GrooveLength(state, midi.StartTick, bars, step);
            var name = c.Name is null ? midi.Name : Name(c.Name, "groove name");
            groove = MidiAlgorithms.Current.GrooveFromNotes(id, name, midi.Notes, midi.StartTick, length, step);
        } else if (state.Regions.TryGetValue(c.RegionId ?? string.Empty, out var audio)) {
            var length = GrooveLength(state, audio.StartTick, bars, step);
            var name = c.Name is null ? audio.Name : Name(c.Name, "groove name");
            groove = MidiAlgorithms.Current.GrooveFromOnsets(
                id,
                name,
                OnsetTicks(state, audio, analysis),
                audio.StartTick,
                length,
                step
            );
        } else {
            throw new EditException($"No MIDI or audio region '{c.RegionId}'.");
        }

        return [new GrooveUpserted(CheckGroove(groove))];
    }

    /// <summary>A template's length: <paramref name="bars" /> bars of the signature at <paramref name="startTick" />.</summary>
    static long GrooveLength(ProjectState state, long startTick, int bars, long step) {
        var length = bars * SignatureMap.BarTicks(state.SignatureMap.At(startTick));
        if (step < 1 || length % step != 0 || length / step > MaxGrooveSteps) {
            throw new EditException(
                $"Groove steps of {step} ticks do not divide {length} ticks into 1…{MaxGrooveSteps} steps."
            );
        }

        return length;
    }

    /// <summary>An audio region's onsets (its file's, inside the region) at the project ticks where they play.</summary>
    static List<GrooveOnsetTick> OnsetTicks(ProjectState state, AudioRegion region, IAnalysisSource analysis) {
        var file = FileOf(state, region.FileId);
        var onsets = analysis.Onsets(file)
            ?? throw new EditException($"A groove from audio needs the onsets of '{file.Name}': it has none.");
        var timing = RegionTiming.Of(state, region);
        var end = region.FileOffset + region.Length;
        return [
            ..onsets.Where(o => o.Frame >= region.FileOffset && o.Frame < end)
                .Select(o => new GrooveOnsetTick((long)Math.Round(timing.TickAt(o.Frame)), Math.Clamp(o.Strength, 0, 1))
                )
        ];
    }

    /// <summary>A groove template checked: a safe id, a name, one point per step, each within a step of its grid.</summary>
    public static GrooveTemplate CheckGroove(GrooveTemplate? groove) {
        if (groove is null) {
            throw new EditException("Name a groove template.");
        }

        CheckSetId(groove.Id, "groove template");
        var name = Name(groove.Name ?? string.Empty, "groove name");
        if (groove.StepTicks < 1
            || groove.LengthTicks < groove.StepTicks
            || groove.LengthTicks % groove.StepTicks != 0
            || groove.LengthTicks / groove.StepTicks > MaxGrooveSteps) {
            throw new EditException(
                $"A groove template's steps divide its length into 1…{MaxGrooveSteps} steps, not {groove.LengthTicks} "
                + $"by {groove.StepTicks}."
            );
        }

        var count = groove.LengthTicks / groove.StepTicks;
        if (groove.Points is not { } points || points.Count != count) {
            throw new EditException($"A groove template of {count} steps has {count} points.");
        }

        for (var n = 0; n < points.Count; n++) {
            if (points[n] is not { } p
                || Math.Abs(p.Tick - n * groove.StepTicks) > groove.StepTicks
                || !(p.VelocityScale is >= 0 and <= 1)) {
                throw new EditException(
                    $"Groove point {n} lands within a step of its grid position, with a velocity scale 0…1."
                );
            }
        }

        return groove with { Name = name, Points = [..points] };
    }

    /// <summary>A set's id: 1…64 letters, digits, <c>_</c>, <c>-</c> or <c>.</c>.</summary>
    static void CheckSetId(string? id, string what) {
        if (id is not { Length: > 0 and <= 64 }
            || !id.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')) {
            throw new EditException($"A {what}'s id has 1…64 letters, digits, '_', '-' or '.'.");
        }
    }

    static List<DeltaOp> DeleteGroove(ProjectState state, DeleteGrooveCommand c) {
        if (!state.Grooves.ContainsKey(c.GrooveId ?? string.Empty)) {
            throw new EditException($"No groove template '{c.GrooveId}'.");
        }

        // Regions whose playback quantise used it return to the grid.
        var ops = new List<DeltaOp>();
        ops.AddRange(
            state.MidiRegions.Values.Where(r => r.Playback.GrooveId == c.GrooveId)
                .Select(static r => new MidiRegionUpserted(r with { Playback = r.Playback with { GrooveId = null } }))
        );
        if (state.LiveLoops.Cells.Any(cell => cell.Content.Midi?.Playback.GrooveId == c.GrooveId)) {
            ops.Add(
                new LiveLoopsChanged(
                    state.LiveLoops with {
                        Cells = [
                            ..state.LiveLoops.Cells.Select(cell => cell.Content.Midi is { } m
                                && m.Playback.GrooveId == c.GrooveId
                                    ? cell with {
                                        Content = cell.Content with {
                                            Midi = m with { Playback = m.Playback with { GrooveId = null } }
                                        }
                                    }
                                    : cell
                            )
                        ]
                    }
                )
            );
        }

        ops.Add(new GrooveRemoved(c.GrooveId!));
        return ops;
    }

    /// <summary>Whether a track can be the groove track or match it: one with audio or MIDI regions.</summary>
    static Track GrooveCapable(ProjectState state, string? trackId) {
        var track = TrackOf(state, trackId ?? string.Empty);
        return track.Kind == TrackKind.Audio || HoldsMidi(track)
            ? track
            : throw new EditException($"A {Kind(track.Kind)} track has no regions to take a groove from.");
    }

    static List<DeltaOp> SetGrooveTrack(ProjectState state, SetGrooveTrackCommand c) {
        var ops = new List<DeltaOp>();
        if (c.TrackId is not null) {
            // The groove track does not match itself.
            var track = GrooveCapable(state, c.TrackId);
            if (track.MatchGroove) {
                ops.Add(new TrackUpserted(track with { MatchGroove = false }, state.IndexOfTrack(track.Id)));
            }
        }

        ops.Add(new SettingsChanged(state.Settings with { GrooveTrackId = c.TrackId }));
        return ops;
    }

    static DeltaOp[] SetMatchGroove(ProjectState state, SetMatchGrooveCommand c) {
        var track = GrooveCapable(state, c.TrackId);
        if (c.MatchGroove && state.Settings.GrooveTrackId == track.Id) {
            throw new EditException($"Track '{track.Name}' is the groove track: it cannot match itself.");
        }

        return [new TrackUpserted(track with { MatchGroove = c.MatchGroove }, state.IndexOfTrack(track.Id))];
    }

    /// <summary>
    ///     The groove a quantise on <paramref name="trackId" /> uses: the project's template <paramref name="grooveId" />
    ///     when one is named; else, on a track matching the groove track, the groove made now from the groove track's
    ///     first region (its bars, 1…4, at 1/16 steps); else null (the grid).
    /// </summary>
    static GrooveTemplate? GrooveFor(ProjectState state, string trackId, string? grooveId, IAnalysisSource analysis) {
        if (grooveId is not null) {
            return state.Grooves.TryGetValue(grooveId, out var groove)
                ? groove
                : throw new EditException($"No groove template '{grooveId}'.");
        }

        if (state.FindTrack(trackId) is not { MatchGroove: true }
            || state.Settings.GrooveTrackId is not { } grooveTrack
            || grooveTrack == trackId) {
            return null;
        }

        return LiveGroove(state, grooveTrack, analysis);
    }

    /// <summary>The groove of the groove track: made from its first region (MIDI notes, or audio onsets), now.</summary>
    public static GrooveTemplate LiveGroove(ProjectState state, string grooveTrackId, IAnalysisSource analysis) {
        var midi = state.MidiRegionsOf(grooveTrackId).FirstOrDefault();
        var audio = state.RegionsOf(grooveTrackId).FirstOrDefault();
        if (midi is null && audio is null) {
            throw new EditException("The groove track has no region to take its groove from.");
        }

        var start = midi is not null && (audio is null || midi.StartTick <= audio.StartTick)
            ? midi.StartTick
            : audio!.StartTick;
        var bar = SignatureMap.BarTicks(state.SignatureMap.At(start));
        var span = midi is not null && start == midi.StartTick
            ? midi.LengthTicks
            : EndTick(state, audio!) - audio!.StartTick;
        var bars = (int)Math.Clamp((span + bar - 1) / bar, 1, MaxGrooveBars);
        var length = GrooveLength(state, start, bars, DefaultGrooveStepTicks);
        var groove = midi is not null && start == midi.StartTick
            ? MidiAlgorithms.Current.GrooveFromNotes(
                LiveGrooveId,
                "Groove Track",
                midi.Notes,
                midi.StartTick,
                length,
                DefaultGrooveStepTicks
            )
            : MidiAlgorithms.Current.GrooveFromOnsets(
                LiveGrooveId,
                "Groove Track",
                OnsetTicks(state, audio!, analysis),
                audio!.StartTick,
                length,
                DefaultGrooveStepTicks
            );
        return CheckGroove(groove);
    }

    /// <summary>An articulation set checked: ids in range and unique, switches and outputs of its articulations.</summary>
    public static ArticulationSet CheckArticulationSet(ArticulationSet? set) {
        if (set is null) {
            throw new EditException("Name an articulation set.");
        }

        CheckSetId(set.Id, "articulation set");
        var name = Name(set.Name ?? string.Empty, "articulation set name");
        if (set.Articulations is not { Count: <= MaxArticulations } articulations
            || set.Switches is not { Count: <= MaxArticulationRules } switches
            || set.Outputs is not { Count: <= MaxArticulationRules } outputs) {
            throw new EditException(
                $"An articulation set has 0…{MaxArticulations} articulations and 0…{MaxArticulationRules} switches "
                + "and outputs."
            );
        }

        var named = Articulations(articulations);
        var ids = named.Select(static a => a.Id).ToHashSet();
        foreach (var s in switches) {
            CheckSwitch(s, ids);
        }

        foreach (var o in outputs) {
            CheckOutput(o, ids);
        }

        return set with { Name = name, Articulations = named, Switches = [..switches], Outputs = [..outputs] };
    }

    /// <summary>A set's articulations checked: ids 1…254, unique, names, short symbols.</summary>
    static List<Articulation> Articulations(IReadOnlyList<Articulation> articulations) {
        var ids = new HashSet<int>();
        var named = new List<Articulation>();
        foreach (var a in articulations) {
            if (a is null || a.Id is < 1 or > MaxArticulationId || !ids.Add(a.Id)) {
                throw new EditException($"Articulation ids are 1…{MaxArticulationId} and unique in their set.");
            }

            if (a.Symbol is { Length: > MaxSymbolLength }) {
                throw new EditException($"An articulation's symbol has at most {MaxSymbolLength} characters.");
            }

            named.Add(a with { Name = Name(a.Name ?? string.Empty, "articulation name") });
        }

        return named;
    }

    static void CheckSwitch(ArticulationSwitch? s, HashSet<int> ids) {
        if (s is null || !ids.Contains(s.ArticulationId) || !Enum.IsDefined(s.Kind)) {
            throw new EditException("A switch selects an articulation of its set, by a note or a control change.");
        }

        if (s.Number is < 0 or > 127 || s.Value is < 0 or > 127) {
            throw new EditException("A switch's key or controller, and its value, are 0…127.");
        }
    }

    static void CheckOutput(ArticulationOutput? o, HashSet<int> ids) {
        if (o is null || !ids.Contains(o.ArticulationId) || !Enum.IsDefined(o.Kind)) {
            throw new EditException("An output belongs to an articulation of its set, and has a kind.");
        }

        var ok = o.Kind switch {
            ArticulationOutputKind.Note => o.Number is >= 0 and <= 127 && o.Value is >= 1 and <= 127,
            ArticulationOutputKind.ControlChange => o.Number is >= 0 and <= 127 && o.Value is >= 0 and <= 127,
            ArticulationOutputKind.Program => o.Number == 0 && o.Value is >= 0 and <= 127,
            _ => o.Number == 0 && o.Value is >= 1 and <= 16
        };
        if (!ok) {
            throw new EditException(
                $"A {o.Kind} output's number and value are out of range (a keyswitch: key 0…127 at velocity "
                + "1…127; a control change: 0…127 and 0…127; a program: number 0, 0…127; a channel: number 0, 1…16)."
            );
        }
    }

    static List<DeltaOp> DeleteArticulationSet(ProjectState state, DeleteArticulationSetCommand c) {
        if (!state.ArticulationSets.ContainsKey(c.SetId ?? string.Empty)) {
            throw new EditException($"No articulation set '{c.SetId}'.");
        }

        // Tracks that used it have none; their notes keep their articulation ids.
        var ops = new List<DeltaOp>();
        for (var i = 0; i < state.Tracks.Count; i++) {
            if (state.Tracks[i].ArticulationSetId == c.SetId) {
                ops.Add(new TrackUpserted(state.Tracks[i] with { ArticulationSetId = null }, i));
            }
        }

        ops.Add(new ArticulationSetRemoved(c.SetId!));
        return ops;
    }

    static DeltaOp[] AssignArticulationSet(ProjectState state, AssignArticulationSetCommand c) {
        var track = TrackOf(state, c.TrackId);
        if (!IsMidiKind(track.Kind)) {
            throw new EditException($"A {Kind(track.Kind)} track has no articulation set.");
        }

        if (c.SetId is not null && !state.ArticulationSets.ContainsKey(c.SetId)) {
            throw new EditException($"No articulation set '{c.SetId}'.");
        }

        return [new TrackUpserted(track with { ArticulationSetId = c.SetId }, state.IndexOfTrack(track.Id))];
    }

    /// <summary>A lane set checked: lanes of known kinds, a number where the kind takes one, a resolution.</summary>
    public static LaneSet CheckLaneSet(LaneSet? set) {
        if (set is null) {
            throw new EditException("Name a lane set.");
        }

        CheckSetId(set.Id, "lane set");
        var name = Name(set.Name ?? string.Empty, "lane set name");
        return set.Lanes is { Count: <= MaxLanes } lanes
            ? set with { Name = name, Lanes = [..lanes.Select(CheckedLane)] }
            : throw new EditException($"A lane set has 0…{MaxLanes} lanes.");
    }

    static Lane CheckedLane(Lane? lane) {
        if (lane is null || !Enum.IsDefined(lane.Kind)) {
            throw new EditException("A lane has a kind.");
        }

        var numbered = lane.Kind is LaneKind.ControlChange or LaneKind.PolyPressure or LaneKind.Note;
        if (numbered ? lane.Number is not (>= 0 and <= 127) : lane.Number is not null) {
            throw new EditException(
                $"A {lane.Kind} lane {(numbered ? "names a controller or key 0…127" : "takes no number")}."
            );
        }

        return lane.ResolutionTicks is < 1 or > MaxQuantizeTicks
            ? throw new EditException($"A lane's resolution is 1…{MaxQuantizeTicks} ticks.")
            : lane with { Name = Name(lane.Name ?? string.Empty, "lane name") };
    }

    /// <summary>
    ///     A step of notes at the step position, as the server resolved it: into region <c>regionId</c> (lengthened to
    ///     the bar line after the step's end when it passes it), or into a new region on <c>trackId</c> from the bar line
    ///     at or before the position.
    /// </summary>
    static DeltaOp[] StepNotes(ProjectState state, StepNotesCommand c, IIdSource ids) {
        if (c.Pitches is not { Count: > 0 and <= MaxStepPitches } pitches
            || pitches.Distinct().Count() != pitches.Count) {
            throw new EditException($"A step has 1…{MaxStepPitches} different pitches.");
        }

        if (c.PositionTick is not { } position || c.LengthTicks is not { } length) {
            throw new EditException("A step needs its position and length (the server fills them from step input).");
        }

        if (position is < 0 or > MaxTick) {
            throw new EditException($"Tick {position} is outside the project.");
        }

        CheckLength(length, "step");
        var region = StepRegion(state, c, position, position + length, ids);
        CheckCapacity(region, pitches.Count);
        var next = state.LastEventId;
        Note[] added = [
            ..pitches.Select(p => new Note(
                    ++next,
                    p,
                    c.Velocity ?? DefaultStepVelocity,
                    c.Channel ?? 1,
                    position - region.StartTick,
                    length,
                    0
                )
            )
        ];
        foreach (var note in added) {
            CheckNote(region, note, true);
        }

        return [new MidiRegionUpserted(region with { Notes = Sorted([..region.Notes, ..added]) })];
    }

    /// <summary>
    ///     The region a step goes into: <c>regionId</c>, lengthened to the bar line after the step when it passes the
    ///     end, or a new one on <c>trackId</c> from the bar line at or before the step.
    /// </summary>
    static MidiRegion StepRegion(ProjectState state, StepNotesCommand c, long position, long end, IIdSource ids) {
        var bars = state.SignatureMap;
        if (c.RegionId is not null) {
            var region = MidiRegionOf(state, c.RegionId);
            if (position < region.StartTick) {
                throw new EditException($"Tick {position} is before region '{region.Name}' starts.");
            }

            return end > region.StartTick + region.LengthTicks
                ? region with { LengthTicks = bars.BarLineAtOrAfter(end) - region.StartTick }
                : region;
        }

        var track = TrackOf(state, c.TrackId ?? throw new EditException("A step goes into a region or onto a track."));
        if (!HoldsMidi(track) || track.FrozenFileId is not null) {
            throw new EditException($"Track '{track.Name}' takes no step input.");
        }

        var start = bars.BarLineAtOrBefore(position);
        return new(
            ids.Next(IdPrefix.MidiRegion),
            track.Id,
            track.Name,
            start,
            Math.Max(SignatureMap.BarTicks(bars.At(start)), bars.BarLineAtOrAfter(end) - start),
            ProjectDefaults.Playback,
            [],
            [],
            false
        );
    }

    /// <summary>A tie: the last step's notes (as the server resolved them) last one step longer.</summary>
    static DeltaOp[] StepTie(ProjectState state, StepTieCommand c) {
        if (c.RegionId is null || c.NoteIds is not { Count: > 0 } || c.LengthTicks is not { } length) {
            throw new EditException(
                "A tie needs its region, notes and length (the server fills them from step input)."
            );
        }

        var region = MidiRegionOf(state, c.RegionId);
        CheckLength(length, "tie");
        var tied = Known(region.Notes.Select(static n => n.Id), c.NoteIds, "note", region.Id);
        var notes = region.Notes.Select(n => tied.Contains(n.Id) ? n with { LengthTicks = n.LengthTicks + length } : n)
            .ToArray();
        foreach (var note in notes.Where(n => tied.Contains(n.Id))) {
            CheckNote(region, note, false);
        }

        var end = notes.Where(n => tied.Contains(n.Id)).Max(n => region.StartTick + n.StartTick + n.LengthTicks);
        var lengthened = end > region.StartTick + region.LengthTicks
            ? region with { LengthTicks = state.SignatureMap.BarLineAtOrAfter(end) - region.StartTick }
            : region;
        return [new MidiRegionUpserted(lengthened with { Notes = Sorted(notes) })];
    }

    /// <summary>
    ///     Points a file at another version of itself (the Audio File Editor's job applies it, undo applies it back):
    ///     the path is relative to the project, inside it, a WAV; the regions that use the file fit its frames.
    /// </summary>
    static DeltaOp[] SetFileVersion(ProjectState state, SetFileVersionCommand c) {
        var file = FileOf(state, c.FileId ?? string.Empty);
        if (c.Version < 0) {
            throw new EditException($"A file version is 0 or more, not {c.Version}.");
        }

        var path = (c.Path ?? string.Empty).Replace('\\', '/');
        if (path.Length is 0 or > MaxPathLength
            || path.StartsWith('/')
            || Path.IsPathRooted(path)
            || path.Split('/').Any(static p => p is "" or "." or "..")
            || !path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
            throw new EditException($"'{c.Path}' is not a WAV path inside the project.");
        }

        var frames = c.Frames ?? file.Frames;
        var rate = c.SampleRate ?? file.SampleRate;
        if (frames < 1) {
            throw new EditException($"A file has at least one frame, not {frames}.");
        }

        if (!ProjectDefaults.SampleRates.Contains(rate)) {
            throw new EditException(
                $"Sample rate {rate} Hz is not one of {string.Join(", ", ProjectDefaults.SampleRates)}."
            );
        }

        foreach (var region in state.Regions.Values.Where(r => r.FileId == file.Id)) {
            if (region.FileOffset + region.Length > frames) {
                throw new EditException($"Region '{region.Name}' plays past the end of the new version.");
            }
        }

        return [
            new AudioFileUpserted(file with { Version = c.Version, Path = path, Frames = frames, SampleRate = rate })
        ];
    }

    /// <summary>
    ///     What a length-changing Audio File Editor operation (trim to frames <paramref name="start" /> …
    ///     <paramref name="end" />, or a rate conversion to <paramref name="rate" />) does to the regions that use the
    ///     file: each keeps what it played (a trim clips it and moves its start to where its first kept frame played; a
    ///     conversion scales its frames). Refused when a region would vanish, when a conversion leaves the project's
    ///     rate under a region, and when take folders, track alternatives or Live Loops cells use the file.
    /// </summary>
    public static List<DeltaOp> FileLengthChanged(
        ProjectState state,
        string fileId,
        AudioEditKind kind,
        long start,
        long end,
        int rate
    ) {
        var file = FileOf(state, fileId);
        if (kind is not (AudioEditKind.Trim or AudioEditKind.ConvertRate)) {
            return [];
        }

        if (state.TakeFolders.Values.Any(f => f.Takes.Any(t => t.FileId == fileId))
            || state.Tracks.Exists(t => t.Alternatives.Any(a => a.Regions.Any(r => r.FileId == fileId)
                    || a.TakeFolders.Any(f => f.Takes.Any(x => x.FileId == fileId))
                )
            )
            || state.LiveLoops.Cells.Any(cell => cell.Content.Audio?.FileId == fileId)) {
            throw new EditException(
                $"'{file.Name}' is used by a take folder, a track alternative or a Live Loops cell: its length cannot change."
            );
        }

        var ops = new List<DeltaOp>();
        foreach (var region in state.Regions.Values.Where(r => r.FileId == fileId)) {
            ops.Add(
                new RegionUpserted(
                    kind == AudioEditKind.Trim
                        ? Trimmed(state, region, start, end)
                        : Converted(state, file, region, rate)
                )
            );
        }

        return ops;
    }

    static AudioRegion Trimmed(ProjectState state, AudioRegion region, long start, long end) {
        var (from, to) = (Math.Max(region.FileOffset, start), Math.Min(region.FileOffset + region.Length, end));
        if (to <= from) {
            throw new EditException($"Region '{region.Name}' plays nothing of what the trim keeps: it would vanish.");
        }

        var timing = RegionTiming.Of(state, region);
        var startTick = from > region.FileOffset ? (long)Math.Round(timing.TickAt(from)) : region.StartTick;
        var moved = startTick - region.StartTick;
        var length = to - from;
        return region with {
            StartTick = startTick,
            FileOffset = from - start,
            Length = length,
            FadeIn = Math.Min(region.FadeIn, length),
            FadeOut = Math.Min(region.FadeOut, length),
            FlexMarkers = [
                ..region.FlexMarkers.Where(m => m.FileFrame > from && m.FileFrame < to && m.Tick - moved > 0)
                    .Select(m => new FlexMarker(m.FileFrame - start, m.Tick - moved))
            ],
            PitchNotes = [
                ..region.PitchNotes.Where(n => n.StartFrame >= start && n.EndFrame <= end)
                    .Select(n => n with { StartFrame = n.StartFrame - start, EndFrame = n.EndFrame - start })
            ]
        };
    }

    static AudioRegion Converted(ProjectState state, AudioFile file, AudioRegion region, int rate) {
        if (rate != state.SampleRate) {
            throw new EditException(
                $"Region '{region.Name}' plays '{file.Name}' at the project's {state.SampleRate} Hz: it cannot be "
                + $"converted to {rate} Hz."
            );
        }

        var ratio = (double)rate / file.SampleRate;
        return region with {
            FileOffset = Scale(region.FileOffset),
            Length = Math.Max(1, Scale(region.FileOffset + region.Length) - Scale(region.FileOffset)),
            FlexMarkers = [..region.FlexMarkers.Select(m => m with { FileFrame = Scale(m.FileFrame) })],
            PitchNotes = [
                ..region.PitchNotes.Select(n => n with {
                        StartFrame = Scale(n.StartFrame), EndFrame = Scale(n.EndFrame)
                    }
                )
            ]
        };
        long Scale(long frame) => (long)Math.Round(frame * ratio);
    }

    static DeltaOp[] SetScript(ProjectState state, SetScriptCommand c) {
        var track = MidiFxTrackOf(state, c.TrackId);
        var slot = MidiEffectOf(track, c.SlotId);
        if (slot.PluginId != ScripterId) {
            throw new EditException($"MIDI FX slot '{slot.Name}' is not a Scripter: it takes no script.");
        }

        if (c.Source is not { Length: <= MaxScriptLength } source) {
            throw new EditException($"A script has at most {MaxScriptLength} characters.");
        }

        return [
            WithMidiEffects(
                state,
                track,
                [..track.Strip.MidiEffects.Select(i => i.Id == slot.Id ? i with { Source = source } : i)]
            )
        ];
    }

    /// <summary>
    ///     A MIDI FX slot's run-time parameter list as the host reported it (a script compiled): stored in
    ///     <c>runtimeParameters</c>, with the values kept by id (a parameter new to the slot starts at its default; one
    ///     the script no longer defines keeps its value, so a script that failed to compile, which reports none, gets
    ///     them back when it is fixed). Null when the track or slot is gone, or nothing changes.
    /// </summary>
    public static TrackUpserted? WithRuntimeParameters(
        ProjectState state,
        string trackId,
        string slotId,
        IReadOnlyList<ParameterInfo> parameters
    ) {
        if (state.FindTrack(trackId) is not { } track
            || track.Strip.MidiEffects.FirstOrDefault(i => i.Id == slotId) is not { } slot) {
            return null;
        }

        var list = parameters.Where(static p => p?.Id is { Length: > 0 and <= MaxParameterIdLength })
            .DistinctBy(static p => p.Id)
            .ToArray();
        // Every value stays by id, so a script that failed to compile (an empty list) gets them back once fixed.
        var values = PluginCatalog.Sorted(slot.Parameters);
        foreach (var p in list) {
            values = values.SetItem(
                p.Id,
                slot.Parameters.TryGetValue(p.Id, out var kept) ? Math.Clamp(kept, p.Min, p.Max) : p.Default
            );
        }

        var changed = slot with { RuntimeParameters = list, Parameters = values };
        if (slot.RuntimeParameters is { } old
            && old.SequenceEqual(list)
            && slot.Parameters.Count == values.Count
            && values.All(v => slot.Parameters.TryGetValue(v.Key, out var x) && x.Equals(v.Value))) {
            return null;
        }

        return new(
            track with {
                Strip = track.Strip with {
                    MidiEffects = [..track.Strip.MidiEffects.Select(i => i.Id == slot.Id ? changed : i)]
                }
            },
            state.IndexOfTrack(track.Id)
        );
    }
}
