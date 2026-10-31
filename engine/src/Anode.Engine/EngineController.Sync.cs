using Anode.Audio;
using Anode.Audio.Native;
using Anode.Engine.Midi;
using Anode.Engine.Sync;
using Anode.Ipc;

namespace Anode.Engine;

// Phase 8's control routing and sync on the control plane (docs/09-phase-8.md, workstream A): the server's routing
// table and surface feedback go to the MIDI thread; the sync settings become the callback's SyncConfig (which streams
// it generates, the MTC rate, the Link peer), the MIDI thread's destinations and the follower's source; the Link peer
// is created when the source becomes Link and destroyed when it stops being it; and the sync state is reported from
// all of them.
public sealed partial class EngineController {
    readonly Lock syncGate = new();
    SyncFollower? follower;
    SetSyncMessage? syncSettings;
    NativeLink? link;
    Task transportQueue = Task.CompletedTask;
    readonly Lock transportQueueGate = new();

    /// <summary>The MIDI Clock and MTC follower (created with the controller).</summary>
    public SyncFollower Follower => follower!;

    /// <summary>The Link peer while the sync source is Link (else null).</summary>
    public NativeLink? Link {
        get {
            lock (syncGate) {
                return link;
            }
        }
    }

    void StartSync() {
        follower = new(new FollowerTransport(this));
        Midi.Sync = follower;
    }

    /// <summary>The server's control routing (<see cref="SetControlRoutingMessage" />).</summary>
    public void SetControlRouting(SetControlRoutingMessage message) =>
        Midi.SetRouting(new(message.ControlPorts, message.Matchers, message.Learn));

    /// <summary>A control surface's feedback (<see cref="SendMidiMessage" />), sent now. False when it could not be.</summary>
    public bool SendMidi(string port, byte[] data) => Midi.SendNow(port, data);

    /// <summary>
    ///     The project's sync settings (<see cref="SetSyncMessage" />): the callback's generators and Link, the MIDI
    ///     thread's destinations, the follower's source. Turning Link off waits until no callback uses the peer before
    ///     destroying it. Throws <see cref="InvalidOperationException" /> when the source is Link and an_link is missing.
    /// </summary>
    public void SetSync(SetSyncMessage message) {
        NativeLink? retired = null;
        lock (syncGate) {
            syncSettings = message;
            if (message.Source == EngineSyncSource.Link && link is null) {
                if (!NativeLink.IsAvailable) {
                    throw new InvalidOperationException("Link is not available: an_link was not built (native/build.sh).");
                }

                link = new(CurrentModelBpm()) { Enabled = true };
            } else if (message.Source != EngineSyncSource.Link && link is not null) {
                retired = link;
                link = null;
            }

            if (link is not null) {
                link.StartStopSync = message.LinkStartStop;
            }

            var clock = message.Destinations.Any(static d => d.Clock);
            var mtc = message.Destinations.Any(static d => d.Mtc)
                ? Engine.AppliedSync.Mtc is { } current && current.Rate == message.FrameRate
                    ? current
                    : new MtcEncoder(message.FrameRate)
                : null;
            var offset = link is null ? 0 : link.ClockMicros - Realtime.NowNanoseconds() / 1000;
            Engine.SetSync(
                new(
                    message.Source,
                    clock,
                    mtc,
                    Math.Max(0, message.StartOffsetSeconds),
                    message.LinkStartStop,
                    link,
                    offset
                )
            );
            Midi.SetSyncDestinations([..message.Destinations]);
            follower!.Configure(message);
        }

        if (retired is not null) {
            WaitGrace(); // the callback may still hold the old config
            WaitCallbacks(); // and has applied the new one
            retired.Dispose();
        }
    }

    /// <summary>
    ///     The sync state as the server reports it (<see cref="SyncStateMessage" />): the source, whether it is
    ///     followed, the external tempo (MIDI Clock's estimate, Link's session tempo) and Link's peers. Also tells the
    ///     callback how many peers there are.
    /// </summary>
    public SyncStateMessage SyncState() {
        lock (syncGate) {
            var source = syncSettings?.Source ?? EngineSyncSource.Internal;
            switch (source) {
                case EngineSyncSource.Link when link is not null:
                    var peers = link.Peers;
                    Engine.LinkPeers = peers;
                    double tempo;
                    lock (link.AppGate) {
                        link.CaptureApp();
                        tempo = NativeLink.Tempo(link.AppState);
                    }

                    return new(source, link.Enabled, Math.Round(tempo, 3), peers);
                case EngineSyncSource.MidiClock or EngineSyncSource.Mtc:
                    var external = follower!.ExternalTempo;
                    return new(source, follower.Locked, external is { } bpm ? Math.Round(bpm, 3) : null, 0);
                default:
                    return new(source, false, null, 0);
            }
        }
    }

    /// <summary>The model's tempo at the playhead (a new Link peer starts at it).</summary>
    double CurrentModelBpm() {
        var root = Engine.Root;
        var tempo = root.Tempo.SampleRate > 0 ? root.Tempo : new(Options.SampleRate);
        return tempo.At(Math.Max(0, Engine.Snapshot.Position)).Bpm;
    }

    /// <summary>Waits until the audio thread has applied every command queued so far (and so has read the settings).</summary>
    void WaitCallbacks() {
        var deadline = DateTime.UtcNow + CommandTimeout;
        var callbacks = Engine.Snapshot.Callbacks;
        while (Engine.Snapshot.Callbacks < callbacks + 2 && DateTime.UtcNow < deadline && !Engine.IsOffline) {
            Thread.Sleep(1);
        }
    }

    void DisposeSync() {
        NativeLink? retired;
        lock (syncGate) {
            retired = link;
            link = null;
        }

        Engine.SetSync(SyncConfig.Internal);
        if (retired is not null) {
            WaitGrace();
            retired.Dispose();
        }
    }

    /// <summary>Runs the follower's transport calls one after another, off the MIDI thread.</summary>
    void Queue(Func<Task> action) {
        lock (transportQueueGate) {
            transportQueue = transportQueue.ContinueWith(
                    async _ => {
                        try {
                            await action().ConfigureAwait(false);
                        } catch (Exception e) when (e is InvalidOperationException or IOException) {
                            Log?.Invoke($"sync: {e.Message}");
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default
                )
                .Unwrap();
        }
    }

    /// <summary>The transport as the follower drives it.</summary>
    sealed class FollowerTransport(EngineController owner) : ISyncTransport {
        public int SampleRate => owner.Options.SampleRate;

        public int OutputLatency => owner.Options.OutputLatency;

        public EngineSnapshot Snapshot => owner.Engine.Snapshot;

        public void Play(long position) => owner.Queue(() => owner.PlayAsync(position));

        public void Stop() =>
            owner.Queue(() => {
                    owner.Stop();
                    return Task.CompletedTask;
                }
            );

        public void Locate(long position) =>
            owner.Queue(() => {
                    owner.Locate(position);
                    return Task.CompletedTask;
                }
            );

        public void LocateAt(long position, long timeNs) =>
            owner.Queue(() => {
                    owner.Send(new(EngineCommandKind.LocateAt, Math.Max(0, position), timeNs));
                    owner.Streamer.Wake();
                    return Task.CompletedTask;
                }
            );

        public void SetTempo(double bpm) => owner.Engine.ExternalTempo = bpm;

        public double SampleAt(double quarter) {
            var root = owner.Engine.Root;
            var tempo = root.Tempo.SampleRate == SampleRate ? root.Tempo : new(SampleRate);
            return tempo.SampleAt(quarter);
        }
    }
}
