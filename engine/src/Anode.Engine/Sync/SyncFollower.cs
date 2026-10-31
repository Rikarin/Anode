using Anode.Ipc;

namespace Anode.Engine.Sync;

/// <summary>What the <see cref="SyncFollower" /> drives: the transport, as the host's control plane offers it.</summary>
public interface ISyncTransport {
    int SampleRate { get; }

    /// <summary>The engine's output latency, in frames: the position at a callback is heard this much later.</summary>
    int OutputLatency { get; }

    /// <summary>The transport as of the last callback.</summary>
    EngineSnapshot Snapshot { get; }

    /// <summary>Plays from <paramref name="position" /> (−1: from the playhead). Asynchronous; in order with the rest.</summary>
    void Play(long position);

    void Stop();

    void Locate(long position);

    /// <summary>
    ///     Moves the playhead so that position <paramref name="position" /> is heard at host time
    ///     <paramref name="timeNs" /> (and later positions after it), to the sample.
    /// </summary>
    void LocateAt(long position, long timeNs);

    /// <summary>The transport's tempo while following clock (not the tempo map's); 0 gives it back to the map.</summary>
    void SetTempo(double bpm);

    /// <summary>The project sample of quarter note <paramref name="quarter" /> on the project's tempo map.</summary>
    double SampleAt(double quarter);
}

/// <summary>
///     The host following external MIDI Clock or MTC (docs/09-phase-8.md, "Sync"): system messages from the clock or
///     MTC input are decoded on the MIDI thread (<see cref="Receive" />, <see cref="Poll" />) and drive the transport
///     through <see cref="ISyncTransport" />:
///     <list type="bullet">
///         <item>
///             MIDI Clock: the tempo estimator's tempo becomes the transport's (not the tempo map's); Start plays from
///             the project start, Continue from the playhead, Stop stops, a Song Position Pointer locates.
///         </item>
///         <item>
///             MTC: quarter frames chase: stopped, it plays from the timecode (less the start offset); playing, it
///             relocates (to the sample, at the timecode's time) when the playhead is half a frame or more off; a full
///             frame locates; and it stops when the quarter frames stop.
///         </item>
///     </list>
///     The decision runs without allocating; the transport calls it makes are queued.
/// </summary>
public sealed class SyncFollower {
    /// <summary>How long after a relocate the chase waits before judging the playhead again (the disk catches up).</summary>
    const long RelocateHoldNs = 250_000_000;

    /// <summary>How far ahead of now a chase from a stop aims, for the disk to prime.</summary>
    const long StartLeadNs = 150_000_000;

    readonly ISyncTransport transport;
    readonly MidiClockDecoder clock = new();
    readonly TempoEstimator estimator = new();
    readonly Lock gate = new();
    MtcDecoder mtc = new(EngineMtcRate.Fps25);
    Settings settings = new(EngineSyncSource.Internal, null, null, EngineMtcRate.Fps25, 0);
    bool chasing; // MTC started the transport
    bool startRequested;
    long lastRelocateNs;
    int pendingPointer = -1;

    public SyncFollower(ISyncTransport transport) {
        this.transport = transport;
    }

    /// <summary>Whether the external source is followed now (MIDI Clock: ticks lock; MTC: timecode runs).</summary>
    public bool Locked {
        get {
            lock (gate) {
                return settings.Source switch {
                    EngineSyncSource.MidiClock => estimator.Locked,
                    EngineSyncSource.Mtc => mtc.Running,
                    _ => false
                };
            }
        }
    }

    /// <summary>The external tempo (MIDI Clock, once estimated); null otherwise.</summary>
    public double? ExternalTempo {
        get {
            lock (gate) {
                return settings.Source == EngineSyncSource.MidiClock && estimator.Tempo > 0 ? estimator.Tempo : null;
            }
        }
    }

    /// <summary>Control thread: the project's sync settings. A change of source or input forgets what was received.</summary>
    public void Configure(SetSyncMessage message) {
        lock (gate) {
            var next = new Settings(
                message.Source,
                string.IsNullOrEmpty(message.ClockInPort) ? null : message.ClockInPort,
                string.IsNullOrEmpty(message.MtcInPort) ? null : message.MtcInPort,
                message.FrameRate,
                message.StartOffsetSeconds
            );
            if (next.Source != settings.Source || next.ClockInPort != settings.ClockInPort
                || next.MtcInPort != settings.MtcInPort || next.FrameRate != settings.FrameRate) {
                estimator.Reset();
                mtc = new(next.FrameRate);
                chasing = false;
                startRequested = false;
                pendingPointer = -1;
                if (settings.Source == EngineSyncSource.MidiClock) {
                    transport.SetTempo(0);
                }
            }

            settings = next;
        }
    }

