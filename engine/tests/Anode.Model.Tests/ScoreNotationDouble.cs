using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.Midi;

namespace Anode.Model.Tests;

/// <summary>
///     A small stand-in for the notation engine's staff positions and factory styles (docs/10-phase-9.md, workstream A),
///     enough for the score edits' tests and properties: pitches spelled by letter in the key signature, clefs by their
///     bottom line, a few factory styles. Shared (linked) by the model, server and interchange tests until A's engine
///     merges.
/// </summary>
public sealed class ScoreNotationDouble : IScoreNotation {
    public static ScoreNotationDouble Instance { get; } = new();

    static readonly int[] Naturals = [0, 2, 4, 5, 7, 9, 11];

    // Sharps are added F C G D A E B, flats B E A D G C F (letters 0…6 are C…B).
    static readonly int[] SharpOrder = [3, 0, 4, 1, 5, 2, 6];
    static readonly int[] FlatOrder = [6, 2, 5, 1, 4, 0, 3];

    /// <summary>The factory styles this stand-in knows (the others read as treble).</summary>
    public static IReadOnlyList<StaffStyle> Styles { get; } = [
        new(StaffStyleIds.Treble, "Treble", [new(Clef.Treble, 0, null, 1)], null, 0, 0, false),
        new(StaffStyleIds.Bass, "Bass", [new(Clef.Bass, 0, null, 1)], null, 0, 0, false),
        new(
            StaffStyleIds.Piano,
            "Piano",
            [new(Clef.Treble, 0, 60, 2), new(Clef.Bass, 0, null, 2)],
            null,
            240,
            160,
            true
        ),
        new(
            StaffStyleIds.Drums,
            "Drums",
            [new(Clef.Percussion, 0, null, 2)],
            [
                new(36, 1, Notehead.Normal, "Kick"), new(38, 5, Notehead.Normal, "Snare"),
                new(42, 9, Notehead.Cross, "Hi-hat")
            ],
            240,
            0,
            false
        ),
        new(StaffStyleIds.ClarinetBb, "B♭ Clarinet", [new(Clef.Treble, 2, null, 1)], null, 0, 0, false)
    ];

    public StaffStyle? FactoryStyle(string id) =>
        Styles.FirstOrDefault(s => s.Id == id)
        ?? (StaffStyleIds.All.Contains(id) ? Styles[0] with { Id = id, Name = id } : null);

    public int PitchAt(Clef clef, int transpose, KeyEvent key, int staffPosition, Accidental? accidental) {
        var index = BottomLine(clef) + staffPosition;
        return Written(index, key, accidental) - transpose;
    }

    public int Step(int pitch, int steps, int transpose, KeyEvent key, Accidental? accidental) =>
        Written(IndexOf(pitch + transpose, key) + steps, key, accidental) - transpose;

    /// <summary>The diatonic index (octave · 7 + letter, C4 = 28 + 7) of a clef's bottom line.</summary>
    static int BottomLine(Clef clef) =>
        clef switch {
            Clef.Bass => Index(4, 2), // G2
            Clef.Alto => Index(3, 3), // F3
            Clef.Tenor => Index(1, 3), // D3
            Clef.Treble8vb => Index(2, 3), // E3
            Clef.Treble8va => Index(2, 5), // E5
            Clef.Bass8vb => Index(4, 1), // G1
            _ => Index(2, 4) // E4
        };

    static int Index(int letter, int octave) => octave * 7 + letter;

    /// <summary>The pitch of a diatonic index: its letter as the key alters it, or as the accidental says.</summary>
    static int Written(int index, KeyEvent key, Accidental? accidental) {
        var octave = Math.DivRem(index, 7, out var letter);
        if (letter < 0) {
            letter += 7;
            octave--;
        }

        var alter = accidental switch {
            Accidental.Sharp => 1,
            Accidental.Flat => -1,
            Accidental.DoubleSharp => 2,
            Accidental.DoubleFlat => -2,
            Accidental.Natural => 0,
            _ => Alter(letter, key)
        };
        return Math.Clamp((octave + 1) * 12 + Naturals[letter] + alter, 0, 127);
    }

    /// <summary>The diatonic index of a pitch: its letter in the key, else the letter below raised.</summary>
    static int IndexOf(int pitch, KeyEvent key) {
        var octave = pitch / 12 - 1;
        var pc = pitch % 12;
        for (var letter = 6; letter >= 0; letter--) {
            var at = Naturals[letter] + Alter(letter, key);
            if ((at % 12 + 12) % 12 == pc) {
                return Index(letter, octave + (at >= 12 ? -1 : at < 0 ? 1 : 0));
            }
        }

        for (var letter = 6; letter >= 0; letter--) {
            if (Naturals[letter] <= pc) {
                return Index(letter, octave);
            }
        }

        return Index(0, octave);
    }

    static int Alter(int letter, KeyEvent key) {
        var fifths = StandardMidiFile.FifthsOf(key);
        return fifths > 0 && Array.IndexOf(SharpOrder, letter) < fifths ? 1
            : fifths < 0 && Array.IndexOf(FlatOrder, letter) < -fifths ? -1
            : 0;
    }
}
