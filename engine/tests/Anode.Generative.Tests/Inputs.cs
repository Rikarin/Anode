using Anode.Contracts;
using System.Runtime.CompilerServices;

namespace Anode.Generative.Tests;

/// <summary>Generation inputs for the tests: regions over sections and chord tracks, and random chord tracks.</summary>
static class Inputs {
    public const int Bar = 3_840;
    public const int Beat = 960;

    public static StyleLibrary Library { get; } = StyleLibrary.Builtin();
    public static SessionPlayerGenerator Generator { get; } = new(Library);

    public static IReadOnlyList<SignatureEvent> FourFour { get; } = [new(0, 4, 4)];
    public static IReadOnlyList<KeyEvent> CMajor { get; } = [new(0, 0, KeyMode.Major)];

    /// <summary>The style library's source folder (Styles/ of Anode.Generative).</summary>
    public static string StylesFolder([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "src", "Anode.Generative", "Styles"));

    public static SessionPlayerStyle Style(string id) => Library.Catalog.Styles.Single(s => s.Id == id);

    public static IEnumerable<SessionPlayerStyle> StylesOf(SessionPlayerKind player) =>
        Library.Catalog.Styles.Where(s => s.Player == player);

    /// <summary>
    ///     A style's first preset's parameters with the pad at (<paramref name="complexity" />,
    ///     <paramref name="intensity" />).
    /// </summary>
    public static SessionPlayerParameters Parameters(string styleId, double complexity = 0.5, double intensity = 0.5) {
        var preset = Style(styleId).Presets[0].Parameters;
        return preset with { Pad = new(complexity, intensity) };
    }

    /// <summary>Two four-bar sections: a verse, then a chorus.</summary>
    public static IReadOnlyList<ArrangementMarker> VerseChorus { get; } = [
        new("s1", "Verse", 0, 4 * Bar, "#888"), new("s2", "Chorus", 4 * Bar, 4 * Bar, "#888")
    ];

    /// <summary>The golden path's progression, a chord a bar: C, Am7, Fmaj7, G7, Em, A7, Dm7, G/B.</summary>
    public static ChordTrack Progression { get; } = new(
        [
            Chord(0, PitchStep.C, string.Empty), Chord(1, PitchStep.A, "m7"), Chord(2, PitchStep.F, "maj7"),
            Chord(3, PitchStep.G, "7"), Chord(4, PitchStep.E, "m"), Chord(5, PitchStep.A, "7"),
            Chord(6, PitchStep.D, "m7"), Chord(7, PitchStep.G, string.Empty, new(PitchStep.B, 0))
        ],
        []
    );

    static Chord Chord(int bar, PitchStep root, string quality, PitchName? bass = null) =>
        new($"c{bar}", bar * (long)Bar, new(root, 0), quality, bass);

    public static GenerationInput Input(
        SessionPlayerKind player,
        SessionPlayerParameters parameters,
        long seed = 1,
        long start = 0,
        long length = 8 * Bar,
        ChordTrack? chords = null,
        IReadOnlyList<ArrangementMarker>? sections = null,
        IReadOnlyList<long>? onsets = null,
        IReadOnlyList<SignatureEvent>? signatures = null
    ) =>
        new(
            player,
            parameters,
            seed,
            start,
            length,
            chords ?? Progression,
            sections ?? VerseChorus,
            signatures ?? FourFour,
            CMajor,
            onsets
        );

    static readonly PitchStep[] Steps = Enum.GetValues<PitchStep>();

    /// <summary>
    ///     A random chord track over <paramref name="bars" /> bars: a chord at a random beat or bar line, any root
    ///     (spelled with a random alteration), any quality, now and then a slash bass.
    /// </summary>
    public static ChordTrack RandomChords(Random random, int bars) {
        var chords = new List<Chord>();
        long tick = 0;
        while (tick < bars * (long)Bar) {
            var quality = ChordQualities.All[random.Next(ChordQualities.All.Count)];
            PitchName? bass = random.Next(5) == 0 ? new(Steps[random.Next(7)], random.Next(-1, 2)) : null;
            chords.Add(new($"c{chords.Count}", tick, new(Steps[random.Next(7)], random.Next(-1, 2)), quality, bass));
            tick += Beat * (long)random.Next(1, 9);
        }

        return new(chords, []);
    }

    /// <summary>The chord sounding at <paramref name="tick" /> of <paramref name="track" />.</summary>
    public static Chord ChordAt(ChordTrack track, long tick) =>
        track.Chords.LastOrDefault(c => c.Tick <= tick) ?? track.Chords[0];

    /// <summary>Whether <paramref name="pitch" /> is a tone of <paramref name="chord" /> (or its slash bass).</summary>
    public static bool Holds(Chord chord, int pitch) {
        var root = PitchSources.PitchClass(chord.Root);
        var pc = pitch % 12;
        return chord.Bass is { } bass && PitchSources.PitchClass(bass) == pc
            || ChordQualities.Intervals(chord.Quality).Any(i => (root + i) % 12 == pc);
    }
}
