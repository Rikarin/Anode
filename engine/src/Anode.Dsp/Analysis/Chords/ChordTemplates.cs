namespace Anode.Dsp.Analysis.Chords;

/// <summary>
///     The chord templates Chord ID matches (docs/adr/0014-session-players-and-chord-id.md): each quality as the chord
///     track spells it (the contract's <c>ChordQualities</c>, in its order; a test checks they agree) and its intervals
///     above the root. Real data, not a stub.
/// </summary>
public static class ChordTemplates {
    public static IReadOnlyList<string> Qualities { get; } = [
        string.Empty, "m", "7", "maj7", "m7", "dim", "m7b5", "aug", "sus2", "sus4", "6", "m6", "9"
    ];

    static readonly int[][] IntervalsByIndex = [
        [0, 4, 7], [0, 3, 7], [0, 4, 7, 10], [0, 4, 7, 11], [0, 3, 7, 10], [0, 3, 6], [0, 3, 6, 10], [0, 4, 8],
        [0, 2, 7], [0, 5, 7], [0, 4, 7, 9], [0, 3, 7, 9], [0, 4, 7, 10, 14]
    ];

    /// <summary>The intervals of quality <paramref name="index" /> of <see cref="Qualities" />, ascending.</summary>
    public static ReadOnlySpan<int> Intervals(int index) => IntervalsByIndex[index];
}
