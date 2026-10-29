using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>What a pattern row plays (docs/07-phase-6.md, "Pattern regions").</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PatternRowKind>))]
public enum PatternRowKind {
    /// <summary>MIDI note <c>note</c>.</summary>
    [JsonStringEnumMemberName("note")]
    Note,

    /// <summary>
    ///     A triad of the project key's scale on degree <c>note</c> (1…7) of the key at the step, its root in the octave
    ///     from MIDI note 60.
    /// </summary>
    [JsonStringEnumMemberName("chordDegree")]
    ChordDegree,

    /// <summary>The Drum Machine Designer pad on note <c>note</c> of the region's track (its name is the pad's).</summary>
    [JsonStringEnumMemberName("pad")]
    Pad
}

/// <summary>The order a pattern row walks its steps in, each pass.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PatternPlayMode>))]
public enum PatternPlayMode {
    [JsonStringEnumMemberName("forward")]
    Forward,

    [JsonStringEnumMemberName("reverse")]
    Reverse,

    /// <summary>Forward then back, without repeating the end steps (1 2 3 4 3 2 1 2 …): a cycle of 2·length − 2 steps.</summary>
    [JsonStringEnumMemberName("pingPong")]
    PingPong,

    /// <summary>A step drawn at each position from the pattern's deterministic generator.</summary>
    [JsonStringEnumMemberName("random")]
    Random
}

/// <summary>
///     One step of a pattern row. <c>velocity</c> 1…127; <c>gate</c> is the note's length in steps, 0…16 (exclusive of
///     0; 1 is the whole step, over 1 holds into the next steps, a tie); <c>repeats</c> 1…8 split the gate evenly into
///     that many notes (ratchets); <c>chance</c> 0…1 is the probability the step plays on a pass; <c>offset</c> −0.5…0.5
///     moves it by that fraction of a step. An off step plays nothing but keeps its values.
/// </summary>
public sealed record PatternStep(bool On, int Velocity, double Gate, int Repeats, double Chance, double Offset);

/// <summary>
///     A row of a pattern: what it plays (<c>kind</c> and <c>note</c>), its name, its length in steps (1…64; it cycles
///     independently of other rows' lengths), its step length in ticks (<c>stepTicks</c>; null: the pattern's), its
///     play mode, and whether it is muted. <c>steps</c> always has exactly <c>length</c> entries.
/// </summary>
public sealed record PatternRow(
    string Id,
    PatternRowKind Kind,
    int Note,
    string Name,
    int Length,
    int? StepTicks,
    PatternPlayMode PlayMode,
    bool Muted,
    IReadOnlyList<PatternStep> Steps
);

/// <summary>
///     A Step Sequencer pattern: its rows; <c>swing</c> 0…1 delays every even 16th (the second, fourth … of each beat)
///     by <c>swing</c> × half a 16th; <c>seed</c> seeds the deterministic generator that draws chance and random steps;
///     <c>stepTicks</c> is the rows' step length (240, a 16th, unless set).
/// </summary>
public sealed record Pattern(IReadOnlyList<PatternRow> Rows, double Swing, long Seed, int StepTicks = 240);

/// <summary>
///     A pattern region on an instrument track: it plays <c>pattern</c> from <c>startTick</c> for
///     <c>lengthTicks</c>, every row cycling from the region's start.
/// </summary>
/// <remarks>
///     The server compiles it to note events unrolled over the region's length (docs/07-phase-6.md): a row's pass is
///     its length in steps; a step on a pass plays when the generator, seeded by (pattern seed, region id, row, step,
///     pass), draws under its chance; offset, swing and repeats as <see cref="PatternStep" /> and
///     <see cref="Pattern" /> say. The same project always gives the same notes; <c>reseedPattern</c> changes the seed.
///     Workstream B fixes the generator and the unrolling in <c>PatternVectors.json</c>, shared with the editor.
///     Converting to a MIDI region replaces the region with exactly those notes, over the region's length.
/// </remarks>
public sealed record PatternRegion(
    string Id,
    string TrackId,
    string Name,
    long StartTick,
    long LengthTicks,
    bool Muted,
    Pattern Pattern
);

/// <summary>A pad of a Drum Machine Designer: note <c>note</c> (0…127) plays sub-track <c>trackId</c>.</summary>
public sealed record DrumPad(int Note, string Name, string TrackId);

/// <summary>
///     A Drum Machine Designer (docs/07-phase-6.md): the track is a stack whose members are its pads, each an instrument
///     track with its own channel strip; <c>pads</c> (at most one per note, sorted by note) map a note to a pad. The
///     DMD track's region and pattern notes are routed to the pad tracks by note (live MIDI too, by the host), their
///     note numbers unchanged: a pad plays its own sound whatever the note (the Drum Synth ignores pitch; a Quick
///     Sampler pad has key follow off and plays at its root).
/// </summary>
public sealed record DrumMachine(IReadOnlyList<DrumPad> Pads);
