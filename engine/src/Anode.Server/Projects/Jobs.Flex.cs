using Anode.Contracts;
using Anode.Dsp.Effects;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Editing;
using Anode.Model.Time;

namespace Anode.Server.Projects;

/// <summary>
///     Phase 5 jobs (docs/06-phase-5.md, "Server"): pitch analysis, bounce in place (the host renders the tracks as a
///     freeze) and selection-based processing (built-in effects run here, in-process).
/// </summary>
public sealed partial class Jobs {
    public const int MaxChain = 16;

    /// <summary>The tail selection-based processing adds when asked, in seconds.</summary>
    public const double ProcessTailSeconds = 2;

    const int ProcessBlock = 4096;

    /// <summary>Queues pitch analysis of a file (Flex Pitch): 404 for an unknown file.</summary>
    public Job Analyze(string fileId) {
        var (sessionId, folder, file) = session.FileOf(fileId);
        return Enqueue(
            JobKind.Analyze,
            (entry, token) => Task.Run<IReadOnlyList<string>>(
                () => {
                    Progress(entry, 0.1);
                    var analysis = analyses.Pitch(folder, file, token);
                    session.KeepAnalysis(sessionId, analysis);
                    return [FileAnalyses.PathOf(folder, file.Id)];
                },
                token
            )
        );
    }

    /// <summary>What one source track of a bounce in place renders and replaces.</summary>
    sealed record InPlace(Track Track, long Start, long End, string[] Regions, string[] MidiRegions, string[] Folders);

    /// <summary>Queues a bounce in place, checked first: 404 for unknown tracks or regions, 422 for what cannot be.</summary>
    public Job BounceInPlace(BounceInPlaceRequest request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var state = view.State;
        if (request is null
            || !Enum.IsDefined(request.Destination)
            || !Enum.IsDefined(request.Source)
            || !Enum.IsDefined(request.Normalize)) {
            throw ProjectException.Unprocessable("Unknown destination, source action or normalisation.");
        }

        if (state.PlaybackSpeed != 1) {
            throw ProjectException.Unprocessable("Turn Varispeed off to bounce in place.");
        }

        var name = request.Name is null ? null : CheckedName(request.Name);
        var sources = InPlaceSources(request, state);
        if (request.Destination == BounceInPlaceDestination.Replace
            && sources.Any(static s => s.Track.Kind != TrackKind.Audio)) {
            throw ProjectException.Unprocessable("Only audio tracks' regions can be replaced in place.");
        }

        var tail = request.IncludeTail ? (long)(FreezeTailSeconds * state.SampleRate) : 0;
        return Enqueue(
            JobKind.BounceInPlace,
            (entry, token) => BounceInPlaceAsync(entry, view, request, name, sources, tail, token)
        );
    }

    static string CheckedName(string name) {
        var trimmed = name.Trim();
        return trimmed.Length is 0 or > Edits.MaxNameLength
            ? throw ProjectException.Unprocessable($"A name has 1…{Edits.MaxNameLength} characters.")
            : trimmed;
    }

