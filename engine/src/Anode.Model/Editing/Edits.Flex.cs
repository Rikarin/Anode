using Anode.Contracts;
using Anode.Model.Groove;
using Anode.Model.Time;

namespace Anode.Model.Editing;

/// <summary>
///     Flex Time and Pitch, Smart Tempo, beat mapping, Strip Silence and Varispeed (docs/06-phase-5.md). Positions of
///     audio go through <see cref="RegionTiming" />; what needs a file analysed asks <see cref="IAnalysisSource" />.
/// </summary>
public static partial class Edits {
    public const int MaxFlexMarkers = 10_000;
    public const int MaxPitchNotes = 10_000;
    public const int MaxRegionTranspose = 36;
    public const int MaxFineTune = 50;
    public const double MaxPitchCents = 12_700;
    public const double MaxFormantCents = 1_200;
    public const double MaxNoteScale = 2;
    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 1.5;
    public const int MaxFileBeats = 1_000_000;
    public const double MaxStripMs = 10_000;

    /// <summary>The operations of a Phase 5 command, or null when <paramref name="command" /> is not one.</summary>
    static IReadOnlyList<DeltaOp>? FlexOps(
        ProjectState state,
        EditCommand command,
        IIdSource ids,
        PluginCatalog plugins,
        IAnalysisSource analysis
    ) =>
        command switch {
            SetTrackFlexCommand c => SetTrackFlex(state, c),
            SetRegionFlexCommand c => SetRegionFlex(state, c, analysis),
            SetFlexMarkersCommand c => SetFlexMarkers(state, c),
            QuantizeAudioCommand c => QuantizeAudio(state, c, analysis),
            SetPitchNotesCommand c => SetPitchNotes(state, c),
            FlexPitchToMidiCommand c => FlexPitchToMidi(state, c, ids, plugins),
            SetFileTempoCommand c => SetFileTempo(state, c),
            AdaptTempoCommand c => AdaptTempo(state, c, analysis),
            MapBeatsCommand c => MapBeats(state, c),
            StripSilenceCommand c => StripSilence(state, c, ids, analysis),
            NewTrackAlternativeCommand c => NewTrackAlternative(state, c, ids),
            SelectTrackAlternativeCommand c => SelectTrackAlternative(state, c),
            SetTrackAlternativeCommand c => SetTrackAlternative(state, c),
            DeleteTrackAlternativeCommand c => DeleteTrackAlternative(state, c),
            SetVarispeedCommand c => SetVarispeed(state, c),
            SetSmartTempoCommand c => SetSmartTempo(state, c),
            _ => null
        };

    static DeltaOp[] SetTrackFlex(ProjectState state, SetTrackFlexCommand c) {
        var track = AudioTrackOf(state, c.TrackId);
        if (c.Mode is { } mode && !Enum.IsDefined(mode)) {
            throw new EditException($"Unknown flex mode {mode}.");
        }

        return [
            new TrackUpserted(
                track with { FlexOn = c.On ?? track.FlexOn, FlexMode = c.Mode ?? track.FlexMode },
                state.IndexOfTrack(track.Id)
            )
        ];
    }

