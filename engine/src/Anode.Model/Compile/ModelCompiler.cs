using Anode.Contracts;
using Anode.Ipc.Segments;
using Anode.Model.Time;

namespace Anode.Model.Compile;

/// <summary>
///     The compiled playback model: one <see cref="TrackModel" /> per track in track order, then the metronome's
///     (<see cref="TrackEntry.MetronomeTrack" />); its length in samples; and the engine ids this compilation had to
///     recompile (the rest are the very instances of the previous one).
/// </summary>
public sealed record CompiledModel(IReadOnlyList<TrackModel> Tracks, long LengthSamples, IReadOnlyList<int> Recompiled);

/// <summary>
///     Compiles a <see cref="ProjectState" /> into what the host plays (docs/adr/0006-bulk-ipc.md): each track's
///     regions and take folder comps as a playlist in samples (ticks through the <see cref="TempoMap" /> and its ramps,
///     ADR-0005), its MIDI regions as events (<see cref="MidiEvents" />), and the metronome's clicks on the beats of the
///     signature track. Muted regions and folders play nothing, and a folder stack has no engine track of its own.
///     Incremental: a track is recompiled only when its regions (audio or MIDI), take folders, their files, the tempo
///     track or the sample rate changed since the previous call; otherwise the previous <see cref="TrackModel" />
///     instance is reused, so a caller finds the dirty tracks by reference.
/// </summary>
public sealed class ModelCompiler {
    readonly Dictionary<int, (TrackKey Key, TrackModel Model)> cache = [];
    (ClickKey Key, TrackModel Model)? metronome;

    public CompiledModel Compile(ProjectState state, EngineIds ids) {
        var map = state.TempoMap;
        var byTrack = state.Regions.Values.ToLookup(static r => r.TrackId, StringComparer.Ordinal);
        var midiByTrack = state.MidiRegions.Values.ToLookup(static r => r.TrackId, StringComparer.Ordinal);
        var foldersByTrack = state.TakeFolders.Values.ToLookup(static f => f.TrackId, StringComparer.Ordinal);
        var tempo = state.Settings.TempoEvents ?? [];
        var models = new List<TrackModel>(state.Tracks.Count + 1);
        var recompiled = new List<int>();
        var seen = new HashSet<int>();
        long end = 0;
        foreach (var track in state.Tracks) {
            if (track.Kind == TrackKind.Stack) {
                continue;
            }

            var engineId = ids.Track(track.Id);
            seen.Add(engineId);
            var regions = byTrack[track.Id]
                .OrderBy(static r => r.StartTick)
                .ThenBy(static r => r.Id, StringComparer.Ordinal)
                .ToArray();
            var midi = midiByTrack[track.Id]
                .OrderBy(static r => r.StartTick)
                .ThenBy(static r => r.Id, StringComparer.Ordinal)
                .ToArray();
            var folders = foldersByTrack[track.Id]
                .OrderBy(static f => f.StartTick)
                .ThenBy(static f => f.Id, StringComparer.Ordinal)
                .ToArray();
            var key = new TrackKey(
                tempo,
                state.Settings.Tempo,
                state.SampleRate,
                [..regions.Select(r => (r, state.Files[r.FileId]))],
                midi,
                [..folders.Select(f => (f, FilesOf(state, f)))]
            );
            if (!cache.TryGetValue(engineId, out var cached) || !cached.Key.Equals(key)) {
                cached = (key,
                    new(engineId, MidiEvents(state, map, engineId, midi), Playlist(state, map, ids, regions, folders)));
                cache[engineId] = cached;
                recompiled.Add(engineId);
            }

            models.Add(cached.Model);
            if (cached.Model.Playlist is [.., var last]) {
                end = Math.Max(end, last.Start + last.Length);
            }

            if (cached.Model.Events is [.., var lastEvent]) {
                end = Math.Max(end, lastEvent.SamplePos + 1);
            }
        }

        foreach (var stale in cache.Keys.Where(k => !seen.Contains(k)).ToArray()) {
            cache.Remove(stale);
        }

        var settings = state.Settings;
        var endTick = Math.Max(settings.EndTick, settings.Cycle.EndTick);
        var clickKey = new ClickKey(tempo, settings.Signatures ?? [], settings.Signature, endTick, state.SampleRate);
        if (metronome is not { } m || !m.Key.Equals(clickKey)) {
            metronome = m = (clickKey, new(TrackEntry.MetronomeTrack, Clicks(state, map, endTick), []));
            recompiled.Add(TrackEntry.MetronomeTrack);
        }

        models.Add(m.Model);
        end = Math.Max(end, map.SampleAt(endTick, state.SampleRate));
        return new(models, end, recompiled);
    }

