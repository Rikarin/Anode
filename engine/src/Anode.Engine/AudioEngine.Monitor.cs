namespace Anode.Engine;

// The monitor section (issue #138): the last stage before the device, for what the speakers get and nothing else. The
// mix, its meters, a render, the listen-in tap and device output pairs a strip goes to directly are all behind it or
// beside it. In order: AFL or PFL replace the master by the listen bus while a strip is soloed, the mono check folds the
// front pair, and the level (one gain the server works out from the level, the reference and the dim) applies to every
// channel and the click, ramped over a block. With nothing set it touches nothing, so the output stays bit for bit.
public sealed unsafe partial class AudioEngine {
    float monitorGain = 1f;
    int monitorMono;
    float appliedMonitorGain = 1f;

    /// <summary>Control thread: the monitor section's gain and mono check.</summary>
    public void SetMonitorSection(float gain, bool mono) {
        Volatile.Write(ref monitorGain, float.IsFinite(gain) ? Math.Max(0f, gain) : 1f);
        Volatile.Write(ref monitorMono, mono ? 1 : 0);
    }

    /// <summary>A stereo master on its way to the device.</summary>
    void MonitorSection(Span<float> left, Span<float> right) {
        if (Volatile.Read(ref offline) != 0) {
            return;
        }

        if (blockListen) {
            listenLeft.AsSpan(0, left.Length).CopyTo(left);
            listenRight.AsSpan(0, right.Length).CopyTo(right);
        }

        if (Volatile.Read(ref monitorMono) != 0) {
            Fold(left, right);
        }

        var (from, to) = MonitorRamp();
        if (from != 1f || to != 1f) {
            Scale(left, from, to);
            Scale(right, from, to);
            Scale(clicks.AsSpan(0, left.Length), from, to);
        }
    }

    /// <summary>
    ///     What the monitoring sends (<paramref name="count" /> planar channels of <paramref name="sent" />), on its way
    ///     to the device: the listen bus takes the front pair and the rest fall silent.
    /// </summary>
    void MonitorSection(float[] sent, int count) {
        if (Volatile.Read(ref offline) != 0) {
            return;
        }

        var frames = blockFrames;
        var left = sent.AsSpan(0, frames);
        var right = sent.AsSpan(Math.Min(1, count - 1) * frames, frames);
        if (blockListen) {
            sent.AsSpan(0, count * frames).Clear();
            ListenInto(left, right, count);
        }

        if (Volatile.Read(ref monitorMono) != 0 && count > 1) {
            Fold(left, right);
        }

        var (from, to) = MonitorRamp();
        if (from == 1f && to == 1f) {
            return;
        }

        for (var c = 0; c < count; c++) {
            Scale(sent.AsSpan(c * frames, frames), from, to);
        }

        Scale(clicks.AsSpan(0, frames), from, to);
    }

    /// <summary>The listen bus into a front pair, or folded into a single channel.</summary>
    void ListenInto(Span<float> left, Span<float> right, int count) {
        var frames = blockFrames;
        if (count > 1) {
            listenLeft.AsSpan(0, frames).CopyTo(left);
            listenRight.AsSpan(0, frames).CopyTo(right);
            return;
        }

        for (var f = 0; f < frames; f++) {
            left[f] = 0.5f * (listenLeft[f] + listenRight[f]);
        }
    }

    (float From, float To) MonitorRamp() {
        var from = appliedMonitorGain;
        var to = Volatile.Read(ref monitorGain);
        appliedMonitorGain = to;
        return (from, to);
    }

    /// <summary>Both channels of a pair become their mean.</summary>
    static void Fold(Span<float> left, Span<float> right) {
        for (var i = 0; i < left.Length; i++) {
            left[i] = right[i] = 0.5f * (left[i] + right[i]);
        }
    }
}
