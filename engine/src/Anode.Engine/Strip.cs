using Anode.Ipc;

namespace Anode.Engine;

/// <summary>
///     A channel strip's fader settings (linear gain, pan −1…1, mute, solo) and input stage (signed trim, delay, dual
///     pan), written by the control thread and read by the audio thread field by field, plus its peak meter. The mixer
///     (<c>AudioEngine.Mix.cs</c>) applies them.
/// </summary>
public sealed class Strip {
    float gain = 1f;
    float pan;
    bool mute;
    bool solo;
    float trim = 1f;
    int delayFrames;
    bool dualPan;
    float panLeft = -1f;
    float panRight = 1f;
    bool soloSafe;

    /// <summary>The most a strip's delay is, either way, in frames (500 ms at the highest rate, and then some).</summary>
    const int MaxDelayFrames = 1 << 18;

    // Meters: the audio thread accumulates peaks and publishes them; the reader asks for a reset by bumping a counter.
    float accumulatedLeft;
    float accumulatedRight;
    float publishedLeft;
    float publishedRight;
    int resetRequested;
    int resetSeen;

    // Phase 10: a surround meter's channels (published while meterChannels > 0).
    readonly float[] accumulatedChannels = new float[Spatial.Formats.MaxChannels];
    readonly float[] publishedChannels = new float[Spatial.Formats.MaxChannels];
    int meterChannels;
    EnginePanner? panner;

    public Strip() { }

    public Strip(EngineStrip strip) {
        Set(strip);
    }

    public float Gain => Volatile.Read(ref gain);

    public float Pan => Volatile.Read(ref pan);

    public bool Mute => Volatile.Read(ref mute);

    public bool Solo => Volatile.Read(ref solo);

    /// <summary>The source's gain before the inserts; negative: polarity inverted.</summary>
    public float Trim => Volatile.Read(ref trim);

    /// <summary>Frames the strip plays late (negative: early).</summary>
    public int DelayFrames => Volatile.Read(ref delayFrames);

    /// <summary>A stereo signal's channels pan on their own, to <see cref="PanLeft" /> and <see cref="PanRight" />.</summary>
    public bool DualPan => Volatile.Read(ref dualPan);

    public float PanLeft => Volatile.Read(ref panLeft);

    public float PanRight => Volatile.Read(ref panRight);

    /// <summary>The strip stays audible under others' solo in place.</summary>
    public bool SoloSafe => Volatile.Read(ref soloSafe);

    /// <summary>The panner's settings (Phase 10; null: the stereo pan), swapped whole by the control thread.</summary>
    public EnginePanner? Panner => Volatile.Read(ref panner);

    /// <summary>Control thread.</summary>
    public void Set(EngineStrip strip) {
        Volatile.Write(ref gain, float.IsFinite(strip.Gain) ? Math.Max(0f, strip.Gain) : 0f);
        Volatile.Write(ref pan, float.IsFinite(strip.Pan) ? Math.Clamp(strip.Pan, -1f, 1f) : 0f);
        Volatile.Write(ref mute, strip.Mute);
        Volatile.Write(ref solo, strip.Solo);
        Volatile.Write(ref panner, strip.Panner);
        Volatile.Write(ref trim, float.IsFinite(strip.Trim) ? strip.Trim : 1f);
        Volatile.Write(ref delayFrames, Math.Clamp(strip.DelayFrames, -MaxDelayFrames, MaxDelayFrames));
        Volatile.Write(ref dualPan, strip.DualPan);
        Volatile.Write(ref panLeft, float.IsFinite(strip.PanLeft) ? Math.Clamp(strip.PanLeft, -1f, 1f) : -1f);
        Volatile.Write(ref panRight, float.IsFinite(strip.PanRight) ? Math.Clamp(strip.PanRight, -1f, 1f) : 1f);
        Volatile.Write(ref soloSafe, strip.SoloSafe);
    }

    /// <summary>Control thread (Phase 11, an Environment control target): the volume alone.</summary>
    public void SetGain(float value) => Volatile.Write(ref gain, float.IsFinite(value) ? Math.Max(0f, value) : 0f);

    /// <summary>Control thread: the pan alone.</summary>
    public void SetPan(float value) => Volatile.Write(ref pan, float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f);

    /// <summary>Control thread: the mute alone.</summary>
    public void SetMute(bool value) => Volatile.Write(ref mute, value);

    public EngineStrip Settings => new(Gain, Pan, Mute, Solo);

