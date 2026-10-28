using Anode.Contracts;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Model;
using Anode.Model.Compile;
using Anode.Model.Editing;
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
///     many tracks changed; otherwise per-track segments; then strips, track IO, instruments, parameters, MIDI I/O,
///     cycle, autopunch and metronome messages, and the MIDI-thru track. A track's settings (<see cref="EngineTrack" />)
///     travel whole with its segment; its plugins' saved states only when the host does not have them yet. A folder
///     stack and a VCA have no engine track: their members get their effective strips (docs/04-phase-3.md, "Stacks").
///     Phase 4 (docs/05-phase-4.md): a strip whose structure changed (inserts, sends, output) goes as
///     <see cref="SetChannelMessage" />, its levels alone as <see cref="SetStripMessage" />, a send's level and pan as
///     <see cref="SetSendLevelMessage" />, an insert's parameters as <see cref="SetInsertParameterMessage" />; the mixer
///     settings as <see cref="SetMixerMessage" />; every model carries the automation targets, and a track whose aux
///     bus or freeze file changed is sent again with its segment.
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
    int sentThru = -1;

    /// <summary>Whether the host has been sent a project's model on this connection.</summary>
    public bool HasProject => sent is not null;

    /// <summary>
    ///     Brings the host to <paramref name="target" />, with <paramref name="thru" /> (a domain track id, or null) as
    ///     the track that hears live MIDI when none is armed.
    /// </summary>
    public async Task SyncAsync(OpenProjectView? target, string? thru, CancellationToken cancellationToken) {
        var thruId = ThruId(target, thru);
        var resendThru = false;
        if (!ReferenceEquals(target, sent)) {
            resendThru = target is null
                ? await UnloadAsync(cancellationToken).ConfigureAwait(false)
                : await LoadAsync(target, cancellationToken).ConfigureAwait(false);
        }

        if (resendThru || thruId != sentThru) {
            await SendThruAsync(thruId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The project closed: the host stops and plays an empty model.</summary>
    async Task<bool> UnloadAsync(CancellationToken cancellationToken) {
        if (hasModel) {
            await link.SendAsync(new TransportMessage(TransportAction.Stop), cancellationToken).ConfigureAwait(false);
            await LoadWholeAsync(
                    [new(TrackEntry.MetronomeTrack, [], [])],
                    0,
                    [],
                    [],
                    new(1, 0, false, false),
                    Path.GetTempPath(),
                    [],
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        sent = null;
        sentModel = null;
        return true;
    }

    /// <summary>Sends what changed since the last sync; true when it sent the project whole.</summary>
    async Task<bool> LoadAsync(OpenProjectView target, CancellationToken cancellationToken) {
        if (compilerSession != target.Session) {
            compiler = new();
            compilerSession = target.Session;
        }

        var model = compiler.Compile(target.State, target.Ids, target.Analyses);
        var full = sent is null || sent.Session != target.Session;
        var previous = sent;
        var carried = new HashSet<string>(StringComparer.Ordinal);
        if (full || NeedsWhole(previous!, target, model)) {
            await LoadWholeAsync(
                    model.Tracks,
                    model.LengthSamples,
                    [..target.State.Tracks.Where(Stacks.IsPlayable).Select(t => Engine(t, target, true))],
                    NewFiles(target),
                    Master(target, true),
                    target.AudioFolder,
                    target.Ids.AutomationTargets(target.State),
                    cancellationToken
                )
                .ConfigureAwait(false);
            carried.UnionWith(target.State.Tracks.Select(static t => t.Id));
        } else {
            await PatchAsync(target, model, carried, cancellationToken).ConfigureAwait(false);
        }

        foreach (var message in Messages(full ? null : previous, target, carried)) {
            await link.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }

        sent = target;
        sentModel = model;
        return full;
    }

    async Task SendThruAsync(int thruId, CancellationToken cancellationToken) {
        await link.SendAsync(new SetMidiThruMessage(thruId), cancellationToken).ConfigureAwait(false);
        sentThru = thruId;
    }

    /// <summary>The engine id of the MIDI-thru track: an instrument or external MIDI track of the project, else −1.</summary>
    static int ThruId(OpenProjectView? target, string? thru) =>
        target is not null
        && thru is not null
        && target.State.FindTrack(thru) is { } track
        && Edits.IsMidiKind(track.Kind)
            ? target.Ids.Track(track.Id)
            : -1;

    bool NeedsWhole(OpenProjectView previous, OpenProjectView target, CompiledModel model) {
        // A global change (the tempo map) recompiles every track: one whole swap (docs/adr/0006-bulk-ipc.md).
        if (TempoChanged(previous.State, target.State)
            || previous.State.Tracks.Exists(t => Stacks.IsPlayable(t) && target.State.FindTrack(t.Id) is null)) {
            return true;
        }

        return Dirty(model).Count > MaxPatchTracks;
    }

    List<TrackModel> Dirty(CompiledModel model, OpenProjectView? target = null) {
        var before = sentModel?.Tracks.ToDictionary(static t => t.TrackId) ?? [];
        return [
            ..model.Tracks.Where(t => !before.TryGetValue(t.TrackId, out var old)
                || !ReferenceEquals(old, t)
                || (target is not null && Rerouted(target, t.TrackId))
            )
        ];
    }

    /// <summary>Whether a track's aux bus or freeze file changed since the last sync (it then goes with a segment).</summary>
    bool Rerouted(OpenProjectView target, int engineId) {
        if (sent is null || target.Ids.TrackOf(engineId) is not { } id) {
            return false;
        }

        var (old, now) = (sent.State.FindTrack(id), target.State.FindTrack(id));
        return old is not null
            && now is not null
            && (old.InputBus != now.InputBus || old.FrozenFileId != now.FrozenFileId);
    }

    async Task PatchAsync(
        OpenProjectView target,
        CompiledModel model,
        HashSet<string> carried,
        CancellationToken cancellationToken
    ) {
        var files = NewFiles(target);
        var targets = target.Ids.AutomationTargets(target.State);
        foreach (var track in Dirty(model, target)) {
            var domainId = target.Ids.TrackOf(track.TrackId);
            var domainTrack = domainId is null ? null : target.State.FindTrack(domainId);
            var version = pool.NextVersion();
            using var segment = pool.WriteTracks([track], version, model.LengthSamples);
            var message = new LoadModelMessage(
                new(segment.Name, segment.Length, version),
                false,
                domainTrack is null
                    ? []
                    : [Engine(domainTrack, target, sent?.State.FindTrack(domainTrack.Id) is null)],
                files,
                Master(target, false),
                target.AudioFolder,
                targets
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
                    new(
                        new(segment.Name, segment.Length, version),
                        false,
                        [],
                        files,
                        Master(target, false),
                        target.AudioFolder,
                        targets
                    ),
                    LoadTimeout,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    async Task LoadWholeAsync(
        IReadOnlyList<TrackModel> tracks,
        long lengthSamples,
        IReadOnlyList<EngineTrack> engineTracks,
        IReadOnlyList<EngineFile> files,
        EngineStrip master,
        string recordDirectory,
        IReadOnlyList<EngineAutomationTarget> targets,
        CancellationToken cancellationToken
    ) {
        var version = pool.NextVersion();
        var size = SegmentWriter.Size(tracks);
        var (slot, segment) = pool.RentSlot(size);
        SegmentWriter.Write(tracks, version, lengthSamples, pool.Prefix, 0, size < 4 << 20, segment);
        var message = new LoadModelMessage(
            new(segment.Name, segment.Length, version),
            true,
            engineTracks,
            files,
            master,
            recordDirectory,
            targets
        );
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

    static bool TempoChanged(ProjectState before, ProjectState now) =>
        before.SampleRate != now.SampleRate
        || before.Settings.Tempo != now.Settings.Tempo
        || before.PlaybackSpeed != now.PlaybackSpeed
        || before.Settings.Varispeed?.Mode != now.Settings.Varispeed?.Mode
        || !(before.Settings.TempoEvents ?? []).SequenceEqual(now.Settings.TempoEvents ?? []);

    /// <summary>Strips, track IO, cycle, punch and metronome that changed (all of them after a whole load).</summary>
    static IEnumerable<IpcMessage> Messages(
        OpenProjectView? previous,
        OpenProjectView target,
        HashSet<string> carried
    ) {
        var state = target.State;
        var before = previous?.State;
        if (before is null) {
            yield return new SetStripMessage(-1, Master(target, false));
        } else if (!ReferenceEquals(before.Master, state.Master)) {
            foreach (var message in StripMessages(-1, Master(previous!, false), Master(target, false), target, null)) {
                yield return message;
            }
        }

        foreach (var track in state.Tracks) {
            if (Stacks.IsPlayable(track) && !carried.Contains(track.Id) && before?.FindTrack(track.Id) is { } old) {
                foreach (var message in TrackMessages(previous!, old, track, target)) {
                    yield return message;
                }
            }
        }

        foreach (var message in SettingsMessages(before, state)) {
            yield return message;
        }
    }

    /// <summary>
    ///     What changed in a strip the host has: its structure (then the whole strip, with the saved states of inserts it
    ///     does not have yet), or its levels, sends' levels and inserts' parameters.
    /// </summary>
    static IEnumerable<IpcMessage> StripMessages(
        int id,
        EngineStrip old,
        EngineStrip now,
        OpenProjectView target,
        string? trackId
    ) {
        if (Strips.StructureDiffers(old, now)) {
            return [Channel(id, old, target, trackId)];
        }

        IEnumerable<IpcMessage> levels =
            old.Gain != now.Gain || old.Pan != now.Pan || old.Mute != now.Mute || old.Solo != now.Solo
                ? [new SetStripMessage(id, now)]
                : [];
        return levels.Concat(
                (old.Sends ?? []).Zip(now.Sends ?? [])
                .Where(static p => p.First.Gain != p.Second.Gain || p.First.Pan != p.Second.Pan)
                .Select(p => new SetSendLevelMessage(id, p.Second.Id, p.Second.Gain, p.Second.Pan))
            )
            .Concat(
                (old.Inserts ?? []).Zip(now.Inserts ?? [])
                .SelectMany(p => p.Second.Parameters
                    .Where(q => !p.First.Parameters.TryGetValue(q.Key, out var was) || !was.Equals(q.Value))
                    .Select(q => new SetInsertParameterMessage(id, p.Second.Id, q.Key, q.Value))
                )
            );
    }

    /// <summary>A strip whole, with the saved states of the inserts the host does not have yet (by id and plugin).</summary>
    static SetChannelMessage Channel(int id, EngineStrip old, OpenProjectView target, string? trackId) {
        var known = (old.Inserts ?? []).Select(static i => (i.Id, i.PluginId)).ToHashSet();
        var strip = trackId is null
            ? Master(target, true)
            : Engine(target.State.FindTrack(trackId)!, target, true).Strip;
        return new(
            id,
            strip with {
                Inserts = [
                    ..(strip.Inserts ?? []).Select(i => known.Contains((i.Id, i.PluginId)) ? i with { State = null } : i
                    )
                ]
            }
        );
    }

    /// <summary>Cycle, punch and metronome that changed (all of them without <paramref name="before" />).</summary>
    static IEnumerable<IpcMessage> SettingsMessages(ProjectState? before, ProjectState state) {
        var settings = state.Settings;
        var tempoChanged = before is null || TempoChanged(before, state);
        var map = state.PlaybackMap;
        if (tempoChanged || before!.Settings.Cycle != settings.Cycle) {
            yield return new SetCycleMessage(
                settings.Cycle.Enabled,
                map.SampleAt(settings.Cycle.StartTick, state.SampleRate),
                map.SampleAt(settings.Cycle.EndTick, state.SampleRate)
            );
        }

        // Autopunch in samples, whenever the settings, the tempo or the project change (docs/04-phase-3.md).
        if (tempoChanged || before!.Settings.Recording != settings.Recording) {
            var recording = settings.Recording;
            yield return new SetPunchMessage(
                recording.Autopunch,
                map.SampleAt(recording.PunchInTick, state.SampleRate),
                map.SampleAt(recording.PunchOutTick, state.SampleRate)
            );
        }

        if (before is null || before.Settings.Mixer != settings.Mixer) {
            var mixer = settings.Mixer;
            yield return new SetMixerMessage(
                mixer.Pdc switch {
                    PdcMode.Tracks => "tracks",
                    PdcMode.Off => "off",
                    _ => "all"
                },
                mixer.LowLatency,
                mixer.LowLatencyLimitMs
            );
        }

        if (before is null
            || before.Settings.Metronome.Enabled != settings.Metronome.Enabled
            || before.Settings.Metronome.LevelDb != settings.Metronome.LevelDb
            || before.Settings.Metronome.CountIn != settings.Metronome.CountIn) {
            yield return new SetMetronomeMessage(
                settings.Metronome.Enabled,
                Units.DbToGain(settings.Metronome.LevelDb),
                settings.Metronome.CountIn
            );
        }
    }

    /// <summary>What changed in a track the host has and is not sent whole (its effective strip: a stack's change too).</summary>
    static IEnumerable<IpcMessage> TrackMessages(
        OpenProjectView previous,
        Track old,
        Track track,
        OpenProjectView target
    ) {
        var id = target.Ids.Track(track.Id);
        var strip = Engine(track, target, false).Strip;
        foreach (var message in StripMessages(
                     id,
                     Engine(old, previous with { Ids = target.Ids }, false).Strip,
                     strip,
                     target,
                     track.Id
                 )) {
            yield return message;
        }

        if (old.Input != track.Input
            || old.RecordArmed != track.RecordArmed
            || old.InputMonitoring != track.InputMonitoring) {
            var e = Engine(track, target, false);
            yield return new SetTrackIoMessage(id, e.InputChannel, e.Stereo, e.RecordArmed, e.Monitoring);
        }

        foreach (var message in InstrumentMessages(id, old.Instrument, track.Instrument, target)) {
            yield return message;
        }

        if (old.MidiInput != track.MidiInput || old.MidiOutput != track.MidiOutput) {
            yield return new SetMidiIoMessage(id, Midi(track)!);
        }
    }

    /// <summary>
    ///     What changed in a track's instrument: a new plugin or file rebuilds it (<see cref="SetInstrumentMessage" />,
    ///     at its parameters, without a saved state); otherwise each parameter that moved is set.
    /// </summary>
    static IEnumerable<IpcMessage> InstrumentMessages(
        int id,
        InstrumentSlot? old,
        InstrumentSlot? now,
        OpenProjectView target
    ) {
        if (ReferenceEquals(old, now) || old == now) {
            yield break;
        }

        if (old is null || now is null || old.PluginId != now.PluginId || old.File != now.File) {
            // A patch's instrument comes with its state; any other new instrument starts from its parameters.
            var track = target.State.Tracks.Find(t => target.Ids.TrackOf(id) == t.Id);
            var state = track is null ? null : target.StateOf(track);
            yield return new SetInstrumentMessage(id, now is null ? null : Engine(now, target, state));
            yield break;
        }

        foreach (var (parameter, value) in now.Parameters) {
            if (!old.Parameters.TryGetValue(parameter, out var before) || !before.Equals(value)) {
                yield return new SetParameterMessage(id, parameter, value);
            }
        }
    }

    /// <summary>The master's engine strip; its inserts' saved states when <paramref name="withState" />.</summary>
    public static EngineStrip Master(OpenProjectView view, bool withState) =>
        Strips.Engine(view.State, null, view.Ids, withState ? i => Base64(view.StateOf(null, i)) : null);

    /// <summary>
    ///     A track's runtime settings; its instrument and inserts with their saved states when
    ///     <paramref name="withState" /> (the host is to build them from scratch); an aux's input bus; a frozen track's
    ///     freeze file.
    /// </summary>
    public static EngineTrack Engine(Track track, OpenProjectView view, bool withState) {
        var state = withState ? view.StateOf(track) : null;
        return new(
            view.Ids.Track(track.Id),
            track.Input.Channel - 1,
            track.Input.Stereo,
            track.RecordArmed,
            track.InputMonitoring,
            Strips.Engine(view.State, track, view.Ids, withState ? i => Base64(view.StateOf(track.Id, i)) : null),
            track.Instrument is { } slot ? Engine(slot, view, state) : null,
            Midi(track),
            track.Kind == TrackKind.Aux ? track.InputBus : 0,
            track.FrozenFileId is { } frozen && view.State.Files.TryGetValue(frozen, out var file)
                ? Path.GetFullPath(Path.Combine(view.Folder, file.Path))
                : null
        );
    }

    static EngineInstrument Engine(InstrumentSlot slot, OpenProjectView view, byte[]? state) =>
        new(slot.PluginId, slot.Parameters, view.InstrumentFile(slot), Base64(state));

    static string? Base64(byte[]? bytes) => bytes is null ? null : Convert.ToBase64String(bytes);

    /// <summary>The MIDI I/O of an instrument or external MIDI track (null for an audio track).</summary>
    static EngineMidiIo? Midi(Track track) =>
        Edits.IsMidiKind(track.Kind)
            ? new(
                track.MidiInput.Device,
                track.MidiInput.Channel,
                track.MidiOutput?.Device,
                track.MidiOutput?.Channel ?? 0
            )
            : null;
}

static partial class EngineLog {
    [LoggerMessage(Level = LogLevel.Warning, Message = "The host did not load model {Version}")]
    public static partial void LoadFailed(ILogger logger, long version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host reported: {Message} ({Request})")]
    public static partial void HostError(ILogger logger, string message, string? request);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The engine bypassed plugin {PluginId}: {Message}")]
    public static partial void PluginProblem(ILogger logger, string pluginId, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Applying a finished recording failed")]
    public static partial void RecordingFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host did not report the instruments' states in time")]
    public static partial void StatesTimedOut(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The host reported a state for engine track {TrackId} that is not base64"
    )]
    public static partial void BadState(ILogger logger, int trackId);
}
