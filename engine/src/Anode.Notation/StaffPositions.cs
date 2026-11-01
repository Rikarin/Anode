using Anode.Contracts;

namespace Anode.Notation;

/// <summary>Where a pitch sits on a staff: its staff position (diatonic steps above the bottom line) and written spelling.</summary>
public sealed record StaffPlacement(int StaffPosition, ScorePitch Written);

/// <summary>
///     Staff position ↔ pitch for a clef, key and transposition (docs/10-phase-9.md, "Score edits"): what
///     <c>moveScoreNotes</c> and <c>addScoreNote</c> are converted with. Pitches in and out are concert MIDI pitches;
///     written = concert + <c>transpose</c>; the key is the concert key (a transposing staff's written key follows).
///     Staff positions are MEI's <c>loc</c>: 0 the bottom line, 8 the top line, negative below. An octave clef's octave
///     is the clef's (a treble 8vb staff's bottom line is E3), and the percussion clef and no clef read as treble.
/// </summary>
public static class StaffPositions {
    /// <summary>
    ///     The concert pitch at <paramref name="staffPosition" /> on a <paramref name="clef" /> staff: the written note of
    ///     the key there (with <paramref name="accidental" /> forcing its alteration), less <paramref name="transpose" />.
    /// </summary>
    public static int PitchAt(
        Clef clef,
        int transpose,
        KeyEvent key,
        int staffPosition,
        Accidental? accidental = null
    ) {
        var written = WrittenKey(KeyOf(key), transpose);
        var diatonic = Spelling.BottomLine(clef) + staffPosition;
        var step = (diatonic % 7 + 7) % 7;
        var octave = (int)Math.Floor(diatonic / 7.0);
        var alter = accidental is { } a ? Spelling.AlterOf(a) : Spelling.KeyAlter(written.Fifths, step);
        return Spelling.PitchOf(new((PitchStep)step, alter, octave)) - transpose;
    }

    /// <summary>Where <paramref name="pitch" /> is written on a <paramref name="clef" /> staff, spelled in the key.</summary>
    public static StaffPlacement PlacementOf(Clef clef, int transpose, KeyEvent key, int pitch) =>
        PlacementOf(clef, WrittenKey(KeyOf(key), transpose), pitch + transpose);

    /// <summary>
    ///     The concert pitch <paramref name="steps" /> diatonic steps above (below when negative) <paramref name="pitch" />
    ///     in the written key, as dragging a note does (<paramref name="accidental" /> forcing the alteration). The note
    ///     moves from its spelling in the key (an F♯ in C major moves up to G).
    /// </summary>
    public static int Step(int pitch, int steps, int transpose, KeyEvent key, Accidental? accidental = null) {
        var writtenKey = WrittenKey(KeyOf(key), transpose);
        var from = Spelling.Spell(pitch + transpose, writtenKey);
        var diatonic = Spelling.DiatonicOf(from) + steps;
        var step = (diatonic % 7 + 7) % 7;
        var octave = (int)Math.Floor(diatonic / 7.0);
        var alter = accidental is { } a ? Spelling.AlterOf(a) : Spelling.KeyAlter(writtenKey.Fifths, step);
        return Spelling.PitchOf(new((PitchStep)step, alter, octave)) - transpose;
    }

    /// <summary>A project key as a key signature (fifths and mode): F♯ major, D♭ major, E♭ minor, B♭ minor, G♯ minor.</summary>
    public static ScoreKey KeyOf(KeyEvent key) => Spelling.KeyOf(key);

    /// <summary>
    ///     The written key of a staff transposed by <paramref name="transpose" /> semitones (a B♭ clarinet's, +2, in
    ///     concert C is D major), kept within six accidentals (C♯ major is written D♭).
    /// </summary>
    public static ScoreKey WrittenKey(ScoreKey concert, int transpose) => Spelling.Transpose(concert, transpose);

    /// <summary>
    ///     A transposition's diatonic steps (MEI's <c>trans.diat</c> is its negation, with <c>trans.semi</c> the negated
    ///     semitones): +2 is 1, +7 is 4, +9 is 5, +14 is 8.
    /// </summary>
    public static int DiatonicSteps(int transpose) => Spelling.DiatonicSteps(transpose);

    /// <summary>
    ///     The pitch a percussion staff's drum map puts at <paramref name="staffPosition" /> (its first entry there), or
    ///     null when nothing is mapped there (<c>addScoreNote</c> on a drum staff).
    /// </summary>
    public static int? DrumPitchAt(IReadOnlyList<DrumMapEntry> drumMap, int staffPosition) {
        foreach (var entry in drumMap) {
            if (entry.StaffPosition == staffPosition) {
                return entry.Pitch;
            }
        }

        return null;
    }

    /// <summary>A written pitch's placement on a clef, spelled in the written key.</summary>
    internal static StaffPlacement PlacementOf(Clef clef, ScoreKey writtenKey, int writtenPitch) {
        var spelled = Spelling.Spell(writtenPitch, writtenKey);
        return new(PositionOf(clef, spelled), spelled);
    }

    internal static int PositionOf(Clef clef, ScorePitch written) =>
        Spelling.DiatonicOf(written) - Spelling.BottomLine(clef);

    /// <summary>The written pitch (natural) at a staff position of a clef.</summary>
    internal static ScorePitch WrittenAt(Clef clef, int staffPosition) {
        var diatonic = Spelling.BottomLine(clef) + staffPosition;
        return new((PitchStep)((diatonic % 7 + 7) % 7), 0, (int)Math.Floor(diatonic / 7.0));
    }
}