    /// <summary>
    ///     A mono source's gains under <paramref name="law" />; by default constant power, compensated by 3 dB so the
    ///     centre is unity and a hard pan is +3 dB on its side (Logic's default). A stereo source is balanced instead,
    ///     whatever the law: the far side is attenuated linearly and the near side stays at unity.
    /// </summary>
    public static (float Left, float Right) PanGains(
        float pan,
        bool stereoSource,
        EnginePanLaw law = EnginePanLaw.Minus3DbCompensated
    ) {
        if (stereoSource) {
            return pan == 0f ? (1f, 1f) : Balance(pan);
        }

        if (law == EnginePanLaw.Minus3DbCompensated && pan == 0f) {
            return (1f, 1f);
        }

        var angle = (pan + 1.0) * Math.PI / 4;
        var (powerLeft, powerRight) = (Math.Cos(angle), Math.Sin(angle));
        var (linearLeft, linearRight) = ((1.0 - pan) / 2, (1.0 + pan) / 2);
        return law switch {
            EnginePanLaw.ZeroDb => Balance(pan),
            EnginePanLaw.Minus3Db => ((float)powerLeft, (float)powerRight),
            EnginePanLaw.Minus4Point5Db => (
                (float)Math.Sqrt(powerLeft * linearLeft), (float)Math.Sqrt(powerRight * linearRight)),
            EnginePanLaw.Minus6Db => ((float)linearLeft, (float)linearRight),
            _ => ((float)(Math.Sqrt(2) * powerLeft), (float)(Math.Sqrt(2) * powerRight))
        };
    }

    /// <summary>
    ///     One channel of a dual pan: the law's shape with a hard pan at unity, so −1 and 1 pass a stereo signal as it is.
    /// </summary>
    public static (float Left, float Right) DualGains(float pan, EnginePanLaw law) =>
        pan switch {
            <= -1f => (1f, 0f),
            >= 1f => (0f, 1f),
            _ => PanGains(pan, false, law == EnginePanLaw.Minus3DbCompensated ? EnginePanLaw.Minus3Db : law)
        };

    static (float Left, float Right) Balance(float pan) => (pan > 0 ? 1f - pan : 1f, pan < 0 ? 1f + pan : 1f);

    /// <summary>Reader (telemetry): the peaks since the previous call.</summary>
    public (float Left, float Right) TakePeaks() {
        var peaks = (Volatile.Read(ref publishedLeft), Volatile.Read(ref publishedRight));
        Interlocked.Increment(ref resetRequested);
        return peaks;
    }

    /// <summary>
    ///     Reader (telemetry, Phase 10): the peaks since the previous call, and each channel's when the strip's meter
    ///     has more than two (a surround output, a surround bus or master): their count (0 for a mono or stereo meter)
    ///     with the peaks in <paramref name="channels" />.
    /// </summary>
    public (float Left, float Right) TakePeaks(Span<float> channels, out int count) {
        count = Math.Min(Volatile.Read(ref meterChannels), channels.Length);
        for (var c = 0; c < count; c++) {
            channels[c] = Volatile.Read(ref publishedChannels[c]);
        }

        return TakePeaks();
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
        if (meterChannels != 0) {
            Volatile.Write(ref meterChannels, 0); // a mono or stereo meter again
        }
    }

    /// <summary>
    ///     Audio thread (Phase 10): adds a block's post-fader peaks per channel (more than two: a surround meter; channels
    ///     0 and 1 are also the left and right peaks).
    /// </summary>
    internal void MeterChannels(ReadOnlySpan<float> peaks) {
        if (peaks.Length <= 2) {
            Meter(peaks[0], peaks.Length > 1 ? peaks[1] : peaks[0]);
            return;
        }

        var request = Volatile.Read(ref resetRequested);
        if (request != resetSeen) {
            resetSeen = request;
            accumulatedLeft = accumulatedRight = 0;
            Array.Clear(accumulatedChannels);
        }

        var n = Math.Min(peaks.Length, accumulatedChannels.Length);
        for (var c = 0; c < n; c++) {
            accumulatedChannels[c] = Math.Max(accumulatedChannels[c], peaks[c]);
            Volatile.Write(ref publishedChannels[c], accumulatedChannels[c]);
        }

        accumulatedLeft = accumulatedChannels[0];
        accumulatedRight = accumulatedChannels[1];
        Volatile.Write(ref publishedLeft, accumulatedLeft);
        Volatile.Write(ref publishedRight, accumulatedRight);
        Volatile.Write(ref meterChannels, n);
    }
}
