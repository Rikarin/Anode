using Anode.Contracts;
using Anode.Ipc.Segments;
using Anode.Model.Time;
using System.Runtime.InteropServices;

namespace Anode.Model.Compile;

/// <summary>
///     The compiled playback model: one <see cref="TrackModel" /> per track in track order, then the metronome's
///     (<see cref="TrackEntry.MetronomeTrack" />); its length in samples; and the engine ids this compilation had to
///     recompile (the rest are the very instances of the previous one).
/// </summary>
public sealed record CompiledModel(
    IReadOnlyList<TrackModel> Tracks,
    long LengthSamples,
    IReadOnlyList<int> Recompiled) {
    /// <summary>The compiled tempo map (format 4, <see cref="TempoPoints" />).</summary>
    public IReadOnlyList<TempoMapPoint> Tempo { get; init; } = [];

    /// <summary>Every Live Loops cell's content (format 4, <see cref="CellCompiler" />), by cell index.</summary>
    public IReadOnlyList<CellModel> Cells { get; init; } = [];

    /// <summary>
    ///     Each engine track's cells: the very array of the previous compilation while none of them changed, so a caller
    ///     finds the tracks whose cells are dirty by reference.
    /// </summary>
    public IReadOnlyDictionary<int, CellModel[]> CellsByTrack { get; init; } = new Dictionary<int, CellModel[]>();
}

/// <summary>
///     Compiles a <see cref="ProjectState" /> into what the host plays (docs/adr/0006-bulk-ipc.md): each track's
///     regions and take folder comps as a playlist in samples (ticks through the <see cref="TempoMap" /> and its ramps,
///     ADR-0005), its MIDI regions as events (<see cref="MidiEvents" />), and the metronome's clicks on the beats of the
///     signature track; its automation lanes as automation events (<see cref="AutomationEvents" />), and the master's
///     under engine track −1 when it has any. Muted regions and folders play nothing, and a folder stack and a VCA have
///     no engine track of their own.
///     Incremental: a track is recompiled only when its regions (audio or MIDI), take folders, their files, the tempo
///     track or the sample rate changed since the previous call; otherwise the previous <see cref="TrackModel" />
///     instance is reused, so a caller finds the dirty tracks by reference.
/// </summary>
public sealed class ModelCompiler {
    readonly Dictionary<int, (TrackKey Key, TrackModel Model)> cache = [];
    readonly Dictionary<int, (TrackModel Base, PlaybackEvent[] Automation, TrackModel Model)> automated = [];
    readonly Dictionary<int, TrackModel> previous = [];
    readonly TrackModel masterBase = new(AutomationEvents.MasterTrack, [], []);
    readonly CellCompiler cellCompiler = new();
    Dictionary<int, CellModel[]> cellsByTrack = [];
    bool masterAutomated;
    (ClickKey Key, TrackModel Model, Varispeed Varispeed)? metronome;

