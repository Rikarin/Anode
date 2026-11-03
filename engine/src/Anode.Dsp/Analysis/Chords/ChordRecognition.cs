namespace Anode.Dsp.Analysis.Chords;

/// <summary>A note Chord ID hears: its project ticks, pitch and velocity (MIDI regions' notes, already in place).</summary>
public readonly record struct ChordNote(long StartTick, long LengthTicks, int Pitch, int Velocity);

/// <summary>
///     A chord found from <c>Start</c> to <c>End</c> (ticks for MIDI, frames for audio): its root pitch class (0 C … 11),
///     quality (an index of <see cref="ChordTemplates.Qualities" />), slash bass pitch class (−1: none, or the root),
///     and a confidence 0…1.
/// </summary>
public sealed record RecognisedChord(long Start, long End, int Root, int Quality, int Bass, double Confidence);

/// <summary>A key found: its tonic pitch class (0 C … 11), major or minor, and a confidence 0…1.</summary>
public sealed record DetectedMusicalKey(int Tonic, bool Minor, double Confidence);

/// <summary>
///     Chord recognition from MIDI (docs/adr/0014-session-players-and-chord-id.md; workstream D): notes weighted by
///     duration and velocity per beat, matched against <see cref="ChordTemplates" />, the bass from the lowest note,
///     smoothed by a Viterbi pass over the beats with a change penalty and the key (<c>key</c>, null:
///     estimated). Deterministic.
/// </summary>
/// <remarks>
///     A note counts in a beat by how much of the beat it sounds, times 0.5 + 0.5 × velocity ÷ 127. The bass is the
///     lowest note sounding through at least a quarter of the beat (else the lowest sounding at all). Scoring and
///     smoothing are <see cref="ChordDecoder" />'s (docs/generative/README.md).
/// </remarks>
public static class MidiChordRecognizer {
    /// <summary>
    ///     The chords over <paramref name="beats" /> (ascending beat ticks, the last the end of the range), merged where
    ///     neighbouring beats hold the same chord, none shorter than <paramref name="resolutionTicks" />.
    /// </summary>
    public static IReadOnlyList<RecognisedChord> Recognise(
        IReadOnlyList<ChordNote> notes,
        IReadOnlyList<long> beats,
        long resolutionTicks,
        DetectedMusicalKey? key = null
    ) {
        if (beats.Count < 2) {
            return [];
        }

        key ??= KeyDetector.FromNotes(notes);
        var frames = new ChordFrame[beats.Count - 1];
        for (var b = 0; b + 1 < beats.Count; b++) {
            frames[b] = Frame(notes, beats[b], beats[b + 1]);
        }

        var path = ChordDecoder.Decode(frames, key);
        return ChordDecoder.Merge(frames, beats, path, resolutionTicks);
    }

    static ChordFrame Frame(IReadOnlyList<ChordNote> notes, long start, long end) {
        var chroma = new double[12];
        var length = Math.Max(1, end - start);
        int lowest = int.MaxValue, lowestHeld = int.MaxValue;
        foreach (var note in notes) {
            var overlap = Math.Min(end, note.StartTick + note.LengthTicks) - Math.Max(start, note.StartTick);
            if (overlap <= 0) {
                continue;
            }

            chroma[note.Pitch % 12] += overlap / (double)length * (0.5 + 0.5 * Math.Clamp(note.Velocity, 1, 127) / 127);
            lowest = Math.Min(lowest, note.Pitch);
            if (overlap * 4 >= length) {
                lowestHeld = Math.Min(lowestHeld, note.Pitch);
            }
        }

        var bass = lowestHeld != int.MaxValue ? lowestHeld : lowest;
        return new(chroma, bass == int.MaxValue ? -1 : bass % 12, bass == int.MaxValue);
    }
}