    /// <summary>
    ///     MIDI thread: whether a system message (or SysEx) from input <paramref name="port" /> is one the follower takes;
    ///     cheap, so the hub can ask before decoding.
    /// </summary>
    public bool Wants(string? port, byte status) {
        var current = Volatile.Read(ref settings);
        return current.Source switch {
            EngineSyncSource.MidiClock => status is 0xF8 or 0xFA or 0xFB or 0xFC or 0xF2
                                          && (current.ClockInPort is null || current.ClockInPort == port),
            EngineSyncSource.Mtc => status is 0xF1 or 0xF0 && (current.MtcInPort is null || current.MtcInPort == port),
            _ => false
        };
    }

    /// <summary>MIDI thread: one message from input <paramref name="port" /> that arrived at <paramref name="timeNs" />.</summary>
    public void Receive(string? port, ReadOnlySpan<byte> message, long timeNs) {
        if (message.IsEmpty || !Wants(port, message[0])) {
            return;
        }

        lock (gate) {
            if (settings.Source == EngineSyncSource.MidiClock) {
                Clock(clock.Receive(message, timeNs));
            } else if (settings.Source == EngineSyncSource.Mtc && mtc.Receive(message, timeNs) is { } position) {
                Chase(position);
            }
        }
    }

    /// <summary>MIDI thread, every pass: notices clock or timecode that stopped.</summary>
    public void Poll(long nowNs) {
        lock (gate) {
            if (settings.Source == EngineSyncSource.MidiClock) {
                estimator.Check(nowNs);
            } else if (settings.Source == EngineSyncSource.Mtc && mtc.Check(nowNs) && (chasing || startRequested)) {
                chasing = false;
                startRequested = false;
                transport.Stop();
            }
        }
    }

    void Clock(in ClockInput input) {
        switch (input.Kind) {
            case ClockInputKind.Tick:
                var tempo = estimator.Tick(input.TimeNs);
                if (tempo > 0) {
                    transport.SetTempo(tempo);
                }

                break;
            case ClockInputKind.Start:
                pendingPointer = -1;
                transport.Play(0);
                break;
            case ClockInputKind.Continue:
                if (pendingPointer >= 0) {
                    transport.Play(PointerSample(pendingPointer));
                    pendingPointer = -1;
                } else {
                    transport.Play(-1);
                }

                break;
            case ClockInputKind.Stop:
                transport.Stop();
                break;
            case ClockInputKind.SongPosition:
                if (transport.Snapshot.State == TransportState.Stopped) {
                    pendingPointer = input.SongPosition;
                    transport.Locate(PointerSample(input.SongPosition));
                } else {
                    pendingPointer = input.SongPosition; // taken by the Continue that follows
                }

                break;
        }
    }

    long PointerSample(int sixteenths) => (long)Math.Round(Math.Max(0, transport.SampleAt(sixteenths / 4.0)));

    void Chase(in MtcPosition position) {
        var rate = transport.SampleRate;
        var seconds = position.Seconds - settings.StartOffsetSeconds;
        if (position.FullFrame) {
            if (!chasing && transport.Snapshot.State == TransportState.Stopped) {
                transport.Locate((long)Math.Round(Math.Max(0, seconds) * rate));
            }

            return;
        }

        var snapshot = transport.Snapshot;
        if (snapshot.State == TransportState.Stopped) {
            if (!startRequested) {
                startRequested = true;
                chasing = true;
                lastRelocateNs = position.TimeNs;
                var lead = seconds + StartLeadNs / 1e9;
                transport.Play((long)Math.Round(Math.Max(0, lead) * rate));
            }

            return;
        }

        startRequested = false;
        if (!chasing || position.TimeNs - lastRelocateNs < RelocateHoldNs) {
            return;
        }

        var heard = snapshot.Position + (position.TimeNs - snapshot.HostTimeNs) * rate / 1e9 - transport.OutputLatency;
        var error = heard / rate - seconds;
        var frame = 1 / Timecode.FramesPerSecond(mtc.Rate);
        if (Math.Abs(error) >= frame / 2) {
            lastRelocateNs = position.TimeNs;
            transport.LocateAt((long)Math.Round(Math.Max(0, seconds) * rate), position.TimeNs);
        }
    }

    sealed record Settings(
        EngineSyncSource Source,
        string? ClockInPort,
        string? MtcInPort,
        EngineMtcRate FrameRate,
        double StartOffsetSeconds
    );
}
