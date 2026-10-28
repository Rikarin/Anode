using Anode.Contracts;
using System.Collections.Immutable;

namespace Anode.Model.Editing;

/// <summary>
///     The Live Loops grid (docs/07-phase-6.md): scenes, cells (at most one per track and scene), the launch quantise,
///     its visibility, and track activation. Every grid edit is one <see cref="LiveLoopsChanged" />.
/// </summary>
public static partial class Edits {
    public const int MaxScenes = 256;
    public const string RemixFxId = "anode.remix-fx";

    static List<DeltaOp> AddScene(ProjectState state, AddSceneCommand c, IIdSource ids) {
        var grid = state.LiveLoops;
        if (grid.Scenes.Count >= MaxScenes) {
            throw new EditException($"The grid has at most {MaxScenes} scenes.");
        }

        var index = c.Index ?? grid.Scenes.Count;
        if (index < 0 || index > grid.Scenes.Count) {
            throw new EditException($"Scene index {index} is outside 0…{grid.Scenes.Count}.");
        }

        var name = c.Name is null ? NextSceneName(grid) : Name(c.Name, "scene name");
        var scene = new Scene(ids.Next(IdPrefix.Scene), name);
        return [new LiveLoopsChanged(grid with { Scenes = grid.Scenes.ToImmutableList().Insert(index, scene) })];
    }

    static string NextSceneName(LiveLoopsGrid grid) {
        for (var n = grid.Scenes.Count + 1;; n++) {
            var name = $"Scene {n}";
            if (!grid.Scenes.Any(s => s.Name == name)) {
                return name;
            }
        }
    }

    static List<DeltaOp> RemoveScene(ProjectState state, RemoveSceneCommand c) {
        var grid = state.LiveLoops;
        var scene = SceneOf(grid, c.SceneId);
        return [
            new LiveLoopsChanged(
                grid with {
                    Scenes = [..grid.Scenes.Where(s => s.Id != scene.Id)],
                    Cells = [..grid.Cells.Where(x => x.SceneId != scene.Id)]
                }
            )
        ];
    }

    static List<DeltaOp> SetScene(ProjectState state, SetSceneCommand c) {
        var grid = state.LiveLoops;
        var scene = SceneOf(grid, c.SceneId);
        var changed = scene with { Name = c.Name is null ? scene.Name : Name(c.Name, "scene name") };
        var scenes = grid.Scenes.Where(s => s.Id != scene.Id).ToImmutableList();
        var index = c.Index ?? grid.Scenes.ToList().FindIndex(s => s.Id == scene.Id);
        if (index < 0 || index > scenes.Count) {
            throw new EditException($"Scene index {index} is outside 0…{scenes.Count}.");
        }

        return [new LiveLoopsChanged(grid with { Scenes = scenes.Insert(index, changed) })];
    }

    static List<DeltaOp> SetCell(ProjectState state, SetCellCommand c, IIdSource ids) {
        var grid = state.LiveLoops;
        var track = TrackOf(state, c.TrackId);
        var scene = SceneOf(grid, c.SceneId);
        var existing = grid.Cells.FirstOrDefault(x => x.TrackId == track.Id && x.SceneId == scene.Id);
        if (existing is null && c.Content is null) {
            throw new EditException("A new cell needs its content.");
        }

        var id = existing?.Id ?? ids.Next(IdPrefix.Cell);
        var (kind, content) = c.Content is null
            ? (existing!.Kind, existing.Content)
            : CheckContent(state, track, id, c.Content, existing);
        if (c.PlayMode is { } mode && !Enum.IsDefined(mode)) {
            throw new EditException($"Unknown cell play mode {mode}.");
        }

        if (c.Quantize is { } q && !Enum.IsDefined(q)) {
            throw new EditException($"Unknown launch quantise {q}.");
        }

        var length = c.LengthTicks ?? (c.Content is null ? existing!.LengthTicks : ContentLength(state, content));
        CheckLength(length);
        var cell = new Cell(
            id,
            track.Id,
            scene.Id,
            kind,
            c.Name is null ? existing?.Name ?? ContentName(content) : Name(c.Name, "cell name"),
            length,
            c.PlayMode ?? existing?.PlayMode ?? CellPlayMode.Loop,
            c.GridQuantize == true ? null : c.Quantize ?? existing?.Quantize,
            content
        );
        return [new LiveLoopsChanged(grid with { Cells = WithCell(grid.Cells, cell) })];
    }

    /// <summary>The cells with <paramref name="cell" /> in its slot (replacing the one there, or its old self).</summary>
    static List<Cell> WithCell(IReadOnlyList<Cell> cells, Cell cell) {
        var list = cells.Where(x => x.Id != cell.Id && !(x.TrackId == cell.TrackId && x.SceneId == cell.SceneId))
            .ToList();
        var at = cells.ToList().FindIndex(x => x.Id == cell.Id || (x.TrackId == cell.TrackId && x.SceneId == cell.SceneId));
        list.Insert(at < 0 ? list.Count : Math.Min(at, list.Count), cell);
        return list;
    }

