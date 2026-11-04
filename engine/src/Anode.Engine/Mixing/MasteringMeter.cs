using Anode.Dsp.Dynamics;
using Anode.Dsp.Mastering;
using Anode.Dsp.Metering;
using Anode.Ipc.Telemetry;

namespace Anode.Engine.Mixing;

/// <summary>
///     What the host reports of a Mastering Assistant (docs/12-phase-11.md, <see cref="MasteringEntry" />): its output's
///     BS.1770 momentary, short-term and integrated loudness (Phase 4's meter; integrated since play started) and its
///     true peak since then (4× interpolated, as the limiters detect it), measured on the audio thread after the insert
///     runs; and the gain reductions its compressor and limiter report. Allocation-free once built.
/// </summary>
public sealed class MasteringMeter(IMasteringChain chain, double sampleRate) {
    /// <summary>What silence reads (the contract's −144).</summary>
    public const float SilenceDb = -144f;

    readonly LoudnessMeter loudness = new(sampleRate);

    // The last TruePeak.Taps samples of each channel, twice over (so the window from any start is contiguous).
    readonly float[] historyLeft = new float[2 * TruePeak.Taps];
    readonly float[] historyRight = new float[2 * TruePeak.Taps];
    int at;
    float peak;
    float momentary = SilenceDb;
    float shortTerm = SilenceDb;
    float integrated = SilenceDb;
    float truePeak = SilenceDb;
    bool rolling;

    /// <summary>The assistant, whose reductions the entry reads.</summary>
    public IMasteringChain Chain { get; } = chain;

    /// <summary>
    ///     Audio thread: measures a block of the insert's output; play starting (<paramref name="playing" /> after a
    ///     stop) starts the integrated loudness and the true peak afresh.
    /// </summary>
    public void Measure(ReadOnlySpan<float> left, ReadOnlySpan<float> right, bool playing) {
        if (playing && !rolling) {
            loudness.Reset();
            peak = 0;
        }

        rolling = playing;
        loudness.Process(left, right);
        peak = Math.Max(peak, Peak(left, historyLeft, at));
        peak = Math.Max(peak, Peak(right, historyRight, at));
        at = (at + left.Length) % TruePeak.Taps;
        Volatile.Write(ref momentary, Lufs(loudness.Momentary));
        Volatile.Write(ref shortTerm, Lufs(loudness.ShortTerm));
        Volatile.Write(ref integrated, Lufs(loudness.Integrated));
        Volatile.Write(ref truePeak, peak > 0 ? (float)(20 * Math.Log10(peak)) : SilenceDb);
    }

    /// <summary>Audio thread: silence (a render's start).</summary>
    public void Reset() {
        loudness.Reset();
        Array.Clear(historyLeft);
        Array.Clear(historyRight);
        peak = 0;
        rolling = false;
    }

    /// <summary>Any thread: the telemetry's entry.</summary>
    public MasteringEntry Reading() =>
        new() {
            Active = 1,
            MomentaryLufs = Volatile.Read(ref momentary),
            ShortTermLufs = Volatile.Read(ref shortTerm),
            IntegratedLufs = Volatile.Read(ref integrated),
            TruePeakDbtp = Volatile.Read(ref truePeak),
            CompressorReductionDb = Math.Min(0f, Chain.CompressorReductionDb),
            LimiterReductionDb = Math.Min(0f, Chain.LimiterReductionDb)
        };

    static float Lufs(float value) => value <= LoudnessMeter.FloorLufs ? SilenceDb : value;

    /// <summary>
    ///     The largest sample, and interpolated point at ¼, ½ and ¾ between samples, of <paramref name="block" />; the
    ///     last <see cref="TruePeak.Taps" /> samples it saw are kept in <paramref name="history" /> (twice, from
    ///     <paramref name="start" />, the oldest's place).
    /// </summary>
    static float Peak(ReadOnlySpan<float> block, float[] history, int start) {
        var kernels = TruePeak.Kernels;
        var taps = TruePeak.Taps;
        var top = 0f;
        var next = start;
        foreach (var x in block) {
            history[next] = history[next + taps] = x;
            next = next + 1 == taps ? 0 : next + 1;
            top = Math.Max(top, Math.Abs(x));
            var window = history.AsSpan(next, taps); // oldest first
            foreach (var kernel in kernels) {
                var sum = 0f;
                for (var j = 0; j < taps; j++) {
                    sum += kernel[j] * window[j];
                }

                top = Math.Max(top, Math.Abs(sum));
            }
        }

        return top;
    }
}
