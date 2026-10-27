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
///     regions as a playlist in samples (ticks through the <see cref="TempoMap" />, ADR-0005), its MIDI regions as
///     events (<see cref="MidiEvents" />), and the metronome's clicks. Incremental: a track is recompiled only when its
///     regions (audio or MIDI), their files, the tempo or the sample rate
///     changed since the previous call; otherwise the previous <see cref="TrackModel" /> instance is reused, so a
///     caller finds the dirty tracks by reference.
/// </summary>
public sealed class ModelCompiler {
    readonly Dictionary<int, (TrackKey Key, TrackModel Model)> cache = [];
    (ClickKey Key, TrackModel Model)? metronome;

    public CompiledModel Compile(ProjectState state, EngineIds ids) {
        var map = state.TempoMap;
        var byTrack = state.Regions.Values.ToLookup(static r => r.TrackId, StringComparer.Ordinal);
        var midiByTrack = state.MidiRegions.Values.ToLookup(static r => r.TrackId, StringComparer.Ordinal);
        var models = new List<TrackModel>(state.Tracks.Count + 1);
        var recompiled = new List<int>();
        var seen = new HashSet<int>();
        long end = 0;
        foreach (var track in state.Tracks) {
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
            var key = new TrackKey(
                state.Settings.Tempo,
                state.SampleRate,
                [..regions.Select(r => (r, state.Files[r.FileId]))],
                midi
            );
            if (!cache.TryGetValue(engineId, out var cached) || !cached.Key.Equals(key)) {
                cached = (key, new(engineId, MidiEvents(state, map, engineId, midi), Playlist(state, map, ids, regions)));
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
        var clickKey = new ClickKey(settings.Tempo, settings.Signature, endTick, state.SampleRate);
        if (metronome is not { } m || m.Key != clickKey) {
            metronome = m = (clickKey, new(TrackEntry.MetronomeTrack, Clicks(state, map, endTick), []));
            recompiled.Add(TrackEntry.MetronomeTrack);
        }

        models.Add(m.Model);
        end = Math.Max(end, map.SampleAt(endTick, state.SampleRate));
        return new(models, end, recompiled);
    }

    /// <summary>
    ///     A track's playlist: its regions in samples, sorted and not overlapping. Where regions overlap, the one that
    ///     starts later plays (the earlier one resumes after it, without the fade it would have had at that point).
    /// </summary>
    public static AudioPlaylistEntry[] Playlist(
        ProjectState state,
        TempoMap map,
        EngineIds ids,
        IReadOnlyList<AudioRegion> regions
    ) {
        var entries = new AudioPlaylistEntry[regions.Count];
        for (var i = 0; i < regions.Count; i++) {
            var r = regions[i];
            entries[i] = new() {
                Start = map.SampleAt(r.StartTick, state.SampleRate),
                FileOffset = r.FileOffset,
                Length = r.Length,
                FileId = ids.File(r.FileId),
                Gain = Units.DbToGain(r.GainDb),
                FadeIn = (int)Math.Min(r.FadeIn, int.MaxValue),
                FadeOut = (int)Math.Min(r.FadeOut, int.MaxValue)
            };
        }

        // Ties on start keep the regions' order (by id), so the later one in that order wins.
        var sorted = entries.Select(static (e, i) => (e, i))
            .OrderBy(static x => x.e.Start)
            .ThenBy(static x => x.i)
            .Select(static x => x.e)
            .ToArray();
        return Overlaps(sorted) ? Flatten(sorted) : sorted;
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

    /// <summary>A click on every beat from 0 to <paramref name="endTick" />; <c>Data1</c> is 1 on a bar's downbeat.</summary>
    public static PlaybackEvent[] Clicks(ProjectState state, TempoMap map, long endTick) {
        var signature = state.Settings.Signature;
        var beat = ProjectDefaults.BeatTicks(signature);
        var clicks = new PlaybackEvent[(int)((endTick + beat - 1) / beat)];
        for (var i = 0; i < clicks.Length; i++) {
            clicks[i] = new() {
                SamplePos = map.SampleAt(i * beat, state.SampleRate),
                Track = TrackEntry.MetronomeTrack,
                Kind = PlaybackEventKind.Click,
                Data1 = (byte)(i % signature.Numerator == 0 ? 1 : 0),
                Value = 1
            };
        }

        return clicks;
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

        var rate = state.SampleRate;
        var events = new List<(PlaybackEvent Event, int Rank)>();
        for (var i = 0; i < regions.Count; i++) {
            var region = regions[i];
            var windows = Windows(regions, i);
            if (windows.Count == 0) {
                continue;
            }

            var playback = region.Playback;
            foreach (var note in region.Notes) {
                var at = region.StartTick + note.StartTick;
                if (playback.QuantizeTicks > 0) {
                    at = Editing.Quantize.Tick(at, playback.QuantizeTicks, playback.QuantizeStrength, playback.Swing);
                }

                if (WindowAt(windows, at) is not { } window) {
                    continue;
                }

                var on = map.SampleAt(at, rate);
                var off = Math.Max(map.SampleAt(Math.Min(at + note.LengthTicks, window.To), rate), on + 1);
                var pitch = (byte)Math.Clamp(note.Pitch + playback.Transpose, 0, 127);
                var velocity = (byte)Math.Clamp(note.Velocity + playback.Velocity, 1, 127);
                var channel = (byte)(note.Channel - 1);
                events.Add((Midi(engineId, on, PlaybackEventKind.NoteOn, pitch, velocity, channel), 2));
                events.Add((Midi(engineId, off, PlaybackEventKind.NoteOff, pitch, 0, channel), 0));
            }

            foreach (var control in region.Controls) {
                var at = region.StartTick + control.Tick;
                if (WindowAt(windows, at) is null) {
                    continue;
                }

                var sample = map.SampleAt(at, rate);
                var channel = (byte)(control.Channel - 1);
                var e = control.Kind switch {
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
                events.Add((e, 1));
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

    static PlaybackEvent Midi(int track, long sample, PlaybackEventKind kind, byte data1, byte data2, byte channel) =>
        new() { SamplePos = sample, Track = track, Kind = kind, Data1 = data1, Data2 = data2, Channel = channel };

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

    sealed record TrackKey(
        double Tempo,
        int SampleRate,
        (AudioRegion Region, AudioFile File)[] Regions,
        MidiRegion[] Midi
    ) {
        public bool Equals(TrackKey? other) =>
            other is not null
            && Tempo.Equals(other.Tempo)
            && SampleRate == other.SampleRate
            && Regions.AsSpan().SequenceEqual(other.Regions)
            && Midi.AsSpan().SequenceEqual(other.Midi);

        public override int GetHashCode() => HashCode.Combine(Tempo, SampleRate, Regions.Length, Midi.Length);
    }

    readonly record struct ClickKey(double Tempo, TimeSignature Signature, long EndTick, int SampleRate);
}