    /// <summary>
    ///     Compiles <paramref name="state" />; <paramref name="analyses" /> are the files' cached analyses by file id
    ///     (Flex Pitch shapes pitch notes' drift and vibrato from their pitch curves).
    /// </summary>
    public CompiledModel Compile(
        ProjectState state,
        EngineIds ids,
        IReadOnlyDictionary<string, FileAnalysis>? analyses = null
    ) {
        var map = state.TempoMap;
        var source = new Source(
            state,
            ids,
            map,
            analyses ?? new Dictionary<string, FileAnalysis>(StringComparer.Ordinal),
            state.Regions.Values.ToLookup(static r => r.TrackId, StringComparer.Ordinal),
            state.MidiRegions.Values.ToLookup(static r => r.TrackId, StringComparer.Ordinal),
            state.TakeFolders.Values.ToLookup(static f => f.TrackId, StringComparer.Ordinal)
        ) { Patterns = state.PatternRegions.Values.ToLookup(static r => r.TrackId, StringComparer.Ordinal) };
        var models = new List<TrackModel>(state.Tracks.Count + 2);
        var seen = new HashSet<int>();
        long end = 0;
        var varispeed = Varispeed.Of(state);
        foreach (var track in state.Tracks.Where(Stacks.IsPlayable)) {
            var model = varispeed.Scaled(Track(source, track));
            seen.Add(model.TrackId);
            models.Add(model);
            if (model.Playlist is [.., var last]) {
                end = Math.Max(end, last.Start + last.Length);
            }

            if (cache[model.TrackId].Model.Events is [.., var lastEvent]) {
                end = Math.Max(end, lastEvent.SamplePos + 1);
            }
        }

        // Once the master has had lanes, its entry stays (empty when they are gone), so a patch clears it.
        var master = AutomationEvents.Compile(state, map, ids, null, AutomationEvents.MasterTrack);
        masterAutomated |= master.Length > 0;
        if (masterAutomated) {
            seen.Add(AutomationEvents.MasterTrack);
            models.Add(varispeed.Scaled(WithAutomation(masterBase, master)));
        }

        var recompiled = models
            .Where(m => !previous.TryGetValue(m.TrackId, out var before) || !ReferenceEquals(before, m))
            .Select(static m => m.TrackId)
            .ToList();
        previous.Clear();
        foreach (var model in models) {
            previous[model.TrackId] = model;
        }

        Forget(seen);
        var settings = state.Settings;
        var endTick = Math.Max(settings.EndTick, settings.Cycle.EndTick);
        var tempo = settings.TempoEvents ?? [];
        var clickKey = new ClickKey(tempo, settings.Signatures ?? [], settings.Signature, endTick, state.SampleRate);
        if (metronome is not { } m || !m.Key.Equals(clickKey) || m.Varispeed != varispeed) {
            metronome = m = (clickKey,
                varispeed.Scaled(new(TrackEntry.MetronomeTrack, Clicks(state, map, endTick), [])), varispeed);
            recompiled.Add(TrackEntry.MetronomeTrack);
        }

        models.Add(m.Model);
        end = Math.Max(end, varispeed.Sample(map.SampleAt(endTick, state.SampleRate)));
        var cells = cellCompiler.Compile(state, ids, varispeed);
        return new(models, end, recompiled) {
            Tempo = TempoPoints.Of(state), Cells = cells, CellsByTrack = CellsByTrack(cells)
        };
    }

    /// <summary>The cells by engine track, reusing the previous array of a track whose cells are the same instances.</summary>
    Dictionary<int, CellModel[]> CellsByTrack(IReadOnlyList<CellModel> cells) {
        var now = new Dictionary<int, CellModel[]>();
        foreach (var group in cells.GroupBy(static c => c.TrackId)) {
            CellModel[] list = [..group.OrderBy(static c => c.Index)];
            now[group.Key] = cellsByTrack.TryGetValue(group.Key, out var before)
                && before.AsSpan().SequenceEqual(list, ReferenceEqualityComparer.Instance)
                    ? before
                    : list;
        }

        cellsByTrack = now;
        return now;
    }

    /// <summary>What a compilation reads: the project, its ids and tempo map, and its regions by track.</summary>
    sealed record Source(
        ProjectState State,
        EngineIds Ids,
        TempoMap Map,
        IReadOnlyDictionary<string, FileAnalysis> Analyses,
        ILookup<string, AudioRegion> Regions,
        ILookup<string, MidiRegion> Midi,
        ILookup<string, TakeFolder> Folders
    ) {
        public ILookup<string, PatternRegion> Patterns { get; init; } =
            Array.Empty<PatternRegion>().ToLookup(static r => r.TrackId);
    }

