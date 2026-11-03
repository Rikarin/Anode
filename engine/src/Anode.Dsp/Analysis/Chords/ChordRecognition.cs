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
    ) =>
        throw Pending.WorkstreamD();
}

/// <summary>
///     Chord recognition from audio (workstream D): a harmonic chromagram (constant-Q style bins from Anode's FFT, the
///     tuning estimated, percussive energy suppressed by median filtering), beat-synchronous on <c>beats</c> (Phase 5's
///     beat tracker), the same templates as MIDI and an HMM. Deterministic and allocation-bounded by the input's length.
/// </summary>
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
    ) =>
        throw Pending.WorkstreamD();
}

/// <summary>Key detection (workstream D): a key profile correlation over pitch-class weights (Krumhansl-style).</summary>
public static class KeyDetector {
    /// <summary>The key of <paramref name="notes" /> (weighted by duration and velocity).</summary>
    public static DetectedMusicalKey FromNotes(IReadOnlyList<ChordNote> notes) => throw Pending.WorkstreamD();

    /// <summary>The key of <paramref name="mono" />, from its harmonic chromagram.</summary>
    public static DetectedMusicalKey FromAudio(ReadOnlySpan<float> mono, float sampleRate) =>
        throw Pending.WorkstreamD();
}
