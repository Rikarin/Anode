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
// another track's post-fader signal or a bus), the delay compensation line, and its outputs and sends into their
// summing points at per-sample automated gains. The master then runs its inserts and fader into the device; device
// output pairs bypass it. Everything here is audio-thread code: no allocation, no locks.
//
// Sidechains: a source processed later in the same cycle (a track after the insert's own, or a bus fed by one) is
// taken from the previous cycle, one block late; with plugin delay compensation a sidechain is not delayed to match.
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
    readonly float[] masterLeft = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] masterRight = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] edgeLeft = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] edgeRight = GC.AllocateArray<float>(MaxFrames, true);
    readonly Dsp.Instruments.MidiEvent[] chunkEvents = new Dsp.Instruments.MidiEvent[MidiTrackState.MaxEvents];

    int delayCompensation;
    int lowLatency;
    int lowLatencyLimit;
    int latencyOverflow;
    int masterOutputDelay;
    RenderCapture? capture;
    RenderCapture? blockCapture;
    IAudioCycleObserver? cycleObserver;

    /// <summary>The master's channel: its fader is <see cref="Master" />.</summary>
    public ChannelState MasterChannel { get; }

    /// <summary>Which automation targets a control move holds (control thread sets, audio thread clears).</summary>
    public AutomationOverrides Overrides { get; } = new(0);

    /// <summary>Control thread: delay compensation, and low-latency mode with its limit in frames.</summary>
    public void SetMixer(DelayCompensation compensation, bool lowLatencyMode, int limitFrames) {
        Volatile.Write(ref delayCompensation, (int)compensation);
        Volatile.Write(ref lowLatencyLimit, Math.Max(0, limitFrames));
        Volatile.Write(ref lowLatency, lowLatencyMode ? 1 : 0);
    }

    public DelayCompensation Compensation => (DelayCompensation)Volatile.Read(ref delayCompensation);

    /// <summary>
    ///     Set by the audio thread when a path needed more delay than its channel's line holds (a latency grew); the
    ///     control thread then grows the lines (<see cref="EngineController" />).
    /// </summary>
    public bool TakeLatencyOverflow() => Interlocked.Exchange(ref latencyOverflow, 0) != 0;

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
        var plan = model.Mix;
        var rolling = transport.Rolling && !countIn;
        blockCapture = rolling ? Volatile.Read(ref capture) : null; // a render captures from its first rolling frame
        FillPositions(position, frames, rolling);
        foreach (var d in plan.UsedDestinations) {
            destLeft[d].AsSpan(0, frames).Clear();
            destRight[d].AsSpan(0, frames).Clear();
        }

        Latencies(plan);
        var order = plan.Order;
        for (var k = 0; k < order.Length; k++) {
            var node = order[k];
            var track = node.Track!;
            var i = node.TrackIndex;
            var left = trackLeft.AsSpan(0, frames);
            var right = trackRight.AsSpan(0, frames);
            ReadTrack(track, model.Generations[i], model.Lanes[i], left, right);
            bool stereo;
            if (node.InputBus > 0) {
                if (!track.Stereo) {
                    left.CopyTo(right);
                }

                Add(left, destLeft[node.InputBus].AsSpan(0, frames));
                Add(right, destRight[node.InputBus].AsSpan(0, frames));
                stereo = true;
            } else if (node.Frozen) {
                stereo = track.Stereo && (track.SourceStereo || track.Channel.Config.Inserts.Length > 0);
            } else if (track.IsMidi) {
                RenderMidi(
                    track,
                    node,
                    model.Automation,
                    model.Views[i],
                    model.Generations[i],
                    offset,
                    position,
                    clock,
                    countIn,
                    left,
                    right
                );
                stereo = true; // instruments are stereo; an external MIDI track is silent
            } else {
                InputTrack(track, inputs, inputChannels, offset, left, right, position, clock, countIn);
                stereo = track.Stereo;
            }

            Channel(model, node, left, right, stereo, frames);
        }

        // The master: everything routed to it (and the preview), its inserts, then its fader.
        var master = plan.Master;
        var sumLeft = trackLeft.AsSpan(0, frames);
        var sumRight = trackRight.AsSpan(0, frames);
        destLeft[0].AsSpan(0, frames).CopyTo(sumLeft);
        destRight[0].AsSpan(0, frames).CopyTo(sumRight);
        Volatile.Read(ref preview)?.MixInto(sumLeft, sumRight);
        Channel(model, master, sumLeft, sumRight, true, frames, outLeft, outRight);

        // Device output pairs bypass the master.
        foreach (var d in plan.UsedDestinations) {
            if (d <= MixPlan.Buses) {
                continue;
            }

            var channel = 2 * (d - MixPlan.Buses - 1);
            AddOutput(outputs, outputChannels, channel, offset, destLeft[d].AsSpan(0, frames));
            AddOutput(outputs, outputChannels, channel + 1, offset, destRight[d].AsSpan(0, frames));
        }

        foreach (var bus in plan.SnapshotBuses) {
            if (snapshotLeft[bus] is { } l && snapshotRight[bus] is { } r) {
                destLeft[bus].AsSpan(0, frames).CopyTo(l);
                destRight[bus].AsSpan(0, frames).CopyTo(r);
                snapshotFrames[bus] = frames;
            }
        }
    }

    static void AddOutput(float** outputs, int outputChannels, int channel, int offset, ReadOnlySpan<float> source) {
        if (channel >= outputChannels) {
            return;
        }

        var destination = outputs[channel] + offset;
        for (var i = 0; i < source.Length; i++) {
            destination[i] += source[i];
        }
    }

    static void Add(Span<float> destination, ReadOnlySpan<float> source) {
        for (var i = 0; i < destination.Length; i++) {
            destination[i] += source[i];
        }
    }

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
            var ch = node.Channel;
            var track = node.Track!;
            int input;
            bool live, implied;
            if (node.InputBus > 0) {
                input = destLatency[node.InputBus];
                live = destLive[node.InputBus];
                implied = destSolo[node.InputBus];
            } else {
                input = 0;
                live = track.Armed || track.Monitoring;
                implied = false;
            }

            ch.Live = live;
            ch.ChainLatency = InsertLatency(node.Config, live);
            ch.Latency = mode switch {
                DelayCompensation.Off => 0,
                DelayCompensation.Tracks when node.InputBus > 0 => input,
                _ => input + ch.ChainLatency
            };
            var solo = ch.Strip.Solo;
            ch.ImpliedSolo = implied;
            ch.Audible = !anySolo || solo || implied;
            var soloing = anySolo && ch.Audible;
            Reach(node.OutputDestination, ch.Latency, live, soloing);
            foreach (var d in node.SendDestinations) {
                Reach(d, ch.Latency, live, soloing);
            }
        }

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

        // Every output lines up: the master's and the device pairs'.
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
    Dsp.Effects.IAudioEffect? Active(ChannelInsert insert, bool live) {
        if (insert.Bypassed || insert.Slot.Effect is not { } effect || insert.Slot.Failed) {
            return null;
        }

        if (live && Volatile.Read(ref lowLatency) != 0 && effect.LatencySamples > Volatile.Read(ref lowLatencyLimit)) {
            return null;
        }

        return effect;
    }

    /// <summary>
    ///     One channel from its source in <paramref name="left" /> and <paramref name="right" /> (a mono source in
    ///     <paramref name="left" />): inserts, delay compensation, outputs and sends; the master's output goes to
    ///     <paramref name="outLeft" /> and <paramref name="outRight" />.
    /// </summary>
    void Channel(
        ModelRoot model,
        MixNode node,
        Span<float> left,
        Span<float> right,
        bool stereo,
        int frames,
        Span<float> outLeft = default,
        Span<float> outRight = default
    ) {
        var ch = node.Channel;
        stereo = Inserts(model, node, left, right, stereo, frames);
        var source = stereo ? right : left;
        var capturing = blockCapture;
        if (capturing is { Kind: RenderKind.Freeze } && node.Track is { } frozen
            && capturing.Tracks.TryGetValue(frozen.Id, out var freezeTarget)) {
            freezeTarget.Write(left, source, ch.ChainLatency);
        }

        var line = ch.Delay;
        line?.Write(left, source);
        var tapped = -1;
        var automation = model.Automation;

        // The output (the fader and pan).
        var isMaster = node.Track is null;
        var outputDestination = isMaster ? -1 : node.OutputDestination;
        var outputDelay = isMaster ? masterOutputDelay : Delay(outputDestination, ch.Latency);
        Tap(line, outputDelay, left, source, ref tapped, frames, out var srcL, out var srcR);
        OutputCoefficients(node, automation, stereo, frames);
        var coefL = coefLeft.AsSpan(0, frames);
        var coefR = coefRight.AsSpan(0, frames);
        if (node.WritePost && ch.PostLeft is { } postL && ch.PostRight is { } postR) {
            for (var i = 0; i < frames; i++) {
                postL[i] = left[i] * coefL[i];
                postR[i] = source[i] * coefR[i];
            }

            ch.PostFrames = frames;
        }

        var stem = capturing is { Kind: RenderKind.Stems } && node.Track is { } stemTrack
            && capturing.Tracks.TryGetValue(stemTrack.Id, out var stemTarget)
                ? stemTarget
                : null;
        float peakL = 0, peakR = 0;
        if (isMaster) {
            for (var i = 0; i < frames; i++) {
                var l = srcL[i] * coefL[i];
                var r = srcR[i] * coefR[i];
                outLeft[i] = l;
                outRight[i] = r;
                peakL = Math.Max(peakL, Math.Abs(l));
                peakR = Math.Max(peakR, Math.Abs(r));
            }

            capturing?.Master?.Write(outLeft[..frames], outRight[..frames], ch.Latency + masterOutputDelay);
        } else {
            var hasDestination = outputDestination >= 0;
            var dl = hasDestination ? destLeft[outputDestination] : edgeLeft;
            var dr = hasDestination ? destRight[outputDestination] : edgeRight;
            if (stem is not null) {
                var el = edgeLeft.AsSpan(0, frames);
                var er = edgeRight.AsSpan(0, frames);
                for (var i = 0; i < frames; i++) {
                    var l = srcL[i] * coefL[i];
                    var r = srcR[i] * coefR[i];
                    el[i] = l;
                    er[i] = r;
                    peakL = Math.Max(peakL, Math.Abs(l));
                    peakR = Math.Max(peakR, Math.Abs(r));
                }

                stem.Write(el, er, hasDestination ? destLatency[outputDestination] : 0);
                if (hasDestination) {
                    Add(dl.AsSpan(0, frames), el);
                    Add(dr.AsSpan(0, frames), er);
                }
            } else if (hasDestination) {
                for (var i = 0; i < frames; i++) {
                    var l = srcL[i] * coefL[i];
                    var r = srcR[i] * coefR[i];
                    dl[i] += l;
                    dr[i] += r;
                    peakL = Math.Max(peakL, Math.Abs(l));
                    peakR = Math.Max(peakR, Math.Abs(r));
                }
            } else {
                for (var i = 0; i < frames; i++) {
                    peakL = Math.Max(peakL, Math.Abs(srcL[i] * coefL[i]));
                    peakR = Math.Max(peakR, Math.Abs(srcR[i] * coefR[i]));
                }
            }
        }

        ch.Strip.Meter(peakL, peakR);

        // Sends.
        var sends = node.Config.Sends;
        for (var s = 0; s < node.SendDestinations.Length; s++) {
            var destination = node.SendDestinations[s];
            if (destination < 0) {
                continue;
            }

            var send = sends[s];
            Tap(line, Delay(destination, ch.Latency), left, source, ref tapped, frames, out srcL, out srcR);
            SendCoefficients(node, send, node.SendTargets[s], automation, stereo, frames);
            var dl = destLeft[destination];
            var dr = destRight[destination];
            for (var i = 0; i < frames; i++) {
                dl[i] += srcL[i] * coefL[i];
                dr[i] += srcR[i] * coefR[i];
            }
        }
    }

    /// <summary>The delay an edge into <paramref name="destination" /> needs so it lines up there.</summary>
    int Delay(int destination, int latency) =>
        destination < 0 ? 0 : Math.Max(0, destLatency[destination] - latency);

    /// <summary>The channel's signal <paramref name="delay" /> frames late (read once per distinct delay).</summary>
    void Tap(
        DelayLine? line,
        int delay,
        Span<float> left,
        Span<float> right,
        ref int tapped,
        int frames,
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

        var l = tapLeft.AsSpan(0, frames);
        var r = tapRight.AsSpan(0, frames);
        if (tapped != delay) {
            line.Read(delay, l, r);
            tapped = delay;
        }

        outLeft = l;
        outRight = r;
    }

    /// <summary>The output's gains per frame into <see cref="coefLeft" /> and <see cref="coefRight" />.</summary>
    void OutputCoefficients(MixNode node, AutomationSet automation, bool stereo, int frames) {
        var ch = node.Channel;
        var strip = ch.Strip;
        var volume = LaneFor(automation, node.VolumeTarget);
        var pan = LaneFor(automation, node.PanTarget);
        var mute = LaneFor(automation, node.MuteTarget);
        var coefL = coefLeft.AsSpan(0, frames);
        var coefR = coefRight.AsSpan(0, frames);
        if (volume is null && pan is null && mute is null) {
            float targetL = 0, targetR = 0;
            if (ch.Audible && !strip.Mute) {
                var g = strip.Gain;
                var (l, r) = Strip.PanGains(strip.Pan, stereo);
                targetL = g * l;
                targetR = g * r;
            }

            Ramp(ref ch.Output, targetL, targetR, coefL, coefR);
            return;
        }

        var p = positions.AsSpan(0, frames);
        var volumes = volumeLane.AsSpan(0, frames);
        var pans = panLane.AsSpan(0, frames);
        var mutes = muteLane.AsSpan(0, frames);
        if (volume is not null) {
            volume.Fill(p, volumes);
        } else {
            volumes.Fill(strip.Gain);
        }

        if (mute is not null) {
            mute.Fill(p, mutes);
        } else {
            mutes.Fill(strip.Mute ? 1f : 0f);
        }

        var audible = ch.Audible;
        if (pan is null) {
            var (l, r) = Strip.PanGains(strip.Pan, stereo);
            for (var i = 0; i < frames; i++) {
                var g = audible && mutes[i] < 0.5f ? volumes[i] : 0f;
                coefL[i] = g * l;
                coefR[i] = g * r;
            }
        } else {
            pan.Fill(p, pans);
            var last = float.NaN;
            float l = 1, r = 1;
            for (var i = 0; i < frames; i++) {
                var x = Math.Clamp(pans[i], -1f, 1f);
                if (x != last) {
                    (l, r) = Strip.PanGains(x, stereo);
                    last = x;
                }

                var g = audible && mutes[i] < 0.5f ? volumes[i] : 0f;
                coefL[i] = g * l;
                coefR[i] = g * r;
            }
        }

        ch.Output.Left = coefL[frames - 1];
        ch.Output.Right = coefR[frames - 1];
        ch.Output.Set = true;
    }

    /// <summary>A send's gains per frame: its level (times the fader's for a post-fader send) and its pan.</summary>
    void SendCoefficients(
        MixNode node,
        ChannelSend send,
        int target,
        AutomationSet automation,
        bool stereo,
        int frames
    ) {
        var ch = node.Channel;
        var strip = ch.Strip;
        var slot = send.Slot;
        var level = LaneFor(automation, target);
        var volume = send.PreFader ? null : LaneFor(automation, node.VolumeTarget);
        var mute = LaneFor(automation, node.MuteTarget);
        var (l, r) = Strip.PanGains(slot.Pan, stereo);
        var coefL = coefLeft.AsSpan(0, frames);
        var coefR = coefRight.AsSpan(0, frames);
        if (level is null && volume is null && mute is null) {
            float targetL = 0, targetR = 0;
            if (ch.Audible && !strip.Mute) {
                var g = send.PreFader ? slot.Gain : slot.Gain * strip.Gain;
                targetL = g * l;
                targetR = g * r;
            }

            Ramp(ref slot.Edge, targetL, targetR, coefL, coefR);
            return;
        }

        var p = positions.AsSpan(0, frames);
        var levels = levelLane.AsSpan(0, frames);
        var volumes = volumeLane.AsSpan(0, frames);
        var mutes = muteLane.AsSpan(0, frames);
        if (level is not null) {
            level.Fill(p, levels);
        } else {
            levels.Fill(slot.Gain);
        }

        if (send.PreFader) {
            volumes.Fill(1f);
        } else if (volume is not null) {
            volume.Fill(p, volumes);
        } else {
            volumes.Fill(strip.Gain);
        }

        if (mute is not null) {
            mute.Fill(p, mutes);
        } else {
            mutes.Fill(strip.Mute ? 1f : 0f);
        }

        var audible = ch.Audible;
        for (var i = 0; i < frames; i++) {
            var g = audible && mutes[i] < 0.5f ? (send.PreFader ? levels[i] : levels[i] * volumes[i]) : 0f;
            coefL[i] = g * l;
            coefR[i] = g * r;
        }

        slot.Edge.Left = coefL[frames - 1];
        slot.Edge.Right = coefR[frames - 1];
        slot.Edge.Set = true;
    }

    /// <summary>Constant gains, ramped linearly across the block from the last ones when they changed.</summary>
    static void Ramp(ref EdgeGain edge, float targetL, float targetR, Span<float> coefL, Span<float> coefR) {
        var frames = coefL.Length;
        var startL = edge.Set ? edge.Left : targetL;
        var startR = edge.Set ? edge.Right : targetR;
        if (startL == targetL && startR == targetR) {
            coefL.Fill(targetL);
            coefR.Fill(targetR);
        } else {
            var stepL = (targetL - startL) / frames;
            var stepR = (targetR - startR) / frames;
            for (var i = 0; i < frames; i++) {
                coefL[i] = startL + stepL * (i + 1);
                coefR[i] = startR + stepR * (i + 1);
            }
        }

        edge.Left = targetL;
        edge.Right = targetR;
        edge.Set = true;
    }

    /// <summary>The lane of <paramref name="target" /> when it drives playback now (not held by a control move).</summary>
    AutomationLane? LaneFor(AutomationSet automation, int target) =>
        target >= 0 && !Overrides.IsHeld(target) ? automation.Lane(target) : null;

    /// <summary>The channel's inserts in order; returns whether the signal is stereo after them.</summary>
    bool Inserts(ModelRoot model, MixNode node, Span<float> left, Span<float> right, bool stereo, int frames) {
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

            Sidechain(node.Sidechains[k], frames, out var sideL, out var sideR);
            var bindings = node.InsertBindings[k];
            if (bindings.Length == 0) {
                effect.Process(left, right, sideL, sideR);
                continue;
            }

            var last = node.InsertLast[k];
            for (var at = 0; at < frames; at += ParameterBlock) {
                var count = Math.Min(ParameterBlock, frames - at);
                for (var b = 0; b < bindings.Length; b++) {
                    if (LaneFor(model.Automation, bindings[b].Target) is { } lane) {
                        var value = lane.ValueAt(positions[at]);
                        if (value != last[b]) {
                            effect.SetParameter(bindings[b].Parameter, value);
                            last[b] = value;
                        }
                    }
                }

                effect.Process(
                    left.Slice(at, count),
                    right.Slice(at, count),
                    sideL.IsEmpty ? sideL : sideL.Slice(at, count),
                    sideR.IsEmpty ? sideR : sideR.Slice(at, count)
                );
            }
        }

        return stereo;
    }

    /// <summary>An insert's sidechain for this block (empty spans: none).</summary>
    void Sidechain(SidechainSource source, int frames, out ReadOnlySpan<float> left, out ReadOnlySpan<float> right) {
        left = default;
        right = default;
        if (source.Channel is { } ch) {
            if (ch.PostLeft is { } l && ch.PostRight is { } r) {
                Padded(l, r, ch.PostFrames, frames, out left, out right);
            }

            return;
        }

        if (source.Bus <= 0) {
            return;
        }

        if (!source.Late) {
            left = destLeft[source.Bus].AsSpan(0, frames);
            right = destRight[source.Bus].AsSpan(0, frames);
        } else if (snapshotLeft[source.Bus] is { } sl && snapshotRight[source.Bus] is { } sr) {
            Padded(sl, sr, snapshotFrames[source.Bus], frames, out left, out right);
        }
    }

    /// <summary>A previous block's buffers as <paramref name="frames" /> frames, zero-padded when it was shorter.</summary>
    void Padded(
        float[] l,
        float[] r,
        int have,
        int frames,
        out ReadOnlySpan<float> left,
        out ReadOnlySpan<float> right
    ) {
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
        AutomationSet automation,
        Midi.InstrumentSlot slot,
        ReadOnlySpan<Dsp.Instruments.MidiEvent> events,
        Span<float> left,
        Span<float> right
    ) {
        var bindings = node.InstrumentBindings;
        if (bindings.Length == 0) {
            slot.Instrument.Process(events, left, right);
            return;
        }

        var frames = left.Length;
        var last = node.InstrumentLast;
        var e = 0;
        for (var at = 0; at < frames; at += ParameterBlock) {
            var count = Math.Min(ParameterBlock, frames - at);
            for (var b = 0; b < bindings.Length; b++) {
                if (LaneFor(automation, bindings[b].Target) is { } lane) {
                    var value = lane.ValueAt(positions[at]);
                    if (value != last[b]) {
                        slot.Instrument.SetParameter(bindings[b].Parameter, value);
                        last[b] = value;
                    }
                }
            }

            var n = 0;
            while (e < events.Length && events[e].Frame < at + count) {
                var ev = events[e++];
                chunkEvents[n++] = ev with { Frame = Math.Max(0, ev.Frame - at) };
            }

            slot.Instrument.Process(chunkEvents.AsSpan(0, n), left.Slice(at, count), right.Slice(at, count));
        }
    }

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

        for (var b = 0; b < snapshotLeft.Length; b++) {
            snapshotFrames[b] = 0;
        }
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
