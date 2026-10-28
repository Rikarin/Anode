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

/// <summary>
///     A note found in a pitch curve: frames <c>startFrame</c> (inclusive) to <c>endFrame</c> (exclusive), its pitch in
///     MIDI cents (the median of its curve) and a confidence 0…1 (its mean voicing probability).
/// </summary>
public sealed record NoteSegment(long StartFrame, long EndFrame, double Cents, double Confidence);

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