    /// <summary>The tracks to bounce in place, each with its range in samples and what it replaces.</summary>
    static List<InPlace> InPlaceSources(BounceInPlaceRequest request, ProjectState state) {
        var byTracks = request.TrackIds is { Count: > 0 };
        var byRegions = request.RegionIds is { Count: > 0 };
        if (byTracks == byRegions) {
            throw ProjectException.Unprocessable("Bounce either tracks or regions in place.");
        }

        var map = state.TempoMap;
        var rate = state.SampleRate;
        var sources = new List<InPlace>();
        if (byTracks) {
            foreach (var id in request.TrackIds!.Distinct(StringComparer.Ordinal)) {
                var track = state.FindTrack(id ?? string.Empty) ?? throw ProjectException.NotFound($"No track '{id}'.");
                if (track.Kind is not (TrackKind.Audio or TrackKind.Instrument)) {
                    throw ProjectException.Unprocessable("Only audio and instrument tracks bounce in place.");
                }

                var regions = state.RegionsOf(track.Id).ToArray();
                var midi = state.MidiRegionsOf(track.Id).ToArray();
                var folders = state.TakeFoldersOf(track.Id).ToArray();
                var spans = regions.Select(r => (RegionTiming.Of(state, r).StartSample, Edits.EndSample(state, r)))
                    .Concat(midi.Select(r => (map.SampleAt(r.StartTick, rate), map.SampleAt(r.StartTick + r.LengthTicks, rate))))
                    .Concat(folders.Select(f => (map.SampleAt(f.StartTick, rate), map.SampleAt(f.StartTick + f.LengthTicks, rate))))
                    .ToArray();
                if (spans.Length == 0) {
                    throw ProjectException.Unprocessable($"Track '{track.Name}' has nothing to bounce.");
                }

                sources.Add(
                    new(
                        track,
                        spans.Min(static s => s.Item1),
                        spans.Max(static s => s.Item2),
                        [..regions.Select(static r => r.Id)],
                        [..midi.Select(static r => r.Id)],
                        [..folders.Select(static f => f.Id)]
                    )
                );
            }

            return sources;
        }

        var items = request.RegionIds!.Distinct(StringComparer.Ordinal)
            .Select(id => id is not null && state.Regions.TryGetValue(id, out var audio)
                ? (audio.TrackId, id, RegionTiming.Of(state, audio).StartSample, Edits.EndSample(state, audio), false)
                : id is not null && state.MidiRegions.TryGetValue(id, out var midi)
                    ? (midi.TrackId, id, map.SampleAt(midi.StartTick, rate),
                        map.SampleAt(midi.StartTick + midi.LengthTicks, rate), true)
                    : throw ProjectException.NotFound($"No region '{id}'.")
            )
            .ToArray();
        foreach (var group in items.GroupBy(static i => i.TrackId, StringComparer.Ordinal)) {
            var track = state.FindTrack(group.Key)!;
            if (track.Kind is not (TrackKind.Audio or TrackKind.Instrument)) {
                throw ProjectException.Unprocessable("Only audio and instrument tracks bounce in place.");
            }

            sources.Add(
                new(
                    track,
                    group.Min(static i => i.Item3),
                    group.Max(static i => i.Item4),
                    [..group.Where(static i => !i.Item5).Select(static i => i.id)],
                    [..group.Where(static i => i.Item5).Select(static i => i.id)],
                    []
                )
            );
        }

        return sources;
    }

    async Task<IReadOnlyList<string>> BounceInPlaceAsync(
        Entry entry,
        OpenProjectView view,
        BounceInPlaceRequest request,
        string? name,
        List<InPlace> sources,
        long tail,
        CancellationToken token
    ) {
        var scratch = Path.Combine(view.Folder, $".bounce-in-place-{entry.Job.Id}");
        Directory.CreateDirectory(scratch);
        try {
            var start = sources.Min(static s => s.Start);
            var end = sources.Max(static s => s.End) + tail;
            var tracks = sources.Select(s => view.Ids.Track(s.Track.Id)).ToArray();
            var finished = await engine.RenderAsync(
                    new(
                        entry.EngineId,
                        start,
                        end,
                        scratch,
                        tracks,
                        true,
                        false,
                        request.IncludeVolumePan,
                        request.BypassEffects
                    ),
                    f => Progress(entry, 0.8 * f),
                    token
                )
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (finished.Error is { } error) {
                throw ProjectException.Unprocessable($"The engine could not render: {error}");
            }

            var files = new List<(string Path, WavInfo Info)>();
            for (var i = 0; i < sources.Count; i++) {
                var source = sources[i];
                var rendered = Path.Combine(scratch, $"{tracks[i]}.wav");
                var (audio, rate) = BounceProcessor.Read(rendered);
                var from = (int)(source.Start - start);
                var length = (int)Math.Min(source.End + tail - source.Start, audio[0].Length - from);
                var part = audio.Select(c => c.AsSpan(from, Math.Max(0, length)).ToArray()).ToArray();
                if (request.Normalize != NormalizeMode.Off) {
                    BounceProcessor.Normalise(part, rate, request.Normalize, -16, -1);
                }

                var path = Path.Combine(scratch, $"{Safe(name ?? source.Track.Name + " Bounce")}.wav");
                BounceProcessor.Write(path, part, new(AudioFormat.Wav, 32, rate, DitherMode.None, NormalizeMode.Off, -16, -1));
                var (_, brought, info) = import.Bring(path);
                files.Add((brought, info));
                Progress(entry, 0.8 + 0.15 * (i + 1) / sources.Count);
            }

            session.ApplyJob(
                view.Session,
                "Bounce in Place",
                files,
                (job, added) => {
                    for (var i = 0; i < sources.Count; i++) {
                        PlaceInPlace(job, request, name, sources[i], added[i]);
                    }
                }
            );
            return [..files.Select(static f => f.Path)];
        } finally {
            try {
                Directory.Delete(scratch, true);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                ProjectLog.JobFailed(logger, entry.Job.Id, $"could not remove {scratch}: {e.Message}");
            }
        }
    }

