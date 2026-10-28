using Anode.Contracts;
using Anode.Ipc;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Model.Storage;
using Anode.Server.Engine;
using Anode.Server.Realtime;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;

namespace Anode.Server.Projects;

/// <summary>
///     Long-running jobs (docs/05-phase-4.md, "Bounce", "Freeze"): one at a time, in order, each rendered through the
///     engine (<see cref="EngineConnection.RenderAsync" />). A bounce renders the mix or stems as 32-bit float WAV into
///     a scratch folder in the project, post-processes each file (<see cref="BounceProcessor" />) into
///     <c>directory</c> (default <c>&lt;project&gt;/Bounces</c>) and, with <c>addToProject</c>, adds a copy at the
///     project's rate to its audio files. A freeze renders a track's source and inserts into the project's
///     <c>Audio Files</c>, from the start to its last region's end plus <see cref="FreezeTailSeconds" /> s, and makes it
///     play that as one undoable step. Every change of state, and progress at most a few times a second, goes out as a
///     <see cref="JobEvent" />. Cancelling a queued job drops it; a running one asks the engine to stop.
/// </summary>
public sealed class Jobs : IDisposable {
    public const double FreezeTailSeconds = 2;
    static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    readonly ProjectSession session;
    readonly EngineConnection engine;
    readonly EventHub hub;
    readonly AudioImport import;
    readonly TimeProvider time;
    readonly ILogger<Jobs> logger;
    readonly ConcurrentDictionary<string, Entry> jobs = new(StringComparer.Ordinal);
    readonly Channel<Entry> queue = Channel.CreateUnbounded<Entry>();
    readonly CancellationTokenSource stopping = new();
    readonly Task worker;
    int engineIds;

    public Jobs(
        ProjectSession session,
        EngineConnection engine,
        EventHub hub,
        AudioImport import,
        TimeProvider time,
        ILogger<Jobs> logger
    ) {
        this.session = session;
        this.engine = engine;
        this.hub = hub;
        this.import = import;
        this.time = time;
        this.logger = logger;
        worker = Task.Run(RunAsync);
    }

    public Job? Get(string id) => jobs.TryGetValue(id, out var entry) ? entry.Job : null;

    /// <summary>Cancels a job (a finished one is left as it is); null when there is none.</summary>
    public Job? Cancel(string id) {
        if (!jobs.TryGetValue(id, out var entry)) {
            return null;
        }

        lock (entry) {
            if (entry.Job.State == JobState.Queued) {
                Set(entry, entry.Job with { State = JobState.Cancelled, Message = "Cancelled." });
            }
        }

        entry.Cancel.Cancel();
        return entry.Job;
    }

    /// <summary>Queues a bounce, checked first: 422 for a request that cannot be carried out.</summary>
    public Job Bounce(BounceRequest request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var state = view.State;
        var name = (request?.FileName ?? string.Empty).Trim();
        if (name.Length is 0 or > Edits.MaxNameLength || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith('.')) {
            throw ProjectException.Unprocessable("A file name has 1…128 characters and no path separators.");
        }

        if (request!.BitDepth is not (16 or 24 or 32)) {
            throw ProjectException.Unprocessable($"A bounce is 16, 24 or 32-bit (float), not {request.BitDepth}.");
        }

        var rate = request.SampleRate ?? state.SampleRate;
        if (!ProjectDefaults.SampleRates.Contains(rate)) {
            throw ProjectException.Unprocessable($"Sample rate {rate} Hz is not one of {string.Join(", ", ProjectDefaults.SampleRates)}.");
        }

        if (!Enum.IsDefined(request.Format) || !Enum.IsDefined(request.Dither) || !Enum.IsDefined(request.Normalize) || !Enum.IsDefined(request.Source)) {
            throw ProjectException.Unprocessable("Unknown format, dither, normalisation or source.");
        }

        if (!(request.LoudnessLufs is >= -70 and <= 0) || !(request.PeakCeilingDb is >= -20 and <= 0)) {
            throw ProjectException.Unprocessable("Loudness is −70…0 LUFS and the ceiling −20…0 dBTP.");
        }

        var (start, end) = Range(state, request.StartTick, request.EndTick);
        string? directory = null;
        if (request.Directory is { Length: > 0 } d) {
            try {
                directory = Path.GetFullPath(d);
            } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) {
                throw ProjectException.Unprocessable($"'{d}' is not a folder path: {e.Message}");
            }
        }

