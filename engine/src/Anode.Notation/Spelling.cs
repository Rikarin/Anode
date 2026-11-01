using Anode.Contracts;

namespace Anode.Notation;

/// <summary>
///     Pitch spelling (docs/notation/README.md, "Spelling"): keys as fifths, a pitch's spelling in a key, transposition
///     intervals and clefs' reference lines.
/// </summary>
static class Spelling {
    /// <summary>The pitch class of each step (C D E F G A B).</summary>
    static readonly int[] StepClass = [0, 2, 4, 5, 7, 9, 11];

    /// <summary>A major key's fifths by its tonic's pitch class (F♯ major rather than G♭; D♭ rather than C♯).</summary>
    static readonly int[] MajorFifths = [0, -5, 2, -3, 4, -1, 6, 1, -4, 3, -2, 5];

    /// <summary>A transposition's change of key in fifths, by semitones mod 12 (a minor second up is −5: C to D♭).</summary>
    static readonly int[] IntervalFifths = [0, -5, 2, -3, 4, -1, 6, 1, -4, 3, -2, 5];

    /// <summary>A transposition's diatonic steps, by semitones mod 12.</summary>
    static readonly int[] IntervalSteps = [0, 1, 1, 2, 2, 3, 3, 4, 5, 5, 6, 6];

    /// <summary>The order sharps are added in (F C G D A E B) as steps.</summary>
    static readonly int[] SharpOrder = [3, 0, 4, 1, 5, 2, 6];

    public static int StepIndex(PitchStep step) => (int)step;

    public static int ClassOf(PitchStep step) => StepClass[(int)step];

    /// <summary>A project key as a key signature: major by its tonic, minor by its relative major (E♭ minor, not D♯).</summary>
    public static ScoreKey KeyOf(KeyEvent key) {
        var tonic = ((key.Tonic % 12) + 12) % 12;
        if (key.Mode == KeyMode.Major) {
            return new(MajorFifths[tonic], KeyMode.Major);
        }

        var fifths = MajorFifths[(tonic + 3) % 12];
        return new(fifths == 6 ? -6 : fifths, KeyMode.Minor);
    }

    /// <summary>The written key of a staff transposed by <paramref name="semitones" />, kept within six accidentals.</summary>
    public static ScoreKey Transpose(ScoreKey key, int semitones) {
        if (semitones == 0) {
            return key;
        }

        var fifths = key.Fifths + IntervalFifths[((semitones % 12) + 12) % 12];
        while (fifths > 6) {
            fifths -= 12;
        }

        while (fifths < -6) {
            fifths += 12;
        }

        return key with { Fifths = fifths };
    }

    /// <summary>The diatonic steps of a transposition of <paramref name="semitones" /> (+14 is a ninth: 8).</summary>
    public static int DiatonicSteps(int semitones) {
        if (semitones < 0) {
            return -DiatonicSteps(-semitones);
        }

        return IntervalSteps[semitones % 12] + semitones / 12 * 7;
    }

    /// <summary>The alteration a key signature gives <paramref name="step" />.</summary>
    public static int KeyAlter(int fifths, int step) {
        for (var i = 0; i < Math.Abs(fifths) && i < 7; i++) {
            var altered = fifths > 0 ? SharpOrder[i] : SharpOrder[6 - i];
            if (altered == step) {
                return Math.Sign(fifths);
            }
        }

        return 0;
    }

    /// <summary>
    ///     How <paramref name="pitch" /> is spelled in <paramref name="key" />: the key's own notes; in minor its raised
    ///     sixth and seventh; naturals; then sharps in sharp keys and flats in flat keys (C major and A minor: C♯ E♭ F♯ G♯
    ///     B♭).
    /// </summary>
    public static ScorePitch Spell(int pitch, ScoreKey key) {
        var pc = ((pitch % 12) + 12) % 12;
        for (var step = 0; step < 7; step++) {
            var alter = KeyAlter(key.Fifths, step);
            if ((((StepClass[step] + alter) % 12) + 12) % 12 == pc) {
                return Of(pitch, step, alter);
            }
        }

        if (key.Mode == KeyMode.Minor) {
            // The tonic is three fifths above the relative major's.
            var (tonicStep, tonicAlter) = FromFifths(key.Fifths + 3);
            var tonicClass = StepClass[tonicStep] + tonicAlter;
            foreach (var (degreeBelow, semitones) in new[] { (1, 1), (2, 3) }) {
                if ((((tonicClass - semitones) % 12) + 12) % 12 == pc) {
                    var step = (tonicStep - degreeBelow + 7) % 7;
                    return Of(pitch, step, AlterFor(pc, step));
                }
            }
        }

        var natural = Array.IndexOf(StepClass, pc);
        if (natural >= 0) {
            return Of(pitch, natural, 0);
        }

        var sharp = key.Fifths > 0 || (key.Fifths == 0 && pc is 1 or 6 or 8);
        return sharp ? Of(pitch, Array.IndexOf(StepClass, pc - 1), 1) : Of(pitch, Array.IndexOf(StepClass, (pc + 1) % 12), -1);
    }

    /// <summary>The step and alteration at a position on the line of fifths (0 C, 1 G, −1 F).</summary>
    static (int Step, int Alter) FromFifths(int fifths) {
        // F C G D A E B are −1…5.
        var index = fifths + 1;
        var alter = (int)Math.Floor(index / 7.0);
        var step = SharpOrder[((index % 7) + 7) % 7];
        return (step, alter);
    }

    static int AlterFor(int pc, int step) {
        var alter = (((pc - StepClass[step]) % 12) + 12) % 12;
        return alter > 6 ? alter - 12 : alter;
    }

    /// <summary>A pitch written as <paramref name="step" /> with <paramref name="alter" />, its octave worked out.</summary>
    public static ScorePitch Of(int pitch, int step, int alter) {
        var octave = (int)Math.Floor((pitch - alter - StepClass[step]) / 12.0) - 1;
        return new((PitchStep)step, alter, octave);
    }

    /// <summary>The MIDI pitch of a written pitch.</summary>
    public static int PitchOf(ScorePitch written) => (written.Octave + 1) * 12 + StepClass[(int)written.Step] + written.Alter;

    /// <summary>Diatonic number: octave × 7 + step (C4 is 28).</summary>
    public static int DiatonicOf(ScorePitch written) => written.Octave * 7 + (int)written.Step;

    /// <summary>The diatonic number of a clef's bottom line (treble E4; the percussion and no clef read as treble).</summary>
    public static int BottomLine(Clef clef) =>
        clef switch {
            Clef.Bass => 2 * 7 + 4,
            Clef.Alto => 3 * 7 + 3,
            Clef.Tenor => 3 * 7 + 1,
            Clef.Treble8vb => 3 * 7 + 2,
            Clef.Treble8va => 5 * 7 + 2,
            Clef.Bass8vb => 1 * 7 + 4,
            _ => 4 * 7 + 2
        };

    /// <summary>The alteration an accidental forces.</summary>
    public static int AlterOf(Accidental accidental) =>
        accidental switch {
            Accidental.Sharp => 1,
            Accidental.Flat => -1,
            Accidental.DoubleSharp => 2,
            Accidental.DoubleFlat => -2,
            _ => 0
        };

    /// <summary>The accidental drawn for an alteration.</summary>
    public static Accidental AccidentalOf(int alter) =>
        alter switch {
            1 => Accidental.Sharp,
            -1 => Accidental.Flat,
            2 => Accidental.DoubleSharp,
            -2 => Accidental.DoubleFlat,
            _ => Accidental.Natural
        };
}
