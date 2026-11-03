using System.Text.Json.Serialization;

namespace Anode.Contracts;

// The chord track, Chord ID and Pitch Source (docs/12-phase-11.md, docs/adr/0014-session-players-and-chord-id.md). A
// chord holds from its tick to the next chord's; a region's key is its own (Region.key: set by the user or detected by
// Chord ID), else the project's key (settings.keys) at the region's start; Pitch Source transposes by the shortest
// interval from that key's tonic to the chord's root (−6…+5 semitones, PitchSources.RootInterval).

/// <summary>
///     The chord qualities the chord track, Chord ID and the players use: the strings written after a chord's root, as
///     the score's <c>ChordSymbol.quality</c> (docs/10-phase-9.md) spells them, with their intervals above the root.
///     Chord ID matches every quality of <see cref="All" /> (ADR-0014's templates); a slash bass is the chord's
///     <c>bass</c>, not part of its quality.
/// </summary>
public static class ChordQualities {
    public const string Major = "";
    public const string Minor = "m";
    public const string Seventh = "7";
    public const string MajorSeventh = "maj7";
    public const string MinorSeventh = "m7";
    public const string Diminished = "dim";
    public const string HalfDiminished = "m7b5";
    public const string Augmented = "aug";
    public const string SuspendedSecond = "sus2";
    public const string SuspendedFourth = "sus4";
    public const string Sixth = "6";
    public const string MinorSixth = "m6";
    public const string Ninth = "9";

    /// <summary>Every quality, in this order (the order Chord ID's templates and the editor's quality menu use).</summary>
    public static IReadOnlyList<string> All { get; } = [
        Major, Minor, Seventh, MajorSeventh, MinorSeventh, Diminished, HalfDiminished, Augmented, SuspendedSecond,
        SuspendedFourth, Sixth, MinorSixth, Ninth
    ];

    static readonly Dictionary<string, int[]> IntervalsOf = new(StringComparer.Ordinal) {
        [Major] = [0, 4, 7],
        [Minor] = [0, 3, 7],
        [Seventh] = [0, 4, 7, 10],
        [MajorSeventh] = [0, 4, 7, 11],
        [MinorSeventh] = [0, 3, 7, 10],
        [Diminished] = [0, 3, 6],
        [HalfDiminished] = [0, 3, 6, 10],
        [Augmented] = [0, 4, 8],
        [SuspendedSecond] = [0, 2, 7],
        [SuspendedFourth] = [0, 5, 7],
        [Sixth] = [0, 4, 7, 9],
        [MinorSixth] = [0, 3, 7, 9],
        [Ninth] = [0, 4, 7, 10, 14]
    };

    public static bool IsKnown(string quality) => IntervalsOf.ContainsKey(quality);

    /// <summary>
    ///     The chord's tones as semitones above its root, ascending (a ninth is 14); empty for a quality not in
    ///     <see cref="All" />.
    /// </summary>
    public static IReadOnlyList<int> Intervals(string quality) =>
        IntervalsOf.TryGetValue(quality, out var intervals) ? intervals : [];
}

/// <summary>
///     A chord of the chord track at project tick <c>tick</c> (it holds until the next chord): its root, its quality
///     (<see cref="ChordQualities" />) and an optional slash bass ("G/B"). Chords are in tick order, at most one per tick.
/// </summary>
public sealed record Chord(string Id, long Tick, PitchName Root, string Quality, PitchName? Bass);

/// <summary>A scale of the chord track (its key and scale changes; Logic's).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScaleKind>))]
public enum ScaleKind {
    [JsonStringEnumMemberName("major")]
    Major,

    /// <summary>Natural minor (Aeolian).</summary>
    [JsonStringEnumMemberName("minor")]
    Minor,

    [JsonStringEnumMemberName("harmonicMinor")]
    HarmonicMinor,

    [JsonStringEnumMemberName("melodicMinor")]
    MelodicMinor,

    [JsonStringEnumMemberName("dorian")]
    Dorian,

    [JsonStringEnumMemberName("phrygian")]
    Phrygian,

    [JsonStringEnumMemberName("lydian")]
    Lydian,

    [JsonStringEnumMemberName("mixolydian")]
    Mixolydian,

    [JsonStringEnumMemberName("locrian")]
    Locrian
}

/// <summary>
///     A key and scale change of the chord track at <c>tick</c> (it holds until the next): the scale the players take
///     passing and approach notes from, and Chord ID's key prior. Before the first (or with none) the chord track's key
///     is the project's key track (<c>settings.keys</c>), a major key read as <c>major</c>, a minor one as <c>minor</c>.
/// </summary>
public sealed record ScaleChange(long Tick, PitchName Tonic, ScaleKind Scale);