    static List<DeltaOp> SetRegionFlex(ProjectState state, SetRegionFlexCommand c, IAnalysisSource analysis) {
        var region = RegionOf(state, c.RegionId);
        var track = TrackOf(state, region.TrackId);
        var follow = c.Follow ?? region.Follow;
        if (!Enum.IsDefined(follow)) {
            throw new EditException($"Unknown follow mode {follow}.");
        }

        var transpose = c.Transpose ?? region.Transpose;
        if (transpose is < -MaxRegionTranspose or > MaxRegionTranspose) {
            throw new EditException(
                $"A region transposes −{MaxRegionTranspose}…{MaxRegionTranspose} semitones, not {transpose}."
            );
        }

        var fineTune = c.FineTune ?? region.FineTune;
        if (fineTune is < -MaxFineTune or > MaxFineTune) {
            throw new EditException($"A region's fine tune is −{MaxFineTune}…{MaxFineTune} cents, not {fineTune}.");
        }

        var changed = region with { Follow = follow, Transpose = transpose, FineTune = fineTune };
        if (!track.FlexOn
            && follow == FlexFollow.Off
            && (transpose != 0 || fineTune != 0 || changed.PitchNotes.Count > 0)) {
            throw new EditException("A transpose, fine tune or pitch notes need Flex or Follow: turn one on first.");
        }

        var ops = new List<DeltaOp>();
        if (follow != FlexFollow.Off) {
            // Following needs the file's beats: analysed now when it has none.
            var file = FileOf(state, region.FileId);
            if (file.Tempo is null) {
                var tempo = analysis.Rhythm(file)?.Tempo is { } found && HasBeats(found)
                    ? found
                    : throw new EditException($"Following the tempo needs the beats of '{file.Name}': it has none.");
                ops.Add(new AudioFileUpserted(file with { Tempo = tempo }));
            }
        }

        ops.Add(new RegionUpserted(changed));
        return ops;
    }

    static DeltaOp[] SetFlexMarkers(ProjectState state, SetFlexMarkersCommand c) {
        var region = RegionOf(state, c.RegionId);
        if (!TrackOf(state, region.TrackId).FlexOn) {
            throw new EditException("Flex markers need the track's flex on.");
        }

        if (c.Markers is not { Count: <= MaxFlexMarkers } markers) {
            throw new EditException($"A region has 0…{MaxFlexMarkers} flex markers.");
        }

        var sorted = markers.Select(static m => m ?? throw new EditException("A flex marker is null."))
            .OrderBy(static m => m.FileFrame)
            .ToArray();
        var timing = RegionTiming.Of(state, region with { FlexMarkers = [] });
        var end = timing.Base(region.FileOffset + region.Length);
        FlexMarker? previous = null;
        foreach (var m in sorted) {
            if (m.FileFrame <= region.FileOffset
                || m.FileFrame >= region.FileOffset + region.Length
                || m.Tick <= 0
                || m.Tick >= end) {
                throw new EditException(
                    $"A flex marker lies strictly inside its region: frames {region.FileOffset}…"
                    + $"{region.FileOffset + region.Length}, ticks 0…{Math.Floor(end)} from its start."
                );
            }

            if (previous is not null && (m.FileFrame <= previous.FileFrame || m.Tick <= previous.Tick)) {
                throw new EditException("Flex markers' ticks increase with their frames.");
            }

            previous = m;
        }

        return [new RegionUpserted(region with { FlexMarkers = sorted })];
    }

    /// <summary>
    ///     Flex markers on the regions' transients, moved toward the grid; their tracks' flex is turned on. A transient
    ///     whose quantised tick would not be strictly inside the region, or not after the marker before it, gets none.
    /// </summary>
    static List<DeltaOp> QuantizeAudio(ProjectState state, QuantizeAudioCommand c, IAnalysisSource analysis) {
        if (c.RegionIds is not { Count: > 0 and <= MaxBatch }) {
            throw new EditException($"Quantise 1…{MaxBatch} regions.");
        }

        CheckQuantize(c);
        var ops = new List<DeltaOp>();
        var flexed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in c.RegionIds.Distinct(StringComparer.Ordinal)) {
            var region = RegionOf(state, id ?? string.Empty);
            var track = AudioTrackOf(state, region.TrackId);
            var file = FileOf(state, region.FileId);
            var transients = analysis.Rhythm(file)?.Transients
                ?? throw new EditException($"Quantising audio needs the transients of '{file.Name}': it has none.");
            var markers = QuantizedMarkers(state, region, file, transients, Target(state, track, c, analysis));
            if (!track.FlexOn && flexed.Add(track.Id)) {
                ops.Add(new TrackUpserted(track with { FlexOn = true }, state.IndexOfTrack(track.Id)));
            }

            ops.Add(new RegionUpserted(region with { FlexMarkers = markers }));
        }

