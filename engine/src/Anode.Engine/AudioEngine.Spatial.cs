using Anode.Dsp.Effects;
using Anode.Dsp.Spatial;
using Anode.Engine.Mixing;
using Anode.Engine.Rendering;
using Anode.Engine.Spatial;
using Anode.Ipc;

namespace Anode.Engine;

// The multichannel graph (docs/11-phase-10.md, workstream A). A channel whose signal has more than two channels (an aux
// on a surround bus, a surround master), or whose output or a send reaches a summing point that is not stereo, runs
// here; every other channel runs the stereo path of AudioEngine.Mix.cs unchanged, so a stereo project renders bit for
// bit as before and costs what it did.
//
// Buffers: a summing point keeps its first two channels where the stereo path does (destLeft, destRight) and the rest
// in destExtra (allocated by the control thread for points wider than stereo), in its layout's (ADM) order; a channel's
// signal is trackLeft, trackRight and trackExtra (its work buffers when prefetched). Each edge into a point that is not
// stereo renders its contribution into a planar block (channel c at [c × frames, (c + 1) × frames)), which is added to
// the point, metered and captured for stems:
// - a mono or stereo signal into a surround point goes through the strip's Surround Panner (sends through their own,
//   with the strip's surround settings: Send.pan is the stereo buses'), in 32-frame pieces while panner parameters are
//   automated (each piece's settings the lanes' value at its last frame, which the panner reaches over the piece);
// - a signal of the point's format passes channel to channel (a bed); one of another surround format through a gain
//   matrix (workstream D's down-mix to a smaller layout, else each channel to its label's speaker or the nearest);
// - into a stereo point, a surround signal is down-mixed and then balanced as a stereo one; into a mono point, the mean
//   of a stereo signal, the down-mix of a surround one;
// - a strip in object mode whose output is a surround master is an object: its post-fader signal (delay-compensated as
//   any edge into the master) and position go to the master's object renderer, after the bed sum and before the
//   master's inserts.
// Fader, mute, solo and their automation apply as gains to every channel; a surround output has no stereo pan.
public sealed unsafe partial class AudioEngine {
    const int ExtraChannels = Formats.MaxChannels - 2;
    const int Planar = Formats.MaxChannels * MaxFrames;

