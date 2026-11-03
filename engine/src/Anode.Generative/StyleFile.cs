using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anode.Generative;

// The style library's file format (docs/generative/README.md, "The style library"): what the JSON under Styles/ holds,
// before StyleCompiler checks it and turns it into Style. Version 1 of the format.

/// <summary><c>Styles/library.json</c>: the format and the library's own version (bumped with any change to its data).</summary>
sealed record LibraryFile(int Format, string Version);

/// <summary>A style file: one style of one player, its phrase bank, grammar, presets and section defaults.</summary>
sealed record StyleFile(
    int Format,
    string Id,
    string Name,
    string Player,
    string Genre,
    string Description,
    HumaniseFile Humanise,
    ParameterFile Defaults,
    IReadOnlyList<PresetFile> Presets,
    IReadOnlyDictionary<string, ParameterFile> Sections,
    IReadOnlyDictionary<string, GrammarFile> Grammar,
    IReadOnlyList<PhraseFile> Phrases,
    bool SynthPlayer = false,
    string? KitPatchId = null,
    int StepsPerBar = 16,
    int SwingGrid = 240,
    string? Timekeeper = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Variations = null,
    IReadOnlyList<RhythmFile>? Rhythms = null
);

/// <summary>Seeded humanisation: timing (ticks, at most) and velocity (at most) either way.</summary>
sealed record HumaniseFile(int TimingTicks, int Velocity);

/// <summary>
///     A partial set of parameters: the style's defaults, a preset's or a section kind's (what is left out comes from
///     the defaults, then the player's built-in switches).
/// </summary>
sealed record ParameterFile(
    double? Complexity = null,
    double? Intensity = null,
    double? FillAmount = null,
    bool? FillAtSectionEnds = null,
    double? Swing = null,
    double? Feel = null,
    bool? FollowChords = null,
    IReadOnlyDictionary<string, PieceFile>? Pieces = null,
    BassFile? Bass = null,
    KeysFile? Keys = null
);

/// <summary>A kit piece's switch in a parameter set.</summary>
sealed record PieceFile(bool? On = null, int? Variation = null);

sealed record BassFile(int? LowNote = null, int? HighNote = null, bool? Slides = null, bool? DeadNotes = null);

sealed record KeysFile(
    string? Voicing = null,
    int? LowNote = null,
    int? HighNote = null,
    string? LeftHand = null,
    string? RhythmId = null
);

sealed record PresetFile(string Id, string Name, ParameterFile Parameters);

/// <summary>
///     The variation grammar of one section kind: the pad's offsets there, the bar form (letters A and B: the section's
///     main and alternate phrase, cycled), the substitutions it allows, and the roles of its first and last bars.
/// </summary>
sealed record GrammarFile(
    string Form,
    double Complexity = 0,
    double Intensity = 0,
    IReadOnlyList<string>? Substitutions = null,
    string? First = null,
    string? Last = null
);

/// <summary>
///     A phrase of the bank: its role, its place in the pad's space, its length in bars, the step a fill takes over
///     from, and its lanes (drums: a lane per kit sound, a character per step; bass: <c>bass</c>; keys: <c>rh</c> and
///     <c>lh</c>, a token per step).
/// </summary>
sealed record PhraseFile(
    string Id,
    string Role,
    double Complexity,
    double Intensity,
    IReadOnlyDictionary<string, string> Lanes,
    int Bars = 1,
    int From = 0
);

/// <summary>A Keyboard Player rhythm (<c>KeysSwitches.rhythmId</c>): one bar's <c>rh</c> (and <c>lh</c>) lanes.</summary>
sealed record RhythmFile(string Id, string Name, string Rh, string? Lh = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true
)]
[JsonSerializable(typeof(LibraryFile))]
[JsonSerializable(typeof(StyleFile))]
partial class StyleJsonContext : JsonSerializerContext;