    /// <summary>A track's model: its cached regions and events when they did not change, with its automation.</summary>
    TrackModel Track(Source source, Track track) {
        var (state, ids, map) = (source.State, source.Ids, source.Map);
        var engineId = ids.Track(track.Id);
        var regions = source.Regions[track.Id]
            .OrderBy(static r => r.StartTick)
            .ThenBy(static r => r.Id, StringComparer.Ordinal)
            .ToArray();
        var midi = source.Midi[track.Id]
            .OrderBy(static r => r.StartTick)
            .ThenBy(static r => r.Id, StringComparer.Ordinal)
            .ToArray();
        var folders = source.Folders[track.Id]
            .OrderBy(static f => f.StartTick)
            .ThenBy(static f => f.Id, StringComparer.Ordinal)
            .ToArray();
        // A Drum Machine Designer plays nothing itself: its notes go to its pads (docs/07-phase-6.md).
        var dmd = track.DrumMachine is not null;
        if (dmd) {
            midi = [];
        }

        var patterns = dmd ? [] : Ordered(source.Patterns[track.Id]);
        var routed = Routed(source, track);
        var key = new TrackKey(
            state.Settings.TempoEvents ?? [],
            state.Settings.Tempo,
            state.SampleRate,
            [..regions.Select(r => (r, state.Files[r.FileId]))],
            midi,
            [..folders.Select(f => (f, FilesOf(state, f)))]
        ) {
            Flex = (track.FlexOn, track.FlexMode),
            Signatures = regions.Any(static r => r.Follow is FlexFollow.Bars or FlexFollow.BarsAndBeats)
                ? state.Settings.Signatures ?? []
                : [],
            Analyses = [
                ..regions.Where(static r => r.PitchNotes.Count > 0)
                    .Select(r => source.Analyses.GetValueOrDefault(r.FileId))
            ],
            Sources = new(
                patterns,
                routed?.Midi ?? [],
                routed?.Patterns ?? [],
                routed?.Note ?? -1,
                patterns.Length + (routed?.Patterns.Length ?? 0) > 0 ? state.Settings.Keys ?? [] : []
            )
        };
        if (!cache.TryGetValue(engineId, out var cached) || !cached.Key.Equals(key)) {
            var (playlist, warp, pitch) = Playlist(state, map, ids, regions, folders, track, source.Analyses);
            var events = Merge(
                MidiEvents(state, map, engineId, midi),
                PatternEvents(state, map, engineId, patterns),
                routed is { } r ? RoutedEvents(state, map, engineId, r) : []
            );
            cached = (key, new(engineId, events, playlist, warp, pitch));
            cache[engineId] = cached;
        }

        return WithAutomation(cached.Model, AutomationEvents.Compile(state, map, ids, track, engineId));
    }

    static PatternRegion[] Ordered(IEnumerable<PatternRegion> regions) => [
        ..regions.OrderBy(static r => r.StartTick).ThenBy(static r => r.Id, StringComparer.Ordinal)
    ];

    /// <summary>What a pad of a Drum Machine Designer plays of its DMD's regions: their notes on its pad's note.</summary>
    sealed record Route(MidiRegion[] Midi, PatternRegion[] Patterns, int Note);

    /// <summary>The DMD notes routed to <paramref name="track" /> when it is a pad (none while its DMD plays its cells).</summary>
    static Route? Routed(Source source, Track track) {
        if (track.ParentId is null
            || source.State.FindTrack(track.ParentId) is not { DrumMachine: { } machine, LiveLoopsActive: false } dmd
            || machine.Pads.FirstOrDefault(p => p.TrackId == track.Id) is not { } pad) {
            return null;
        }

        return new(
            [
                ..source.Midi[dmd.Id]
                    .OrderBy(static r => r.StartTick)
                    .ThenBy(static r => r.Id, StringComparer.Ordinal)
            ],
            Ordered(source.Patterns[dmd.Id]),
            pad.Note
        );
    }

    /// <summary>
    ///     A DMD's notes on a pad's note, on the pad's engine track with their note number unchanged (as the host routes
    ///     live MIDI; the pad's instrument plays its own sound whatever the note).
    /// </summary>
    static PlaybackEvent[] RoutedEvents(ProjectState state, TempoMap map, int engineId, Route route) {
        var all = Merge(
            MidiEvents(state, map, engineId, route.Midi),
            PatternEvents(state, map, engineId, route.Patterns)
        );
        return [
            ..all.Where(e => e.Kind is PlaybackEventKind.NoteOn or PlaybackEventKind.NoteOff && e.Data1 == route.Note)
        ];
    }

    /// <summary>
    ///     Pattern regions' notes as events (<see cref="PatternUnroller" />): unmuted regions only, notes cut at their
    ///     region's end, channel 1, sorted with note-offs before note-ons at equal samples.
    /// </summary>
    public static PlaybackEvent[] PatternEvents(
        ProjectState state,
        TempoMap map,
        int engineId,
        IReadOnlyList<PatternRegion> regions
    ) {
        if (regions.Count == 0) {
            return [];
        }

        var events = new List<PlaybackEvent>();
        foreach (var region in regions) {
            if (region.Muted) {
                continue;
            }

            foreach (var note in PatternUnroller.Unroll(region, t => Editing.Edits.KeyAt(state, t))) {
                var at = region.StartTick + note.Tick;
                var on = map.SampleAt(at, state.SampleRate);
                var off = Math.Max(map.SampleAt(at + note.Length, state.SampleRate), on + 1);
                var pitch = (byte)note.Pitch;
                events.Add(Midi(engineId, on, PlaybackEventKind.NoteOn, pitch, (byte)note.Velocity, 0));
                events.Add(Midi(engineId, off, PlaybackEventKind.NoteOff, pitch, 0, 0));
            }
        }

        return Merge([..events]);
    }

