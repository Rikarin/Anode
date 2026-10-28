using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;
using Anode.Engine.Mixing;
using Anode.Engine.Plugins;
using Anode.Engine.Rendering;

namespace Anode.Engine;

/// <summary>How plugin delay compensation aligns parallel paths (<c>setMixer</c>'s <c>pdc</c>).</summary>
public enum DelayCompensation {
    /// <summary>Every summing point, outputs included, on every insert's latency.</summary>
    All,

    /// <summary>Buses and the master on the latency of tracks' inserts only (not aux or master inserts).</summary>
    Tracks,

    /// <summary>None.</summary>
    Off
}

// The mixer graph (docs/05-phase-4.md, workstream A): per block, first a pass over the plan that works out each
// channel's latency, whether it is on an armed or monitored path (low-latency mode) and whether it sounds under the
// solos, then each channel in plan order: its source (the track's stream, instrument or input; an aux's input bus; the
// master's sum), its inserts in order (bypassed ones skipped, automated parameters set every 32 frames, sidechains from
// another track's post-fader signal or a bus), the delay compensation line, and its output and sends into their
// summing points at per-sample automated gains. The master then runs its inserts and fader into the device; device
// output pairs bypass it. Everything here is audio-thread code: no allocation, no locks.
//
// Sidechains: a source processed later in the same cycle (a track after the insert's own, or a bus fed by one) is
// taken from the previous cycle, one block late; with plugin delay compensation a sidechain is not delayed to match.
//
// Delay compensation delays a channel's post-insert signal before its fader, so automation on a delayed path applies
// to audio as late as the latent path's (where the latent insert delays the audio but not the fader).
public sealed unsafe partial class AudioEngine {
    /// <summary>Parameter automation is applied at this granularity, in frames.</summary>
    public const int ParameterBlock = 32;

