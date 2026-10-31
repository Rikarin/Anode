using Anode.Audio.Native;
using Anode.Engine.Midi;
using Anode.Engine.Sync;
using Anode.Engine.Timing;
using Anode.Ipc;
using Anode.Ipc.Segments;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Anode.Engine;

// Sync on the audio thread (docs/09-phase-8.md, "Sync", workstream A). Every block:
//   - the transport's tempo: while following MIDI Clock or Link, a one-point tempo map at the external tempo replaces
//     the model's for the block's transport context, the launcher and the clock it sends, anchored so the quarter-note
//     position runs on continuously (and rebased on the model's after a locate). Tempo from clock is never written
//     into the model's tempo map; the model's regions keep their sample positions.
//   - Link: the audio session state is captured, Anode's tempo changes (the model's at the playhead) are committed to
//     it, and while rolling the quarter-note position is aligned to Link's beat at the block's output time, at a
//     quantum of the bar at the playhead (so Anode's phase within the bar is Link's). Play waits for the frame where
//     Link's phase reaches the playhead's (quantized launch; at once when alone, which instead maps Link's beat to
//     Anode's); with start/stop sync, Link's start and stop drive the transport and Anode's go to Link.
//   - MIDI Clock and MTC out: generated from the block's transport and stamped with each message's frame's output
//     host time on a smoothed clock (BlockClock), for the MIDI thread to send to each destination plus its delay.
// No allocation, no locks: Link's audio-thread calls are real-time safe.
public sealed unsafe partial class AudioEngine {
    const int SyncEventCapacity = 256;

    readonly MidiClockEncoder clockOut = new();
    readonly SyncMidiEvent[] syncEvents = new SyncMidiEvent[SyncEventCapacity];
    readonly BlockClock callbackClock = new();
    readonly TempoMapPoint[] syncPoints = GC.AllocateArray<TempoMapPoint>(1, true);
    SyncConfig syncConfig = SyncConfig.Internal;
    SyncConfig appliedSync = SyncConfig.Internal;
    bool syncOverride;
    long syncAnchorId = -1;
    bool syncLocated;
    long smoothedNs;
    double externalTempo;
    int linkPeers;
    bool linkPending;
    bool linkWasPlaying;
    bool linkWasRolling;
    double linkModelBpm = double.NaN;
    double linkTempo;
    double linkPhaseError;

    /// <summary>Sync messages for the MIDI thread to send (consumer: the MIDI thread).</summary>
    public SpscChannel<OutgoingSync> SyncOut { get; } = new(8192);

    /// <summary>Control thread: the sync settings the callback works to.</summary>
    public void SetSync(SyncConfig config) => Volatile.Write(ref syncConfig, config);

    /// <summary>The settings the callback works to now (after the next callback, the last <see cref="SetSync" />).</summary>
    public SyncConfig AppliedSync => Volatile.Read(ref appliedSync);

    /// <summary>MIDI thread: the tempo MIDI Clock gives the transport (0: the tempo map's).</summary>
    public double ExternalTempo {
        get => Volatile.Read(ref externalTempo);
        set => Volatile.Write(ref externalTempo, double.IsFinite(value) && value > 0 ? value : 0);
    }

    /// <summary>Control thread: the Link session's other peers, as last polled (alone, a start does not wait).</summary>
    public int LinkPeers {
        get => Volatile.Read(ref linkPeers);
        set => Volatile.Write(ref linkPeers, value);
    }

    /// <summary>The Link session's tempo as the last callback saw it (0 without Link).</summary>
    public double LinkTempo => Volatile.Read(ref linkTempo);

    /// <summary>
    ///     How far the last rolling block's quarter-note position was from Link's beat before it was aligned, in beats
    ///     (the drift between the device's clock and the host's, plus any tempo change; diagnostics and tests).
    /// </summary>
    public double LinkPhaseError => Volatile.Read(ref linkPhaseError);

    /// <summary>The smoothed host time of the current callback (see <see cref="BlockClock" />).</summary>
    public long SmoothedCallbackNs => smoothedNs;

    TempoMapPoint* SyncPoint => (TempoMapPoint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(syncPoints));

    /// <summary>The tempo map the block runs on: the sync override while one is active, else the model's.</summary>
    TempoMap TempoOf(ModelRoot model) => syncOverride ? new(SyncPoint, 1, SampleRate) : ModelTempo(model);

    /// <summary>The model's tempo map at the engine's rate (a model without tempo points: 120 bpm in 4/4).</summary>
    TempoMap ModelTempo(ModelRoot model) => model.Tempo.SampleRate == SampleRate ? model.Tempo : new(SampleRate);

    /// <summary>The output host time of frame <paramref name="frame" /> of the callback.</summary>
    long OutputNs(int frame) => smoothedNs + (long)((OutputLatency + frame) * 1e9 / SampleRate);