    /// <summary>
    ///     A track's playlist (docs/04-phase-3.md, "Crossfades"): its unmuted regions and the comps of its unmuted take
    ///     folders in samples, sorted by start. Entries overlap only as crossfades — where the earlier fades out and the
    ///     later fades in over all of the overlap, never three at once. Where entries overlap any other way, the one
    ///     that starts later plays (at an equal start, the later in region order), and the earlier one resumes after
    ///     it, without the fade it would have had at that point.
    /// </summary>
    public static AudioPlaylistEntry[] Playlist(
        ProjectState state,
        TempoMap map,
        EngineIds ids,
        IReadOnlyList<AudioRegion> regions,
        IReadOnlyList<TakeFolder>? folders = null
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
        var entries = new List<(AudioPlaylistEntry Entry, int Order)>();
        foreach (var (_, _, region, folder) in placed) {
            if (region is not null) {
                entries.Add((Entry(region, map, state.SampleRate, ids), entries.Count));
                continue;
            }

            foreach (var piece in Comps.Pieces(state, folder!)) {
                entries.Add((Entry(piece, ids), entries.Count));
            }
        }

        var sorted = entries.OrderBy(static x => x.Entry.Start)
            .ThenBy(static x => x.Order)
            .Select(static x => x.Entry)
            .ToArray();
        return Overlaps(sorted) ? Resolve(sorted) : sorted;
    }

    /// <summary>Sorted, overlapping entries as the host plays them: crossfades kept, other overlaps flattened.</summary>
    static AudioPlaylistEntry[] Resolve(AudioPlaylistEntry[] sorted) {
        // Each run of overlapping entries stays as it is when it is a chain of crossfades, else is flattened.
        var result = new List<AudioPlaylistEntry>(sorted.Length);
        for (var i = 0; i < sorted.Length;) {
            var end = sorted[i].Start + sorted[i].Length;
            var j = i + 1;
            while (j < sorted.Length && sorted[j].Start < end) {
                end = Math.Max(end, sorted[j].Start + sorted[j].Length);
                j++;
            }

            var cluster = sorted[i..j];
            result.AddRange(cluster.Length == 1 || IsCrossfadeChain(cluster) ? cluster : Flatten(cluster));
            i = j;
        }

        return [..result];
    }

    static AudioPlaylistEntry Entry(AudioRegion r, TempoMap map, int sampleRate, EngineIds ids) =>
        new() {
            Start = map.SampleAt(r.StartTick, sampleRate),
            FileOffset = r.FileOffset,
            Length = r.Length,
            FileId = ids.File(r.FileId),
            Gain = Units.DbToGain(r.GainDb),
            FadeIn = (int)Math.Min(r.FadeIn, int.MaxValue),
            FadeOut = (int)Math.Min(r.FadeOut, int.MaxValue),
            FadeInCurve = (float)r.FadeInCurve,
            FadeOutCurve = (float)r.FadeOutCurve
        };

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
    static bool IsCrossfadeChain(AudioPlaylistEntry[] cluster) {
        for (var k = 1; k < cluster.Length; k++) {
            var (previous, current) = (cluster[k - 1], cluster[k]);
            var overlap = previous.Start + previous.Length - current.Start;
            if (overlap > previous.FadeOut
                || overlap > current.FadeIn
                || current.Start + current.Length < previous.Start + previous.Length
                || (k >= 2 && cluster[k - 2].Start + cluster[k - 2].Length > current.Start)) {
                return false;
            }
        }

        return true;
    }

    static bool Overlaps(AudioPlaylistEntry[] sorted) {
        for (var i = 1; i < sorted.Length; i++) {
            if (sorted[i].Start < sorted[i - 1].Start + sorted[i - 1].Length) {
                return true;
            }
        }

        return false;
    }

    static AudioPlaylistEntry[] Flatten(AudioPlaylistEntry[] sorted) {
        var bounds = sorted.SelectMany(static e => new[] { e.Start, e.Start + e.Length }).Distinct().Order().ToArray();
        var pieces = new List<AudioPlaylistEntry>();
        int? current = null;
        long pieceStart = 0;
        for (var b = 0; b < bounds.Length; b++) {
            // The entry that plays from bounds[b]: the latest-starting one covering it.
            int? top = null;
            if (b < bounds.Length - 1) {
                for (var i = 0; i < sorted.Length; i++) {
                    if (sorted[i].Start <= bounds[b] && bounds[b] < sorted[i].Start + sorted[i].Length) {
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

    static AudioPlaylistEntry Piece(AudioPlaylistEntry e, long start, long end) {
        var length = end - start;
        return e with {
            Start = start,
            FileOffset = e.FileOffset + (start - e.Start),
            Length = length,
            FadeIn = start == e.Start ? (int)Math.Min(e.FadeIn, length) : 0,
            FadeOut = end == e.Start + e.Length ? (int)Math.Min(e.FadeOut, length) : 0
        };
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
        public bool Equals(TrackKey? other) =>
            other is not null
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
