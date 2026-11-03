using System.Text.Json.Serialization;

namespace Anode.Contracts;

// Session Players (docs/12-phase-11.md, docs/adr/0014-session-players-and-chord-id.md): Drummer, Bass Player and
// Keyboard Player tracks play Session Player regions, whose notes the server's generator (Anode.Generative) makes from
// the region's parameters, length and seed, the chord track and the arrangement sections over its range, and the rhythm
// source's notes over its range: the same inputs and style library version give the same notes on every machine. A
// Session Player region is not a MIDI region: editing its notes needs Convert to MIDI first.

/// <summary>Which player a Session Player track or style is for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionPlayerKind>))]
public enum SessionPlayerKind {
    /// <summary>Drummer: a <c>drummer</c> track, drums through its Drum Machine Designer kit.</summary>
    [JsonStringEnumMemberName("drummer")]
    Drummer,

    /// <summary>Bass Player: a <c>bassPlayer</c> track.</summary>
    [JsonStringEnumMemberName("bass")]
    Bass,

    /// <summary>Keyboard Player: a <c>keyboardPlayer</c> track (its styles include the Synth Player's).</summary>
    [JsonStringEnumMemberName("keyboard")]
    Keyboard
}

/// <summary>What an arrangement section is to the players: their default parameters and variation grammar per kind.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SectionKind>))]
public enum SectionKind {
    [JsonStringEnumMemberName("intro")]
    Intro,

    [JsonStringEnumMemberName("verse")]
    Verse,

    [JsonStringEnumMemberName("chorus")]
    Chorus,

    [JsonStringEnumMemberName("bridge")]
    Bridge,

    [JsonStringEnumMemberName("outro")]
    Outro
}

/// <summary>
///     An arrangement section's kind, by its name (sections have no stored kind): the first of <c>intro</c>,
///     <c>verse</c>, <c>chorus</c>, <c>bridge</c> and <c>outro</c> its name starts with, ignoring case (so "Chorus 2" is
///     a chorus); also <c>pre-chorus</c> and <c>hook</c> as a chorus, <c>middle</c> and <c>break</c> as a bridge,
///     <c>ending</c> and <c>end</c> as an outro; anything else (and a region over no section) is a verse.
/// </summary>
public static class SectionKinds {
    static readonly (string Prefix, SectionKind Kind)[] Prefixes = [
        ("intro", SectionKind.Intro), ("verse", SectionKind.Verse), ("pre-chorus", SectionKind.Chorus),
        ("chorus", SectionKind.Chorus), ("hook", SectionKind.Chorus), ("bridge", SectionKind.Bridge),
        ("middle", SectionKind.Bridge), ("break", SectionKind.Bridge), ("outro", SectionKind.Outro),
        ("ending", SectionKind.Outro), ("end", SectionKind.Outro)
    ];

    public static SectionKind Of(string? sectionName) {
        var name = sectionName?.Trim() ?? string.Empty;
        foreach (var (prefix, kind) in Prefixes) {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                return kind;
            }
        }

        return SectionKind.Verse;
    }
}

/// <summary>
///     The XY pad: <c>complexity</c> (x, 0 simple … 1 busy) and <c>intensity</c> (y, 0 soft … 1 loud); phrases are
///     picked and morphed at that point of the style's phrase space.
/// </summary>
public sealed record SessionPlayerPad(double Complexity, double Intensity);

/// <summary>A Drummer kit piece, switched on or off and given a pattern variation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DrumPiece>))]
public enum DrumPiece {
    [JsonStringEnumMemberName("kick")]
    Kick,

    [JsonStringEnumMemberName("snare")]
    Snare,

    /// <summary>Hi-hat; with it off, the ride or the cymbals keep time instead (the style's choice).</summary>
    [JsonStringEnumMemberName("hiHat")]
    HiHat,

    [JsonStringEnumMemberName("toms")]
    Toms,

    /// <summary>Crashes and the ride.</summary>
    [JsonStringEnumMemberName("cymbals")]
    Cymbals,

    /// <summary>Claps, shakers, tambourine and the rest.</summary>
    [JsonStringEnumMemberName("percussion")]
    Percussion
}

