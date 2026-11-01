using Anode.Contracts;

namespace Anode.Interchange.MusicXml;

/// <summary>The mappings between the score model's vocabulary and MusicXML's, shared by the writer and the reader.</summary>
static class MusicXmlVocabulary {
    /// <summary>Semitones above C of each step, C to B.</summary>
    static readonly int[] StepSemitones = [0, 2, 4, 5, 7, 9, 11];

    /// <summary>Diatonic steps of a chromatic interval of 0…11 semitones (a tritone counts as an augmented fourth).</summary>
    static readonly int[] DiatonicOfSemitones = [0, 1, 1, 2, 2, 3, 3, 4, 5, 5, 6, 6];

    static readonly (string Kind, string Quality)[] Kinds = [
        ("major", ""),
        ("minor", "m"),
        ("augmented", "aug"),
        ("diminished", "dim"),
        ("dominant", "7"),
        ("major-seventh", "maj7"),
        ("minor-seventh", "m7"),
        ("diminished-seventh", "dim7"),
        ("augmented-seventh", "aug7"),
        ("half-diminished", "m7b5"),
        ("major-minor", "mMaj7"),
        ("major-sixth", "6"),
        ("minor-sixth", "m6"),
        ("dominant-ninth", "9"),
        ("major-ninth", "maj9"),
        ("minor-ninth", "m9"),
        ("dominant-11th", "11"),
        ("major-11th", "maj11"),
        ("minor-11th", "m11"),
        ("dominant-13th", "13"),
        ("major-13th", "maj13"),
        ("minor-13th", "m13"),
        ("suspended-second", "sus2"),
        ("suspended-fourth", "sus4"),
        ("power", "5")
    ];

    public static string StepName(PitchStep step) => "CDEFGAB"[(int)step].ToString();

    public static PitchStep? ParseStep(string? text) =>
        text?.Trim() switch {
            "C" or "c" => PitchStep.C,
            "D" or "d" => PitchStep.D,
            "E" or "e" => PitchStep.E,
            "F" or "f" => PitchStep.F,
            "G" or "g" => PitchStep.G,
            "A" or "a" => PitchStep.A,
            "B" or "b" => PitchStep.B,
            _ => null
        };

    /// <summary>The MIDI pitch of a spelled pitch (C4 is 60).</summary>
    public static int Midi(PitchStep step, int alter, int octave) => ((octave + 1) * 12) + StepSemitones[(int)step] + alter;

    /// <summary>MusicXML's <c>transpose</c> for a part written <paramref name="transpose" /> semitones above concert.</summary>
    public static (int Diatonic, int Chromatic, int OctaveChange) TransposeElement(int transpose) {
        // MusicXML's transpose goes from written to sounding: the opposite of the staff's.
        var sounding = -transpose;
        var octaves = sounding / 12;
        var chromatic = sounding % 12;
        var diatonic = Math.Sign(chromatic) * DiatonicOfSemitones[Math.Abs(chromatic)];
        return (diatonic, chromatic, octaves);
    }

    /// <summary>A key signature moved by <paramref name="semitones" />, kept to −6…6 flats or sharps where it can.</summary>
    public static int TransposeFifths(int fifths, int semitones) {
        var shift = ((semitones * 7 % 12) + 12) % 12;
        var result = fifths + shift;
        while (result > 6) {
            result -= 12;
        }

        while (result < -7) {
            result += 12;
        }

        return result;
    }

    /// <summary>The tonic pitch class (0 C … 11 B) of a key signature in a mode.</summary>
    public static int Tonic(int fifths, KeyMode mode) {
        var major = ((fifths * 7 % 12) + 12) % 12;
        return mode == KeyMode.Minor ? (major + 9) % 12 : major;
    }

    public static string TypeName(NoteValue value) =>
        value switch {
            NoteValue.Breve => "breve",
            NoteValue.Whole => "whole",
            NoteValue.Half => "half",
            NoteValue.Quarter => "quarter",
            NoteValue.Eighth => "eighth",
            NoteValue.Sixteenth => "16th",
            NoteValue.ThirtySecond => "32nd",
            NoteValue.SixtyFourth => "64th",
            _ => "128th"
        };

    /// <summary>A note value's length in quarters, as a fraction.</summary>
    public static Rational Quarters(NoteValue value) =>
        value switch {
            NoteValue.Breve => new(8, 1),
            NoteValue.Whole => new(4, 1),
            NoteValue.Half => new(2, 1),
            NoteValue.Quarter => new(1, 1),
            NoteValue.Eighth => new(1, 2),
            NoteValue.Sixteenth => new(1, 4),
            NoteValue.ThirtySecond => new(1, 8),
            NoteValue.SixtyFourth => new(1, 16),
            _ => new(1, 32)
        };

    /// <summary>A MusicXML note type's length in quarters (null: unknown).</summary>
    public static Rational? QuartersOfType(string? type) =>
        type?.Trim() switch {
            "maxima" => new(32, 1),
            "long" => new(16, 1),
            "breve" => new(8, 1),
            "whole" => new(4, 1),
            "half" => new(2, 1),
            "quarter" => new(1, 1),
            "eighth" => new(1, 2),
            "16th" => new(1, 4),
            "32nd" => new(1, 8),
            "64th" => new(1, 16),
            "128th" => new(1, 32),
            "256th" => new(1, 64),
            "512th" => new(1, 128),
            "1024th" => new(1, 256),
            _ => null
        };

    /// <summary>How many beams a value has (eighth 1 … 128th 5; 0 for a quarter and longer).</summary>
    public static int BeamLevels(NoteValue value) =>
        value switch {
            NoteValue.Eighth => 1,
            NoteValue.Sixteenth => 2,
            NoteValue.ThirtySecond => 3,
            NoteValue.SixtyFourth => 4,
            NoteValue.HundredTwentyEighth => 5,
            _ => 0
        };