    /// <summary>
    ///     A cell's content as kept: exactly one snapshot, of a kind the track holds, its id the cell's, on the track, at
    ///     tick 0; a MIDI cell's note and controller ids renumbered where they clash with the project's.
    /// </summary>
    static (CellKind Kind, CellContent Content) CheckContent(
        ProjectState state,
        Track track,
        string cellId,
        CellContent content,
        Cell? existing
    ) {
        var set = (content.Audio is null ? 0 : 1) + (content.Midi is null ? 0 : 1) + (content.Pattern is null ? 0 : 1);
        if (set != 1) {
            throw new EditException("A cell's content is exactly one of audio, MIDI or a pattern.");
        }

        if (content.Audio is { } audio) {
            if (track.Kind != TrackKind.Audio) {
                throw new EditException($"Track '{track.Name}' is not an audio track: it holds no audio cells.");
            }

            var file = FileOf(state, audio.FileId ?? string.Empty);
            CheckSpan(file, audio.FileOffset, audio.Length);
            if (!(audio.GainDb is >= MinRegionGainDb and <= MaxRegionGainDb)) {
                throw new EditException($"Region gain {audio.GainDb} dB is outside {MinRegionGainDb}…{MaxRegionGainDb} dB.");
            }

            return (CellKind.Audio, new(
                Audio: audio with {
                    Id = cellId,
                    TrackId = track.Id,
                    StartTick = 0,
                    Name = Name(audio.Name ?? file.Name, "region name"),
                    FadeIn = Math.Clamp(audio.FadeIn, 0, audio.Length),
                    FadeOut = Math.Clamp(audio.FadeOut, 0, audio.Length - Math.Clamp(audio.FadeIn, 0, audio.Length)),
                    FadeInCurve = Math.Clamp(audio.FadeInCurve, -1, 1),
                    FadeOutCurve = Math.Clamp(audio.FadeOutCurve, -1, 1),
                    Follow = Enum.IsDefined(audio.Follow) ? audio.Follow : FlexFollow.Off,
                    FlexMarkers = [..audio.FlexMarkers ?? []],
                    PitchNotes = [..audio.PitchNotes ?? []]
                }
            ));
        }

        if (!HoldsMidi(track)) {
            throw new EditException($"Track '{track.Name}' holds no MIDI: it takes audio cells.");
        }

        if (content.Pattern is { } pattern) {
            CheckLength(pattern.LengthTicks);
            return (CellKind.Pattern, new(
                Pattern: pattern with {
                    Id = cellId,
                    TrackId = track.Id,
                    StartTick = 0,
                    Name = Name(pattern.Name ?? "Pattern", "region name"),
                    Pattern = CheckPattern(pattern.Pattern)
                }
            ));
        }

        var midi = content.Midi!;
        CheckLength(midi.LengthTicks);
        var used = UsedEventIds(state, existing?.Id);
        var next = state.LastEventId;
        var notes = new List<Note>();
        foreach (var note in midi.Notes ?? []) {
            if (note is null || note.Pitch is < 0 or > 127 || note.Velocity is < 1 or > 127 || note.Channel is < 1 or > 16
                || note.StartTick < 0 || note.LengthTicks < 1) {
                throw new EditException("A note has pitch 0…127, velocity 1…127, channel 1…16 and a positive length.");
            }

            notes.Add(note.Id <= 0 || !used.Add(note.Id) ? note with { Id = ++next } : note);
        }

        var controls = new List<ControlEvent>();
        foreach (var control in midi.Controls ?? []) {
            if (control is null || control.Tick < 0 || control.Channel is < 1 or > 16 || !Enum.IsDefined(control.Kind)) {
                throw new EditException("A controller event has a known kind, a channel 1…16 and a tick of 0 or more.");
            }

            controls.Add(control.Id <= 0 || !used.Add(control.Id) ? control with { Id = ++next } : control);
        }

        return (CellKind.Midi, new(
            Midi: midi with {
                Id = cellId,
                TrackId = track.Id,
                StartTick = 0,
                Name = Name(midi.Name ?? "MIDI", "region name"),
                Playback = midi.Playback ?? ProjectDefaults.Playback,
                Notes = Sorted([..notes]),
                Controls = Sorted([..controls])
            }
        ));
    }