        Track[] stems = [];
        if (request.Source == BounceSource.Stems) {
            stems = request.TrackIds is { Count: > 0 } chosen
                ? [..chosen.Distinct(StringComparer.Ordinal).Select(id => state.FindTrack(id ?? string.Empty) ?? throw ProjectException.NotFound($"No track '{id}'."))]
                : [..state.Tracks.Where(static t => Edits.HasAudioStrip(t.Kind))];
            if (stems.Any(static t => !Edits.HasAudioStrip(t.Kind)) || stems.Length == 0) {
                throw ProjectException.Unprocessable("Stems are of audio, instrument and aux tracks.");
            }
        }

        var format = new BounceFormat(
            request.Format,
            request.BitDepth,
            rate,
            request.Dither,
            request.Normalize,
            request.LoudnessLufs,
            request.PeakCeilingDb
        );
        var plan = new BouncePlan(view.Session, view.Folder, name, directory ?? Path.Combine(view.Folder, "Bounces"), start, end, stems, format, request);
        return Enqueue(JobKind.Bounce, (entry, token) => BounceAsync(entry, plan, token));
    }

    /// <summary>Queues freezing a track: 404 for an unknown track, 422 for one that cannot freeze.</summary>
    public Job Freeze(string trackId) {
        var view = session.View ?? throw ProjectException.NoProject();
        var state = view.State;
        var track = state.FindTrack(trackId) ?? throw ProjectException.NotFound($"No track '{trackId}'.");
        if (track.Kind is not (TrackKind.Audio or TrackKind.Instrument)) {
            throw ProjectException.Unprocessable("Only audio and instrument tracks freeze.");
        }

        if (track.FrozenFileId is not null) {
            throw ProjectException.Unprocessable($"Track '{track.Name}' is frozen already.");
        }

        var end = LastSample(state, track.Id) ?? throw ProjectException.Unprocessable($"Track '{track.Name}' has nothing to freeze.");
        end += (long)(FreezeTailSeconds * state.SampleRate);
        return Enqueue(JobKind.Freeze, (entry, token) => FreezeAsync(entry, view.Session, view.Folder, track, end, token));
    }

    /// <summary>Where a track's last region (audio, MIDI or take folder) ends, in samples, or null when it has none.</summary>
    static long? LastSample(ProjectState state, string trackId) {
        var map = state.TempoMap;
        var ends = state.RegionsOf(trackId)
            .Select(r => Edits.EndSample(state, r))
            .Concat(state.MidiRegionsOf(trackId).Select(r => map.SampleAt(r.StartTick + r.LengthTicks, state.SampleRate)))
            .Concat(state.TakeFoldersOf(trackId).Select(f => map.SampleAt(f.StartTick + f.LengthTicks, state.SampleRate)))
            .ToArray();
        return ends.Length == 0 ? null : ends.Max();
    }

    /// <summary>The range to bounce in samples: the one given, else the cycle when it is on, else the project start to its end.</summary>
    static (long Start, long End) Range(ProjectState state, long? startTick, long? endTick) {
        var settings = state.Settings;
        var (from, to) = startTick is null && endTick is null && settings.Cycle.Enabled
            ? (settings.Cycle.StartTick, settings.Cycle.EndTick)
            : (startTick ?? 0, endTick ?? settings.EndTick);
        if (from < 0 || to > Edits.MaxTick || to <= from) {
            throw ProjectException.Unprocessable("A bounce ends after it starts, inside the project.");
        }

        var map = state.TempoMap;
        return (map.SampleAt(from, state.SampleRate), map.SampleAt(to, state.SampleRate));
    }

    Job Enqueue(JobKind kind, Func<Entry, CancellationToken, Task<IReadOnlyList<string>>> work) {
        var entry = new Entry(
            new(RandomIds.Instance.Next(IdPrefix.Job), kind, JobState.Queued, 0, null, []),
            Interlocked.Increment(ref engineIds),
            work
        );
        jobs[entry.Job.Id] = entry;
        hub.Publish(new JobEvent(entry.Job));
        queue.Writer.TryWrite(entry);
        return entry.Job;
    }

    async Task RunAsync() {
        try {
            await foreach (var entry in queue.Reader.ReadAllAsync(stopping.Token).ConfigureAwait(false)) {
                lock (entry) {
                    if (entry.Job.State != JobState.Queued) {
                        continue;
                    }

                    Set(entry, entry.Job with { State = JobState.Running });
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancel.Token, stopping.Token);
                try {
                    var files = await entry.Work(entry, linked.Token).ConfigureAwait(false);
                    Set(entry, entry.Job with { State = JobState.Done, Progress = 1, Files = files, Message = null });
                } catch (OperationCanceledException) when (entry.Cancel.IsCancellationRequested) {
                    Set(entry, entry.Job with { State = JobState.Cancelled, Message = "Cancelled." });
                } catch (Exception e) when (e is ProjectException
                                                or IOException
                                                or UnauthorizedAccessException
                                                or WavFormatException
                                                or FormatException
                                                or InvalidOperationException
                                                or ArgumentException) {
                    ProjectLog.JobFailed(logger, entry.Job.Id, e.Message);
                    Set(entry, entry.Job with { State = JobState.Failed, Message = e.Message });
                }
            }
        } catch (OperationCanceledException) {
            // shutting down
        }
    }

    async Task<IReadOnlyList<string>> BounceAsync(Entry entry, BouncePlan plan, CancellationToken token) {
        var scratch = Path.Combine(plan.Folder, $".bounce-{entry.Job.Id}");
        Directory.CreateDirectory(scratch);
        try {
            var view = session.View is { } v && v.Session == plan.Session
                ? v
                : throw ProjectException.Conflict("The project was closed.");
            IReadOnlyList<int>? tracks = plan.Request.Source == BounceSource.Stems ? [..plan.Stems.Select(t => view.Ids.Track(t.Id))] : null;
            var target = tracks is null ? Path.Combine(scratch, "mix.wav") : scratch;
            var finished = await engine.RenderAsync(
                    new(entry.EngineId, plan.Start, plan.End, target, tracks, false, plan.Request.Realtime),
                    f => Progress(entry, 0.8 * f),
                    token
                )
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (finished.Error is { } error) {
                throw ProjectException.Unprocessable($"The engine could not render: {error}");
            }

            Directory.CreateDirectory(plan.Directory);
            var outputs = new List<string>();
            var sources = tracks is null
                ? [(Path.Combine(scratch, "mix.wav"), plan.Name)]
                : plan.Stems.Select(t => (Path.Combine(scratch, $"{view.Ids.Track(t.Id)}.wav"), $"{plan.Name} - {Safe(t.Name)}")).ToList();
            for (var i = 0; i < sources.Count; i++) {
                token.ThrowIfCancellationRequested();
                var (source, name) = sources[i];
                var (audio, rate) = BounceProcessor.Read(source);
                var processed = BounceProcessor.Process(audio, rate, plan.Format);
                var output = Path.Combine(plan.Directory, name + plan.Format.Extension);
                BounceProcessor.Write(output, processed, plan.Format, i);
                outputs.Add(output);
                if (plan.Request.AddToProject) {
                    AddToProject(plan, output, audio, rate, processed, scratch, name);
                }

                Progress(entry, 0.8 + 0.2 * (i + 1) / sources.Count);
            }

            return outputs;
        } finally {
            try {
                Directory.Delete(scratch, true);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                ProjectLog.JobFailed(logger, entry.Job.Id, $"could not remove {scratch}: {e.Message}");
            }
        }
    }

    /// <summary>
    ///     Adds a bounced file to the project: as it is when it is a WAV at the project's rate, else a 32-bit float WAV
    ///     of the same mix at the project's rate.
    /// </summary>
    void AddToProject(BouncePlan plan, string output, float[][] render, int renderRate, float[][] processed, string scratch, string name) {
        if (plan.Format.Format == AudioFormat.Wav && plan.Format.SampleRate == renderRate) {
            import.Import(output);
            return;
        }

        var copy = plan.Format.SampleRate == renderRate
            ? processed
            : BounceProcessor.Process(render, renderRate, plan.Format with { SampleRate = renderRate });
        var path = Path.Combine(scratch, name + ".wav");
        BounceProcessor.Write(path, copy, plan.Format with { Format = AudioFormat.Wav, BitDepth = 32, SampleRate = renderRate });
        import.Import(path);
    }

    async Task<IReadOnlyList<string>> FreezeAsync(
        Entry entry,
        long sessionId,
        string folder,
        Track track,
        long end,
        CancellationToken token
    ) {
        var view = session.View is { } v && v.Session == sessionId ? v : throw ProjectException.Conflict("The project was closed.");
        var audio = Path.Combine(folder, ProjectPackage.AudioFolder);
        Directory.CreateDirectory(audio);
        var stamp = time.GetUtcNow().ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(audio, $"Freeze {Safe(track.Name)} {stamp}.wav");
        var finished = await engine.RenderAsync(
                new(entry.EngineId, 0, end, path, [view.Ids.Track(track.Id)], true),
                f => Progress(entry, 0.95 * f),
                token
            )
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (finished.Error is { } error) {
            throw ProjectException.Unprocessable($"The engine could not render: {error}");
        }

        var file = finished.Files is [var written, ..] ? written : path;
        session.Freeze(sessionId, track.Id, file, WavReader.Probe(file));
        return [file];
    }

    static string Safe(string name) {
        var safe = new string([..name.Select(static c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]).Trim();
        return safe.Length == 0 || safe.StartsWith('.') ? "Track" + safe : safe;
    }

    void Progress(Entry entry, double fraction) {
        lock (entry) {
            if (entry.Job.State != JobState.Running) {
                return;
            }

            var now = time.GetUtcNow();
            entry.Job = entry.Job with { Progress = Math.Clamp(fraction, 0, 1) };
            if (now - entry.Published < ProgressInterval) {
                return;
            }

            entry.Published = now;
        }

        hub.Publish(new JobEvent(entry.Job));
    }

    void Set(Entry entry, Job job) {
        lock (entry) {
            entry.Job = job;
            entry.Published = time.GetUtcNow();
        }

        hub.Publish(new JobEvent(job));
    }

    public void Dispose() {
        stopping.Cancel();
        queue.Writer.TryComplete();
        try {
            worker.Wait(TimeSpan.FromSeconds(5));
        } catch (AggregateException) {
            // stopping
        }

        foreach (var entry in jobs.Values) {
            entry.Cancel.Dispose();
        }

        stopping.Dispose();
    }

    sealed record BouncePlan(
        long Session,
        string Folder,
        string Name,
        string Directory,
        long Start,
        long End,
        IReadOnlyList<Track> Stems,
        BounceFormat Format,
        BounceRequest Request
    );

    sealed class Entry(Job job, int engineId, Func<Entry, CancellationToken, Task<IReadOnlyList<string>>> work) {
        public Job Job { get; set; } = job;
        public int EngineId { get; } = engineId;
        public Func<Entry, CancellationToken, Task<IReadOnlyList<string>>> Work { get; } = work;
        public CancellationTokenSource Cancel { get; } = new();
        public DateTimeOffset Published { get; set; }
    }
}