/// <summary>
///     The chord track (<c>Project.chordTrack</c>): a global track of chords and of key and scale changes, shown with
///     the other global tracks. Session Players and Pitch Source follow it; Chord ID writes it. Empty in every project
///     from before Phase 11.
/// </summary>
public sealed record ChordTrack(IReadOnlyList<Chord> Chords, IReadOnlyList<ScaleChange> Scales) {
    public static ChordTrack Empty { get; } = new([], []);
}

/// <summary>How a region follows the chord track (<c>pitchSource</c> on MIDI and audio regions).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PitchSourceMode>))]
public enum PitchSourceMode {
    /// <summary>The region plays as it is (every region from before Phase 11).</summary>
    [JsonStringEnumMemberName("off")]
    Off,

    /// <summary>
    ///     Each chord segment transposes by the shortest interval from the region's key tonic to the chord's root
    ///     (−6…+5). MIDI follows at compile time; audio through the region's pitch shift (Phase 5's, or Flex Pitch when
    ///     its track's flex is Flex Pitch), one transposition per chord segment, crossfaded at the changes.
    /// </summary>
    [JsonStringEnumMemberName("root")]
    Root,

    /// <summary>
    ///     MIDI regions only: each note, transposed as <see cref="Root" />, then moved to the nearest tone of the chord
    ///     sounding at its start, keeping its octave (a tie goes down).
    /// </summary>
    [JsonStringEnumMemberName("chords")]
    Chords
}

/// <summary>A region's own key (<c>key</c> on MIDI and audio regions; null: the project's at the region's start).</summary>
public sealed record RegionKey(PitchName Tonic, KeyMode Mode);

/// <summary>Pitch-class arithmetic the chord track's followers share (the editor mirrors it in models.ts).</summary>
public static class PitchSources {
    static readonly int[] StepClasses = [0, 2, 4, 5, 7, 9, 11];

    /// <summary>A spelled pitch's class, 0 (C) … 11 (B): C♭ is 11, B♯ 0.</summary>
    public static int PitchClass(PitchName pitch) => ((StepClasses[(int)pitch.Step] + pitch.Alter) % 12 + 12) % 12;

    /// <summary>
    ///     The shortest transposition from pitch class <paramref name="from" /> to <paramref name="to" />: −6…+5
    ///     semitones (a tritone goes down).
    /// </summary>
    public static int RootInterval(int from, int to) {
        var up = ((to - from) % 12 + 12) % 12;
        return up >= 6 ? up - 12 : up;
    }
}

/// <summary>How Chord ID writes what it found into the chord track over its range.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChordIdWrite>))]
public enum ChordIdWrite {
    /// <summary>The chords in the range are replaced by the detected ones.</summary>
    [JsonStringEnumMemberName("replace")]
    Replace,

    /// <summary>Detected chords are added where the chord track has none within a beat; existing chords stay.</summary>
    [JsonStringEnumMemberName("merge")]
    Merge,

    /// <summary>Nothing is written to the chord track (regions' keys still are, when asked).</summary>
    [JsonStringEnumMemberName("none")]
    None
}

/// <summary>
///     Chord ID (<c>POST /project/chords/detect</c>, job <c>chordId</c>): over MIDI regions (template matching per
///     beat, smoothed) and audio regions (a harmonic chromagram, beat-synchronous, the same templates and an HMM), every
///     region of <c>regionIds</c> together, between <c>startTick</c> and <c>endTick</c> (left out: the regions' span).
///     The job writes the chord track by <c>write</c> and, when <c>detectKeys</c>, each region's detected key, in one undo
///     step (<c>applyChordId</c>). <c>resolutionTicks</c> is the shortest chord it reports (left out: a beat).
/// </summary>
public sealed record ChordIdRequest(
    IReadOnlyList<string> RegionIds,
    long? StartTick = null,
    long? EndTick = null,
    ChordIdWrite Write = ChordIdWrite.Replace,
    bool DetectKeys = true,
    long? ResolutionTicks = null
);

/// <summary>A chord Chord ID found: from <c>tick</c> for <c>lengthTicks</c>, with a confidence 0…1.</summary>
public sealed record DetectedChord(
    long Tick,
    long LengthTicks,
    PitchName Root,
    string Quality,
    PitchName? Bass,
    double Confidence
);

/// <summary>A region's key Chord ID found, with a confidence 0…1.</summary>
public sealed record DetectedKey(string RegionId, RegionKey Key, double Confidence);

/// <summary>
///     What a Chord ID job found over <c>startTick</c>…<c>endTick</c> (the range it analysed), and how it writes it:
///     <c>applyChordId</c>'s content.
/// </summary>
public sealed record ChordIdResult(
    long StartTick,
    long EndTick,
    ChordIdWrite Write,
    IReadOnlyList<DetectedChord> Chords,
    IReadOnlyList<DetectedKey> Keys
);