    readonly float[][] destLeft = Buffers(MixPlan.Destinations);
    readonly float[][] destRight = Buffers(MixPlan.Destinations);
    readonly float[]?[] snapshotLeft = new float[]?[MixPlan.Buses + 1];
    readonly float[]?[] snapshotRight = new float[]?[MixPlan.Buses + 1];
    readonly int[] snapshotFrames = new int[MixPlan.Buses + 1];
    readonly int[] destLatency = new int[MixPlan.Destinations];
    readonly bool[] destLive = new bool[MixPlan.Destinations];
    readonly bool[] destSolo = new bool[MixPlan.Destinations];
    readonly long[] positions = new long[MaxFrames];
    readonly float[] tapLeft = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] tapRight = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] coefLeft = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] coefRight = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] volumeLane = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] panLane = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] muteLane = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] levelLane = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] sideLeft = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] sideRight = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] edgeLeft = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] edgeRight = GC.AllocateArray<float>(MaxFrames, true);
    readonly MidiEvent[] chunkEvents = new MidiEvent[MidiTrackState.MaxEvents];

    int delayCompensation;
    int lowLatency;
    int lowLatencyLimit;
    int latencyOverflow;
    int masterOutputDelay;
    RenderCapture? capture;
    IAudioCycleObserver? cycleObserver;

    // The block being mixed (audio thread).
    ModelRoot blockModel = ModelRoot.Empty;
    RenderCapture? blockCapture;
    float** blockInputs;
    int blockInputChannels;
    int blockOffset;
    int blockFrames;
    long blockPosition;
    long blockClock;
    bool blockCountIn;

    // The gains of the edge being mixed: constant (gainLeft, gainRight) or per frame (coefLeft, coefRight).
    bool gainConstant;
    float appliedTempo;
    MixPlan? tempoPlan;
    float gainLeft;
    float gainRight;

    /// <summary>The master's channel: its fader is <see cref="Master" />.</summary>
    public ChannelState MasterChannel { get; }

    /// <summary>Which automation targets a control move holds (control thread sets, audio thread clears).</summary>
    public AutomationOverrides Overrides { get; } = new(0);

    public DelayCompensation Compensation => (DelayCompensation)Volatile.Read(ref delayCompensation);

    /// <summary>The render the callback captures into (null: none). Control thread sets it.</summary>
    public RenderCapture? Rendering {
        get => Volatile.Read(ref capture);
        set => Volatile.Write(ref capture, value);
    }

    /// <summary>Hears every callback (a plugin factory's sandboxes). Control thread sets it.</summary>
    public IAudioCycleObserver? CycleObserver {
        get => Volatile.Read(ref cycleObserver);
        set => Volatile.Write(ref cycleObserver, value);
    }

    /// <summary>Control thread: delay compensation, and low-latency mode with its limit in frames.</summary>
    public void SetMixer(DelayCompensation compensation, bool lowLatencyMode, int limitFrames) {
        Volatile.Write(ref delayCompensation, (int)compensation);
        Volatile.Write(ref lowLatencyLimit, Math.Max(0, limitFrames));
        Volatile.Write(ref lowLatency, lowLatencyMode ? 1 : 0);
    }

    /// <summary>
    ///     Set by the audio thread when a path needed more delay than its channel's line holds (a latency grew); the
    ///     control thread then grows the lines (<see cref="EngineController" />).
    /// </summary>
    public bool TakeLatencyOverflow() => Interlocked.Exchange(ref latencyOverflow, 0) != 0;

    /// <summary>Control thread: makes the previous-block copy of bus <paramref name="bus" /> (before a plan reads it).</summary>
    public void EnsureBusSnapshot(int bus) {
        if (snapshotLeft[bus] is null) {
            Volatile.Write(ref snapshotRight[bus], GC.AllocateArray<float>(MaxFrames, true));
            Volatile.Write(ref snapshotLeft[bus], GC.AllocateArray<float>(MaxFrames, true));
        }
    }

    static float[][] Buffers(int count) {
        var buffers = new float[count][];
        for (var i = 0; i < count; i++) {
            buffers[i] = GC.AllocateArray<float>(MaxFrames, true);
        }

        return buffers;
    }

    // ---- The block ---------------------------------------------------------------------------------------------------

    /// <summary>
    ///     The block's mix: <paramref name="outLeft" /> and <paramref name="outRight" /> get the master's output; device
    ///     output pairs are added to <paramref name="outputs" /> directly.
    /// </summary>
    void Mix(
        ModelRoot model,
        float** inputs,
        int inputChannels,
        float** outputs,
        int outputChannels,
        int offset,
        int frames,
        long position,
        long clock,
        bool countIn,
        Span<float> outLeft,
        Span<float> outRight
    ) {
        blockModel = model;
        blockInputs = inputs;
        blockInputChannels = inputChannels;
        blockOffset = offset;
        blockFrames = frames;
        blockPosition = position;
        blockClock = clock;
        blockCountIn = countIn;
        var plan = model.Mix;
        var rolling = transport.Rolling && !countIn;
        blockCapture = rolling ? Volatile.Read(ref capture) : null; // a render captures from its first rolling frame
        FillPositions(position, frames, rolling);
        foreach (var d in plan.UsedDestinations) {
            destLeft[d].AsSpan(0, frames).Clear();
            destRight[d].AsSpan(0, frames).Clear();
        }

        Latencies(plan);
        FollowTempo(model, plan, position);
        foreach (var node in plan.Order) {
            var left = trackLeft.AsSpan(0, frames);
            var right = trackRight.AsSpan(0, frames);
            var stereo = Source(node, left, right);
            Channel(node, left, right, stereo, default, default);
        }

        // The master: everything routed to it (and the preview), its inserts, then its fader.
        var sumLeft = trackLeft.AsSpan(0, frames);
        var sumRight = trackRight.AsSpan(0, frames);
        destLeft[0].AsSpan(0, frames).CopyTo(sumLeft);
        destRight[0].AsSpan(0, frames).CopyTo(sumRight);
        Volatile.Read(ref preview)?.MixInto(sumLeft, sumRight);
        Channel(plan.Master, sumLeft, sumRight, true, outLeft, outRight);
        WritePairs(plan, outputs, outputChannels);
        SnapshotBuses(plan);
    }

    /// <summary>The play-order position of every frame of the block (held while stopped or counting in).</summary>
    void FillPositions(long position, int frames, bool rolling) {
        var span = positions.AsSpan(0, frames);
        if (!rolling) {
            span.Fill(position);
            return;
        }

        var order = transport.PlayOrder;
        var done = 0;
        while (done < frames) {
            var count = (int)Math.Min(frames - done, order.UntilJump(position));
            for (var i = 0; i < count; i++) {
                span[done + i] = position + i;
            }

            done += count;
            position = order.Advance(position, count);
        }
    }

    /// <summary>
    ///     A track's source into <paramref name="left" /> and <paramref name="right" />: its stream, plus an aux's input
    ///     bus, an instrument's render or the device input. Returns whether it is stereo (mono: only the left).
    /// </summary>
    bool Source(MixNode node, Span<float> left, Span<float> right) {
        var track = node.Track!;
        var i = node.TrackIndex;
        var model = blockModel;
        ReadTrack(track, model.Generations[i], model.Lanes[i], left, right);
        if (node.InputBus > 0) {
            if (!track.Stereo) {
                left.CopyTo(right);
            }

            Add(left, destLeft[node.InputBus].AsSpan(0, left.Length));
            Add(right, destRight[node.InputBus].AsSpan(0, right.Length));
            return true;
        }

        if (node.Frozen) {
            // A mono track frozen without inserts has equal channels: it keeps its mono pan law.
            return track.Stereo && (track.SourceStereo || track.Channel.Config.Inserts.Length > 0);
        }

        if (track.IsMidi) {
            RenderMidi(
                track,
                node,
                model.Views[i],
                model.Generations[i],
                blockOffset,
                blockPosition,
                blockClock,
                blockCountIn,
                left,
                right
            );
            return true; // instruments are stereo; an external MIDI track is silent
        }

        InputTrack(
            track,
            blockInputs,
            blockInputChannels,
            blockOffset,
            left,
            right,
            blockPosition,
            blockClock,
            blockCountIn
        );
        return track.Stereo;
    }

    /// <summary>Device output pairs bypass the master.</summary>
    void WritePairs(MixPlan plan, float** outputs, int outputChannels) {
        foreach (var d in plan.UsedDestinations) {
            if (d > MixPlan.Buses) {
                var channel = 2 * (d - MixPlan.Buses - 1);
                AddOutput(outputs, outputChannels, channel, destLeft[d].AsSpan(0, blockFrames));
                AddOutput(outputs, outputChannels, channel + 1, destRight[d].AsSpan(0, blockFrames));
            }
        }
    }

    void AddOutput(float** outputs, int outputChannels, int channel, ReadOnlySpan<float> source) {
        if (channel >= outputChannels) {
            return;
        }

        var destination = outputs[channel] + blockOffset;
        for (var i = 0; i < source.Length; i++) {
            destination[i] += source[i];
        }
    }

    /// <summary>Keeps the buses a sidechain reads one block late.</summary>
    void SnapshotBuses(MixPlan plan) {
        foreach (var bus in plan.SnapshotBuses) {
            if (snapshotLeft[bus] is { } l && snapshotRight[bus] is { } r) {
                destLeft[bus].AsSpan(0, blockFrames).CopyTo(l);
                destRight[bus].AsSpan(0, blockFrames).CopyTo(r);
                snapshotFrames[bus] = blockFrames;
            }
        }
    }

    static void Add(Span<float> destination, ReadOnlySpan<float> source) {
        for (var i = 0; i < destination.Length; i++) {
            destination[i] += source[i];
        }
    }

    // ---- Tempo -------------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Sets every insert's <c>tempo</c> parameter to the project's tempo at <paramref name="position" /> when it or
    ///     the plan changed. The tempo comes from the model's metronome clicks (one per beat): 60 s over the interval
    ///     between the beat at or before the playhead and the next. Without clicks the parameters keep their value.
    /// </summary>
    void FollowTempo(ModelRoot model, MixPlan plan, long position) {
        var tempo = TempoAt(model.Metronome, position);
        if (tempo <= 0 || (tempo == appliedTempo && ReferenceEquals(plan, tempoPlan))) {
            return;
        }

        appliedTempo = tempo;
        tempoPlan = plan;
        foreach (var node in plan.Order) {
            SetTempo(node, tempo);
        }

        SetTempo(plan.Master, tempo);
    }

    static void SetTempo(MixNode node, float tempo) {
        var inserts = node.Config.Inserts;
        for (var k = 0; k < inserts.Length; k++) {
            if (node.TempoParameters[k] >= 0) {
                inserts[k].Slot.Effect?.SetParameter(node.TempoParameters[k], tempo);
            }
        }
    }

    /// <summary>The tempo (bpm) at <paramref name="position" /> from the clicks' spacing; 0 without two clicks.</summary>
    float TempoAt(in Ipc.Segments.TrackView clicks, long position) {
        var count = clicks.EventCount;
        if (count < 2) {
            return 0;
        }

        var at = Math.Clamp(LowerBound(clicks.Events, count, position + 1) - 1, 0, count - 2);
        var interval = clicks.Events[at + 1].SamplePos - clicks.Events[at].SamplePos;
        return interval <= 0 ? 0 : (float)Math.Round(60.0 * SampleRate / interval, 3);
    }

    // ---- Latency, liveness, solo -------------------------------------------------------------------------------------

    /// <summary>
    ///     The block's first pass, in plan order: each channel's insert latency and the latency compensation aligns it
    ///     on, whether it is on an armed or monitored path, and whether it sounds under the solos; each summing point's
    ///     latency is the largest of what reaches it.
    /// </summary>
    void Latencies(MixPlan plan) {
        var mode = (DelayCompensation)Volatile.Read(ref delayCompensation);
        foreach (var d in plan.UsedDestinations) {
            destLatency[d] = 0;
            destLive[d] = false;
            destSolo[d] = false;
        }

        var anySolo = false;
        foreach (var node in plan.Order) {
            anySolo |= node.Channel.Strip.Solo;
        }

        foreach (var node in plan.Order) {
            NodeLatency(node, mode, anySolo);
        }

        MasterLatency(plan, mode);
    }

    void NodeLatency(MixNode node, DelayCompensation mode, bool anySolo) {
        var ch = node.Channel;
        var aux = node.InputBus > 0;
        var input = aux ? destLatency[node.InputBus] : 0;
        var live = aux ? destLive[node.InputBus] : node.Track!.Armed || node.Track.Monitoring;
        var implied = aux && destSolo[node.InputBus];
        ch.Live = live;
        ch.ChainLatency = InsertLatency(node.Config, live);
        ch.Latency = mode switch {
            DelayCompensation.Off => 0,
            DelayCompensation.Tracks when aux => input,
            _ => input + ch.ChainLatency
        };
        ch.ImpliedSolo = implied;
        ch.Audible = !anySolo || ch.Strip.Solo || implied;
        var soloing = anySolo && ch.Audible;
        Reach(node.OutputDestination, ch.Latency, live, soloing);
        foreach (var d in node.SendDestinations) {
            Reach(d, ch.Latency, live, soloing);
        }
    }

    /// <summary>The master's latency and, with full compensation, the delay that lines it up with the output pairs.</summary>
    void MasterLatency(MixPlan plan, DelayCompensation mode) {
        var m = plan.Master.Channel;
        m.Live = destLive[0];
        m.Audible = true;
        m.ChainLatency = InsertLatency(plan.Master.Config, m.Live);
        m.Latency = mode switch {
            DelayCompensation.Off => 0,
            DelayCompensation.Tracks => destLatency[0],
            _ => destLatency[0] + m.ChainLatency
        };
        masterOutputDelay = 0;
        if (mode != DelayCompensation.All) {
            return;
        }

        var output = m.Latency;
        foreach (var d in plan.UsedDestinations) {
            if (d > MixPlan.Buses) {
                output = Math.Max(output, destLatency[d]);
            }
        }

        foreach (var d in plan.UsedDestinations) {
            if (d > MixPlan.Buses) {
                destLatency[d] = output;
            }
        }

        masterOutputDelay = output - m.Latency;
    }

    void Reach(int destination, int latency, bool live, bool soloing) {
        if (destination < 0) {
            return;
        }

        destLatency[destination] = Math.Max(destLatency[destination], latency);
        destLive[destination] |= live;
        destSolo[destination] |= soloing;
    }

    int InsertLatency(ChannelConfig config, bool live) {
        var total = 0;
        foreach (var insert in config.Inserts) {
            if (Active(insert, live) is { } effect) {
                total += Math.Max(0, effect.LatencySamples);
            }
        }

        return total;
    }

    /// <summary>The insert's effect when it processes: not bypassed, loaded, not failed, not cut by low-latency mode.</summary>
    IAudioEffect? Active(ChannelInsert insert, bool live) {
        if (insert.Bypassed || insert.Slot.Effect is not { } effect || insert.Slot.Failed) {
            return null;
        }

        var cut = live && Volatile.Read(ref lowLatency) != 0;
        return cut && effect.LatencySamples > Volatile.Read(ref lowLatencyLimit) ? null : effect;
    }

    // ---- A channel ---------------------------------------------------------------------------------------------------

    /// <summary>
    ///     One channel from its source in <paramref name="left" /> and <paramref name="right" /> (a mono source in
    ///     <paramref name="left" />): inserts, delay compensation, output and sends. The master's output goes to
    ///     <paramref name="outLeft" /> and <paramref name="outRight" />.
    /// </summary>
    void Channel(
        MixNode node,
        Span<float> left,
        Span<float> right,
        bool stereo,
        Span<float> outLeft,
        Span<float> outRight
    ) {
        var ch = node.Channel;
        stereo = Inserts(node, left, right, stereo);
        var source = stereo ? right : left;
        if (blockCapture is { Kind: RenderKind.Freeze } freeze
            && node.Track is { } frozen
            && freeze.Tracks.TryGetValue(frozen.Id, out var target)) {
            target.Write(left, source, ch.ChainLatency);
        }

        var line = ch.Delay;
        line?.Write(left, source);
        var tapped = -1;
        OutputGains(node, stereo);
        if (node.WritePost) {
            WritePost(ch, left, source);
        }

        if (node.Track is null) {
            MasterOutput(ch, line, left, source, ref tapped, outLeft, outRight);
        } else {
            TrackOutput(node, line, left, source, ref tapped);
        }

        var sends = node.Config.Sends;
        for (var s = 0; s < node.SendDestinations.Length; s++) {
            var destination = node.SendDestinations[s];
            if (destination >= 0) {
                Tap(line, Delay(destination, ch.Latency), left, source, ref tapped, out var srcL, out var srcR);
                SendGains(node, sends[s], node.SendTargets[s], stereo);
                Accumulate(
                    srcL,
                    srcR,
                    destLeft[destination].AsSpan(0, blockFrames),
                    destRight[destination].AsSpan(0, blockFrames)
                );
            }
        }
    }

    /// <summary>The post-fader signal another insert takes as a sidechain (undelayed).</summary>
    void WritePost(ChannelState ch, ReadOnlySpan<float> left, ReadOnlySpan<float> right) {
        if (ch.PostLeft is not { } postL || ch.PostRight is not { } postR) {
            return;
        }

        var l = postL.AsSpan(0, blockFrames);
        var r = postR.AsSpan(0, blockFrames);
        l.Clear();
        r.Clear();
        Accumulate(left, right, l, r);
        ch.PostFrames = blockFrames;
    }

    /// <summary>The master's fader into the device's first pair, delayed to line up with the other pairs.</summary>
    void MasterOutput(
        ChannelState ch,
        DelayLine? line,
        Span<float> left,
        Span<float> right,
        ref int tapped,
        Span<float> outLeft,
        Span<float> outRight
    ) {
        Tap(line, masterOutputDelay, left, right, ref tapped, out var srcL, out var srcR);
        outLeft.Clear();
        outRight.Clear();
        var (peakL, peakR) = AccumulateMetered(srcL, srcR, outLeft, outRight);
        ch.Strip.Meter(peakL, peakR);
        blockCapture?.Master?.Write(outLeft, outRight, ch.Latency + masterOutputDelay);
    }

    /// <summary>A track's fader and pan into its destination (and its stem, when one is rendered).</summary>
    void TrackOutput(MixNode node, DelayLine? line, Span<float> left, Span<float> right, ref int tapped) {
        var ch = node.Channel;
        var destination = node.OutputDestination;
        Tap(line, Delay(destination, ch.Latency), left, right, ref tapped, out var srcL, out var srcR);
        var frames = blockFrames;
        RenderTarget? stem = null;
        if (blockCapture is { Kind: RenderKind.Stems } stems) {
            stems.Tracks.TryGetValue(node.TrackId, out stem);
        }

        if (stem is null && destination >= 0) {
            var (l, r) = AccumulateMetered(
                srcL,
                srcR,
                destLeft[destination].AsSpan(0, frames),
                destRight[destination].AsSpan(0, frames)
            );
            ch.Strip.Meter(l, r);
            return;
        }

        // The stem (or a channel routed nowhere, metered all the same).
        var el = edgeLeft.AsSpan(0, frames);
        var er = edgeRight.AsSpan(0, frames);
        el.Clear();
        er.Clear();
        var (peakL, peakR) = AccumulateMetered(srcL, srcR, el, er);
        ch.Strip.Meter(peakL, peakR);
        stem?.Write(el, er, destination >= 0 ? destLatency[destination] : 0);
        if (destination >= 0) {
            Add(destLeft[destination].AsSpan(0, frames), el);
            Add(destRight[destination].AsSpan(0, frames), er);
        }
    }

    /// <summary>The delay an edge into <paramref name="destination" /> needs so it lines up there.</summary>
    int Delay(int destination, int latency) => destination < 0 ? 0 : Math.Max(0, destLatency[destination] - latency);

    /// <summary>The channel's signal <paramref name="delay" /> frames late (read once per distinct delay).</summary>
    void Tap(
        DelayLine? line,
        int delay,
        Span<float> left,
        Span<float> right,
        ref int tapped,
        out ReadOnlySpan<float> outLeft,
        out ReadOnlySpan<float> outRight
    ) {
        outLeft = left;
        outRight = right;
        if (delay == 0) {
            return;
        }

        if (line is null || delay > line.MaxDelay) {
            Volatile.Write(ref latencyOverflow, 1);
            if (line is null) {
                return;
            }
        }

        var l = tapLeft.AsSpan(0, blockFrames);
        var r = tapRight.AsSpan(0, blockFrames);
        if (tapped != delay) {
            line.Read(delay, l, r);
            tapped = delay;
        }

        outLeft = l;
        outRight = r;
    }

    // ---- Gains -------------------------------------------------------------------------------------------------------

    /// <summary>dst += src × the edge's gains.</summary>
    void Accumulate(ReadOnlySpan<float> srcL, ReadOnlySpan<float> srcR, Span<float> dstL, Span<float> dstR) {
        if (gainConstant) {
            float gl = gainLeft, gr = gainRight;
            for (var i = 0; i < dstL.Length; i++) {
                dstL[i] += srcL[i] * gl;
                dstR[i] += srcR[i] * gr;
            }

            return;
        }

        var cl = coefLeft;
        var cr = coefRight;
        for (var i = 0; i < dstL.Length; i++) {
            dstL[i] += srcL[i] * cl[i];
            dstR[i] += srcR[i] * cr[i];
        }
    }

    /// <summary>As <see cref="Accumulate" />, returning the peaks of what it added (the strip's meter).</summary>
    (float Left, float Right) AccumulateMetered(
        ReadOnlySpan<float> srcL,
        ReadOnlySpan<float> srcR,
        Span<float> dstL,
        Span<float> dstR
    ) {
        float peakL = 0, peakR = 0;
        for (var i = 0; i < dstL.Length; i++) {
            var l = srcL[i] * (gainConstant ? gainLeft : coefLeft[i]);
            var r = srcR[i] * (gainConstant ? gainRight : coefRight[i]);
            dstL[i] += l;
            dstR[i] += r;
            peakL = Math.Max(peakL, Math.Abs(l));
            peakR = Math.Max(peakR, Math.Abs(r));
        }

        return (peakL, peakR);
    }

    /// <summary>The output's gains (fader, pan, mute, solo, and their automation).</summary>
    void OutputGains(MixNode node, bool stereo) {
        var ch = node.Channel;
        var strip = ch.Strip;
        var automation = blockModel.Automation;
        var volume = LaneFor(automation, node.VolumeTarget);
        var pan = LaneFor(automation, node.PanTarget);
        var mute = LaneFor(automation, node.MuteTarget);
        if (volume is null && pan is null && mute is null) {
            var g = ch.Audible && !strip.Mute ? strip.Gain : 0f;
            var (l, r) = Strip.PanGains(strip.Pan, stereo);
            Ramp(ref ch.Output, g * l, g * r);
            return;
        }

        Lane(volume, volumeLane, strip.Gain);
        Lane(mute, muteLane, strip.Mute ? 1f : 0f);
        Lane(pan, panLane, strip.Pan);
        Pan(panLane.AsSpan(0, blockFrames), pan is not null, stereo);
        Scale(volumeLane, ch.Audible);
        Remember(ref ch.Output);
    }

    /// <summary>A send's gains: its level (times the fader's for a post-fader send), its pan, the strip's mute.</summary>
    void SendGains(MixNode node, ChannelSend send, int target, bool stereo) {
        var ch = node.Channel;
        var strip = ch.Strip;
        var slot = send.Slot;
        var automation = blockModel.Automation;
        var level = LaneFor(automation, target);
        var volume = send.PreFader ? null : LaneFor(automation, node.VolumeTarget);
        var mute = LaneFor(automation, node.MuteTarget);
        var (pl, pr) = Strip.PanGains(slot.Pan, stereo);
        if (level is null && volume is null && mute is null) {
            var g = !ch.Audible || strip.Mute ? 0f
                : send.PreFader ? slot.Gain
                : slot.Gain * strip.Gain;
            Ramp(ref slot.Edge, g * pl, g * pr);
            return;
        }

        Lane(level, levelLane, slot.Gain);
        Lane(mute, muteLane, strip.Mute ? 1f : 0f);
        if (!send.PreFader) {
            Lane(volume, volumeLane, strip.Gain);
            for (var i = 0; i < blockFrames; i++) {
                levelLane[i] *= volumeLane[i];
            }
        }

        coefLeft.AsSpan(0, blockFrames).Fill(pl);
        coefRight.AsSpan(0, blockFrames).Fill(pr);
        Scale(levelLane, ch.Audible);
        Remember(ref slot.Edge);
    }

    /// <summary>A lane's values for the block into <paramref name="destination" />, or <paramref name="value" /> throughout.</summary>
    void Lane(AutomationLane? lane, float[] destination, float value) {
        var span = destination.AsSpan(0, blockFrames);
        if (lane is null) {
            span.Fill(value);
        } else {
            lane.Fill(positions.AsSpan(0, blockFrames), span);
        }
    }

    /// <summary>Pan gains per frame into the coefficients (computed once when the pan does not move).</summary>
    void Pan(ReadOnlySpan<float> pans, bool automated, bool stereo) {
        var cl = coefLeft.AsSpan(0, pans.Length);
        var cr = coefRight.AsSpan(0, pans.Length);
        if (!automated) {
            var (l, r) = Strip.PanGains(pans[0], stereo);
            cl.Fill(l);
            cr.Fill(r);
            return;
        }

        var last = float.NaN;
        float pl = 1, pr = 1;
        for (var i = 0; i < pans.Length; i++) {
            var x = Math.Clamp(pans[i], -1f, 1f);
            if (x != last) {
                (pl, pr) = Strip.PanGains(x, stereo);
                last = x;
            }

            cl[i] = pl;
            cr[i] = pr;
        }
    }

    /// <summary>Coefficients = gain (0 where muted or not audible) × the pan gains already in them.</summary>
    void Scale(float[] gains, bool audible) {
        for (var i = 0; i < blockFrames; i++) {
            var g = audible && muteLane[i] < 0.5f ? gains[i] : 0f;
            coefLeft[i] = g * coefLeft[i];
            coefRight[i] = g * coefRight[i];
        }

        gainConstant = false;
    }

    void Remember(ref EdgeGain edge) {
        edge.Left = coefLeft[blockFrames - 1];
        edge.Right = coefRight[blockFrames - 1];
        edge.Set = true;
    }

    /// <summary>Constant gains, ramped linearly across the block from the last ones when they changed.</summary>
    void Ramp(ref EdgeGain edge, float targetL, float targetR) {
        var startL = edge.Set ? edge.Left : targetL;
        var startR = edge.Set ? edge.Right : targetR;
        edge.Left = targetL;
        edge.Right = targetR;
        edge.Set = true;
        gainConstant = startL == targetL && startR == targetR;
        gainLeft = targetL;
        gainRight = targetR;
        if (gainConstant) {
            return;
        }

        var frames = blockFrames;
        var stepL = (targetL - startL) / frames;
        var stepR = (targetR - startR) / frames;
        for (var i = 0; i < frames; i++) {
            coefLeft[i] = startL + stepL * (i + 1);
            coefRight[i] = startR + stepR * (i + 1);
        }
    }

    /// <summary>The lane of <paramref name="target" /> when it drives playback now (not held by a control move).</summary>
    AutomationLane? LaneFor(AutomationSet automation, int target) =>
        target >= 0 && !Overrides.IsHeld(target) ? automation.Lane(target) : null;

    // ---- Inserts -----------------------------------------------------------------------------------------------------

    /// <summary>The channel's inserts in order; returns whether the signal is stereo after them.</summary>
    bool Inserts(MixNode node, Span<float> left, Span<float> right, bool stereo) {
        var inserts = node.Config.Inserts;
        var live = node.Channel.Live;
        for (var k = 0; k < inserts.Length; k++) {
            var insert = inserts[k];
            insert.Slot.ApplyParameters();
            if (Active(insert, live) is not { } effect) {
                continue;
            }

            if (!stereo) {
                left.CopyTo(right); // a mono track's inserts are stereo: from here on it is a stereo signal
                stereo = true;
            }

            Sidechain(node.Sidechains[k], out var sideL, out var sideR);
            if (node.InsertBindings[k].Length == 0) {
                effect.Process(left, right, sideL, sideR);
            } else {
                ProcessAutomated(effect, node.InsertBindings[k], node.InsertLast[k], left, right, sideL, sideR);
            }
        }

        return stereo;
    }

    /// <summary>An insert with automated parameters, <see cref="ParameterBlock" /> frames at a time.</summary>
    void ProcessAutomated(
        IAudioEffect effect,
        ParameterBinding[] bindings,
        float[] last,
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sideL,
        ReadOnlySpan<float> sideR
    ) {
        for (var at = 0; at < left.Length; at += ParameterBlock) {
            var count = Math.Min(ParameterBlock, left.Length - at);
            SetAutomated(bindings, last, at, effect, null);
            effect.Process(
                left.Slice(at, count),
                right.Slice(at, count),
                sideL.IsEmpty ? sideL : sideL.Slice(at, count),
                sideR.IsEmpty ? sideR : sideR.Slice(at, count)
            );
        }
    }

    /// <summary>
    ///     Sets each automated parameter of <paramref name="effect" /> or <paramref name="instrument" /> to its lane's
    ///     value at frame <paramref name="at" />, when it changed.
    /// </summary>
    void SetAutomated(
        ParameterBinding[] bindings,
        float[] last,
        int at,
        IAudioEffect? effect,
        IInstrument? instrument
    ) {
        var automation = blockModel.Automation;
        for (var b = 0; b < bindings.Length; b++) {
            if (LaneFor(automation, bindings[b].Target) is not { } lane) {
                continue;
            }

            var value = lane.ValueAt(positions[at]);
            if (value != last[b]) {
                effect?.SetParameter(bindings[b].Parameter, value);
                instrument?.SetParameter(bindings[b].Parameter, value);
                last[b] = value;
            }
        }
    }

    /// <summary>An insert's sidechain for this block (empty spans: none).</summary>
    void Sidechain(SidechainSource source, out ReadOnlySpan<float> left, out ReadOnlySpan<float> right) {
        left = default;
        right = default;
        var frames = blockFrames;
        if (source.Channel is { } ch) {
            if (ch.PostLeft is { } l && ch.PostRight is { } r) {
                Padded(l, r, ch.PostFrames, out left, out right);
            }
        } else if (source.Bus > 0 && !source.Late) {
            left = destLeft[source.Bus].AsSpan(0, frames);
            right = destRight[source.Bus].AsSpan(0, frames);
        } else if (source.Bus > 0 && snapshotLeft[source.Bus] is { } sl && snapshotRight[source.Bus] is { } sr) {
            Padded(sl, sr, snapshotFrames[source.Bus], out left, out right);
        }
    }

    /// <summary>A previous block's buffers as this block's frames, zero-padded when it was shorter.</summary>
    void Padded(float[] l, float[] r, int have, out ReadOnlySpan<float> left, out ReadOnlySpan<float> right) {
        var frames = blockFrames;
        if (have >= frames) {
            left = l.AsSpan(0, frames);
            right = r.AsSpan(0, frames);
            return;
        }

        var pl = sideLeft.AsSpan(0, frames);
        var pr = sideRight.AsSpan(0, frames);
        l.AsSpan(0, have).CopyTo(pl);
        r.AsSpan(0, have).CopyTo(pr);
        pl[have..].Clear();
        pr[have..].Clear();
        left = pl;
        right = pr;
    }

    /// <summary>
    ///     Renders an instrument, at most <see cref="ParameterBlock" /> frames at a time when it has automated
    ///     parameters, setting them at the start of each.
    /// </summary>
    void RenderInstrument(
        MixNode node,
        Midi.InstrumentSlot slot,
        ReadOnlySpan<MidiEvent> events,
        Span<float> left,
        Span<float> right
    ) {
        var bindings = node.InstrumentBindings;
        if (bindings.Length == 0) {
            slot.Instrument.Process(events, left, right);
            return;
        }

        var e = 0;
        for (var at = 0; at < left.Length; at += ParameterBlock) {
            var count = Math.Min(ParameterBlock, left.Length - at);
            SetAutomated(bindings, node.InstrumentLast, at, null, slot.Instrument);
            var n = 0;
            while (e < events.Length && events[e].Frame < at + count) {
                var ev = events[e++];
                chunkEvents[n++] = ev with { Frame = Math.Max(0, ev.Frame - at) };
            }

            slot.Instrument.Process(chunkEvents.AsSpan(0, n), left.Slice(at, count), right.Slice(at, count));
        }
    }

    // ---- Resets ------------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Audio thread (a render's start): silences every insert's and instrument's tails, the delay lines and the
    ///     sidechain history, and restarts the gain ramps, so a render does not depend on what played before.
    /// </summary>
    void ResetMix(ModelRoot model) {
        foreach (var node in model.Mix.Order) {
            ResetNode(node);
            node.Track?.Instrument?.Instrument.Reset();
        }

        ResetNode(model.Mix.Master);
        foreach (var d in model.Mix.UsedDestinations) {
            destLeft[d].AsSpan().Clear();
            destRight[d].AsSpan().Clear();
        }

        Array.Clear(snapshotFrames);
        tempoPlan = null; // the tempo is set again
    }

    static void ResetNode(MixNode node) {
        var ch = node.Channel;
        foreach (var insert in node.Config.Inserts) {
            insert.Slot.ApplyParameters();
            insert.Slot.Effect?.Reset();
        }

        foreach (var send in node.Config.Sends) {
            send.Slot.Edge = default;
        }

        ch.Delay?.Clear();
        ch.PostFrames = 0;
        ch.Output = default;
        foreach (var last in node.InsertLast) {
            last.AsSpan().Fill(float.NaN);
        }

        node.InstrumentLast.AsSpan().Fill(float.NaN);
    }

    /// <summary>Play and locate: every automation target follows its lane again.</summary>
    void ReleaseOverrides() => Overrides.Release();
}
