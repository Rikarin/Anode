using Anode.Contracts;
using Anode.Ipc.Segments;

namespace Anode.Model.Compile;

/// <summary>
///     Compiles Live Loops cells (format 4, docs/07-phase-6.md) into <see cref="CellModel" />s: each cell's content as a
///     region at tick 0 (MIDI and pattern notes as events, audio as a playlist entry with its warp), positions relative
///     to the cell's start, at the project's tempo at its start (a cell loops at one tempo wherever it is launched) and
///     Varispeed's speed; its length in samples (an audio cell at most 60 s). A Drum Machine Designer's cell keeps its
///     notes on the DMD's track: the host routes them through the track's note routes, as it does live MIDI. Incremental:
///     a cell that did not change is the very instance of the previous compilation.
/// </summary>
public sealed class CellCompiler {
    /// <summary>The longest an audio cell plays, in seconds (it is held in memory whole).</summary>
    public const double MaxAudioSeconds = 60;

    readonly Dictionary<string, (CellKey Key, CellModel Model)> cache = new(StringComparer.Ordinal);

    public IReadOnlyList<CellModel> Compile(ProjectState state, EngineIds ids, Varispeed varispeed) {
        if (state.LiveLoops.Cells.Count == 0) {
            cache.Clear();
            return [];
        }

        var at = CellState(state);
        var models = new List<CellModel>(state.LiveLoops.Cells.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in state.LiveLoops.Cells) {
            if (state.FindTrack(cell.TrackId) is not { } track || !Stacks.IsPlayable(track)) {
                continue;
            }

            seen.Add(cell.Id);
            var key = new CellKey(
                cell,
                track.FlexOn,
                track.FlexMode,
                at.Settings.TempoEvents![0],
                state.SampleRate,
                varispeed,
                cell.Content.Audio is { } a ? state.Files.GetValueOrDefault(a.FileId) : null,
                cell.Kind == CellKind.Pattern ? state.Settings.Keys ?? [] : []
            );
            if (!cache.TryGetValue(cell.Id, out var cached) || !cached.Key.Equals(key)) {
                cached = (key, Compile(at, ids, track, cell, varispeed));
                cache[cell.Id] = cached;
            }

            models.Add(cached.Model);
        }

        foreach (var stale in cache.Keys.Where(k => !seen.Contains(k)).ToArray()) {
            cache.Remove(stale);
        }

        return [..models.OrderBy(static m => m.Index)];
    }

    /// <summary>The project at one tempo (its tempo at the start) and one signature: what cells play at.</summary>
    static ProjectState CellState(ProjectState state) {
        var settings = state.Settings;
        var bpm = state.TempoMap.BpmAt(0);
        var signature = state.SignatureMap.At(0);
        return state with {
            Settings = settings with {
                Tempo = bpm,
                TempoEvents = [new(0, bpm, TempoCurve.Step)],
                Signatures = [signature],
                Varispeed = ProjectDefaults.Varispeed
            }
        };
    }

    static CellModel Compile(ProjectState state, EngineIds ids, Track track, Cell cell, Varispeed varispeed) {
        var map = state.TempoMap;
        var engineId = ids.Track(track.Id);
        var length = map.SampleAt(cell.LengthTicks, state.SampleRate);
        TrackModel model;
        if (cell.Content.Audio is { } audio) {
            length = Math.Min(length, (long)(MaxAudioSeconds * state.SampleRate));
            var region = audio with { StartTick = 0, Muted = false, TrackId = track.Id };
            var (playlist, warp, pitch) = ModelCompiler.Playlist(state, map, ids, [region], null, track);
            model = new(engineId, [], playlist, warp, pitch);
        } else if (cell.Content.Midi is { } midi) {
            model = new(
                engineId,
                ModelCompiler.MidiEvents(state, map, engineId, [midi with { StartTick = 0, Muted = false }]),
                []
            );
        } else {
            var pattern = cell.Content.Pattern!;
            model = new(
                engineId,
                ModelCompiler.PatternEvents(state, map, engineId, [pattern with { StartTick = 0, Muted = false }]),
                []
            );
        }

        model = varispeed.Scaled(model);
        length = Math.Max(1, varispeed.Sample(length));
        return new(
            ids.Cell(cell.Id),
            engineId,
            cell.PlayMode switch {
                CellPlayMode.OneShot => CellMode.OneShot,
                CellPlayMode.Retrigger => CellMode.Retrigger,
                _ => CellMode.Loop
            },
            length,
            [..model.Events.Where(e => e.SamplePos < length)],
            [..model.Playlist.Where(e => e.Start < length)],
            model.Warp,
            model.Pitch
        );
    }

    sealed record CellKey(
        Cell Cell,
        bool FlexOn,
        FlexMode FlexMode,
        TempoEvent Tempo,
        int SampleRate,
        Varispeed Varispeed,
        AudioFile? File,
        IReadOnlyList<KeyEvent> Keys
    ) {
        public bool Equals(CellKey? other) =>
            other is not null
            && Cell == other.Cell
            && FlexOn == other.FlexOn
            && FlexMode == other.FlexMode
            && Tempo == other.Tempo
            && SampleRate == other.SampleRate
            && Varispeed == other.Varispeed
            && File == other.File
            && Keys.SequenceEqual(other.Keys);

        public override int GetHashCode() => HashCode.Combine(Cell.Id, SampleRate);
    }
}