    /// <summary>Start of the callback: the smoothed clock and the settings.</summary>
    void BeginSync(long now, int frames) {
        smoothedNs = callbackClock.Update(now, frames, SampleRate);
        var config = Volatile.Read(ref syncConfig);
        if (ReferenceEquals(config, appliedSync)) {
            return;
        }

        if (config.Source != appliedSync.Source || !ReferenceEquals(config.Link, appliedSync.Link)) {
            syncOverride = false;
            linkPending = false;
            linkModelBpm = double.NaN;
            linkWasPlaying = false;
            Volatile.Write(ref linkTempo, 0);
        }

        if (config.ClockOut != appliedSync.ClockOut) {
            clockOut.Reset();
        }

        Volatile.Write(ref appliedSync, config);
    }

    /// <summary>Whether Play waits for Link's phase (the source is Link).</summary>
    bool LinkAligns => appliedSync is { Source: EngineSyncSource.Link, Link: not null };

    /// <summary>The Play command: rolls now, or once Link's phase comes round.</summary>
    void RequestPlay() {
        if (LinkAligns) {
            linkPending = true;
        } else {
            StartRolling();
        }
    }

    void StartRolling() {
        transport.Play();
        flashbackRestart = true;
        ReleaseOverrides();
    }

    /// <summary>Link's start/stop stopped the session: the transport stops as the Stop command would (not recording).</summary>
    void StopFromSync(ModelRoot model) {
        if (transport.State != TransportState.Playing || countInRemaining > 0) {
            return;
        }

        transport.Stop();
        Panic();
        StopLaunchers(model);
    }

    /// <summary>
    ///     Called before each stretch of the callback while a Link start is pending: how many frames from
    ///     <paramref name="offset" /> until it starts (0: now, and the transport has started), capped at
    ///     <paramref name="count" />.
    /// </summary>
    int LinkStart(ModelRoot model, int offset, int count) {
        var link = appliedSync.Link;
        if (link is null || transport.Rolling) {
            linkPending = false;
            return count;
        }

        link.CaptureAudio();
        var state = link.AudioState;
        var tempo = ModelTempo(model);
        var position = transport.Position;
        var point = tempo[tempo.IndexAt(position)];
        var quantum = Quantum(point);
        var quarter = tempo.QuarterAt(position);
        var time = OutputNs(offset) / 1000 + appliedSync.LinkOffsetMicros;
        if (LinkPeers == 0) {
            NativeLink.RequestBeatAtTime(state, quarter - point.BarQuarter, time, quantum); // alone: Link follows us
            Begin(link, state, time);
            return 0;
        }

        var bpm = NativeLink.Tempo(state);
        var phase = NativeLink.PhaseAtTime(state, time, quantum);
        var wanted = Modulo(quarter - point.BarQuarter, quantum);
        var beats = Modulo(wanted - phase, quantum);
        var frame = bpm / 60 / SampleRate; // one frame, in beats
        if (beats > quantum - frame) {
            beats = 0; // the phase is up to a frame past it (the stretch before ended on it): now, not a bar later
        }

        var frames = (long)Math.Floor(beats / frame);
        if (frames <= 0) {
            Begin(link, state, time);
            return 0;
        }

        return (int)Math.Min(count, frames);

        void Begin(NativeLink peer, nint session, long at) {
            linkPending = false;
            if (appliedSync.LinkStartStop && !NativeLink.IsPlaying(session)) {
                NativeLink.SetIsPlaying(session, true, at);
                linkWasPlaying = true;
            }

            peer.CommitAudio();
            StartRolling();
            linkWasRolling = true;
            syncAnchorId = -1; // the anchor is the model's quarter at the start
        }
    }

    /// <summary>
    ///     Before a block's transport context: the sync tempo map for the block (MIDI Clock's tempo, Link's tempo and
    ///     beat), and Link's start/stop and tempo exchange.
    /// </summary>
    void PrepareSync(ModelRoot model, long position, int offset, bool rolling) {
        var config = appliedSync;
        switch (config.Source) {
            case EngineSyncSource.MidiClock when ExternalTempo > 0:
                Override(model, position, ExternalTempo, double.NaN);
                break;
            case EngineSyncSource.Link when config.Link is { } link:
                LinkBlock(model, link, config, position, offset, rolling);
                break;
            default:
                syncOverride = false;
                break;
        }
    }