    public static (string Sign, int? Line, int OctaveChange) ClefElement(Clef clef) =>
        clef switch {
            Clef.Bass => ("F", 4, 0),
            Clef.Alto => ("C", 3, 0),
            Clef.Tenor => ("C", 4, 0),
            Clef.Treble8vb => ("G", 2, -1),
            Clef.Treble8va => ("G", 2, 1),
            Clef.Bass8vb => ("F", 4, -1),
            Clef.Percussion => ("percussion", null, 0),
            Clef.None => ("none", null, 0),
            _ => ("G", 2, 0)
        };

    public static Clef ParseClef(string? sign, int? line, int octaveChange) =>
        sign?.Trim() switch {
            "G" => octaveChange switch {
                < 0 => Clef.Treble8vb,
                > 0 => Clef.Treble8va,
                _ => Clef.Treble
            },
            "F" => octaveChange < 0 ? Clef.Bass8vb : Clef.Bass,
            "C" => line == 4 ? Clef.Tenor : Clef.Alto,
            "percussion" => Clef.Percussion,
            "TAB" => Clef.Treble8vb,
            "none" => Clef.None,
            _ => Clef.Treble
        };

    public static string AccidentalName(Accidental accidental) =>
        accidental switch {
            Accidental.Natural => "natural",
            Accidental.Sharp => "sharp",
            Accidental.Flat => "flat",
            Accidental.DoubleSharp => "double-sharp",
            _ => "flat-flat"
        };

    public static string? NoteheadName(Notehead notehead) =>
        notehead switch {
            Notehead.Cross => "x",
            Notehead.CircleCross => "circle-x",
            Notehead.Diamond => "diamond",
            Notehead.Triangle => "triangle",
            Notehead.Slash => "slash",
            _ => null
        };

    /// <summary>MusicXML's <c>kind</c> for a chord quality as written after the root.</summary>
    public static string KindOf(string quality) {
        foreach (var (kind, known) in Kinds) {
            if (known == quality) {
                return kind;
            }
        }

        var q = quality.Trim();
        return q switch {
            "M" or "maj" or "Maj" or "Δ" => "major",
            "min" or "-" => "minor",
            "+" => "augmented",
            "°" or "o" => "diminished",
            "M7" or "Maj7" or "Δ7" => "major-seventh",
            "min7" or "-7" => "minor-seventh",
            "ø" or "ø7" => "half-diminished",
            "sus" => "suspended-fourth",
            _ when q.StartsWith("maj", StringComparison.Ordinal) => "major-seventh",
            _ when q.StartsWith("m7", StringComparison.Ordinal) => "minor-seventh",
            _ when q.StartsWith("dim", StringComparison.Ordinal) => "diminished",
            _ when q.StartsWith("aug", StringComparison.Ordinal) => "augmented",
            _ when q.StartsWith("sus2", StringComparison.Ordinal) => "suspended-second",
            _ when q.StartsWith("sus", StringComparison.Ordinal) => "suspended-fourth",
            _ when q.StartsWith('m') => "minor",
            _ when q.StartsWith('7') => "dominant",
            _ when q.StartsWith('9') => "dominant-ninth",
            _ when q.StartsWith("13", StringComparison.Ordinal) => "dominant-13th",
            _ when q.StartsWith("11", StringComparison.Ordinal) => "dominant-11th",
            _ when q.StartsWith('6') => "major-sixth",
            _ => "other"
        };
    }

    /// <summary>The quality written for a MusicXML <c>kind</c> without a <c>text</c> attribute.</summary>
    public static string QualityOf(string kind) {
        foreach (var (known, quality) in Kinds) {
            if (known == kind) {
                return quality;
            }
        }

        return string.Empty;
    }

    /// <summary>A dynamic's typical velocity, for notes under it (null: an accent that does not change the level).</summary>
    public static int? Velocity(string dynamic) =>
        dynamic switch {
            "pppp" => 16,
            "ppp" => 24,
            "pp" => 36,
            "p" => 50,
            "mp" => 64,
            "mf" => 80,
            "f" => 96,
            "ff" => 112,
            "fff" => 120,
            "ffff" => 127,
            _ => null
        };

    /// <summary>The General MIDI percussion name of a key (35…81), for a percussion part's instruments.</summary>
    public static string DrumName(int pitch) =>
        pitch is >= 35 and <= 81 ? GmDrums[pitch - 35] : $"Percussion {pitch}";

    static readonly string[] GmDrums = [
        "Acoustic Bass Drum", "Bass Drum 1", "Side Stick", "Acoustic Snare", "Hand Clap", "Electric Snare",
        "Low Floor Tom", "Closed Hi-Hat", "High Floor Tom", "Pedal Hi-Hat", "Low Tom", "Open Hi-Hat", "Low-Mid Tom",
        "Hi-Mid Tom", "Crash Cymbal 1", "High Tom", "Ride Cymbal 1", "Chinese Cymbal", "Ride Bell", "Tambourine",
        "Splash Cymbal", "Cowbell", "Crash Cymbal 2", "Vibraslap", "Ride Cymbal 2", "Hi Bongo", "Low Bongo",
        "Mute Hi Conga", "Open Hi Conga", "Low Conga", "High Timbale", "Low Timbale", "High Agogo", "Low Agogo",
        "Cabasa", "Maracas", "Short Whistle", "Long Whistle", "Short Guiro", "Long Guiro", "Claves", "Hi Wood Block",
        "Low Wood Block", "Mute Cuica", "Open Cuica", "Mute Triangle", "Open Triangle"
    ];
}