/// <summary>
///     Chord recognition from audio (workstream D): a harmonic chromagram (constant-Q style bins from Anode's FFT, the
///     tuning estimated, percussive energy suppressed by median filtering), beat-synchronous on <c>beats</c> (Phase 5's
///     beat tracker), the same templates as MIDI and an HMM. Deterministic and allocation-bounded by the input's length.
/// </summary>
/// <remarks>
///     The chromagram is <see cref="Chromagram" />'s; each beat's chroma is the mean of the frames centred in it, its bass
///     the strongest pitch class of the low register's (<see cref="Chromagram.BassOf" />). Decoding is
///     <see cref="ChordDecoder" />'s. With fewer than two <c>beats</c>, Phase 5's <see cref="BeatTracker" /> finds them
///     (and the end of the audio closes the last).
/// </remarks>
public static class AudioChordRecognizer {
    /// <summary>
    ///     The chords of <paramref name="mono" /> (at <paramref name="sampleRate" />) over <paramref name="beats" />
    ///     (ascending frames, the last the end), positions in frames.
    /// </summary>
    public static IReadOnlyList<RecognisedChord> Recognise(
        ReadOnlySpan<float> mono,
        float sampleRate,
        IReadOnlyList<long> beats,
        DetectedMusicalKey? key = null
    ) {
        if (beats.Count < 2) {
            var tracked = BeatTracker.Track(mono, (int)Math.Round(sampleRate)).Beats;
            beats = tracked.Length == 0 ? [] : [..tracked, mono.Length];
            if (beats.Count < 2) {
                return [];
            }
        }

        var chromagram = Chromagram.Compute(mono, sampleRate);
        key ??= KeyDetector.FromChroma(chromagram.Total());
        var frames = new ChordFrame[beats.Count - 1];
        for (var b = 0; b + 1 < beats.Count; b++) {
            frames[b] = chromagram.Frame(beats[b], beats[b + 1]);
        }

        var path = ChordDecoder.Decode(frames, key);
        return ChordDecoder.Merge(frames, beats, path, 1);
    }
}

/// <summary>Key detection (workstream D): a key profile correlation over pitch-class weights (Krumhansl-style).</summary>
/// <remarks>
///     The weights' Pearson correlation with the Krumhansl–Kessler major and minor profiles rotated to each tonic; the
///     best of the 24 keys, its confidence the correlation (0 when negative).
/// </remarks>
public static class KeyDetector {
    static readonly double[] MajorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    static readonly double[] MinorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    /// <summary>The key of <paramref name="notes" /> (weighted by duration and velocity).</summary>
    public static DetectedMusicalKey FromNotes(IReadOnlyList<ChordNote> notes) {
        var weights = new double[12];
        foreach (var note in notes) {
            weights[note.Pitch % 12] += Math.Max(0, note.LengthTicks)
                * (0.5 + 0.5 * Math.Clamp(note.Velocity, 1, 127) / 127);
        }

        return FromChroma(weights);
    }

    /// <summary>The key of <paramref name="mono" />, from its harmonic chromagram.</summary>
    public static DetectedMusicalKey FromAudio(ReadOnlySpan<float> mono, float sampleRate) =>
        FromChroma(Chromagram.Compute(mono, sampleRate).Total());

    /// <summary>The key of pitch-class weights (C major, confidence 0, with none).</summary>
    internal static DetectedMusicalKey FromChroma(double[] weights) {
        if (weights.Sum() <= 0) {
            return new(0, false, 0);
        }

        var best = new DetectedMusicalKey(0, false, double.NegativeInfinity);
        foreach (var minor in (ReadOnlySpan<bool>)[false, true]) {
            for (var tonic = 0; tonic < 12; tonic++) {
                var r = Correlation(weights, minor ? MinorProfile : MajorProfile, tonic);
                if (r > best.Confidence) {
                    best = new(tonic, minor, r);
                }
            }
        }

        return best with { Confidence = Math.Clamp(best.Confidence, 0, 1) };
    }

    static double Correlation(double[] weights, double[] profile, int tonic) {
        double mx = weights.Average(), my = profile.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (var pc = 0; pc < 12; pc++) {
            var x = weights[pc] - mx;
            var y = profile[((pc - tonic) % 12 + 12) % 12] - my;
            sxy += x * y;
            sxx += x * x;
            syy += y * y;
        }

        return sxx <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }
}