    /// <summary>The note and controller ids the project uses (except in cell <paramref name="except" />).</summary>
    static HashSet<long> UsedEventIds(ProjectState state, string? except) {
        var used = new HashSet<long>();
        foreach (var region in state.MidiRegions.Values) {
            used.UnionWith(region.Notes.Select(static n => n.Id));
            used.UnionWith(region.Controls.Select(static e => e.Id));
        }

        foreach (var cell in state.LiveLoops.Cells) {
            if (cell.Id != except && cell.Content.Midi is { } midi) {
                used.UnionWith(midi.Notes.Select(static n => n.Id));
                used.UnionWith(midi.Controls.Select(static e => e.Id));
            }
        }

        return used;
    }

    /// <summary>How long content is in ticks, as a new cell loops it.</summary>
    static long ContentLength(ProjectState state, CellContent content) =>
        content.Midi?.LengthTicks
        ?? content.Pattern?.LengthTicks
        ?? Math.Max(1, EndTick(state, content.Audio!));

    static string ContentName(CellContent content) =>
        content.Midi?.Name ?? content.Pattern?.Name ?? content.Audio?.Name ?? "Cell";

    static List<DeltaOp> DeleteCells(ProjectState state, DeleteCellsCommand c) {
        if (c.CellIds is not { Count: > 0 }) {
            throw new EditException("Name at least one cell to delete.");
        }

        var grid = state.LiveLoops;
        var ids = c.CellIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids) {
            _ = CellOf(grid, id);
        }

        return [new LiveLoopsChanged(grid with { Cells = [..grid.Cells.Where(x => !ids.Contains(x.Id))] })];
    }

    static List<DeltaOp> MoveCell(ProjectState state, MoveCellCommand c, IIdSource ids) {
        var grid = state.LiveLoops;
        var cell = CellOf(grid, c.CellId);
        var track = TrackOf(state, c.TrackId);
        var scene = SceneOf(grid, c.SceneId);
        if (!c.Copy && cell.TrackId == track.Id && cell.SceneId == scene.Id) {
            return [];
        }

        var id = c.Copy ? ids.Next(IdPrefix.Cell) : cell.Id;
        // A copy's MIDI renumbers (its ids clash with the original's); a move keeps them.
        var (_, content) = CheckContent(state, track, id, cell.Content, c.Copy ? null : cell);
        var moved = cell with { Id = id, TrackId = track.Id, SceneId = scene.Id, Content = content };
        var cells = c.Copy ? grid.Cells : [..grid.Cells.Where(x => x.Id != cell.Id)];
        return [new LiveLoopsChanged(grid with { Cells = WithCell(cells, moved) })];
    }

    static List<DeltaOp> SetLiveLoops(ProjectState state, SetLiveLoopsCommand c, IIdSource ids, PluginCatalog plugins) {
        var grid = state.LiveLoops;
        if (c.Quantize is { } q && !Enum.IsDefined(q)) {
            throw new EditException($"Unknown launch quantise {q}.");
        }

        var ops = new List<DeltaOp> {
            new LiveLoopsChanged(grid with { Quantize = c.Quantize ?? grid.Quantize, Shown = c.Shown ?? grid.Shown })
        };
        // Remix FX goes on the master the first time the grid is shown (when the engine has it).
        if (c.Shown == true
            && !grid.Shown
            && plugins.Find(RemixFxId) is { } remix
            && state.Master.Inserts.All(i => i.PluginId != RemixFxId)
            && state.Master.Inserts.Count < MaxInserts) {
            var insert = new Insert(
                ids.Next(IdPrefix.Insert),
                remix.Id,
                remix.Name,
                false,
                PluginCatalog.Defaults(remix).Remove(HostTempoParameter),
                null
            );
            ops.Add(new MasterChanged(state.Master with { Inserts = [..state.Master.Inserts, insert] }));
        }

        return ops;
    }

    static List<DeltaOp> SetTrackActivation(ProjectState state, SetTrackActivationCommand c) {
        if (c.TrackIds is not { Count: > 0 }) {
            throw new EditException("Name at least one track.");
        }

        var ops = new List<DeltaOp>();
        foreach (var id in c.TrackIds.Distinct(StringComparer.Ordinal)) {
            var track = TrackOf(state, id ?? string.Empty);
            if (track.Kind != TrackKind.Audio && !HoldsMidi(track)) {
                throw new EditException($"A {Kind(track.Kind)} track has no Live Loops cells.");
            }

            if (track.LiveLoopsActive != c.LiveLoopsActive) {
                ops.Add(new TrackUpserted(track with { LiveLoopsActive = c.LiveLoopsActive }, state.IndexOfTrack(id!)));
            }
        }

        return ops;
    }

    static Scene SceneOf(LiveLoopsGrid grid, string? id) =>
        grid.Scenes.FirstOrDefault(s => s.Id == id) ?? throw new EditException($"No scene '{id}'.");

    static Cell CellOf(LiveLoopsGrid grid, string? id) =>
        grid.Cells.FirstOrDefault(x => x.Id == id) ?? throw new EditException($"No cell '{id}'.");
}