    /// <summary>A bounced track's file placed (a new track under the source, or replacing its regions), the source handled.</summary>
    static void PlaceInPlace(JobEdit job, BounceInPlaceRequest request, string? name, InPlace source, AudioFile file) {
        var state = job.State;
        var map = state.TempoMap;
        var rate = state.SampleRate;
        var tick = Recordings.CeilingTick(map, source.Start, rate);
        var offset = map.SampleAt(tick, rate) - source.Start;
        if (offset >= file.Frames) {
            return;
        }

        var trackId = source.Track.Id;
        if (request.Destination == BounceInPlaceDestination.Replace) {
            var replaced = source.Regions.Concat(source.Folders).Where(id => job.State.Regions.ContainsKey(id) || job.State.TakeFolders.ContainsKey(id)).ToArray();
            if (replaced.Length > 0) {
                job.Apply(new DeleteRegionsCommand(replaced));
            }
        } else {
            var trackName = name ?? Truncated($"{source.Track.Name} Bounce");
            var added = job.Apply(
                new AddTrackCommand(trackName, job.State.IndexOfTrack(trackId) + 1, file.Channels > 1, TrackKind.Audio)
            );
            trackId = ((TrackUpserted)added[0]).Track.Id;
        }

        var region = job.Apply(new AddRegionCommand(trackId, file.Id, tick, offset, file.Frames - offset));
        if (name is not null) {
            job.Apply(new SetRegionCommand(((RegionUpserted)region[^1]).Region.Id, name));
        }

        if (request.Destination == BounceInPlaceDestination.Replace) {
            return;
        }

        var byTracks = request.TrackIds is { Count: > 0 };
        switch (request.Source) {
            case BounceSourceAction.Mute when byTracks:
                job.Apply(new SetStripCommand(source.Track.Id, Mute: true));
                break;
            case BounceSourceAction.Mute:
                foreach (var id in source.Regions) {
                    job.Apply(new SetRegionCommand(id, Muted: true));
                }

                foreach (var id in source.MidiRegions) {
                    job.Apply(new SetMidiRegionCommand(id, Muted: true));
                }

                break;
            case BounceSourceAction.Delete when byTracks:
                job.Apply(new RemoveTrackCommand(source.Track.Id));
                break;
            case BounceSourceAction.Delete:
                job.Apply(new DeleteRegionsCommand([..source.Regions, ..source.MidiRegions]));
                break;
        }
    }

    static string Truncated(string name) => name.Length > Edits.MaxNameLength ? name[..Edits.MaxNameLength] : name;