    readonly float[][] trackExtra = Buffers(ExtraChannels);
    readonly float[][] tapExtra = Buffers(ExtraChannels);
    readonly float[]?[][] destExtra = new float[]?[MixPlan.Destinations][];
    readonly float[] planar = GC.AllocateArray<float>(Planar, true);
    readonly float[] pieceIn = GC.AllocateArray<float>(Planar, true);
    readonly float[] pieceOut = GC.AllocateArray<float>(Planar, true);
    readonly float[] masterPlanar = GC.AllocateArray<float>(Planar, true);
    readonly float[] sentPlanar = GC.AllocateArray<float>(Planar, true);
    readonly float[] tapPlanar = GC.AllocateArray<float>(2 * MaxFrames, true);
    readonly float[] faderLane = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] pairScratch = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] peaks = new float[Formats.MaxChannels];
    readonly float[]?[] objectBuffers = new float[]?[ObjectRenderer.MaxObjects];
    readonly int[] objectWidths = new int[ObjectRenderer.MaxObjects];
    readonly int[] objectOwners = [..Enumerable.Repeat(int.MinValue, ObjectRenderer.MaxObjects)];
    readonly bool[] objectActive = new bool[ObjectRenderer.MaxObjects];

    // The channel being mixed on the multichannel path: its signal's first two channels (the rest in trackExtra).
    float[] signalLeft = [];
    float[] signalRight = [];
    int tappedExtra;
    bool faderConstant;
    float faderValue;
    MonitorSetup? monitor;
    MasterTapWriter? masterTap;
    int masterTapOn;
    bool tapFresh = true;
    long tapNext = -1;

    /// <summary>Control thread: the spatial DSP plans are built with (default: workstream D's).</summary>
    public SpatialContext Spatial { get; set; } = SpatialContext.Default;

    /// <summary>The monitoring the device stage applies (null: the master as it is). Control thread sets it.</summary>
    public MonitorSetup? Monitoring {
        get => Volatile.Read(ref monitor);
        set => Volatile.Write(ref monitor, value);
    }

    /// <summary>The listen-in ring (null: none). Control thread sets it before the tap is turned on.</summary>
    public MasterTapWriter? MasterTap {
        get => Volatile.Read(ref masterTap);
        set => Volatile.Write(ref masterTap, value);
    }

    /// <summary>Control thread: whether the callback writes the master tap (<c>setMasterTap</c>).</summary>
    public void SetMasterTap(bool enabled) => Volatile.Write(ref masterTapOn, enabled ? 1 : 0);

    public bool MasterTapEnabled => Volatile.Read(ref masterTapOn) != 0;

    /// <summary>Control thread: gives summing point <paramref name="destination" /> room for <paramref name="width" /> channels.</summary>
    public void EnsureDestination(int destination, int width) {
        var have = destExtra[destination];
        if (width - 2 > (have?.Length ?? 0)) {
            Volatile.Write(ref destExtra[destination], Buffers(width - 2));
        }
    }

    /// <summary>Control thread: buffers for <paramref name="count" /> objects (before a plan with them is published).</summary>
    public void EnsureObjects(int count) {
        for (var k = 0; k < Math.Min(count, objectBuffers.Length); k++) {
            if (objectBuffers[k] is null) {
                Volatile.Write(ref objectBuffers[k], GC.AllocateArray<float>(2 * MaxFrames, true));
            }
        }
    }

    /// <summary>Clears the channels after the first two of every summing point wider than stereo.</summary>
    void ClearExtras(MixPlan plan, int frames) {
        foreach (var d in plan.UsedDestinations) {
            if (plan.DestinationWidths[d] > 2 && destExtra[d] is { } extra) {
                foreach (var buffer in extra) {
                    buffer?.AsSpan(0, frames).Clear();
                }
            }
        }
    }

    // ---- Channels ----------------------------------------------------------------------------------------------------

    /// <summary>
    ///     A multichannel channel's source: an aux on a bus that is not stereo reads every channel of it (its own
    ///     regions, if any, into the first two); anything else as on the stereo path. Returns the signal's channels.
    /// </summary>
    int SourceMulti(MixNode node, Span<float> left, Span<float> right) {
        if (node.InputBus <= 0 || blockModel.Mix.DestinationWidths[node.InputBus] == 2) {
            return Source(node, left, right) ? 2 : 1;
        }

        var track = node.Track!;
        var i = node.TrackIndex;
        ReadTrack(track, blockModel.Generations[i], blockModel.Lanes[i], left, right);
        var bus = node.InputBus;
        var width = blockModel.Mix.DestinationWidths[bus];
        if (width > 1 && !track.Stereo) {
            left.CopyTo(right); // a mono stream feeds both front channels
        }

        Add(left, destLeft[bus].AsSpan(0, left.Length));
        if (width == 1) {
            return 1;
        }

        Add(right, destRight[bus].AsSpan(0, right.Length));
        var extra = destExtra[bus];
        for (var c = 2; c < width; c++) {
            if (extra is not null && c - 2 < extra.Length && extra[c - 2] is { } source) {
                source.AsSpan(0, left.Length).CopyTo(trackExtra[c - 2]);
            } else {
                trackExtra[c - 2].AsSpan(0, left.Length).Clear();
            }
        }

        return width;
    }

    /// <summary>
    ///     A channel on the multichannel path, from its source in <paramref name="left" />, <paramref name="right" /> and
    ///     the extra channels: inserts, delay compensation, then its output and sends by their edges.
    /// </summary>
    void ChannelMulti(MixNode node, float[] left, float[] right, int width, int firstInsert = 0) {
        var ch = node.Channel;
        var frames = blockFrames;
        signalLeft = left;
        signalRight = right;
        var l = left.AsSpan(0, frames);
        var r = right.AsSpan(0, frames);
        bool stereo;
        if (width <= 2) {
            stereo = Inserts(node, l, r, width == 2, firstInsert);
            width = stereo ? 2 : 1;
        } else {
            stereo = true;
            InsertsMulti(node, width, firstInsert);
        }

        var source = stereo ? r : l;
        if (width <= 2) {
            CaptureFreeze(node, l, source, stereo);
        }

        var line = ch.Delay;
        if (line is not null) {
            for (var c = 2; c < width && c - 2 < line.ExtraChannels; c++) {
                line.WriteExtra(c - 2, trackExtra[c - 2].AsSpan(0, frames));
            }

            line.Write(l, source);
        }

        var tapped = -1;
        tappedExtra = -1;
        var edge = node.OutputEdge;
        if (edge is null or { Kind: EdgeKind.Stereo }) {
            if (width <= 2) {
                OutputGains(node, stereo);
                if (node.WritePost) {
                    WritePost(ch, l, source);
                }

                TrackOutput(node, line, l, source, ref tapped);
            } else {
                // A surround aux routed nowhere: metered all the same.
                FaderGains(node, ref ch.Fader);
                MeterPlanar(ch, Gained(line, 0, width, ref tapped), width);
            }
        } else {
            FaderGains(node, ref ch.Fader);
            if (node.WritePost) {
                PostMulti(ch, width);
            }

            OutputEdge(node, edge, line, width, ref tapped);
        }

        var sends = node.Config.Sends;
        for (var s = 0; s < node.SendDestinations.Length; s++) {
            var destination = node.SendDestinations[s];
            if (destination < 0 || node.SendEdges[s] is not { } send) {
                continue;
            }

            var delay = Delay(destination, ch.Latency);
            if (send.Kind == EdgeKind.Stereo) {
                Tap(line, delay, l, source, ref tapped, out var srcL, out var srcR);
                SendGains(node, sends[s], node.SendTargets[s], stereo);
                Accumulate(
                    srcL,
                    srcR,
                    destLeft[destination].AsSpan(0, frames),
                    destRight[destination].AsSpan(0, frames)
                );
                continue;
            }

            if (send.Kind == EdgeKind.DownmixStereo) {
                Downmix(send, line, delay, width, ref tapped);
                SendGains(node, sends[s], node.SendTargets[s], true);
                Accumulate(
                    pieceOut.AsSpan(0, frames),
                    pieceOut.AsSpan(frames, frames),
                    destLeft[destination].AsSpan(0, frames),
                    destRight[destination].AsSpan(0, frames)
                );
                continue;
            }

            SendLevelGains(node, sends[s], node.SendTargets[s]);
            var count = Render(node, send, line, delay, width, ref tapped);
            AddToDestination(destination, send.Width, count);
        }
    }

    /// <summary>The output by its edge (not <see cref="EdgeKind.Stereo" />): into its summing point, its stem and its meter.</summary>
    void OutputEdge(MixNode node, MixEdge edge, DelayLine? line, int width, ref int tapped) {
        var ch = node.Channel;
        var destination = node.OutputDestination;
        var delay = Delay(destination, ch.Latency);
        var frames = blockFrames;
        RenderTarget? stem = null;
        if (blockCapture is { Kind: RenderKind.Stems } stems) {
            stems.Tracks.TryGetValue(node.TrackId, out stem);
        }

        if (edge.Kind == EdgeKind.DownmixStereo) {
            Downmix(edge, line, delay, width, ref tapped);
            OutputGains(node, true);
            var outL = edgeLeft.AsSpan(0, frames);
            var outR = edgeRight.AsSpan(0, frames);
            var (peakL, peakR) = AccumulateTo(pieceOut.AsSpan(0, frames), pieceOut.AsSpan(frames, frames), outL, outR);
            ch.Strip.Meter(peakL, peakR);
            stem?.Write(outL, outR, destLatency[destination]);
            Add(destLeft[destination].AsSpan(0, frames), outL);
            Add(destRight[destination].AsSpan(0, frames), outR);
            return;
        }

        var count = Render(node, edge, line, delay, width, ref tapped);
        MeterPlanar(ch, planar.AsSpan(0, count * frames), count);
        stem?.WritePlanar(planar.AsSpan(0, count * frames), count, frames, destLatency[destination]);
        if (edge.Kind == EdgeKind.Object) {
            var k = edge.ObjectIndex;
            if (objectBuffers[k] is { } buffer) {
                planar.AsSpan(0, count * frames).CopyTo(buffer);
                objectWidths[k] = count;
                objectActive[k] = true;
            }

            return;
        }

        AddToDestination(destination, edge.Width, count);
    }

    /// <summary>
    ///     An edge's contribution, with the gains of <see cref="faderLane" /> (or <see cref="faderValue" />), into
    ///     <see cref="planar" />; returns its channels (the point's, or an object's one or two).
    /// </summary>
    int Render(MixNode node, MixEdge edge, DelayLine? line, int delay, int width, ref int tapped) {
        var frames = blockFrames;
        TapAll(line, delay, width, ref tapped);
        switch (edge.Kind) {
            case EdgeKind.Mono: {
                var o = planar.AsSpan(0, frames);
                if (edge.Matrix is { } m) {
                    Mix(m, 1, width, 0, o);
                } else if (width == 1) {
                    Tapped(0).CopyTo(o);
                } else {
                    var a = Tapped(0);
                    var b = Tapped(1);
                    for (var i = 0; i < frames; i++) {
                        o[i] = 0.5f * (a[i] + b[i]);
                    }
                }

                ApplyFader(o);
                return 1;
            }
            case EdgeKind.Straight: {
                for (var c = 0; c < edge.Width; c++) {
                    var o = planar.AsSpan(c * frames, frames);
                    if (c < width) {
                        Tapped(c).CopyTo(o);
                        ApplyFader(o);
                    } else {
                        o.Clear();
                    }
                }

                return edge.Width;
            }
            case EdgeKind.Matrix: {
                for (var c = 0; c < edge.Width; c++) {
                    var o = planar.AsSpan(c * frames, frames);
                    Mix(edge.Matrix!, edge.Width, width, c, o);
                    ApplyFader(o);
                }

                return edge.Width;
            }
            case EdgeKind.Object: {
                var count = Math.Min(width, 2);
                for (var c = 0; c < count; c++) {
                    var o = planar.AsSpan(c * frames, frames);
                    if (edge.Matrix is { } m) {
                        Mix(m, 2, width, c, o);
                    } else {
                        Tapped(c).CopyTo(o);
                    }

                    ApplyFader(o);
                }

                return count;
            }
            default:
                Pan(node, edge, width);
                return edge.Width;
        }
    }

    /// <summary>
    ///     Output channel <paramref name="o" /> of a gain matrix (<paramref name="outputs" /> rows) over the (tapped)
    ///     signal's <paramref name="width" /> channels.
    /// </summary>
    void Mix(float[] matrix, int outputs, int width, int o, Span<float> output) {
        output.Clear();
        var inputs = matrix.Length / outputs;
        for (var i = 0; i < Math.Min(width, inputs); i++) {
            var g = matrix[o * inputs + i];
            if (g == 0) {
                continue;
            }

            var source = Tapped(i);
            for (var f = 0; f < output.Length; f++) {
                output[f] += g * source[f];
            }
        }
    }

    /// <summary>
    ///     A mono or stereo signal through the edge's Surround Panner into <see cref="planar" />: the fader's gains
    ///     first, then the panner, in 32-frame pieces while the panner's parameters are automated.
    /// </summary>
    void Pan(MixNode node, MixEdge edge, int width) {
        var frames = blockFrames;
        var outputs = edge.Width;
        var inputs = edge.PannerInputs;
        var panner = edge.Panner!;
        planar.AsSpan(0, outputs * frames).Clear();
        var piece = node.PannerAutomated ? ParameterBlock : frames;
        for (var at = 0; at < frames; at += piece) {
            var n = Math.Min(piece, frames - at);
            for (var c = 0; c < inputs; c++) {
                var o = pieceIn.AsSpan(c * n, n);
                if (inputs == width) {
                    Tapped(c).Slice(at, n).CopyTo(o);
                } else if (inputs == 1) {
                    // The plan expected a mono signal, its inserts made it stereo: their mean.
                    var a = Tapped(0).Slice(at, n);
                    var b = Tapped(1).Slice(at, n);
                    for (var i = 0; i < n; i++) {
                        o[i] = 0.5f * (a[i] + b[i]);
                    }
                } else {
                    Tapped(0).Slice(at, n).CopyTo(o); // a mono signal on both inputs
                }

                ApplyFader(o, at);
            }

            panner.Set(SurroundAt(node, at + n - 1));
            var outBlock = pieceOut.AsSpan(0, outputs * n);
            outBlock.Clear();
            panner.Process(new(pieceIn, inputs, n), new(pieceOut, outputs, n));
            for (var c = 0; c < outputs; c++) {
                var o = planar.AsSpan(c * frames + at, n);
                var p = outBlock.Slice(c * n, n);
                for (var i = 0; i < n; i++) {
                    o[i] += p[i];
                }
            }
        }
    }

    /// <summary>The strip's Surround Panner settings at frame <paramref name="frame" /> of the block (its lanes, else its settings).</summary>
    SurroundPannerSettings SurroundAt(MixNode node, int frame) {
        var settings = node.Channel.Strip.Panner?.Surround;
        var targets = node.SurroundTargets;
        var automation = blockModel.Automation;
        var position = positions[frame];
        return new(
            Value(targets[0], settings?.Angle ?? 0f),
            Value(targets[1], settings?.Diversity ?? 1f),
            Value(targets[2], settings?.Elevation ?? 0f),
            Value(targets[3], settings?.CentreGain ?? 1f),
            Value(targets[4], settings?.LfeGain ?? 0f),
            Value(targets[5], settings?.Spread ?? 60f)
        );

        float Value(int target, float value) =>
            LaneFor(automation, target) is { } lane ? lane.ValueAt(position) : value;
    }

    /// <summary>The object's position at frame <paramref name="frame" /> of the block.</summary>
    ObjectPosition ObjectAt(MixNode node, int frame) {
        var settings = node.Channel.Strip.Panner?.Object;
        var targets = node.ObjectTargets;
        var automation = blockModel.Automation;
        var position = positions[frame];
        return new(
            Value(targets[0], settings?.Azimuth ?? 0f),
            Value(targets[1], settings?.Elevation ?? 0f),
            Value(targets[2], settings?.Distance ?? 1f),
            Value(targets[3], settings?.Size ?? 0f)
        );

        float Value(int target, float value) =>
            LaneFor(automation, target) is { } lane ? lane.ValueAt(position) : value;
    }

    /// <summary>
    ///     A surround signal down-mixed to stereo by the edge's matrix (tapped by <paramref name="delay" />), into the
    ///     first two blocks of <see cref="pieceOut" />.
    /// </summary>
    void Downmix(
        MixEdge edge,
        DelayLine? line,
        int delay,
        int width,
        ref int tapped
    ) {
        var frames = blockFrames;
        TapAll(line, delay, width, ref tapped);
        var l = pieceOut.AsSpan(0, frames);
        var r = pieceOut.AsSpan(frames, frames);
        Mix(edge.Matrix!, 2, width, 0, l);
        Mix(edge.Matrix!, 2, width, 1, r);
    }

    /// <summary>dst = src × the edge's gains (the stereo path's coefficients); returns the peaks.</summary>
    (float Left, float Right) AccumulateTo(
        ReadOnlySpan<float> srcL,
        ReadOnlySpan<float> srcR,
        Span<float> dstL,
        Span<float> dstR
    ) {
        dstL.Clear();
        dstR.Clear();
        return AccumulateMetered(srcL, srcR, dstL, dstR);
    }

    /// <summary>Adds <see cref="planar" />'s <paramref name="count" /> channels into a summing point.</summary>
    void AddToDestination(int destination, int width, int count) {
        var frames = blockFrames;
        var extra = destExtra[destination];
        for (var c = 0; c < Math.Min(count, width); c++) {
            var source = planar.AsSpan(c * frames, frames);
            var target = c switch {
                0 => destLeft[destination].AsSpan(0, frames),
                1 => destRight[destination].AsSpan(0, frames),
                _ => extra is not null && c - 2 < extra.Length && extra[c - 2] is { } e ? e.AsSpan(0, frames) : default
            };
            if (target.IsEmpty) {
                continue;
            }

            for (var i = 0; i < frames; i++) {
                target[i] += source[i];
            }
        }
    }

    /// <summary>The (tapped) signal's channel <paramref name="c" />.</summary>
    ReadOnlySpan<float> Tapped(int c) {
        var frames = blockFrames;
        if (tappedCurrent) {
            return c switch {
                0 => tapLeft.AsSpan(0, frames),
                1 => tapRight.AsSpan(0, frames),
                _ => tapExtra[c - 2].AsSpan(0, frames)
            };
        }

        return c switch {
            0 => signalLeft.AsSpan(0, frames),
            1 => signalRight.AsSpan(0, frames),
            _ => trackExtra[c - 2].AsSpan(0, frames)
        };
    }

    bool tappedCurrent;

    /// <summary>
    ///     Makes <see cref="Tapped" /> read every channel <paramref name="delay" /> frames late (from the delay line, once
    ///     per distinct delay), or undelayed.
    /// </summary>
    void TapAll(DelayLine? line, int delay, int width, ref int tapped) {
        tappedCurrent = false;
        if (delay == 0) {
            return;
        }

        if (line is null || delay > line.MaxDelay) {
            Volatile.Write(ref latencyOverflow, 1);
            if (line is null) {
                return;
            }
        }

        var frames = blockFrames;
        if (tapped != delay) {
            line.Read(delay, tapLeft.AsSpan(0, frames), tapRight.AsSpan(0, frames));
            tapped = delay;
        }

        if (tappedExtra != delay) {
            for (var c = 2; c < width; c++) {
                if (c - 2 < line.ExtraChannels) {
                    line.ReadExtra(c - 2, delay, tapExtra[c - 2].AsSpan(0, frames));
                } else {
                    trackExtra[c - 2].AsSpan(0, frames).CopyTo(tapExtra[c - 2]);
                }
            }

            tappedExtra = delay;
        }

        if (width == 1) {
            tapLeft.AsSpan(0, frames).CopyTo(tapRight); // a mono line holds its channel twice
        }

        tappedCurrent = true;
    }

    /// <summary>The signal × the fader into <see cref="planar" /> (for a meter of a channel that goes nowhere).</summary>
    ReadOnlySpan<float> Gained(DelayLine? line, int delay, int width, ref int tapped) {
        var frames = blockFrames;
        TapAll(line, delay, width, ref tapped);
        for (var c = 0; c < width; c++) {
            var o = planar.AsSpan(c * frames, frames);
            Tapped(c).CopyTo(o);
            ApplyFader(o);
        }

        return planar.AsSpan(0, width * frames);
    }

    /// <summary>The peaks of each of <paramref name="count" /> planar channels into the strip's meter.</summary>
    void MeterPlanar(ChannelState ch, ReadOnlySpan<float> block, int count) {
        var frames = blockFrames;
        for (var c = 0; c < count; c++) {
            float peak = 0;
            foreach (var v in block.Slice(c * frames, frames)) {
                peak = Math.Max(peak, Math.Abs(v));
            }

            peaks[c] = peak;
        }

        ch.Strip.MeterChannels(peaks.AsSpan(0, count));
    }

    /// <summary>The post-fader signal another insert takes as a sidechain: the front pair at the fader's gains.</summary>
    void PostMulti(ChannelState ch, int width) {
        if (ch.PostLeft is not { } postL || ch.PostRight is not { } postR) {
            return;
        }

        var frames = blockFrames;
        signalLeft.AsSpan(0, frames).CopyTo(postL);
        (width == 1 ? signalLeft : signalRight).AsSpan(0, frames).CopyTo(postR);
        ApplyFader(postL.AsSpan(0, frames));
        ApplyFader(postR.AsSpan(0, frames));
        ch.PostFrames = frames;
    }

    // ---- Gains without a pan -----------------------------------------------------------------------------------------

    /// <summary>The fader's gains (volume, mute, solo and their automation) into <see cref="faderLane" />, ramped.</summary>
    void FaderGains(MixNode node, ref EdgeGain edge) {
        var ch = node.Channel;
        var strip = ch.Strip;
        var automation = blockModel.Automation;
        var volume = LaneFor(automation, node.VolumeTarget);
        var mute = LaneFor(automation, node.MuteTarget);
        if (volume is null && mute is null) {
            RampScalar(ref edge, ch.Audible && !strip.Mute ? strip.Gain : 0f);
            return;
        }

        Lane(volume, volumeLane, strip.Gain);
        Lane(mute, muteLane, strip.Mute ? 1f : 0f);
        ScaleScalar(volumeLane, ch.Audible, ref edge);
    }

    /// <summary>A send's gains without its pan: its level (times the fader's when post-fader), the strip's mute.</summary>
    void SendLevelGains(MixNode node, ChannelSend send, int target) {
        var ch = node.Channel;
        var strip = ch.Strip;
        var slot = send.Slot;
        var automation = blockModel.Automation;
        var level = LaneFor(automation, target);
        var volume = send.PreFader ? null : LaneFor(automation, node.VolumeTarget);
        var mute = LaneFor(automation, node.MuteTarget);
        if (level is null && volume is null && mute is null) {
            var g = !ch.Audible || strip.Mute ? 0f
                : send.PreFader ? slot.Gain
                : slot.Gain * strip.Gain;
            RampScalar(ref slot.Edge, g);
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

        ScaleScalar(levelLane, ch.Audible, ref slot.Edge);
    }

    void ScaleScalar(float[] gains, bool audible, ref EdgeGain edge) {
        for (var i = 0; i < blockFrames; i++) {
            faderLane[i] = audible && muteLane[i] < 0.5f ? gains[i] : 0f;
        }

        faderConstant = false;
        edge.Left = edge.Right = faderLane[blockFrames - 1];
        edge.Set = true;
    }

    /// <summary>A constant gain, ramped linearly across the block from the last one when it changed.</summary>
    void RampScalar(ref EdgeGain edge, float target) {
        var start = edge.Set ? edge.Left : target;
        edge.Left = edge.Right = target;
        edge.Set = true;
        faderConstant = start == target;
        faderValue = target;
        if (faderConstant) {
            return;
        }

        var step = (target - start) / blockFrames;
        for (var i = 0; i < blockFrames; i++) {
            faderLane[i] = start + step * (i + 1);
        }
    }

    /// <summary>Multiplies <paramref name="block" /> (frames from <paramref name="at" />) by the fader's gains.</summary>
    void ApplyFader(Span<float> block, int at = 0) {
        if (faderConstant) {
            var g = faderValue;
            if (g == 1f) {
                return;
            }

            for (var i = 0; i < block.Length; i++) {
                block[i] *= g;
            }

            return;
        }

        var lane = faderLane.AsSpan(at, block.Length);
        for (var i = 0; i < block.Length; i++) {
            block[i] *= lane[i];
        }
    }

    // ---- Inserts on a multichannel strip -----------------------------------------------------------------------------

    /// <summary>
    ///     The inserts of a channel whose signal has <paramref name="width" /> channels (more than two), each by its
    ///     <see cref="MultichannelPolicy" />.
    /// </summary>
    void InsertsMulti(MixNode node, int width, int first) {
        var inserts = node.Config.Inserts;
        var live = node.Channel.Live;
        for (var k = first; k < inserts.Length; k++) {
            var insert = inserts[k];
            insert.Slot.ApplyParameters();
            if (Active(insert, live) is not { } effect) {
                continue;
            }

            Sidechain(node.Sidechains[k], out var sideL, out var sideR);
            var multi = insert.Slot.Multi;
            var bindings = node.InsertBindings[k];
            var last = node.InsertLast[k];
            if (multi is null || multi.Channels != width) {
                // Not prepared for this format (yet): the front pair only.
                Process(effect, bindings, last, Signal(0), Signal(1), sideL, sideR);
                continue;
            }

            switch (multi.Policy) {
                case MultichannelPolicy.PerChannel:
                    PerChannel(effect, multi, bindings, last, sideL, sideR);
                    break;
                case MultichannelPolicy.Linked:
                    Linked(effect, multi, bindings, last, width, sideL, sideR);
                    break;
                default:
                    Process(effect, bindings, last, Signal(0), Signal(1), sideL, sideR);
                    DelayRest(multi, effect.LatencySamples, width);
                    break;
            }
        }
    }

    /// <summary>The channel being mixed's channel <paramref name="c" />, undelayed, writable.</summary>
    Span<float> Signal(int c) =>
        (c switch {
            0 => signalLeft,
            1 => signalRight,
            _ => trackExtra[c - 2]
        }).AsSpan(0, blockFrames);

    /// <summary>
    ///     Every pair (and single channel: on both inputs, the mean of the outputs kept) through its own instance, the
    ///     parameters mirrored; automated parameters are set on every instance at each 32-frame piece.
    /// </summary>
    void PerChannel(
        IAudioEffect effect,
        MultiInsert multi,
        ParameterBinding[] bindings,
        float[] last,
        ReadOnlySpan<float> sideL,
        ReadOnlySpan<float> sideR
    ) {
        var frames = blockFrames;
        var piece = bindings.Length > 0 ? ParameterBlock : frames;
        var singles = pairScratch;
        for (var at = 0; at < frames; at += piece) {
            var n = Math.Min(piece, frames - at);
            if (bindings.Length > 0) {
                SetAutomated(bindings, last, at, effect, null);
                for (var b = 0; b < bindings.Length; b++) {
                    foreach (var extra in multi.Extras) {
                        extra?.SetParameter(bindings[b].Parameter, last[b]);
                    }
                }
            }

            var transport = Piece(at, n);
            var sl = sideL.IsEmpty ? sideL : sideL.Slice(at, n);
            var sr = sideR.IsEmpty ? sideR : sideR.Slice(at, n);
            for (var p = 0; p < multi.Pairs.Length; p++) {
                var instance = p == 0 ? effect : multi.Extras[p - 1];
                if (instance is null) {
                    continue;
                }

                var (a, b) = multi.Pairs[p];
                var x = Signal(a).Slice(at, n);
                instance.SetTransport(transport);
                if (b >= 0) {
                    instance.Process(x, Signal(b).Slice(at, n), sl, sr);
                    continue;
                }

                var y = singles.AsSpan(0, n);
                x.CopyTo(y);
                instance.Process(x, y, sl, sr);
                for (var i = 0; i < n; i++) {
                    x[i] = 0.5f * (x[i] + y[i]);
                }
            }
        }
    }

    /// <summary>
    ///     A linked dynamics insert: the effect processes the key (each frame's loudest sample of any channel) and the
    ///     gain it gave the key, its latency later, applies to every channel delayed by that latency.
    /// </summary>
    void Linked(
        IAudioEffect effect,
        MultiInsert multi,
        ParameterBinding[] bindings,
        float[] last,
        int width,
        ReadOnlySpan<float> sideL,
        ReadOnlySpan<float> sideR
    ) {
        var frames = blockFrames;
        var key = multi.Key.AsSpan(0, frames);
        for (var i = 0; i < frames; i++) {
            var loud = 0f;
            for (var c = 0; c < width; c++) {
                var v = Signal(c)[i];
                if (Math.Abs(v) > Math.Abs(loud)) {
                    loud = v;
                }
            }

            key[i] = loud;
        }

        var probe = multi.Probe.AsSpan(0, frames);
        var probeRight = multi.ProbeRight.AsSpan(0, frames);
        key.CopyTo(probe);
        key.CopyTo(probeRight);
        Process(effect, bindings, last, probe, probeRight, sideL, sideR);
        var latency = Math.Clamp(effect.LatencySamples, 0, MultiInsert.MaxLatency);
        for (var c = 0; c < width; c++) {
            multi.Write(c, Signal(c));
        }

        multi.Write(width, key);
        multi.Advance(frames);
        var ratio = multi.Ratio.AsSpan(0, frames);
        multi.Read(width, latency, ratio); // the key as the effect's output lines up with it
        var held = multi.LastRatio;
        for (var i = 0; i < frames; i++) {
            var k = ratio[i];
            if (Math.Abs(k) > 1e-9f) {
                held = probe[i] / k;
            }

            ratio[i] = held;
        }

        multi.LastRatio = held;
        for (var c = 0; c < width; c++) {
            var x = Signal(c);
            multi.Read(c, latency, x);
            for (var i = 0; i < frames; i++) {
                x[i] *= ratio[i];
            }
        }
    }

    /// <summary>The channels after the front pair, delayed by the effect's latency (a front-pair insert).</summary>
    void DelayRest(MultiInsert multi, int latency, int width) {
        latency = Math.Clamp(latency, 0, MultiInsert.MaxLatency);
        for (var c = 2; c < width; c++) {
            multi.Write(c, Signal(c));
        }

        multi.Advance(blockFrames);
        if (latency == 0) {
            return;
        }

        for (var c = 2; c < width; c++) {
            multi.Read(c, latency, Signal(c));
        }
    }

    /// <summary>One instance over a pair: whole, or in 32-frame pieces when automated.</summary>
    void Process(
        IAudioEffect effect,
        ParameterBinding[] bindings,
        float[] last,
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sideL,
        ReadOnlySpan<float> sideR
    ) {
        if (bindings.Length == 0) {
            effect.SetTransport(blockTransport);
            effect.Process(left, right, sideL, sideR);
            return;
        }

        ProcessAutomated(effect, bindings, last, left, right, sideL, sideR);
    }

    // ---- The master --------------------------------------------------------------------------------------------------

    /// <summary>
    ///     A surround master: the bed sum (and the preview), the objects rendered into it, its inserts, its fader into
    ///     <see cref="masterPlanar" /> (the first two channels also into <paramref name="outLeft" /> and
    ///     <paramref name="outRight" />), metered and captured.
    /// </summary>
    void MasterMulti(MixPlan plan, Span<float> outLeft, Span<float> outRight) {
        var node = plan.Master;
        var ch = node.Channel;
        var width = node.Width;
        var frames = blockFrames;
        signalLeft = trackLeft;
        signalRight = trackRight;
        destLeft[0].AsSpan(0, frames).CopyTo(trackLeft);
        destRight[0].AsSpan(0, frames).CopyTo(trackRight);
        var extra = destExtra[0];
        for (var c = 2; c < width; c++) {
            if (extra is not null && c - 2 < extra.Length && extra[c - 2] is { } e) {
                e.AsSpan(0, frames).CopyTo(trackExtra[c - 2]);
            } else {
                trackExtra[c - 2].AsSpan(0, frames).Clear();
            }
        }

        MixPreview(trackLeft.AsSpan(0, frames), trackRight.AsSpan(0, frames));
        if (blockCapture is not { BedOnly: true }) {
            RenderObjects(plan, width);
        }

        InsertsMulti(node, width, 0);
        var line = ch.Delay;
        if (line is not null) {
            for (var c = 2; c < width && c - 2 < line.ExtraChannels; c++) {
                line.WriteExtra(c - 2, trackExtra[c - 2].AsSpan(0, frames));
            }

            line.Write(trackLeft.AsSpan(0, frames), trackRight.AsSpan(0, frames));
        }

        FaderGains(node, ref ch.Fader);
        var tapped = -1;
        tappedExtra = -1;
        TapAll(line, masterOutputDelay, width, ref tapped);
        for (var c = 0; c < width; c++) {
            var o = masterPlanar.AsSpan(c * frames, frames);
            Tapped(c).CopyTo(o);
            ApplyFader(o);
        }

        masterPlanar.AsSpan(0, frames).CopyTo(outLeft);
        masterPlanar.AsSpan(width > 1 ? frames : 0, frames).CopyTo(outRight); // a mono master's one channel twice
        MeterPlanar(ch, masterPlanar.AsSpan(0, width * frames), width);
        if (blockCapture is { MonitoringRender: false, Master: { } target }) {
            target.WritePlanar(masterPlanar.AsSpan(0, width * frames), width, frames, ch.Latency + masterOutputDelay);
        }
    }

    /// <summary>
    ///     The block's objects through the master's renderer into the master's sum: each at its position (in 32-frame
    ///     pieces while any object's position is automated). A renderer index whose channel changed starts afresh.
    /// </summary>
    void RenderObjects(MixPlan plan, int width) {
        if (plan.ObjectRenderer is not { } renderer) {
            return;
        }

        var objects = plan.Objects;
        var frames = blockFrames;
        var automated = false;
        for (var k = 0; k < objects.Length; k++) {
            if (objectOwners[k] != objects[k].TrackId) {
                objectOwners[k] = objects[k].TrackId;
                renderer.Reset(k);
            }

            automated |= objects[k].PannerAutomated;
        }

        var piece = automated ? ParameterBlock : frames;
        for (var at = 0; at < frames; at += piece) {
            var n = Math.Min(piece, frames - at);
            var output = pieceOut.AsSpan(0, width * n);
            output.Clear();
            for (var k = 0; k < objects.Length; k++) {
                if (!objectActive[k] || objectBuffers[k] is not { } buffer) {
                    continue;
                }

                var channels = objectWidths[k];
                for (var c = 0; c < channels; c++) {
                    buffer.AsSpan(c * frames + at, n).CopyTo(pieceIn.AsSpan(c * n, n));
                }

                renderer.SetPosition(k, ObjectAt(objects[k], at + n - 1));
                renderer.Render(k, new(pieceIn, channels, n), new(pieceOut, width, n));
            }

            for (var c = 0; c < width; c++) {
                var sum = (c switch {
                    0 => trackLeft,
                    1 => trackRight,
                    _ => trackExtra[c - 2]
                }).AsSpan(at, n);
                var o = output.Slice(c * n, n);
                for (var i = 0; i < n; i++) {
                    sum[i] += o[i];
                }
            }
        }

        Array.Clear(objectActive);
    }

    // ---- The device stage: monitoring and the speaker map ------------------------------------------------------------

    /// <summary>
    ///     The master to the device through the monitoring (docs/11-phase-10.md, "Rendering"): binaural to two channels,
    ///     or a down-mix, or the master's channels as they are; then channel i of what is sent to device output
    ///     <c>map[i]</c> (−1: not sent; past the map: output i). The click joins the first two channels sent. A render that
    ///     asks for the monitoring render captures what is sent, before the map.
    /// </summary>
    void MonitorOutputs(
        float** outputs,
        int outputChannels,
        int offset,
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        ReadOnlySpan<float> click
    ) {
        var plan = blockModel.Mix;
        var frames = blockFrames;
        var width = plan.Master.Width;
        if (width == 2) {
            left.CopyTo(masterPlanar);
            right.CopyTo(masterPlanar.AsSpan(frames));
        }

        var setup = Volatile.Read(ref monitor);
        var sent = masterPlanar;
        var count = width;
        var latency = 0;
        if (setup is not null && setup.From == plan.MasterFormat) {
            if (setup.Binaural is { } binaural) {
                binaural.Process(
                    new(masterPlanar, width, frames),
                    sentPlanar.AsSpan(0, frames),
                    sentPlanar.AsSpan(frames, frames)
                );
                sent = sentPlanar;
                count = 2;
                latency = binaural.LatencySamples;
            } else if (setup.Downmix is { } downmix) {
                downmix.Process(new(masterPlanar, width, frames), new(sentPlanar, setup.Channels, frames));
                sent = sentPlanar;
                count = setup.Channels;
            }
        }

        if (blockCapture is { MonitoringRender: true, Master: { } target }) {
            var m = plan.Master.Channel;
            target.WritePlanar(sent.AsSpan(0, count * frames), count, frames, m.Latency + masterOutputDelay + latency);
        }

        var map = setup?.Map;
        for (var i = 0; i < count; i++) {
            var output = map is not null && i < map.Length ? map[i] : i;
            if (output < 0 || output >= outputChannels) {
                continue;
            }

            var destination = outputs[output] + offset;
            var source = sent.AsSpan(i * frames, frames);
            if (i < 2 || count == 1) {
                for (var f = 0; f < frames; f++) {
                    destination[f] += source[f] + click[f];
                }
            } else {
                for (var f = 0; f < frames; f++) {
                    destination[f] += source[f];
                }
            }
        }
    }

    // ---- The master tap ----------------------------------------------------------------------------------------------

    /// <summary>
    ///     The listen-in tap (segment format 5): the master after its fader (a surround master's stereo down-mix), with
    ///     the block's transport stamp. Never during an offline render.
    /// </summary>
    void TapMaster(ReadOnlySpan<float> left, ReadOnlySpan<float> right, long position, bool rolling) {
        if (Volatile.Read(ref masterTapOn) == 0 || Volatile.Read(ref masterTap) is not { } writer) {
            tapFresh = true;
            return;
        }

        var plan = blockModel.Mix;
        var frames = blockFrames;
        if (plan.MasterMulti && plan.TapDownmix is { } downmix) {
            downmix.Process(new(masterPlanar, plan.Master.Width, frames), new(tapPlanar, 2, frames));
            left = tapPlanar.AsSpan(0, frames);
            right = tapPlanar.AsSpan(frames, frames);
        }

        var flags = rolling ? Ipc.Segments.TapStamp.FlagPlaying : 0;
        if (tapFresh || (rolling && position != tapNext)) {
            flags |= Ipc.Segments.TapStamp.FlagDiscontinuity;
        }

        tapFresh = false;
        tapNext = rolling ? position + frames : -1;
        writer.Write(left, right, rolling ? position : -1, flags);
    }

    // ---- Resets ------------------------------------------------------------------------------------------------------

    /// <summary>Audio thread (a render's start): the multichannel state's share of <see cref="ResetMix" />.</summary>
    void ResetSpatial(ModelRoot model) {
        var plan = model.Mix;
        foreach (var node in plan.Order) {
            ResetEdges(node);
        }

        ResetEdges(plan.Master);
        if (plan.ObjectRenderer is { } renderer) {
            for (var k = 0; k < plan.Objects.Length; k++) {
                renderer.Reset(k);
                objectOwners[k] = plan.Objects[k].TrackId;
            }
        }

        foreach (var d in plan.UsedDestinations) {
            if (destExtra[d] is { } extra) {
                foreach (var buffer in extra) {
                    buffer?.AsSpan().Clear();
                }
            }
        }

        Volatile.Read(ref monitor)?.Binaural?.Reset();
    }

    static void ResetEdges(MixNode node) {
        node.Channel.Fader = default;
        node.OutputEdge?.Panner?.Reset();
        foreach (var edge in node.SendEdges) {
            edge?.Panner?.Reset();
        }

        foreach (var insert in node.Config.Inserts) {
            insert.Slot.Multi?.Clear();
        }
    }
}

/// <summary>
///     The monitoring as the device stage applies it (<c>setMonitoring</c>), prepared by the control thread for one
///     master format: binaural through an HRTF set, or a down-mix to <see cref="Channels" />, and the 0-based speaker
///     map. Immutable; swapped whole.
/// </summary>
public sealed class MonitorSetup {
    public required SpeakerFormat From { get; init; }

    public IBinaural? Binaural { get; init; }

    public IDownmix? Downmix { get; init; }

    /// <summary>What is sent: 2 binaural, the down-mix's channels, or the master's.</summary>
    public required int Channels { get; init; }

    /// <summary>Channel i of what is sent goes to device output <c>Map[i]</c> (−1: not sent; past the end: output i).</summary>
    public int[] Map { get; init; } = [];

    /// <summary>Monitoring that changes nothing: the master's channels to outputs 1…n.</summary>
    public bool IsIdentity => Binaural is null && Downmix is null && Map.Length == 0;
}
