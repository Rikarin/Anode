using Anode.Contracts;

namespace Anode.Notation;

/// <summary>
///     The factory staff styles as data (docs/10-phase-9.md; their ids are <see cref="StaffStyleIds" />), in
///     <see cref="StaffStyleIds.All" />'s order: <c>GET /library/staff-styles</c>. Every one displays at 1/16 with the
///     1/16-triplet grid (Logic's "16,24"); melodic single-line instruments make no voices from overlaps, keyboards,
///     guitars and strings make two per staff, drums one (simultaneous hits are chords).
/// </summary>
public static class FactoryStaffStyles {
    /// <summary>The straight display grid of every factory style: a 1/16.</summary>
    public const int DisplayQuantizeTicks = 240;

    /// <summary>The triplet display grid of every factory style: a 1/16 triplet (1/24).</summary>
    public const int TripletQuantizeTicks = 160;

    /// <summary>The General MIDI drum map of <see cref="StaffStyleIds.Drums" /> (positions on the five lines, 0 the bottom).</summary>
    public static IReadOnlyList<DrumMapEntry> GeneralMidiDrums { get; } = [
        new(35, 0, Notehead.Normal, "Acoustic Bass Drum"),
        new(36, 1, Notehead.Normal, "Bass Drum"),
        new(37, 5, Notehead.Cross, "Side Stick"),
        new(38, 5, Notehead.Normal, "Acoustic Snare"),
        new(39, 5, Notehead.Slash, "Hand Clap"),
        new(40, 5, Notehead.Normal, "Electric Snare"),
        new(41, 2, Notehead.Normal, "Low Floor Tom"),
        new(42, 9, Notehead.Cross, "Closed Hi-Hat"),
        new(43, 3, Notehead.Normal, "High Floor Tom"),
        new(44, -1, Notehead.Cross, "Pedal Hi-Hat"),
        new(45, 4, Notehead.Normal, "Low Tom"),
        new(46, 9, Notehead.CircleCross, "Open Hi-Hat"),
        new(47, 6, Notehead.Normal, "Low-Mid Tom"),
        new(48, 7, Notehead.Normal, "Hi-Mid Tom"),
        new(49, 10, Notehead.Cross, "Crash Cymbal 1"),
        new(50, 8, Notehead.Normal, "High Tom"),
        new(51, 8, Notehead.Cross, "Ride Cymbal 1"),
        new(52, 11, Notehead.Cross, "Chinese Cymbal"),
        new(53, 8, Notehead.Diamond, "Ride Bell"),
        new(54, 7, Notehead.Triangle, "Tambourine"),
        new(55, 11, Notehead.Diamond, "Splash Cymbal"),
        new(56, 7, Notehead.Triangle, "Cowbell"),
        new(57, 12, Notehead.Cross, "Crash Cymbal 2"),
        new(59, 7, Notehead.Cross, "Ride Cymbal 2")
    ];

    public static IReadOnlyList<StaffStyle> All { get; } = [
        Single(StaffStyleIds.Treble, "Treble", Clef.Treble, 0, 2),
        Single(StaffStyleIds.Bass, "Bass", Clef.Bass, 0, 2),
        new(
            StaffStyleIds.Piano,
            "Piano",
            [new(Clef.Treble, 0, 60, 2), new(Clef.Bass, 0, null, 2)],
            null,
            DisplayQuantizeTicks,
            TripletQuantizeTicks,
            true
        ),
        new(
            StaffStyleIds.Drums,
            "Drums",
            [new(Clef.Percussion, 0, null, 1)],
            GeneralMidiDrums,
            DisplayQuantizeTicks,
            TripletQuantizeTicks,
            false
        ),
        Single(StaffStyleIds.Guitar, "Guitar", Clef.Treble8vb, 0, 2),
        Single(StaffStyleIds.BassGuitar, "Bass Guitar", Clef.Bass8vb, 0, 1),
        Single(StaffStyleIds.ClarinetBb, "B♭ Clarinet", Clef.Treble, 2, 1),
        Single(StaffStyleIds.TrumpetBb, "B♭ Trumpet", Clef.Treble, 2, 1),
        Single(StaffStyleIds.HornF, "Horn in F", Clef.Treble, 7, 1),
        Single(StaffStyleIds.AltoSax, "Alto Sax", Clef.Treble, 9, 1),
        Single(StaffStyleIds.TenorSax, "Tenor Sax", Clef.Treble, 14, 1),
        Single(StaffStyleIds.Viola, "Viola", Clef.Alto, 0, 2),
        Single(StaffStyleIds.Cello, "Cello", Clef.Bass, 0, 2)
    ];

    /// <summary>The factory style <paramref name="id" />, or null.</summary>
    public static StaffStyle? Find(string id) {
        foreach (var style in All) {
            if (style.Id == id) {
                return style;
            }
        }

        return null;
    }

    /// <summary>
    ///     The style a track with <paramref name="styleId" /> uses: one of <paramref name="score" />'s own, else a factory
    ///     one; null or an unknown id gives <see cref="StaffStyleIds.Default" />.
    /// </summary>
    public static StaffStyle Resolve(ProjectScore score, string? styleId) {
        if (styleId is not null) {
            foreach (var style in score.StaffStyles) {
                if (style.Id == styleId) {
                    return style;
                }
            }

            if (Find(styleId) is { } factory) {
                return factory;
            }
        }

        return Find(StaffStyleIds.Default)!;
    }

    static StaffStyle Single(string id, string name, Clef clef, int transpose, int voices) =>
        new(id, name, [new(clef, transpose, null, voices)], null, DisplayQuantizeTicks, TripletQuantizeTicks, false);
}