    /// <summary>Queues selection-based processing, checked first: 404 for unknown regions, 422 for what cannot be.</summary>
    public Job Process(ProcessRequest request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var state = view.State;
        if (request?.RegionIds is not { Count: > 0 } ids || request.Chain is not { Count: > 0 and <= MaxChain } chain) {
            throw ProjectException.Unprocessable($"Process one or more regions with a chain of 1…{MaxChain} effects.");
        }

        foreach (var effect in chain) {
            using var instance = BuiltinEffects.Create(effect?.PluginId ?? string.Empty)
                ?? throw ProjectException.Unprocessable($"'{effect?.PluginId}' is not a built-in effect.");
            foreach (var key in effect!.Parameters?.Keys ?? []) {
                if (!instance.Descriptor.Parameters.Any(p => p.Id == key)) {
                    throw ProjectException.Unprocessable($"Effect '{effect.PluginId}' has no parameter '{key}'.");
                }
            }
        }

        if (request.StartTick is < 0 || (request.StartTick ?? 0) >= (request.EndTick ?? long.MaxValue)) {
            throw ProjectException.Unprocessable("The range ends after it starts.");
        }

        var parts = new List<(AudioRegion Region, long From, long To)>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal)) {
            if (id is not null && state.MidiRegions.ContainsKey(id)) {
                throw ProjectException.Unprocessable("Only audio regions are processed.");
            }

            var region = id is not null && state.Regions.TryGetValue(id, out var r)
                ? r
                : throw ProjectException.NotFound($"No region '{id}'.");
            var timing = RegionTiming.Of(state, region);
            var fromTick = Math.Max(region.StartTick, request.StartTick ?? region.StartTick);
            var toTick = Math.Min(timing.EndTick, request.EndTick ?? double.MaxValue);
            var end = region.FileOffset + region.Length;
            var from = Math.Clamp((long)Math.Round(timing.FrameAt(fromTick)), region.FileOffset, end);
            var to = Math.Clamp((long)Math.Round(timing.FrameAt(toTick)), region.FileOffset, end);
            if (to <= from) {
                throw ProjectException.Unprocessable($"Region '{region.Name}' is outside the range.");
            }

            parts.Add((region, from, to));
        }

        return Enqueue(
            JobKind.Process,
            (entry, token) => Task.Run(() => ProcessRegions(entry, view, request, parts, token), token)
        );
    }

    IReadOnlyList<string> ProcessRegions(
        Entry entry,
        OpenProjectView view,
        ProcessRequest request,
        List<(AudioRegion Region, long From, long To)> parts,
        CancellationToken token
    ) {
        var scratch = Path.Combine(view.Folder, $".process-{entry.Job.Id}");
        Directory.CreateDirectory(scratch);
        try {
            var files = new List<(string Path, WavInfo Info)>();
            for (var i = 0; i < parts.Count; i++) {
                token.ThrowIfCancellationRequested();
                var (region, from, to) = parts[i];
                var file = view.State.Files[region.FileId];
                var (audio, rate) = BounceProcessor.Read(Path.Combine(view.Folder, file.Path));
                var tail = request.IncludeTail ? (int)(ProcessTailSeconds * rate) : 0;
                var processed = Chain(audio, (int)from, (int)(to - from), tail, rate, request.Chain);
                var path = Path.Combine(scratch, $"{Safe(region.Name)} Processed.wav");
                BounceProcessor.Write(path, processed, new(AudioFormat.Wav, 32, rate, DitherMode.None, NormalizeMode.Off, -16, -1));
                var (_, brought, info) = import.Bring(path);
                files.Add((brought, info));
                Progress(entry, 0.9 * (i + 1) / parts.Count);
            }

            session.ApplyJob(
                view.Session,
                "Process Regions",
                files,
                (job, added) => {
                    for (var i = 0; i < parts.Count; i++) {
                        PlaceProcessed(job, parts[i].Region.Id, parts[i].From, parts[i].To, added[i]);
                    }
                }
            );
            return [..files.Select(static f => f.Path)];
        } finally {
            try {
                Directory.Delete(scratch, true);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                ProjectLog.JobFailed(logger, entry.Job.Id, $"could not remove {scratch}: {e.Message}");
            }
        }
    }

    /// <summary>
    ///     Frames <paramref name="from" />… of <paramref name="audio" /> (and <paramref name="tail" /> frames of
    ///     silence) through a chain of built-in effects, in stereo.
    /// </summary>
    static float[][] Chain(float[][] audio, int from, int length, int tail, int rate, IReadOnlyList<ProcessEffect> chain) {
        var total = length + tail;
        var left = new float[total];
        var right = new float[total];
        audio[0].AsSpan(from, length).CopyTo(left);
        audio[^1].AsSpan(from, length).CopyTo(right);
        var effects = chain.Select(e => {
                    var effect = BuiltinEffects.Create(e.PluginId)!;
                    effect.Prepare(rate, ProcessBlock);
                    foreach (var (key, value) in e.Parameters ?? new Dictionary<string, double>()) {
                        var p = effect.Descriptor.Parameters.First(x => x.Id == key);
                        effect.SetParameter(p.Index, (float)Math.Clamp(value, p.Min, p.Max));
                    }

                    return effect;
                }
            )
            .ToArray();
        try {
            for (var at = 0; at < total; at += ProcessBlock) {
                var n = Math.Min(ProcessBlock, total - at);
                foreach (var effect in effects) {
                    effect.Process(left.AsSpan(at, n), right.AsSpan(at, n), [], []);
                }
            }
        } finally {
            foreach (var effect in effects) {
                effect.Dispose();
            }
        }

        return [left, right];
    }

    /// <summary>
    ///     A region's frames <paramref name="from" />…<paramref name="to" /> replaced by a processed file: the part
    ///     before keeps the region's id, the new region plays the file where that part played (warped alike), the part
    ///     after is a region of its own.
    /// </summary>
    static void PlaceProcessed(JobEdit job, string regionId, long from, long to, AudioFile file) {
        var state = job.State;
        if (!state.Regions.TryGetValue(regionId, out var region)) {
            throw new EditException($"Region '{regionId}' is gone.");
        }

        var timing = RegionTiming.Of(state, region);
        var end = region.FileOffset + region.Length;
        var fromTick = from == region.FileOffset ? region.StartTick : Tick(timing, state, from);
        var ops = new List<DeltaOp>();
        if (from > region.FileOffset) {
            ops.Add(new RegionUpserted(Edits.Piece(region, region.FileOffset, from, region.StartTick) with { FadeOut = 0 }));
        } else {
            ops.Add(new RegionRemoved(region.Id));
        }

        var middle = Edits.Piece(region, from, to, fromTick);
        ops.Add(
            new RegionUpserted(
                middle with {
                    Id = job.Ids.Next(IdPrefix.Region),
                    FileId = file.Id,
                    FileOffset = 0,
                    Length = file.Frames,
                    FadeIn = 0,
                    FadeOut = 0,
                    FlexMarkers = [..middle.FlexMarkers.Select(m => m with { FileFrame = m.FileFrame - from })],
                    PitchNotes = [
                        ..middle.PitchNotes.Select(n => n with {
                                StartFrame = n.StartFrame - from, EndFrame = n.EndFrame - from
                            }
                        )
                    ]
                }
            )
        );
        if (to < end) {
            var toTick = Tick(timing, state, to);
            var shift = timing.Warped
                ? Math.Max(to, (long)Math.Round(timing.FrameAt(toTick)))
                : to + state.TempoMap.SampleAt(toTick, state.SampleRate) - (timing.StartSample + to - region.FileOffset);
            if (shift < end) {
                ops.Add(
                    new RegionUpserted(
                        Edits.Piece(region, shift, end, toTick) with { Id = job.Ids.Next(IdPrefix.Region), FadeIn = 0 }
                    )
                );
            }
        }

        job.Step(ops);
    }

    /// <summary>The first tick at or after where file frame <paramref name="frame" /> of a region plays.</summary>
    static long Tick(RegionTiming timing, ProjectState state, long frame) =>
        timing.Warped
            ? (long)Math.Ceiling(timing.TickAt(frame) - 1e-6)
            : Recordings.CeilingTick(state.TempoMap, timing.StartSample + frame - timing.Region.FileOffset, state.SampleRate);
}
