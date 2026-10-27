using Anode.Contracts;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Model;
using Anode.Model.Compile;
using Anode.Server.Projects;
using System.Collections.Concurrent;

namespace Anode.Server.Engine;

/// <summary>
///     One connection to a host: writes messages (one at a time) and matches <see cref="ModelLoadedMessage" />s to
///     the loads waiting for them.
/// </summary>
public sealed class HostLink(Stream stream) : IDisposable {
    readonly SemaphoreSlim writing = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> loads = new();

    public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken) {
        await writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            await IpcFraming.WriteAsync(stream, message, cancellationToken).ConfigureAwait(false);
        } finally {
            writing.Release();
        }
    }

    /// <summary>Sends a model and waits for the host's acknowledgement; false on an error reply or a timeout.</summary>
    public async Task<bool> LoadAsync(LoadModelMessage message, TimeSpan timeout, CancellationToken cancellationToken) {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        loads[message.Segment.Version] = done;
        try {
            await SendAsync(message, cancellationToken).ConfigureAwait(false);
            return await done.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        } catch (TimeoutException) {
            return false;
        } finally {
            loads.TryRemove(message.Segment.Version, out _);
        }
    }

    public void ModelLoaded(long version) {
        if (loads.TryGetValue(version, out var done)) {
            done.TrySetResult(true);
        }
    }

    /// <summary>An error reply: fails the loads waiting (the host names the request, or cannot).</summary>
    public void Error(ErrorMessage error) {
        if (error.Request is null || error.Request.Contains("loadModel", StringComparison.OrdinalIgnoreCase)) {
            foreach (var done in loads.Values) {
                done.TrySetResult(false);
            }
        }
    }

    public void Dispose() => writing.Dispose();
}

/// <summary>
///     Keeps a host in step with the open project (docs/02-phase-1.md, "Model swaps"): it compares the project it last
///     sent with the current one and sends only the difference — a whole model through the pool on the first sync (and
///     after every reconnect: a fresh <see cref="HostSync" />), when tracks went away, after a tempo change or when
///     many tracks changed; otherwise per-track segments; then strips, track IO, cycle and metronome messages.
/// </summary>
public sealed class HostSync(HostLink link, SegmentPool pool, ILogger logger) {
    /// <summary>More dirty tracks than this go as one whole model.</summary>
    public const int MaxPatchTracks = 8;

    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(5);

    readonly HashSet<int> sentFiles = [];
    ModelCompiler compiler = new();
    long compilerSession = -1;
    OpenProjectView? sent;
    CompiledModel? sentModel;
    bool hasModel;

    /// <summary>Whether the host has been sent a project's model on this connection.</summary>
    public bool HasProject => sent is not null;

    public async Task SyncAsync(OpenProjectView? target, CancellationToken cancellationToken) {
        if (ReferenceEquals(target, sent)) {
            return;
        }

        if (target is null) {
            if (hasModel) {
                await link.SendAsync(new TransportMessage(TransportAction.Stop), cancellationToken).ConfigureAwait(false);
                await LoadWholeAsync([new(TrackEntry.MetronomeTrack, [], [])], 0, [], [], new(1, 0, false, false),
                    Path.GetTempPath(), cancellationToken).ConfigureAwait(false);
            }

            sent = null;
            sentModel = null;
            return;
        }

        if (compilerSession != target.Session) {
            compiler = new();
            compilerSession = target.Session;
        }

        var model = compiler.Compile(target.State, target.Ids);
        var full = sent is null || sent.Session != target.Session;
        var previous = sent;
        var carried = new HashSet<string>(StringComparer.Ordinal);
        if (full || NeedsWhole(previous!, target, model)) {
            await LoadWholeAsync(
                model.Tracks,
                model.LengthSamples,
                [..target.State.Tracks.Select(t => Engine(t, target.Ids))],
                NewFiles(target),
                Engine(target.State.Master),
                target.AudioFolder,
                cancellationToken
            ).ConfigureAwait(false);
            carried.UnionWith(target.State.Tracks.Select(static t => t.Id));
        } else {
            await PatchAsync(target, model, carried, cancellationToken).ConfigureAwait(false);
        }

        foreach (var message in Messages(full ? null : previous, target, carried)) {
            await link.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }

        sent = target;
        sentModel = model;
    }

    bool NeedsWhole(OpenProjectView previous, OpenProjectView target, CompiledModel model) {
        // A global change (the tempo map) recompiles every track: one whole swap (docs/adr/0006-bulk-ipc.md).
        if (previous.State.Settings.Tempo != target.State.Settings.Tempo
            || previous.State.Tracks.Any(t => target.State.FindTrack(t.Id) is null)) {
            return true;
        }

        return Dirty(model).Count > MaxPatchTracks;
    }

    List<TrackModel> Dirty(CompiledModel model) {
        var before = sentModel?.Tracks.ToDictionary(static t => t.TrackId) ?? [];
        return [..model.Tracks.Where(t => !before.TryGetValue(t.TrackId, out var old) || !ReferenceEquals(old, t))];
    }

