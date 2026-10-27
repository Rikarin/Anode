using Anode.Ipc;

namespace Anode.Engine;

/// <summary>
///     A channel strip's settings (linear gain, pan −1…1, mute, solo), written by the control thread and read by the
///     audio thread field by field, plus the audio thread's smoothed level and its peak meter.
/// </summary>
public sealed class Strip {
    float gain = 1f;
    float pan;
    bool mute;
    bool solo;

    // Audio thread: the level each side is at (ramps to the target over one block when it changes).
    float levelLeft;
    float levelRight;
    bool levelSet;

    // Meters: the audio thread accumulates peaks and publishes them; the reader asks for a reset by bumping a counter.
    float accumulatedLeft;
    float accumulatedRight;
    float publishedLeft;
    float publishedRight;
    int resetRequested;
    int resetSeen;

    public Strip() { }

    public Strip(EngineStrip strip) {
        Set(strip);
    }

    public float Gain => Volatile.Read(ref gain);

    public float Pan => Volatile.Read(ref pan);

    public bool Mute => Volatile.Read(ref mute);

    public bool Solo => Volatile.Read(ref solo);

    /// <summary>Control thread.</summary>
    public void Set(EngineStrip strip) {
        Volatile.Write(ref gain, float.IsFinite(strip.Gain) ? Math.Max(0f, strip.Gain) : 0f);
        Volatile.Write(ref pan, float.IsFinite(strip.Pan) ? Math.Clamp(strip.Pan, -1f, 1f) : 0f);
        Volatile.Write(ref mute, strip.Mute);
        Volatile.Write(ref solo, strip.Solo);
    }

    public EngineStrip Settings => new(Gain, Pan, Mute, Solo);

    /// <summary>
    ///     Pan law for a mono source: constant power, compensated by 3 dB so the centre is unity and a hard pan is
    ///     +3 dB on its side (Logic's default, "−3 dB compensated"). A stereo source is balanced instead: the far side
    ///     is attenuated linearly and the near side stays at unity.
    /// </summary>
    public static (float Left, float Right) PanGains(float pan, bool stereoSource) {
        if (pan == 0f) {
            return (1f, 1f);
        }

        if (stereoSource) {
            return (pan > 0 ? 1f - pan : 1f, pan < 0 ? 1f + pan : 1f);
        }

        var angle = (pan + 1.0) * Math.PI / 4;
        return ((float)(Math.Sqrt(2) * Math.Cos(angle)), (float)(Math.Sqrt(2) * Math.Sin(angle)));
    }

    /// <summary>
    ///     Audio thread: scales <paramref name="left" /> and <paramref name="right" /> (a mono source passes the same
    ///     span twice) by the strip's gain and pan, gated by <paramref name="audible" />, and adds them into the bus.
    ///     A changed level ramps linearly across the block. Accumulates the post-fader peaks.
    /// </summary>
    public void MixInto(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        bool stereoSource,
        bool audible,
        Span<float> busLeft,
        Span<float> busRight
    ) {
        var (targetLeft, targetRight) = Targets(stereoSource, audible);
        var frames = left.Length;
        var startLeft = levelSet ? levelLeft : targetLeft;
        var startRight = levelSet ? levelRight : targetRight;
        float peakLeft = 0, peakRight = 0;
        if (startLeft == targetLeft && startRight == targetRight) {
            for (var i = 0; i < frames; i++) {
                var l = left[i] * targetLeft;
                var r = right[i] * targetRight;
                busLeft[i] += l;
                busRight[i] += r;
                peakLeft = Math.Max(peakLeft, Math.Abs(l));
                peakRight = Math.Max(peakRight, Math.Abs(r));
            }
        } else {
            var stepLeft = (targetLeft - startLeft) / frames;
            var stepRight = (targetRight - startRight) / frames;
            for (var i = 0; i < frames; i++) {
                var l = left[i] * (startLeft + stepLeft * (i + 1));
                var r = right[i] * (startRight + stepRight * (i + 1));
                busLeft[i] += l;
                busRight[i] += r;
                peakLeft = Math.Max(peakLeft, Math.Abs(l));
                peakRight = Math.Max(peakRight, Math.Abs(r));
            }
        }

        levelLeft = targetLeft;
        levelRight = targetRight;
        levelSet = true;
        Meter(peakLeft, peakRight);
    }

    /// <summary>Audio thread: as <see cref="MixInto" /> for a stereo bus processed in place (the master).</summary>
    public void Process(Span<float> left, Span<float> right) {
        var (targetLeft, targetRight) = Targets(true, true);
        var frames = left.Length;
        var startLeft = levelSet ? levelLeft : targetLeft;
        var startRight = levelSet ? levelRight : targetRight;
        var stepLeft = (targetLeft - startLeft) / frames;
        var stepRight = (targetRight - startRight) / frames;
        var ramp = stepLeft != 0 || stepRight != 0;
        float peakLeft = 0, peakRight = 0;
        for (var i = 0; i < frames; i++) {
            var l = left[i] * (ramp ? startLeft + stepLeft * (i + 1) : targetLeft);
            var r = right[i] * (ramp ? startRight + stepRight * (i + 1) : targetRight);
            left[i] = l;
            right[i] = r;
            peakLeft = Math.Max(peakLeft, Math.Abs(l));
            peakRight = Math.Max(peakRight, Math.Abs(r));
        }

        levelLeft = targetLeft;
        levelRight = targetRight;
        levelSet = true;
        Meter(peakLeft, peakRight);
    }

    /// <summary>Reader (telemetry): the peaks since the previous call.</summary>
    public (float Left, float Right) TakePeaks() {
        var peaks = (Volatile.Read(ref publishedLeft), Volatile.Read(ref publishedRight));
        Interlocked.Increment(ref resetRequested);
        return peaks;
    }

    (float, float) Targets(bool stereoSource, bool audible) {
        if (!audible || Mute) {
            return (0f, 0f);
        }

        var g = Gain;
        var (l, r) = PanGains(Pan, stereoSource);
        return (g * l, g * r);
    }

    void Meter(float left, float right) {
        var request = Volatile.Read(ref resetRequested);
        if (request != resetSeen) {
            resetSeen = request;
            accumulatedLeft = accumulatedRight = 0;
        }

        accumulatedLeft = Math.Max(accumulatedLeft, left);
        accumulatedRight = Math.Max(accumulatedRight, right);
        Volatile.Write(ref publishedLeft, accumulatedLeft);
        Volatile.Write(ref publishedRight, accumulatedRight);
    }
}