    void LinkBlock(ModelRoot model, NativeLink link, SyncConfig config, long position, int offset, bool rolling) {
        link.CaptureAudio();
        var state = link.AudioState;
        var tempo = ModelTempo(model);
        var point = tempo[tempo.IndexAt(position)];
        var quantum = Quantum(point);
        var time = OutputNs(offset) / 1000 + config.LinkOffsetMicros;
        var bpm = NativeLink.Tempo(state);
        var commit = false;

        // Anode's tempo (the model's at the playhead) goes to Link when it changes; Link's comes back as the
        // transport's tempo.
        var modelBpm = tempo.At(position).Bpm;
        if (!double.IsNaN(linkModelBpm)
            && Math.Abs(modelBpm - linkModelBpm) > 0.005
            && Math.Abs(modelBpm - bpm) > 0.005) {
            NativeLink.SetTempo(state, modelBpm, time);
            bpm = modelBpm;
            commit = true;
        }

        linkModelBpm = modelBpm;
        Volatile.Write(ref linkTempo, bpm);

        if (config.LinkStartStop && LinkStartStop(model, state, time)) {
            commit = true;
        }

        rolling &= transport.Rolling;
        linkWasRolling = transport.Rolling;
        if (rolling) {
            var current = syncOverride && syncAnchorId == transport.Anchor.Id
                ? new TempoMap(SyncPoint, 1, SampleRate).QuarterAt(position)
                : tempo.QuarterAt(position);
            var anode = Modulo(current - point.BarQuarter, quantum);
            var phase = NativeLink.PhaseAtTime(state, time, quantum);
            var delta = Modulo(phase - anode + quantum / 2, quantum) - quantum / 2;
            Volatile.Write(ref linkPhaseError, delta);
            Override(model, position, bpm, current + delta);
        } else {
            Override(model, position, bpm, double.NaN);
        }

        if (commit) {
            link.CommitAudio();
        }
    }

    /// <summary>
    ///     Link's start/stop sync: the session's start makes a start wait for the phase, its stop stops the transport;
    ///     Anode's own start or stop (one that did not wait for the phase) goes to the session. True when the state
    ///     changed and must be committed.
    /// </summary>
    bool LinkStartStop(ModelRoot model, nint state, long time) {
        var playing = NativeLink.IsPlaying(state);
        if (playing != linkWasPlaying) {
            linkWasPlaying = playing;
            if (playing && !transport.Rolling) {
                linkPending = true;
            } else if (!playing && transport.Rolling) {
                StopFromSync(model);
            }

            return false;
        }

        if (transport.Rolling == linkWasRolling || playing == transport.Rolling) {
            return false;
        }

        NativeLink.SetIsPlaying(state, transport.Rolling, time);
        linkWasPlaying = transport.Rolling;
        return true;
    }

    /// <summary>
    ///     The one-point map at <paramref name="bpm" /> from <paramref name="position" />, at quarter
    ///     <paramref name="quarter" /> (NaN: where the current map has it, or the model's after a locate), with the
    ///     model's signature there.
    /// </summary>
    void Override(ModelRoot model, long position, double bpm, double quarter) {
        var tempo = ModelTempo(model);
        var point = tempo[tempo.IndexAt(position)];
        if (double.IsNaN(quarter)) {
            quarter = syncOverride && syncAnchorId == transport.Anchor.Id
                ? new TempoMap(SyncPoint, 1, SampleRate).QuarterAt(position)
                : tempo.QuarterAt(position);
        }

        *SyncPoint = new() {
            Sample = position,
            Quarter = quarter,
            Bpm = Math.Clamp(bpm, 5, 999),
            Slope = 0,
            BarQuarter = point.BarQuarter,
            Numerator = point.Numerator,
            Denominator = point.Denominator
        };
        syncOverride = true;
        syncAnchorId = transport.Anchor.Id;
    }

    /// <summary>
    ///     After a block's transport context: its MIDI Clock and MTC, stamped at their frames' output times, for the MIDI
    ///     thread.
    /// </summary>
    void EmitSync(in TempoMap tempo, bool rolling, long position, int offset, int frames) {
        var config = appliedSync;
        var events = syncEvents.AsSpan();
        if (config.ClockOut) {
            var startTick = blockTransport.Quarter * 960;
            var count = syncLocated && !rolling
                ? clockOut.Locate(startTick, events)
                : clockOut.Process(rolling, startTick, tempo.QuarterAt(position + frames) * 960, frames, events);
            Queue(SyncMessageKind.Clock, events[..count], offset);
        }

        if (config.Mtc is { } mtc) {
            var start = (double)position / SampleRate + config.StartOffsetSeconds;
            if (syncLocated) {
                Span<byte> full = stackalloc byte[10];
                if (mtc.FullFrame(start, full) == 10) {
                    SyncOut.TryEnqueue(
                        new(OutputNs(offset), SyncMessageKind.Mtc, 10, full[5], full[6], full[7], full[8])
                    );
                }
            }

            var count = mtc.Process(rolling, start, start + (double)frames / SampleRate, frames, events);
            Queue(SyncMessageKind.Mtc, events[..count], offset);
        }

        syncLocated = false;
    }

    void Queue(SyncMessageKind kind, ReadOnlySpan<SyncMidiEvent> events, int offset) {
        foreach (var e in events) {
            SyncOut.TryEnqueue(new(OutputNs(offset + e.Frame), kind, e.Length, e.Status, e.Data1, e.Data2));
        }
    }

    /// <summary>The Link quantum at a tempo map point: its bar, in quarter notes.</summary>
    static double Quantum(in TempoMapPoint point) => point.Numerator * 4.0 / Math.Max(1, (int)point.Denominator);

    static double Modulo(double value, double modulus) {
        var r = value % modulus;
        return r < 0 ? r + modulus : r;
    }
}
