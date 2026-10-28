using Anode.Ipc;

namespace Anode.Engine;

/// <summary>
///     A channel strip's fader settings (linear gain, pan −1…1, mute, solo), written by the control thread and read by
///     the audio thread field by field, plus its peak meter. The mixer (<c>AudioEngine.Mix.cs</c>) applies them.
/// </summary>
public sealed class Strip {
    float gain = 1f;
    float pan;
    bool mute;
    bool solo;

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

    /// <summary>Reader (telemetry): the peaks since the previous call.</summary>
    public (float Left, float Right) TakePeaks() {
        var peaks = (Volatile.Read(ref publishedLeft), Volatile.Read(ref publishedRight));
        Interlocked.Increment(ref resetRequested);
        return peaks;
    }

    /// <summary>Audio thread: adds a block's post-fader peaks to the meter.</summary>
    internal void Meter(float left, float right) {
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