/// <summary>
///     The notes the Drummer plays (General MIDI percussion, which its kits' Drum Machine Designer pads sit on): each
///     kit piece's notes, the first its main one.
/// </summary>
public static class DrumKitNotes {
    public const int Kick = 36;
    public const int SideStick = 37;
    public const int Snare = 38;
    public const int Clap = 39;
    public const int FloorTom = 41;
    public const int ClosedHiHat = 42;
    public const int LowTom = 45;
    public const int PedalHiHat = 44;
    public const int OpenHiHat = 46;
    public const int MidTom = 47;
    public const int Crash = 49;
    public const int HighTom = 50;
    public const int Ride = 51;
    public const int RideBell = 53;
    public const int Tambourine = 54;
    public const int Cowbell = 56;
    public const int Shaker = 82;

    static readonly int[][] ByPiece = [
        [Kick], [Snare, SideStick], [ClosedHiHat, PedalHiHat, OpenHiHat], [HighTom, MidTom, LowTom, FloorTom],
        [Crash, Ride, RideBell], [Clap, Tambourine, Cowbell, Shaker]
    ];

    /// <summary>The notes of kit piece <paramref name="piece" />, its main one first.</summary>
    public static IReadOnlyList<int> Of(DrumPiece piece) => ByPiece[(int)piece];
}

/// <summary>A kit piece's switch: on or off, and its pattern variation (0 the style's default … 3).</summary>
public sealed record DrumPieceSetting(DrumPiece Piece, bool On, int Variation);

/// <summary>
///     The Drummer's switches: every kit piece (<see cref="DrumPiece" />, each once, in that order), and the rhythm
///     source to follow (Logic's Follow: a track whose notes, or audio onsets, the kick and snare lean towards; null:
///     none).
/// </summary>
public sealed record DrummerSwitches(IReadOnlyList<DrumPieceSetting> Pieces, string? RhythmSourceTrackId);

/// <summary>
///     The Bass Player's switches: its range (MIDI notes <c>lowNote</c> … <c>highNote</c>, at least an octave apart,
///     within 24…72), and whether it plays slides and dead notes.
/// </summary>
public sealed record BassSwitches(int LowNote, int HighNote, bool Slides, bool DeadNotes);

/// <summary>How the Keyboard Player voices a chord.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KeysVoicing>))]
public enum KeysVoicing {
    /// <summary>Within an octave.</summary>
    [JsonStringEnumMemberName("close")]
    Close,

    /// <summary>Open: over more than an octave.</summary>
    [JsonStringEnumMemberName("spread")]
    Spread,

    /// <summary>Root (or bass), third and seventh only.</summary>
    [JsonStringEnumMemberName("shell")]
    Shell
}

/// <summary>What the Keyboard Player's left hand plays below the voicing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KeysLeftHand>))]
public enum KeysLeftHand {
    [JsonStringEnumMemberName("off")]
    Off,

    /// <summary>The chord's root (or slash bass).</summary>
    [JsonStringEnumMemberName("root")]
    Root,

    /// <summary>The root and its octave.</summary>
    [JsonStringEnumMemberName("octaves")]
    Octaves,

    /// <summary>The root and fifth, in the style's rhythm.</summary>
    [JsonStringEnumMemberName("rootFifth")]
    RootFifth
}

/// <summary>
///     The Keyboard Player's switches: the voicing, the right hand's range (MIDI notes <c>lowNote</c> …
///     <c>highNote</c>, at least an octave apart, within 36…96), the left-hand part, and the rhythm pattern (one of the
///     style's <c>rhythms</c>; null: the style's phrases' own).
/// </summary>
public sealed record KeysSwitches(
    KeysVoicing Voicing,
    int LowNote,
    int HighNote,
    KeysLeftHand LeftHand,
    string? RhythmId
);

/// <summary>
///     A Session Player region's parameters: the style (<see cref="SessionPlayerStyle" />, of the track's player) and the
///     preset they came from (null: edited since, or none); the XY pad; fills (<c>fillAmount</c> 0…1, and a fill in a
///     section's last bar when <c>fillAtSectionEnds</c>); <c>swing</c> 0…1 (on the style's grid); <c>feel</c> −1 (lay
///     back) … 1 (push), a fraction of a 32nd; whether it follows the chord track (bass and keys: off plays the region's
///     key's tonic chord); and the switches of its player (the others null).
/// </summary>
public sealed record SessionPlayerParameters(
    string StyleId,
    string? PresetId,
    SessionPlayerPad Pad,
    double FillAmount,
    bool FillAtSectionEnds,
    double Swing,
    double Feel,
    bool FollowChords,
    DrummerSwitches? Drums,
    BassSwitches? Bass,
    KeysSwitches? Keys
);