        return ops;
    }

    static void CheckQuantize(QuantizeAudioCommand c) {
        if (c.GrooveId is null && c.GridTicks is < 1 or > MaxTick) {
            throw new EditException($"A grid is 1…{MaxTick} ticks, not {c.GridTicks}.");
        }

        if (!(c.Strength is >= 0 and <= 1) || (c.GrooveId is null && !(c.Swing is >= 0 and <= 1))) {
            throw new EditException("Strength and swing are 0…1.");
        }
    }

    /// <summary>
    ///     Where a quantise moves a tick on a track: towards a groove (named, or the groove track's when the track
    ///     matches it; Phase 7), else the grid.
    /// </summary>
    static Func<long, long> Target(ProjectState state, Track track, QuantizeAudioCommand c, IAnalysisSource analysis) {
        var groove = GrooveFor(state, track.Id, c.GrooveId, analysis);
        return groove is null
            ? at => Quantize.Tick(at, c.GridTicks, c.Strength, c.Swing)
            : at => Grooves.Target(groove, at, c.Strength).Tick;
    }

    /// <summary>Markers on a region's transients, quantised (each strictly inside it and after the one before).</summary>
    static List<FlexMarker> QuantizedMarkers(
        ProjectState state,
        AudioRegion region,
        AudioFile file,
        IReadOnlyList<long> transients,
        Func<long, long> target
    ) {
        var timing = RegionTiming.Of(
            region with { FlexMarkers = [] },
            file.Tempo,
            true,
            state.TempoMap,
            state.SignatureMap,
            state.SampleRate
        );
        var end = region.FileOffset + region.Length;
        var endTick = timing.Base(end);
        var markers = new List<FlexMarker>();
        foreach (var frame in transients.Where(f => f > region.FileOffset && f < end)) {
            var at = (long)Math.Round(region.StartTick + timing.Base(frame));
            var tick = target(at) - region.StartTick;
            if (tick > 0 && tick < endTick && (markers.Count == 0 || tick > markers[^1].Tick)) {
                markers.Add(new(frame, tick));
            }
        }

        return markers;
    }

    static DeltaOp[] SetPitchNotes(ProjectState state, SetPitchNotesCommand c) {
        var region = RegionOf(state, c.RegionId);
        if (!TrackOf(state, region.TrackId).FlexOn && region.Follow == FlexFollow.Off) {
            throw new EditException("Pitch notes need Flex or Follow: turn one on first.");
        }

        if (c.Notes is not { Count: <= MaxPitchNotes } notes) {
            throw new EditException($"A region has 0…{MaxPitchNotes} pitch notes.");
        }

        var (start, end) = (region.FileOffset, region.FileOffset + region.Length);
        PitchNote? previous = null;
        foreach (var n in notes) {
            if (n is null) {
                throw new EditException("A pitch note is null.");
            }

            if (n.StartFrame < start || n.EndFrame > end || n.EndFrame <= n.StartFrame) {
                throw new EditException(
                    $"A pitch note lies inside its region (frames {start}…{end}) and ends after it starts."
                );
            }

            if (previous is not null && n.StartFrame < previous.EndFrame) {
                throw new EditException("Pitch notes are in order and do not overlap.");
            }

            CheckRange(n.DetectedCents, 0, MaxPitchCents, "A note's detected pitch (cents)");
            CheckRange(n.TargetCents, 0, MaxPitchCents, "A note's pitch (cents)");
            CheckRange(n.Drift, 0, MaxNoteScale, "A note's drift");
            CheckRange(n.Vibrato, 0, MaxNoteScale, "A note's vibrato");
            CheckRange(n.FormantCents, -MaxFormantCents, MaxFormantCents, "A note's formant shift (cents)");
            CheckRange(n.GainDb, MinRegionGainDb, MaxRegionGainDb, "A note's gain (dB)");
            previous = n;
        }

        return [new RegionUpserted(region with { PitchNotes = [..notes] })];
    }

    static void CheckRange(double value, double min, double max, string what) {
        if (!(value >= min && value <= max)) {
            throw new EditException($"{what} is {min}…{max}, not {value}.");
        }
    }

    /// <summary>A MIDI region of an audio region's pitch notes: their target pitches where they play.</summary>
    static List<DeltaOp> FlexPitchToMidi(
        ProjectState state,
        FlexPitchToMidiCommand c,
        IIdSource ids,
        PluginCatalog plugins
    ) {
        var region = RegionOf(state, c.RegionId);
        if (region.PitchNotes.Count == 0) {
            throw new EditException($"Region '{region.Id}' has no pitch notes: analyse it for Flex Pitch first.");
        }

        var ops = new List<DeltaOp>();
        Track target;
        if (c.TrackId is not null) {
            target = MidiTrackOf(state, c.TrackId);
        } else {
            var added = AddTrack(
                state,
                new(null, state.IndexOfTrack(region.TrackId) + 1, false, TrackKind.Instrument),
                ids,
                plugins
            );
            ops.AddRange(added);
            target = ((TrackUpserted)added[0]).Track;
        }

        var timing = RegionTiming.Of(state, region);
        var next = state.LastEventId;
        var notes = new List<Note>(region.PitchNotes.Count);
        foreach (var n in region.PitchNotes) {
            var from = Math.Max(0, (long)Math.Round(timing.TickAt(n.StartFrame)) - region.StartTick);
            var to = (long)Math.Round(timing.TickAt(n.EndFrame)) - region.StartTick;
            notes.Add(
                new(
                    ++next,
                    (int)Math.Clamp(Math.Round(n.TargetCents / 100), 0, 127),
                    (int)Math.Clamp(Math.Round(100 + 2 * n.GainDb), 1, 127),
                    1,
                    from,
                    Math.Max(1, to - from),
                    0
                )
            );
        }

        var length = Math.Max(1, (long)Math.Round(timing.EndTick) - region.StartTick);
        ops.Add(
            new MidiRegionUpserted(
                new(
                    ids.Next(IdPrefix.MidiRegion),
                    target.Id,
                    region.Name,
                    region.StartTick,
                    length,
                    ProjectDefaults.Playback,
                    Sorted(notes),
                    [],
                    false
                )
            )
        );
        return ops;
    }

    /// <summary>
    ///     Whether a file tempo has beats: an analysis of silence, noise or under two seconds has none (and a guessed
    ///     120 bpm), and keeps the project tempo.
    /// </summary>
    public static bool HasBeats(FileTempo? tempo) => tempo is { Bpm: > 0, Beats.Count: > 0 };

    static DeltaOp[] SetFileTempo(ProjectState state, SetFileTempoCommand c) {
        var file = FileOf(state, c.FileId);
        if (c.Tempo is not { } tempo) {
            return [new AudioFileUpserted(file with { Tempo = null })];
        }

        CheckBpm(tempo.Bpm);
        return [
            new AudioFileUpserted(
                file with {
                    Tempo = tempo with {
                        Beats = Frames(tempo.Beats, file, "beats"),
                        Downbeats = Frames(tempo.Downbeats, file, "downbeats")
                    }
                }
            )
        ];
    }

    /// <summary>A file's beats or downbeats: strictly ascending frames inside it.</summary>
    static long[] Frames(IReadOnlyList<long>? frames, AudioFile file, string what) {
        if (frames is not { Count: <= MaxFileBeats }) {
            throw new EditException($"A file has 0…{MaxFileBeats} {what}.");
        }

        for (var i = 0; i < frames.Count; i++) {
            if (frames[i] < 0 || frames[i] >= file.Frames || (i > 0 && frames[i] <= frames[i - 1])) {
                throw new EditException($"A file's {what} are strictly ascending frames inside it (0…{file.Frames}).");
            }
        }

        return [..frames];
    }

    static List<DeltaOp> AdaptTempo(ProjectState state, AdaptTempoCommand c, IAnalysisSource analysis) {
        var region = RegionOf(state, c.RegionId);
        var file = FileOf(state, region.FileId);
        var ops = new List<DeltaOp>();
        var tempo = file.Tempo;
        if (tempo is null) {
            tempo = analysis.Rhythm(file)?.Tempo is { } found && HasBeats(found)
                ? found
                : throw new EditException($"Adapting the tempo needs the beats of '{file.Name}': it has none.");
            ops.Add(new AudioFileUpserted(file with { Tempo = tempo }));
            state = state.Apply(ops[0]);
        }

        CheckBpm(tempo.Bpm);
        if (!HasBeats(tempo)) {
            throw new EditException($"'{file.Name}' has no beats to adapt the tempo to.");
        }

        ops.AddRange(Adapted(state, region, tempo));
        return ops;
    }

    /// <summary>
    ///     The tempo events that make an unwarped region's beats land on the project's beats, over its range (a steady
    ///     file: its tempo from the region's start; a variable one: one event per beat), the project's tempo restored
    ///     after it; and the region made to play unwarped.
    /// </summary>
    static DeltaOp[] Adapted(ProjectState state, AudioRegion region, FileTempo tempo) {
        double rate = state.SampleRate;
        var old = state.TempoMap;
        var start = region.StartTick;
        var (from, to) = (region.FileOffset, region.FileOffset + region.Length);
        var beats = (tempo.Beats ?? []).Where(b => b >= from && b < to).ToArray();
        var adapted = new List<TempoEvent>();
        double endTick;
        if (!tempo.Variable || beats.Length < 2) {
            var bpm = Math.Clamp(tempo.Bpm, TempoMap.MinBpm, TempoMap.MaxBpm);
            adapted.Add(new(start, bpm, TempoCurve.Step));
            endTick = start + region.Length * bpm * TempoMap.Ppq / (60 * rate);
        } else {
            var first = Bpm(beats[1] - beats[0]);
            var tick0 = start + (long)Math.Round((beats[0] - from) * first * TempoMap.Ppq / (60 * rate));
            if (tick0 > start) {
                adapted.Add(
                    new(start, Bpm((beats[0] - from) * TempoMap.Ppq / (double)(tick0 - start)), TempoCurve.Step)
                );
            }

            for (var k = 0; k + 1 < beats.Length; k++) {
                adapted.Add(new(tick0 + k * TempoMap.Ppq, Bpm(beats[k + 1] - beats[k]), TempoCurve.Step));
            }

            var last = adapted[^1].Bpm;
            endTick = tick0
                + (beats.Length - 1) * (double)TempoMap.Ppq
                + (to - beats[^1]) * last * TempoMap.Ppq / (60 * rate);
        }

        var restore = (long)Math.Ceiling(endTick);
        var events = (state.Settings.TempoEvents ?? []).Where(e => e.Tick < start).ToList();
        events.AddRange(adapted);
        events.Add(new(restore, Math.Clamp(old.BpmAt(restore), TempoMap.MinBpm, TempoMap.MaxBpm), TempoCurve.Step));
        events.AddRange((state.Settings.TempoEvents ?? []).Where(e => e.Tick > restore));
        if (events.Count > MaxGlobalEvents) {
            throw new EditException($"Adapting would make more than {MaxGlobalEvents} tempo events.");
        }

        return [
            new SettingsChanged(state.Settings with { Tempo = events[0].Bpm, TempoEvents = events }),
            new RegionUpserted(region with { Follow = FlexFollow.Off, FlexMarkers = [] })
        ];

        // The tempo at which a quarter note lasts that many frames (per Ppq-tick span when scaled), clamped.
        double Bpm(double frames) => Math.Clamp(60 * rate / frames, TempoMap.MinBpm, TempoMap.MaxBpm);
    }

    /// <summary>
    ///     Beat mapping, one mapping at a time: the tempo from the last event before its tick changes so that the tick
    ///     lands on its sample, and the tempo from the tick to the next event so that that event keeps its time; the
    ///     positions before that last event (earlier mappings among them) and from the next event on stay.
    /// </summary>
    static DeltaOp[] MapBeats(ProjectState state, MapBeatsCommand c) {
        if (c.Mappings is not { Count: > 0 and <= MaxGlobalEvents } mappings) {
            throw new EditException($"Map 1…{MaxGlobalEvents} beats.");
        }

        var events = (state.Settings.TempoEvents ?? [new(0, state.Settings.Tempo, TempoCurve.Step)]).ToList();
        double rate = state.SampleRate;
        foreach (var m in mappings) {
            if (m is null) {
                throw new EditException("A beat mapping is null.");
            }

            if (m.Tick is <= 0 or > MaxTick || m.Sample < 0) {
                throw new EditException($"A beat mapping maps a tick after 0 (not {m.Tick}) to a sample from 0.");
            }

            events = Mapped(events, m.Tick, m.Sample / rate);
            if (events.Count > MaxGlobalEvents) {
                throw new EditException($"Mapping would make more than {MaxGlobalEvents} tempo events.");
            }
        }

        return [new SettingsChanged(state.Settings with { Tempo = events[0].Bpm, TempoEvents = events })];
    }

    /// <summary>The tempo track with tick <paramref name="tick" /> moved to <paramref name="seconds" />.</summary>
    static List<TempoEvent> Mapped(List<TempoEvent> events, long tick, double seconds) {
        var map = TempoMap.Of(events);
        var e = events.FindLastIndex(x => x.Tick < tick);
        var n = events.FindIndex(x => x.Tick > tick);
        var result = events.Take(e).ToList();
        var anchor = events[e];
        if (e > 0 && events[e - 1].Curve == TempoCurve.Ramp) {
            // A ramp ends at the anchor: its tempo there stays, so the positions before stay too; the new tempo
            // starts a tick later.
            result.Add(anchor with { Curve = TempoCurve.Step, Shape = 0 });
            anchor = anchor with { Tick = anchor.Tick + 1 };
            if (anchor.Tick >= tick) {
                throw new EditException($"Tick {tick} is too close to the tempo change before it to map.");
            }
        }

        var from = map.SecondsAt(anchor.Tick);
        if (seconds <= from) {
            throw new EditException($"Tick {tick} cannot map to a sample at or before the tempo change before it.");
        }

        var bpm = (tick - anchor.Tick) * 60.0 / (TempoMap.Ppq * (seconds - from));
        CheckBpm(bpm);
        result.Add(new(anchor.Tick, bpm, TempoCurve.Step));
        if (n < 0) {
            result.Add(new(tick, Math.Clamp(map.BpmAt(tick), TempoMap.MinBpm, TempoMap.MaxBpm), TempoCurve.Step));
            return result;
        }

        var next = map.SecondsAt(events[n].Tick);
        if (next <= seconds) {
            throw new EditException($"Tick {tick} cannot map to a sample at or after the tempo change after it.");
        }

        var after = (events[n].Tick - tick) * 60.0 / (TempoMap.Ppq * (next - seconds));
        CheckBpm(after);
        result.Add(new(tick, after, TempoCurve.Step));
        result.AddRange(events.Skip(n));
        return result;
    }

    /// <summary>
    ///     Strip Silence: each region becomes one region per non-silent range (the first keeps its id); a region with
    ///     none goes. Each starts on the first tick at or after its range's start, the file following exactly.
    /// </summary>
    static List<DeltaOp> StripSilence(
        ProjectState state,
        StripSilenceCommand c,
        IIdSource ids,
        IAnalysisSource analysis
    ) {
        if (c.RegionIds is not { Count: > 0 and <= MaxBatch }) {
            throw new EditException($"Strip silence from 1…{MaxBatch} regions.");
        }

        CheckRange(c.ThresholdDb, Units.SilenceDb, 0, "The threshold (dB)");
        CheckRange(c.MinTimeMs, 0, MaxStripMs, "The minimum time (ms)");
        CheckRange(c.PreAttackMs, 0, MaxStripMs, "The pre-attack time (ms)");
        CheckRange(c.PostReleaseMs, 0, MaxStripMs, "The post-release time (ms)");
        var ops = new List<DeltaOp>();
        foreach (var id in c.RegionIds.Distinct(StringComparer.Ordinal)) {
            var region = RegionOf(state, id ?? string.Empty);
            var file = FileOf(state, region.FileId);
            var ranges = analysis.Silence(file, c.ThresholdDb, c.MinTimeMs, c.PreAttackMs, c.PostReleaseMs)
                ?? throw new EditException($"Strip Silence needs '{file.Name}' analysed: it cannot be.");
            var pieces = Pieces(state, region, ranges);
            if (pieces.Count == 0) {
                ops.Add(new RegionRemoved(region.Id));
                continue;
            }

            for (var i = 0; i < pieces.Count; i++) {
                ops.Add(new RegionUpserted(i == 0 ? pieces[i] : pieces[i] with { Id = ids.Next(IdPrefix.Region) }));
            }
        }

        return ops;
    }

    /// <summary>The parts of a region over non-silent ranges of its file, each starting on a tick.</summary>
    static List<AudioRegion> Pieces(
        ProjectState state,
        AudioRegion region,
        IReadOnlyList<(long Start, long End)> ranges
    ) {
        var timing = RegionTiming.Of(state, region);
        var map = state.TempoMap;
        var rate = state.SampleRate;
        var (start, end) = (region.FileOffset, region.FileOffset + region.Length);
        var pieces = new List<AudioRegion>();
        foreach (var (a, b) in ranges) {
            var (from, to) = (Math.Max(a, start), Math.Min(b, end));
            if (to <= from) {
                continue;
            }

            long tick;
            if (timing.Warped) {
                tick = (long)Math.Ceiling(timing.TickAt(from) - 1e-6);
                from = Math.Max(from, (long)Math.Round(timing.FrameAt(tick)));
            } else {
                var sample = timing.StartSample + from - start;
                tick = Recordings.CeilingTick(map, sample, rate);
                from += map.SampleAt(tick, rate) - sample;
            }

            if (to > from && (pieces.Count == 0 || from >= pieces[^1].FileOffset + pieces[^1].Length)) {
                pieces.Add(Piece(region, from, to, Math.Max(0, tick)) with { FadeIn = 0, FadeOut = 0 });
            }
        }

        return pieces;
    }

    static DeltaOp[] SetVarispeed(ProjectState state, SetVarispeedCommand c) {
        var old = state.Settings.Varispeed ?? ProjectDefaults.Varispeed;
        if (c.Mode is { } mode && !Enum.IsDefined(mode)) {
            throw new EditException($"Unknown Varispeed mode {mode}.");
        }

        if (c.Speed is { } speed && !(speed is >= MinSpeed and <= MaxSpeed)) {
            throw new EditException($"Varispeed is {MinSpeed * 100}…{MaxSpeed * 100} %, not {speed * 100} %.");
        }

        return [
            new SettingsChanged(
                state.Settings with {
                    Varispeed = new(c.Enabled ?? old.Enabled, c.Mode ?? old.Mode, c.Speed ?? old.Speed)
                }
            )
        ];
    }

    static DeltaOp[] SetSmartTempo(ProjectState state, SetSmartTempoCommand c) {
        var old = state.Settings.SmartTempo ?? ProjectDefaults.SmartTempo;
        if ((c.Mode is { } mode && !Enum.IsDefined(mode))
            || (c.DefaultFollow is { } follow && !Enum.IsDefined(follow))) {
            throw new EditException("Unknown tempo mode or follow mode.");
        }

        return [
            new SettingsChanged(
                state.Settings with {
                    SmartTempo = new(
                        c.Mode ?? old.Mode,
                        c.DefaultFollow ?? old.DefaultFollow,
                        c.AnalyzeImports ?? old.AnalyzeImports
                    )
                }
            )
        ];
    }
}