    /// <summary>
    ///     Event lists merged into one, sorted by sample with note-offs, then other events, then note-ons at an equal
    ///     sample, each list's order kept otherwise (a stable sort).
    /// </summary>
    public static PlaybackEvent[] Merge(params PlaybackEvent[][] lists) {
        var nonEmpty = lists.Where(static l => l.Length > 0).ToArray();
        if (nonEmpty.Length == 0) {
            return [];
        }

        return [
            ..nonEmpty.SelectMany(static l => l)
                .Select(static (e, i) => (e, i))
                .OrderBy(static x => x.e.SamplePos)
                .ThenBy(static x => Rank(x.e.Kind))
                .ThenBy(static x => x.i)
                .Select(static x => x.e)
        ];
    }

    static int Rank(PlaybackEventKind kind) =>
        kind switch {
            PlaybackEventKind.NoteOff => 0,
            PlaybackEventKind.NoteOn => 2,
            _ => 1
        };

    /// <summary>Drops the cached models of engine tracks that are gone.</summary>
    void Forget(HashSet<int> seen) {
        foreach (var stale in cache.Keys.Where(k => !seen.Contains(k)).ToArray()) {
            cache.Remove(stale);
        }

        foreach (var stale in automated.Keys.Where(k => !seen.Contains(k)).ToArray()) {
            automated.Remove(stale);
        }
    }

    /// <summary>
    ///     A track's model with its automation events merged in (before other events at an equal sample): the very
    ///     instance of the previous compilation when neither changed.
    /// </summary>
    TrackModel WithAutomation(TrackModel model, PlaybackEvent[] automation) {
        if (automated.TryGetValue(model.TrackId, out var cached)
            && ReferenceEquals(cached.Base, model)
            && MemoryMarshal.AsBytes(cached.Automation.AsSpan())
                .SequenceEqual(MemoryMarshal.AsBytes(automation.AsSpan()))) {
            return cached.Model;
        }

        var merged = automation.Length == 0
            ? model
            : new TrackModel(
                model.TrackId,
                [
                    ..automation.Select(static e => (e, 0))
                        .Concat(model.Events.Select(static e => (e, 1)))
                        .Select(static (x, i) => (x.e, x.Item2, i))
                        .OrderBy(static x => x.e.SamplePos)
                        .ThenBy(static x => x.Item2)
                        .ThenBy(static x => x.i)
                        .Select(static x => x.e)
                ],
                model.Playlist,
                model.Warp,
                model.Pitch
            );
        automated[model.TrackId] = (model, automation, merged);
        return merged;
    }