/// <summary>
///     A Session Player region on a <c>drummer</c>, <c>bassPlayer</c> or <c>keyboardPlayer</c> track (the project's
///     <c>sessionPlayerRegions</c>, beside its MIDI and pattern regions): it plays from <c>startTick</c> for
///     <c>lengthTicks</c> the notes the generator makes from its parameters and <c>seed</c>. Regenerate draws a new
///     seed. <c>moveRegion</c>, <c>copyRegions</c> and <c>deleteRegions</c> take Session Player regions too.
/// </summary>
public sealed record SessionPlayerRegion(
    string Id,
    string TrackId,
    string Name,
    long StartTick,
    long LengthTicks,
    bool Muted,
    SessionPlayerParameters Parameters,
    long Seed
);

/// <summary>
///     A preset of a style: named parameters (the region's <c>styleId</c> is the style's; switches of the style's player
///     only).
/// </summary>
public sealed record SessionPlayerPreset(string Id, string Name, SessionPlayerParameters Parameters);

/// <summary>A Keyboard Player rhythm pattern a style offers (<c>KeysSwitches.rhythmId</c>).</summary>
public sealed record SessionPlayerRhythm(string Id, string Name);

/// <summary>
///     A style of the style library: its player, a generic, descriptive name (a genre and a feel, never a person's or a
///     product preset's: ADR-0014), its genre, whether it is a Synth Player style (Keyboard Player styles that play a
///     synth patch), the kit patch a new Drummer track of it gets (null: not a drum style), its rhythms (Keyboard
///     Player), its presets (the first is the default) and its default parameters per section kind (a region's defaults:
///     every <see cref="SectionKind" />, each once).
/// </summary>
public sealed record SessionPlayerStyle(
    string Id,
    string Name,
    SessionPlayerKind Player,
    string Genre,
    string Description,
    bool SynthPlayer,
    string? KitPatchId,
    IReadOnlyList<SessionPlayerRhythm> Rhythms,
    IReadOnlyList<SessionPlayerPreset> Presets,
    IReadOnlyList<SectionDefaults> Sections
);

/// <summary>A style's default parameters for a region over a section of kind <c>kind</c>.</summary>
public sealed record SectionDefaults(SectionKind Kind, SessionPlayerParameters Parameters);

/// <summary>
///     The style library's catalogue (<c>GET /library/session-players</c>): its version (part of every region's
///     generation input, so a new library regenerates the regions) and its styles.
/// </summary>
public sealed record SessionPlayerCatalog(string Version, IReadOnlyList<SessionPlayerStyle> Styles);

/// <summary>
///     A Session Player region's generated notes (<c>GET /project/regions/{id}/session-player</c>), positioned relative
///     to the region's start as a MIDI region's, for the editor to draw (read-only in the piano roll); <c>hash</c> is
///     the generation input's hash (the server's cache key; equal hashes, equal notes).
/// </summary>
public sealed record SessionPlayerNotes(string RegionId, string Hash, IReadOnlyList<Note> Notes);

/// <summary>Regenerate (<c>POST /project/regions/{id}/session-player/regenerate</c>): a new seed (left out: drawn).</summary>
public sealed record RegenerateRequest(long? Seed = null);

/// <summary>What Convert turns Session Player regions into.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionPlayerConvertTarget>))]
public enum SessionPlayerConvertTarget {
    /// <summary>A MIDI region of exactly the generated notes.</summary>
    [JsonStringEnumMemberName("midi")]
    Midi,

    /// <summary>Drummer only: a pattern region (one pad row per kit piece played, 16th steps).</summary>
    [JsonStringEnumMemberName("pattern")]
    Pattern
}

/// <summary>
///     Convert to MIDI or to a pattern (<c>POST /project/regions/{id}/session-player/convert</c>, job
///     <c>sessionPlayerConvert</c>): the region (and <c>regionIds</c>, the others selected with it) replaced in one undo
///     step (<c>convertSessionPlayerRegions</c>) by a region of the generated notes, at the same place, with its name
///     and mute.
/// </summary>
public sealed record SessionPlayerConvertRequest(
    SessionPlayerConvertTarget Target = SessionPlayerConvertTarget.Midi,
    IReadOnlyList<string>? RegionIds = null
);

/// <summary>A converted region: the Session Player region it replaces and the notes (or the pattern) it holds.</summary>
public sealed record ConvertedSessionPlayerRegion(
    string RegionId,
    SessionPlayerConvertTarget Target,
    IReadOnlyList<NewNote> Notes,
    Pattern? Pattern
);
