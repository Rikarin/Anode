namespace Anode.Dsp.Editing;

/// <summary>
///     The Audio File Editor's operations (docs/08-phase-7.md, "Audio File Editor"), over a file's channels as
///     <c>WavData.Channels</c> holds them (<c>channels[c][frame]</c>, all the same length). Ranges are frames
///     <c>start</c> (inclusive) … <c>end</c> (exclusive), applied to every channel; a range
///     outside the file is an <see cref="ArgumentOutOfRangeException" />. The in-place operations change the arrays;
///     <see cref="Trim" /> and <see cref="ConvertRate" /> return new ones. Deterministic.
/// </summary>
public static class AudioEdits {
    /// <summary>Scales the range so its peak (over every channel) is <paramref name="peakDb" /> dBFS; silence stays.</summary>
    public static void Normalize(float[][] channels, long start, long end, double peakDb) =>
        throw Pending.WorkstreamE();

    /// <summary>Changes the range's level by <paramref name="gainDb" /> dB (no clipping: floats may exceed 1).</summary>
    public static void Gain(float[][] channels, long start, long end, double gainDb) => throw Pending.WorkstreamE();

    /// <summary>
    ///     Fades the range in from silence: the gain at position x (0…1 across the range) is x^(2^(−2c)) for curve
    ///     <paramref name="curve" /> c (−1…1), as a region's fade-in.
    /// </summary>
    public static void FadeIn(float[][] channels, long start, long end, double curve) => throw Pending.WorkstreamE();

    /// <summary>Fades the range out to silence, the mirror of <see cref="FadeIn" />.</summary>
    public static void FadeOut(float[][] channels, long start, long end, double curve) => throw Pending.WorkstreamE();

    public static void Reverse(float[][] channels, long start, long end) => throw Pending.WorkstreamE();

    /// <summary>Inverts the range's polarity.</summary>
    public static void Invert(float[][] channels, long start, long end) => throw Pending.WorkstreamE();

    public static void Silence(float[][] channels, long start, long end) => throw Pending.WorkstreamE();

    /// <summary>Subtracts each channel's mean over the range from the range.</summary>
    public static void RemoveDc(float[][] channels, long start, long end) => throw Pending.WorkstreamE();

    /// <summary>The range alone, as new channel arrays.</summary>
    public static float[][] Trim(float[][] channels, long start, long end) => throw Pending.WorkstreamE();

    /// <summary>
    ///     The whole file converted from <paramref name="fromRate" /> to <paramref name="toRate" /> Hz with the bounce
    ///     resampler, as new channel arrays (round(frames × toRate ÷ fromRate) frames).
    /// </summary>
    public static float[][] ConvertRate(float[][] channels, int fromRate, int toRate) => throw Pending.WorkstreamE();
}

/// <summary>Zero crossings, for the Audio File Editor's snap.</summary>
public static class ZeroCrossings {
    /// <summary>
    ///     The frame nearest <paramref name="frame" /> (within <paramref name="maxDistance" /> frames either way) where
    ///     <paramref name="mono" /> crosses zero (the frame after a sign change, or a frame that is exactly 0); −1 when
    ///     there is none.
    /// </summary>
    public static long Nearest(ReadOnlySpan<float> mono, long frame, long maxDistance) => throw Pending.WorkstreamE();

    /// <summary>Every zero crossing in frames <paramref name="start" />…<paramref name="end" />, ascending.</summary>
    public static long[] Find(ReadOnlySpan<float> mono, long start, long end) => throw Pending.WorkstreamE();
}

/// <summary>A loop candidate: frames <c>StartFrame</c>…<c>EndFrame</c> (exclusive), scored 0…1 (1: seamless).</summary>
public sealed record LoopCandidate(long StartFrame, long EndFrame, double Score);

/// <summary>
///     Loop search: candidate loop ends correlated with the loop start, scored for continuity (waveform and slope at
///     the seam). Deterministic.
/// </summary>
public static class LoopSearch {
    /// <summary>
    ///     The <paramref name="count" /> best loops inside frames <paramref name="start" />…<paramref name="end" /> of
    ///     <paramref name="mono" />, at least <paramref name="minFrames" /> long, best first (no two within 1 ms of each
    ///     other at both ends).
    /// </summary>
    public static LoopCandidate[] Find(
        ReadOnlySpan<float> mono,
        int sampleRate,
        long start,
        long end,
        int count,
        long minFrames
    ) =>
        throw Pending.WorkstreamE();
}

/// <summary>An onset found for a groove template: its frame and its strength 0…1 (1: the strongest of the file).</summary>
public sealed record GrooveOnset(long Frame, double Strength);

/// <summary>
///     Groove extraction from audio: the onsets of a file (Phase 5's <c>Transients</c>) with their strengths (peak level
///     over the first 20 ms after each). The model turns them into a template with the project's tempo map
///     (<c>Grooves.FromOnsets</c>).
/// </summary>
public static class GrooveFromAudio {
    public static GrooveOnset[] Onsets(ReadOnlySpan<float> mono, int sampleRate) => throw Pending.WorkstreamE();
}

/// <summary>
///     The editing algorithms whose contract is fixed (docs/08-phase-7.md) but whose implementation is still to come.
///     Every use goes away with the editing algorithms workstream (E).
/// </summary>
static class Pending {
    public static NotImplementedException WorkstreamE() => new("Phase 7 workstream E");
}