    /// <summary>
    ///     A track's playlist (docs/04-phase-3.md, "Crossfades"): its unmuted regions and the comps of its unmuted take
    ///     folders in samples, sorted by start. Entries overlap only as crossfades — where the earlier fades out and the
    ///     later fades in over all of the overlap, never three at once. Where entries overlap any other way, the one
    ///     that starts later plays (at an equal start, the later in region order), and the earlier one resumes after
    ///     it, without the fade it would have had at that point. Warped regions (docs/06-phase-5.md) carry their warp
    ///     and pitch points, which their entries index in the returned arrays (null when the track has none).
    /// </summary>
    public static (AudioPlaylistEntry[] Playlist, WarpPoint[]? Warp, PitchPoint[]? Pitch) Playlist(
        ProjectState state,
        TempoMap map,
        EngineIds ids,
        IReadOnlyList<AudioRegion> regions,
        IReadOnlyList<TakeFolder>? folders = null,
        Track? track = null,
        IReadOnlyDictionary<string, FileAnalysis>? analyses = null
    ) {
        // Regions and folders in one order (start tick, then id): later in it wins an overlap.
        var placed = new List<(long Tick, string Id, AudioRegion? Region, TakeFolder? Folder)>(regions.Count);
        placed.AddRange(
            regions.Where(static r => !r.Muted)
                .Select(static r => (r.StartTick, r.Id, (AudioRegion?)r, (TakeFolder?)null))
        );
        placed.AddRange(
            (folders ?? []).Where(static f => !f.Muted)
            .Select(static f => (f.StartTick, f.Id, (AudioRegion?)null, (TakeFolder?)f))
        );

        placed.Sort(static (a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : string.CompareOrdinal(a.Id, b.Id));
        var entries = new List<(PlacedEntry Entry, int Order)>();
        foreach (var (_, _, region, folder) in placed) {
            if (region is not null) {
                var entry = Warps.Entry(
                    RegionTiming.Of(state, region),
                    track ?? state.FindTrack(region.TrackId)!,
                    map,
                    state.SampleRate,
                    ids.File(region.FileId),
                    analyses?.GetValueOrDefault(region.FileId)
                );
                entries.Add((entry, entries.Count));
                continue;
            }

            foreach (var piece in Comps.Pieces(state, folder!)) {
                entries.Add((new(Entry(piece, ids)), entries.Count));
            }
        }

        var sorted = entries.OrderBy(static x => x.Entry.Start)
            .ThenBy(static x => x.Order)
            .Select(static x => x.Entry)
            .ToArray();
        return Indexed(Overlaps(sorted) ? Resolve(sorted) : sorted);
    }

    /// <summary>The entries with their warp and pitch points gathered into the track's arrays, indexed.</summary>
    static (AudioPlaylistEntry[] Playlist, WarpPoint[]? Warp, PitchPoint[]? Pitch) Indexed(PlacedEntry[] placed) {
        if (placed.All(static p => p.Warp is null)) {
            return ([..placed.Select(static p => p.Entry)], null, null);
        }

        var warp = new List<WarpPoint>();
        var pitch = new List<PitchPoint>();
        var playlist = new AudioPlaylistEntry[placed.Length];
        for (var i = 0; i < placed.Length; i++) {
            var p = placed[i];
            var entry = p.Entry;
            if (p.Warp is { } w) {
                entry = entry with { WarpStart = warp.Count, WarpCount = w.Length };
                warp.AddRange(w);
                if (p.Pitch is { Length: > 0 } pp) {
                    entry = entry with { PitchStart = pitch.Count, PitchCount = pp.Length };
                    pitch.AddRange(pp);
                }
            }

            playlist[i] = entry;
        }

        return (playlist, [..warp], [..pitch]);
    }

    /// <summary>Sorted, overlapping entries as the host plays them: crossfades kept, other overlaps flattened.</summary>
    static PlacedEntry[] Resolve(PlacedEntry[] sorted) {
        // Each run of overlapping entries stays as it is when it is a chain of crossfades, else is flattened.
        var result = new List<PlacedEntry>(sorted.Length);
        for (var i = 0; i < sorted.Length;) {
            var end = sorted[i].End;
            var j = i + 1;
            while (j < sorted.Length && sorted[j].Start < end) {
                end = Math.Max(end, sorted[j].End);
                j++;
            }

            var cluster = sorted[i..j];
            result.AddRange(cluster.Length == 1 || IsCrossfadeChain(cluster) ? cluster : Flatten(cluster));
            i = j;
        }

        return [..result];
    }

    /// <summary>A comp piece: unity gain, linear (curve 0) crossfades.</summary>
    static AudioPlaylistEntry Entry(CompPiece piece, EngineIds ids) =>
        new() {
            Start = piece.Start,
            FileOffset = piece.FileOffset,
            Length = piece.Length,
            FileId = ids.File(piece.FileId),
            Gain = 1,
            FadeIn = (int)Math.Min(piece.FadeIn, int.MaxValue),
            FadeOut = (int)Math.Min(piece.FadeOut, int.MaxValue)
        };

    /// <summary>
    ///     Whether sorted, overlapping entries are a chain of crossfades: each overlap lies inside both the earlier's
    ///     fade-out and the later's fade-in, and no entry overlaps any but its neighbours.
    /// </summary>
    static bool IsCrossfadeChain(PlacedEntry[] cluster) {
        for (var k = 1; k < cluster.Length; k++) {
            var (previous, current) = (cluster[k - 1].Entry, cluster[k].Entry);
            var overlap = previous.Start + previous.Length - current.Start;
            if (overlap > previous.FadeOut
                || overlap > current.FadeIn
                || current.Start + current.Length < previous.Start + previous.Length
                || (k >= 2 && cluster[k - 2].End > current.Start)) {
                return false;
            }
        }

        return true;
    }

    static bool Overlaps(PlacedEntry[] sorted) {
        for (var i = 1; i < sorted.Length; i++) {
            if (sorted[i].Start < sorted[i - 1].End) {
                return true;
            }
        }

        return false;
    }

    static PlacedEntry[] Flatten(PlacedEntry[] sorted) {
        var bounds = sorted.SelectMany(static e => new[] { e.Start, e.End }).Distinct().Order().ToArray();
        var pieces = new List<PlacedEntry>();
        int? current = null;
        long pieceStart = 0;
        for (var b = 0; b < bounds.Length; b++) {
            // The entry that plays from bounds[b]: the latest-starting one covering it.
            int? top = null;
            if (b < bounds.Length - 1) {
                for (var i = 0; i < sorted.Length; i++) {
                    if (sorted[i].Start <= bounds[b] && bounds[b] < sorted[i].End) {
                        top = i;
                    }
                }
            }

            if (top == current) {
                continue;
            }

            if (current is { } c) {
                pieces.Add(Piece(sorted[c], pieceStart, bounds[b]));
            }

            current = top;
            pieceStart = bounds[b];
        }

        return [..pieces];
    }

    /// <summary>The part of an entry from <paramref name="start" /> to <paramref name="end" /> (a warped one's map sliced).</summary>
    static PlacedEntry Piece(PlacedEntry placed, long start, long end) {
        var e = placed.Entry;
        var length = end - start;
        var entry = e with {
            Start = start,
            FileOffset = placed.Warp is null ? e.FileOffset + (start - e.Start) : e.FileOffset,
            Length = length,
            FadeIn = start == e.Start ? (int)Math.Min(e.FadeIn, length) : 0,
            FadeOut = end == e.Start + e.Length ? (int)Math.Min(e.FadeOut, length) : 0
        };
        return placed.Warp is { } warp
            ? placed with { Entry = entry, Warp = Warps.Slice(warp, start - e.Start, length) }
            : placed with { Entry = entry };
    }

    /// <summary>
    ///     A click on every beat of the signature track from 0 to <paramref name="endTick" />, through the tempo map;
    ///     <c>Data1</c> is 1 on a bar's downbeat.
    /// </summary>
    public static PlaybackEvent[] Clicks(ProjectState state, TempoMap map, long endTick) {
        var clicks = new List<PlaybackEvent>();
        foreach (var (tick, downbeat) in state.SignatureMap.Beats(endTick)) {
            clicks.Add(
                new() {
                    SamplePos = map.SampleAt(tick, state.SampleRate),
                    Track = TrackEntry.MetronomeTrack,
                    Kind = PlaybackEventKind.Click,
                    Data1 = (byte)(downbeat ? 1 : 0),
                    Value = 1
                }
            );
        }

        return [..clicks];
    }

    /// <summary>
    ///     A track's MIDI regions as events in samples (docs/03-phase-2.md, "Compilation"): region playback applied
    ///     (quantise, transpose clamped to 0…127, velocity offset clamped to 1…127), only what starts inside the part of
    ///     its region that plays, notes cut where that part ends, channels 0…15, sorted with note-offs before
    ///     controllers before note-ons at equal samples. Where regions overlap, the later-starting one plays.
    /// </summary>
    public static PlaybackEvent[] MidiEvents(
        ProjectState state,
        TempoMap map,
        int engineId,
        IReadOnlyList<MidiRegion> regions
    ) {
        if (regions.Count == 0) {
            return [];
        }

        regions = [..regions.Where(static r => !r.Muted)];
        var events = new List<(PlaybackEvent Event, int Rank)>();
        for (var i = 0; i < regions.Count; i++) {
            if (Windows(regions, i) is { Count: > 0 } windows) {
                AddNotes(events, state, map, engineId, regions[i], windows);
                AddControls(events, state, map, engineId, regions[i], windows);
            }
        }

        // A stable sort: equal samples keep note-offs, then controllers, then note-ons, each in region order.
        return [
            ..events.Select(static (e, i) => (e.Event, e.Rank, i))
                .OrderBy(static x => x.Event.SamplePos)
                .ThenBy(static x => x.Rank)
                .ThenBy(static x => x.i)
                .Select(static x => x.Event)
        ];
    }

    /// <summary>A region's notes that start where it plays, with its playback applied (rank 2 on, 0 off).</summary>
    static void AddNotes(
        List<(PlaybackEvent Event, int Rank)> events,
        ProjectState state,
        TempoMap map,
        int engineId,
        MidiRegion region,
        List<(long From, long To)> windows
    ) {
        var playback = region.Playback;
        foreach (var note in region.Notes) {
            var at = Editing.Quantize.Tick(
                region.StartTick + note.StartTick,
                playback.QuantizeTicks,
                playback.QuantizeStrength,
                playback.Swing
            );
            if (WindowAt(windows, at) is not { } window) {
                continue;
            }

            var on = map.SampleAt(at, state.SampleRate);
            var off = Math.Max(map.SampleAt(Math.Min(at + note.LengthTicks, window.To), state.SampleRate), on + 1);
            var pitch = (byte)Math.Clamp(note.Pitch + playback.Transpose, 0, 127);
            var velocity = (byte)Math.Clamp(note.Velocity + playback.Velocity, 1, 127);
            var channel = (byte)(note.Channel - 1);
            events.Add((Midi(engineId, on, PlaybackEventKind.NoteOn, pitch, velocity, channel), 2));
            events.Add((Midi(engineId, off, PlaybackEventKind.NoteOff, pitch, 0, channel), 0));
        }
    }

    /// <summary>A region's controller events where it plays (rank 1: between a sample's note-offs and note-ons).</summary>
    static void AddControls(
        List<(PlaybackEvent Event, int Rank)> events,
        ProjectState state,
        TempoMap map,
        int engineId,
        MidiRegion region,
        List<(long From, long To)> windows
    ) {
        foreach (var control in region.Controls) {
            var at = region.StartTick + control.Tick;
            if (WindowAt(windows, at) is not null) {
                events.Add((Control(engineId, map.SampleAt(at, state.SampleRate), control), 1));
            }
        }
    }

    static PlaybackEvent Control(int engineId, long sample, ControlEvent control) {
        var channel = (byte)(control.Channel - 1);
        return control.Kind switch {
            ControlKind.ControlChange => Midi(
                engineId,
                sample,
                PlaybackEventKind.ControlChange,
                (byte)control.Number,
                (byte)control.Value,
                channel
            ),
            ControlKind.PitchBend => PitchBend(engineId, sample, control.Value, channel),
            ControlKind.ChannelPressure => Midi(
                engineId,
                sample,
                PlaybackEventKind.ChannelPressure,
                (byte)control.Value,
                0,
                channel
            ),
            _ => Midi(engineId, sample, PlaybackEventKind.ProgramChange, (byte)control.Value, 0, channel)
        };
    }

    static PlaybackEvent Midi(int track, long sample, PlaybackEventKind kind, byte data1, byte data2, byte channel) =>
        new() {
            SamplePos = sample,
            Track = track,
            Kind = kind,
            Data1 = data1,
            Data2 = data2,
            Channel = channel
        };

    static PlaybackEvent PitchBend(int track, long sample, int value, byte channel) {
        var bend = Math.Clamp(value + 8192, 0, 16383);
        return Midi(track, sample, PlaybackEventKind.PitchBend, (byte)(bend & 0x7F), (byte)(bend >> 7), channel);
    }

    /// <summary>
    ///     The tick ranges where region <paramref name="index" /> plays: its own span, less the spans of the regions
    ///     after it in start order (later-starting ones; at an equal start, the later id), which win where they overlap.
    /// </summary>
    static List<(long From, long To)> Windows(IReadOnlyList<MidiRegion> regions, int index) {
        var region = regions[index];
        var windows = new List<(long From, long To)> { (region.StartTick, region.StartTick + region.LengthTicks) };
        for (var j = index + 1; j < regions.Count && windows.Count > 0; j++) {
            var (from, to) = (regions[j].StartTick, regions[j].StartTick + regions[j].LengthTicks);
            var next = new List<(long From, long To)>(windows.Count + 1);
            foreach (var w in windows) {
                if (to <= w.From || from >= w.To) {
                    next.Add(w);
                    continue;
                }

                if (from > w.From) {
                    next.Add((w.From, from));
                }

                if (to < w.To) {
                    next.Add((to, w.To));
                }
            }

            windows = next;
        }

        return windows;
    }

    static (long From, long To)? WindowAt(List<(long From, long To)> windows, long tick) {
        foreach (var w in windows) {
            if (tick >= w.From && tick < w.To) {
                return w;
            }
        }

        return null;
    }

    /// <summary>The take folder's files (a missing one as null), for the cache key.</summary>
    static AudioFile?[] FilesOf(ProjectState state, TakeFolder folder) =>
        [..folder.Takes.Select(t => state.Files.GetValueOrDefault(t.FileId))];

    sealed record TrackKey(
        IReadOnlyList<TempoEvent> TempoEvents,
        double Tempo,
        int SampleRate,
        (AudioRegion Region, AudioFile File)[] Regions,
        MidiRegion[] Midi,
        (TakeFolder Folder, AudioFile?[] Files)[] Folders
    ) {
        /// <summary>The track's flex (whether its regions warp, and how).</summary>
        public (bool On, FlexMode Mode) Flex { get; init; }

        /// <summary>The signature track (regions following bars and beats land on it).</summary>
        public IReadOnlyList<SignatureEvent> Signatures { get; init; } = [];

        /// <summary>The analyses of the files of regions with pitch notes (their pitch curves shape the notes).</summary>
        public FileAnalysis?[] Analyses { get; init; } = [];

        /// <summary>Its pattern regions, and what it plays of its Drum Machine Designer's (Phase 6).</summary>
        public MidiSources Sources { get; init; } = MidiSources.None;

        public bool Equals(TrackKey? other) =>
            other is not null
            && Sources.Equals(other.Sources)
            && Flex == other.Flex
            && Signatures.SequenceEqual(other.Signatures)
            && Analyses.AsSpan().SequenceEqual(other.Analyses)
            && Tempo.Equals(other.Tempo)
            && SampleRate == other.SampleRate
            && TempoEvents.SequenceEqual(other.TempoEvents)
            && Regions.AsSpan().SequenceEqual(other.Regions)
            && Midi.AsSpan().SequenceEqual(other.Midi)
            && Folders.Length == other.Folders.Length
            && Folders.Zip(other.Folders)
                .All(static p => p.First.Folder == p.Second.Folder && p.First.Files.SequenceEqual(p.Second.Files));

        public override int GetHashCode() =>
            HashCode.Combine(Tempo, SampleRate, Regions.Length, Midi.Length, Folders.Length);
    }

    /// <summary>
    ///     A track's pattern regions, its DMD's regions and pad note (−1: not a pad), and the key track when a pattern
    ///     reads it.
    /// </summary>
    sealed record MidiSources(
        PatternRegion[] Patterns,
        MidiRegion[] RoutedMidi,
        PatternRegion[] RoutedPatterns,
        int RoutedNote,
        IReadOnlyList<KeyEvent> Keys
    ) {
        public static MidiSources None { get; } = new([], [], [], -1, []);

        public bool Equals(MidiSources? other) =>
            other is not null
            && RoutedNote == other.RoutedNote
            && Patterns.AsSpan().SequenceEqual(other.Patterns)
            && RoutedMidi.AsSpan().SequenceEqual(other.RoutedMidi)
            && RoutedPatterns.AsSpan().SequenceEqual(other.RoutedPatterns)
            && Keys.SequenceEqual(other.Keys);

        public override int GetHashCode() => HashCode.Combine(Patterns.Length, RoutedMidi.Length, RoutedNote);
    }

    sealed record ClickKey(
        IReadOnlyList<TempoEvent> TempoEvents,
        IReadOnlyList<SignatureEvent> Signatures,
        TimeSignature Signature,
        long EndTick,
        int SampleRate
    ) {
        public bool Equals(ClickKey? other) =>
            other is not null
            && Signature == other.Signature
            && EndTick == other.EndTick
            && SampleRate == other.SampleRate
            && TempoEvents.SequenceEqual(other.TempoEvents)
            && Signatures.SequenceEqual(other.Signatures);

        public override int GetHashCode() => HashCode.Combine(Signature, EndTick, SampleRate);
    }
}
