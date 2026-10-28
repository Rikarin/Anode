namespace Anode.Dsp.Analysis;

/// <summary>
///     A file's beats (docs/06-phase-5.md, "Analysis"): its average tempo, whether the tempo varies, its beats and
///     downbeats in frames (ascending), and a confidence 0…1.
/// </summary>
public sealed record BeatTrack(double Bpm, bool Variable, long[] Beats, long[] Downbeats, double Confidence);

/// <summary>
///     A pitch curve: one value per <see cref="Hop" /> frames from frame 0, in MIDI cents (6900 is A4; NaN where
///     unvoiced), and the voicing probability 0…1 of each.
/// </summary>
public sealed record PitchTrack(int Hop, float[] Cents, float[] Voicing);

/// <summary>A note found in a pitch curve: frames <c>startFrame</c>…<c>endFrame</c>, its pitch in MIDI cents.</summary>
public sealed record NoteSegment(long StartFrame, long EndFrame, double Cents, double Confidence);

/// <summary>
///     Transient detection: spectral flux with an adaptive threshold. Deterministic, and about 50× real time or better
///     on one core.
/// </summary>
public static class Transients {
    /// <summary>The frames of the onsets in <paramref name="mono" />, ascending; <paramref name="sensitivity" /> 0…1.</summary>
    public static long[] Detect(ReadOnlySpan<float> mono, int sampleRate, double sensitivity = 0.5) =>
        throw Pending.WorkstreamD();
}

/// <summary>
///     Beat tracking: an onset envelope, a tempo estimate and a dynamic-programming beat tracker, plus a downbeat
///     estimate and whether the tempo varies. Deterministic.
/// </summary>
public static class BeatTracker {
    public static BeatTrack Track(ReadOnlySpan<float> mono, int sampleRate) => throw Pending.WorkstreamD();
}

/// <summary>Pitch tracking: pYIN, with voicing. Deterministic.</summary>
public static class PitchTracker {
    /// <summary>The pitch curve of <paramref name="mono" /> at one value per <paramref name="hop" /> frames (about 5 ms).</summary>
    public static PitchTrack Track(ReadOnlySpan<float> mono, int sampleRate, int hop) => throw Pending.WorkstreamD();
}

/// <summary>Note segmentation: stable voiced runs of a pitch curve. Deterministic.</summary>
public static class NoteSegmenter {
    public static NoteSegment[] Segment(PitchTrack track, int sampleRate) => throw Pending.WorkstreamD();
}

/// <summary>
///     Silence detection (Strip Silence): an RMS gate. Deterministic, and about 50× real time or better on one core.
/// </summary>
public static class Silence {
    /// <summary>
    ///     The non-silent ranges of <paramref name="mono" /> (start inclusive, end exclusive, in frames, ascending): above
    ///     <paramref name="thresholdDb" /> for at least <paramref name="minTimeMs" />, widened by
    ///     <paramref name="preAttackMs" /> before and <paramref name="postReleaseMs" /> after, merged where they meet.
    /// </summary>
    public static (long Start, long End)[] Detect(
        ReadOnlySpan<float> mono,
        int sampleRate,
        double thresholdDb,
        double minTimeMs,
        double preAttackMs,
        double postReleaseMs
    ) =>
        throw Pending.WorkstreamD();
}

/// <summary>Mixes interleaved audio down to mono, the input every analysis takes.</summary>
public static class Downmix {
    /// <summary>
    ///     Averages the <paramref name="channels" /> channels of <paramref name="interleaved" /> into
    ///     <paramref name="mono" /> (which holds at least its frames).
    /// </summary>
    public static void ToMono(ReadOnlySpan<float> interleaved, int channels, Span<float> mono) {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        var frames = interleaved.Length / channels;
        if (mono.Length < frames) {
            throw new ArgumentException("The mono buffer is shorter than the input.", nameof(mono));
        }

        var scale = 1f / channels;
        for (var n = 0; n < frames; n++) {
            var sum = 0f;
            for (var c = 0; c < channels; c++) {
                sum += interleaved[n * channels + c];
            }

            mono[n] = sum * scale;
        }
    }

    /// <summary>The mono mix of a file's channels (as <c>WavData.Channels</c> holds them).</summary>
    public static float[] ToMono(float[][] channels) {
        ArgumentOutOfRangeException.ThrowIfZero(channels.Length);
        var mono = new float[channels[0].Length];
        var scale = 1f / channels.Length;
        foreach (var channel in channels) {
            for (var n = 0; n < mono.Length; n++) {
                mono[n] += channel[n] * scale;
            }
        }

        return mono;
    }
}

/// <summary>
///     The analyses whose contract is fixed (docs/06-phase-5.md) but whose implementation is still to come. Every use
///     goes away with the analysis workstream (D).
/// </summary>
static class Pending {
    public static NotImplementedException WorkstreamD() => new("Phase 5 workstream D");
}