    async Task PatchAsync(
        OpenProjectView target,
        CompiledModel model,
        HashSet<string> carried,
        CancellationToken cancellationToken
    ) {
        var files = NewFiles(target);
        foreach (var track in Dirty(model)) {
            var domainId = target.Ids.TrackOf(track.TrackId);
            var domainTrack = domainId is null ? null : target.State.FindTrack(domainId);
            var version = pool.NextVersion();
            using var segment = pool.WriteTracks([track], version, model.LengthSamples);
            var message = new LoadModelMessage(
                new(segment.Name, segment.Length, version),
                false,
                domainTrack is null ? [] : [Engine(domainTrack, target.Ids)],
                files,
                Engine(target.State.Master),
                target.AudioFolder
            );
            if (!await link.LoadAsync(message, LoadTimeout, cancellationToken).ConfigureAwait(false)) {
                EngineLog.LoadFailed(logger, version);
            }

            files = [];
            if (domainTrack is not null) {
                carried.Add(domainTrack.Id);
            }
        }

        if (files.Count > 0) {
            // New files without a dirty track (an import): they ride on the metronome's patch.
            var version = pool.NextVersion();
            var clicks = model.Tracks[^1];
            using var segment = pool.WriteTracks([clicks], version, model.LengthSamples);
            await link.LoadAsync(
                new(new(segment.Name, segment.Length, version), false, [], files, Engine(target.State.Master),
                    target.AudioFolder),
                LoadTimeout,
                cancellationToken
            ).ConfigureAwait(false);
        }
    }

    async Task LoadWholeAsync(
        IReadOnlyList<TrackModel> tracks,
        long lengthSamples,
        IReadOnlyList<EngineTrack> engineTracks,
        IReadOnlyList<EngineFile> files,
        EngineStrip master,
        string recordDirectory,
        CancellationToken cancellationToken
    ) {
        var version = pool.NextVersion();
        var size = SegmentWriter.Size(tracks);
        var (slot, segment) = pool.RentSlot(size);
        SegmentWriter.Write(tracks, version, lengthSamples, pool.Prefix, 0, size < 4 << 20, segment);
        var message = new LoadModelMessage(new(segment.Name, segment.Length, version), true, engineTracks, files, master,
            recordDirectory);
        if (await link.LoadAsync(message, LoadTimeout, cancellationToken).ConfigureAwait(false)) {
            pool.Loaded(slot);
        } else {
            EngineLog.LoadFailed(logger, version);
            pool.Abandon(slot);
        }

        hasModel = true;
    }

    List<EngineFile> NewFiles(OpenProjectView target) {
        var files = new List<EngineFile>();
        foreach (var file in target.State.Files.Values) {
            var id = target.Ids.File(file.Id);
            if (sentFiles.Add(id)) {
                files.Add(new(id, Path.Combine(target.Folder, file.Path), file.Channels, file.SampleRate, file.Frames));
            }
        }

        return files;
    }

    /// <summary>Strips, track IO, cycle and metronome that changed (all of them after a whole load).</summary>
    static IEnumerable<IpcMessage> Messages(OpenProjectView? previous, OpenProjectView target, HashSet<string> carried) {
        var state = target.State;
        var before = previous?.State;
        if (before is null || before.Master != state.Master) {
            yield return new SetStripMessage(-1, Engine(state.Master));
        }

        foreach (var track in state.Tracks) {
            if (carried.Contains(track.Id) || before?.FindTrack(track.Id) is not { } old) {
                continue;
            }

            var id = target.Ids.Track(track.Id);
            if (old.Strip != track.Strip) {
                yield return new SetStripMessage(id, Engine(track.Strip));
            }

            if (old.Input != track.Input || old.RecordArmed != track.RecordArmed
                || old.InputMonitoring != track.InputMonitoring) {
                var e = Engine(track, target.Ids);
                yield return new SetTrackIoMessage(id, e.InputChannel, e.Stereo, e.RecordArmed, e.Monitoring);
            }
        }

        var settings = state.Settings;
        if (before is null || before.Settings.Cycle != settings.Cycle || before.Settings.Tempo != settings.Tempo
            || before.SampleRate != state.SampleRate) {
            var map = state.TempoMap;
            yield return new SetCycleMessage(
                settings.Cycle.Enabled,
                map.SampleAt(settings.Cycle.StartTick, state.SampleRate),
                map.SampleAt(settings.Cycle.EndTick, state.SampleRate)
            );
        }

        if (before is null || before.Settings.Metronome.Enabled != settings.Metronome.Enabled
            || before.Settings.Metronome.LevelDb != settings.Metronome.LevelDb) {
            yield return new SetMetronomeMessage(settings.Metronome.Enabled,
                Units.DbToGain(settings.Metronome.LevelDb));
        }
    }

    public static EngineStrip Engine(ChannelStrip strip) =>
        new(Units.DbToGain(strip.VolumeDb), (float)strip.Pan, strip.Mute, strip.Solo);

    public static EngineTrack Engine(Track track, EngineIds ids) =>
        new(ids.Track(track.Id), track.Input.Channel - 1, track.Input.Stereo, track.RecordArmed,
            track.InputMonitoring, Engine(track.Strip));
}

static partial class EngineLog {
    [LoggerMessage(Level = LogLevel.Warning, Message = "The host did not load model {Version}")]
    public static partial void LoadFailed(ILogger logger, long version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host reported: {Message} ({Request})")]
    public static partial void HostError(ILogger logger, string message, string? request);

    [LoggerMessage(Level = LogLevel.Error, Message = "Applying a finished recording failed")]
    public static partial void RecordingFailed(ILogger logger, Exception exception);
}
