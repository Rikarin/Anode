using Anode.Contracts;
using System.Text.RegularExpressions;

namespace Anode.Generative;

/// <summary>What a phrase of the bank is for (ADR-0014): the groove, a fill, an intro, an ending or a pickup.</summary>
enum PhraseRole {
    Groove,
    Fill,
    Intro,
    Ending,
    Pickup
}

/// <summary>
///     A lane of a phrase: its name, the kit note it plays (drums; −1 otherwise) and a token per step: <c>.</c> a rest,
///     <c>-</c> holding the note before, else the player's token (docs/generative/README.md).
/// </summary>
sealed record Lane(string Name, int Note, string[] Tokens);

/// <summary>A phrase of a style's bank, checked: every lane has <c>Bars × StepsPerBar</c> tokens.</summary>
sealed record Phrase(
    string Id,
    PhraseRole Role,
    double Complexity,
    double Intensity,
    int Bars,
    int From,
    IReadOnlyList<Lane> Lanes
) {
    public Lane? Find(string name) {
        foreach (var lane in Lanes) {
            if (lane.Name == name) {
                return lane;
            }
        }

        return null;
    }
}

/// <summary>The variation grammar of one section kind, checked.</summary>
sealed record Grammar(
    string Form,
    double Complexity,
    double Intensity,
    IReadOnlySet<string> Substitutions,
    PhraseRole? First,
    PhraseRole? Last
) {
    public bool Allows(string substitution) => Substitutions.Contains(substitution);
}

/// <summary>A Keyboard Player rhythm: one bar of right-hand (and left-hand) tokens.</summary>
sealed record Rhythm(string Id, string Name, string[] Rh, string[]? Lh);

/// <summary>A style of the library, checked and ready for the generator.</summary>
sealed record Style(
    SessionPlayerStyle Info,
    int StepsPerBar,
    int SwingGrid,
    int HumaniseTicks,
    int HumaniseVelocity,
    bool RideKeepsTime,
    IReadOnlyDictionary<SectionKind, Grammar> Grammar,
    IReadOnlyList<Phrase> Phrases,
    IReadOnlyDictionary<DrumPiece, string[][]> Variations,
    IReadOnlyDictionary<string, Rhythm> Rhythms
) {
    /// <summary>A step's length in ticks (a 4/4 bar is 3,840).</summary>
    public int StepTicks => Ticks.Bar / StepsPerBar;

    public SessionPlayerKind Player => Info.Player;
}

/// <summary>Ticks per quarter note and per 4/4 bar, as the project's.</summary>
static class Ticks {
    public const int Quarter = 960;
    public const int Bar = Quarter * 4;
}

/// <summary>
///     Checks a style file and turns it into a <see cref="Style" />: every field in range, every lane the right length
///     and of the player's tokens, every section kind's defaults and grammar given, presets present. A problem throws
///     <see cref="InvalidDataException" /> naming the file.
/// </summary>
static partial class StyleCompiler {
    public const int Format = 1;

    /// <summary>A drum lane's name and its kit note (General MIDI, <see cref="DrumKitNotes" />).</summary>
    public static IReadOnlyDictionary<string, int> DrumLanes { get; } = new Dictionary<string, int>(
        StringComparer.Ordinal
    ) {
        ["kick"] = DrumKitNotes.Kick,
        ["snare"] = DrumKitNotes.Snare,
        ["stick"] = DrumKitNotes.SideStick,
        ["clap"] = DrumKitNotes.Clap,
        ["hat"] = DrumKitNotes.ClosedHiHat,
        ["pedal"] = DrumKitNotes.PedalHiHat,
        ["open"] = DrumKitNotes.OpenHiHat,
        ["tom1"] = DrumKitNotes.HighTom,
        ["tom2"] = DrumKitNotes.MidTom,
        ["tom3"] = DrumKitNotes.LowTom,
        ["floor"] = DrumKitNotes.FloorTom,
        ["crash"] = DrumKitNotes.Crash,
        ["ride"] = DrumKitNotes.Ride,
        ["bell"] = DrumKitNotes.RideBell,
        ["tamb"] = DrumKitNotes.Tambourine,
        ["cowbell"] = DrumKitNotes.Cowbell,
        ["shaker"] = DrumKitNotes.Shaker
    };

    static readonly Dictionary<SessionPlayerKind, string[]> SubstitutionsOf = new() {
        [SessionPlayerKind.Drummer] = ["crashOnOne", "openHat", "rideForHat", "ghostNotes", "halfTime"],
        [SessionPlayerKind.Bass] = ["approachNotes", "octaveDoubling"],
        [SessionPlayerKind.Keyboard] = ["passingNotes", "octaveDoubling"]
    };

    public static Style Compile(StyleFile file, string source) {
        void Check(bool ok, string message) {
            if (!ok) {
                throw new InvalidDataException($"{source}: {message}");
            }
        }

        var player = Header(file, Check);
        var rhythms = Rhythms(file, player, Check);
        var phrases = file.Phrases.Select(p => Phrase(file, p, player, Check)).ToList();
        Check(phrases.Select(static p => p.Id).Distinct().Count() == phrases.Count, "a phrase id is used twice.");
        Check(phrases.Count(static p => p.Role == PhraseRole.Groove) >= 2, "a style needs at least two grooves.");
        Check(phrases.Exists(static p => p.Role == PhraseRole.Fill), "a style needs a fill.");
        // The defaults alone must be valid too.
        Parameters(file.Id, null, player, [file.Defaults], rhythms, Check);
        var (grammar, sections) = Sections(file, player, rhythms, Check);
        Check(file.Presets.Count >= 1, "a style needs a preset.");
        var presets = new List<SessionPlayerPreset>();
        foreach (var preset in file.Presets) {
            Check(
                IdPattern().IsMatch(preset.Id) && presets.TrueForAll(p => p.Id != preset.Id),
                $"preset '{preset.Id}': a new id."
            );
            var parameters = Parameters(file.Id, preset.Id, player, [file.Defaults, preset.Parameters], rhythms, Check);
            presets.Add(new(preset.Id, preset.Name, parameters));
        }

        var info = new SessionPlayerStyle(
            file.Id,
            file.Name,
            player,
            file.Genre,
            file.Description,
            file.SynthPlayer,
            file.KitPatchId,
            [..rhythms.Values.Select(static r => new SessionPlayerRhythm(r.Id, r.Name))],
            presets,
            sections
        );
        return new(
            info,
            file.StepsPerBar,
            file.SwingGrid,
            file.Humanise.TimingTicks,
            file.Humanise.Velocity,
            file.Timekeeper != "cymbals",
            grammar,
            phrases,
            Variations(file, player, Check),
            rhythms
        );
    }

    /// <summary>The style's own fields checked; its player.</summary>
    static SessionPlayerKind Header(StyleFile file, Action<bool, string> check) {
        check(file.Format == Format, $"format {file.Format} is not {Format}.");
        check(IdPattern().IsMatch(file.Id), $"'{file.Id}' is not a style id (lowercase letters, digits and dashes).");
        check(
            !string.IsNullOrWhiteSpace(file.Name) && !string.IsNullOrWhiteSpace(file.Genre),
            "a style needs a name and a genre."
        );
        check(
            file.Player is "drummer" or "bass" or "keyboard",
            $"'{file.Player}' is not a player (drummer, bass, keyboard)."
        );
        var player = file.Player switch {
            "drummer" => SessionPlayerKind.Drummer,
            "bass" => SessionPlayerKind.Bass,
            _ => SessionPlayerKind.Keyboard
        };
        check(file.StepsPerBar is 12 or 16, "stepsPerBar is 12 (triplets) or 16 (sixteenths).");
        check(file.SwingGrid is 240 or 480, "swingGrid is 240 (sixteenths) or 480 (eighths).");
        check(file.Humanise.TimingTicks is >= 0 and <= 60, "humanise.timingTicks is 0…60.");
        check(file.Humanise.Velocity is >= 0 and <= 30, "humanise.velocity is 0…30.");
        check(
            player == SessionPlayerKind.Drummer == file.KitPatchId is not null,
            "a drum style, and only one, names its kit patch."
        );
        check(
            !file.SynthPlayer || player == SessionPlayerKind.Keyboard,
            "only a keyboard style is a Synth Player style."
        );
        check(file.Timekeeper is null or "ride" or "cymbals", "timekeeper is ride or cymbals.");
        return player;
    }

    static Dictionary<string, Rhythm> Rhythms(StyleFile file, SessionPlayerKind player, Action<bool, string> check) {
        var rhythms = new Dictionary<string, Rhythm>(StringComparer.Ordinal);
        foreach (var rhythm in file.Rhythms ?? []) {
            var where = $"rhythm '{rhythm.Id}'";
            check(player == SessionPlayerKind.Keyboard, "only a keyboard style has rhythms.");
            check(!rhythms.ContainsKey(rhythm.Id), $"{where} twice.");
            var rh = Tokens(rhythm.Rh, file.StepsPerBar, "rh", player, where, check);
            var lh = rhythm.Lh is null ? null : Tokens(rhythm.Lh, file.StepsPerBar, "lh", player, where, check);
            rhythms[rhythm.Id] = new(rhythm.Id, rhythm.Name, rh, lh);
        }

        return rhythms;
    }

    static Phrase Phrase(StyleFile file, PhraseFile phrase, SessionPlayerKind player, Action<bool, string> check) {
        var where = $"phrase '{phrase.Id}'";
        check(
            phrase.Role is "groove" or "fill" or "intro" or "ending" or "pickup",
            $"{where}: '{phrase.Role}' is not a role."
        );
        var role = phrase.Role switch {
            "groove" => PhraseRole.Groove,
            "fill" => PhraseRole.Fill,
            "intro" => PhraseRole.Intro,
            "ending" => PhraseRole.Ending,
            _ => PhraseRole.Pickup
        };
        check(
            phrase.Complexity is >= 0 and <= 1 && phrase.Intensity is >= 0 and <= 1,
            $"{where}: complexity and intensity are 0…1."
        );
        check(phrase.Bars is >= 1 and <= 4, $"{where}: 1…4 bars.");
        check(role != PhraseRole.Fill || phrase.Bars == 1, $"{where}: a fill is one bar.");
        check(
            phrase.From >= 0 && phrase.From < file.StepsPerBar && (role == PhraseRole.Fill || phrase.From == 0),
            $"{where}: only a fill starts after the bar's first step."
        );
        check(phrase.Lanes.Count > 0, $"{where} has no lanes.");
        var lanes = new List<Lane>();
        foreach (var (name, text) in phrase.Lanes.OrderBy(static l => l.Key, StringComparer.Ordinal)) {
            var note = -1;
            var known = player switch {
                SessionPlayerKind.Drummer => DrumLanes.TryGetValue(name, out note),
                SessionPlayerKind.Bass => name == "bass",
                _ => name is "rh" or "lh"
            };
            check(known, $"{where}: '{name}' is not a lane of this player.");
            lanes.Add(new(name, note, Tokens(text, phrase.Bars * file.StepsPerBar, name, player, where, check)));
        }

        check(
            player != SessionPlayerKind.Keyboard || lanes.Exists(static l => l.Name == "rh"),
            $"{where} has no rh lane."
        );
        return new(phrase.Id, role, phrase.Complexity, phrase.Intensity, phrase.Bars, phrase.From, lanes);
    }

    static Dictionary<DrumPiece, string[][]> Variations(
        StyleFile file,
        SessionPlayerKind player,
        Action<bool, string> check
    ) {
        var variations = new Dictionary<DrumPiece, string[][]>();
        foreach (var (name, lanes) in file.Variations ?? new Dictionary<string, IReadOnlyList<string>>()) {
            check(player == SessionPlayerKind.Drummer, "only a drum style has piece variations.");
            check(TryPiece(name, out var piece), $"'{name}' is not a kit piece.");
            check(lanes.Count is >= 1 and <= 3, $"variations of {name}: 1…3.");
            variations[piece] =
                [..lanes.Select(l => Tokens(l, file.StepsPerBar, "variation", player, $"variations of {name}", check))];
        }

        return variations;
    }

    /// <summary>Each section kind's grammar and defaults (every kind, once).</summary>
    static (Dictionary<SectionKind, Grammar> Grammar, List<SectionDefaults> Defaults) Sections(
        StyleFile file,
        SessionPlayerKind player,
        Dictionary<string, Rhythm> rhythms,
        Action<bool, string> check
    ) {
        if (Missing(file) is { } missing) {
            check(false, $"no grammar or defaults for {missing}.");
        }

        check(
            file.Sections.Count == 5 && file.Grammar.Count == 5,
            "sections and grammar name the five section kinds only."
        );

        var grammar = new Dictionary<SectionKind, Grammar>();
        var sections = new List<SectionDefaults>();
        foreach (var kind in Enum.GetValues<SectionKind>()) {
            var name = Name(kind);
            var g = file.Grammar[name];
            var where = $"grammar of {name}";
            check(
                g.Form.Length is >= 1 and <= 16 && g.Form.All(static c => c is 'A' or 'B'),
                $"{where}: the form is 1…16 letters A and B."
            );
            check(g.Complexity is >= -1 and <= 1 && g.Intensity is >= -1 and <= 1, $"{where}: offsets are −1…1.");
            foreach (var substitution in g.Substitutions ?? []) {
                check(
                    SubstitutionsOf[player].Contains(substitution),
                    $"{where}: '{substitution}' is not a substitution of this player."
                );
            }

            var substitutions = new HashSet<string>(g.Substitutions ?? [], StringComparer.Ordinal);
            grammar[kind] = new(
                g.Form,
                g.Complexity,
                g.Intensity,
                substitutions,
                Role(g.First, where, check),
                Role(g.Last, where, check)
            );
            sections.Add(
                new(kind, Parameters(file.Id, null, player, [file.Defaults, file.Sections[name]], rhythms, check))
            );
        }

        return (grammar, sections);
    }

    /// <summary>The first section kind the file has no grammar or no defaults for (null: none).</summary>
    static string? Missing(StyleFile file) =>
        Enum.GetValues<SectionKind>()
            .Select(Name)
            .FirstOrDefault(n => !file.Grammar.ContainsKey(n) || !file.Sections.ContainsKey(n));

    /// <summary>A section kind's name in the file (its contract name).</summary>
    public static string Name(SectionKind kind) =>
        kind switch {
            SectionKind.Intro => "intro",
            SectionKind.Verse => "verse",
            SectionKind.Chorus => "chorus",
            SectionKind.Bridge => "bridge",
            _ => "outro"
        };

    /// <summary>The switches a player has before any style sets them.</summary>
    public static (DrummerSwitches? Drums, BassSwitches? Bass, KeysSwitches? Keys) DefaultSwitches(
        SessionPlayerKind player
    ) =>
        player switch {
            SessionPlayerKind.Drummer => (new(
                [..Enum.GetValues<DrumPiece>().Select(static p => new DrumPieceSetting(p, true, 0))],
                null
            ), null, null),
            SessionPlayerKind.Bass => (null, new(28, 60, false, false), null),
            _ => (null, null, new(KeysVoicing.Close, 48, 79, KeysLeftHand.Off, null))
        };

    static bool TryPiece(string name, out DrumPiece piece) {
        foreach (var p in Enum.GetValues<DrumPiece>()) {
            if (PieceName(p) == name) {
                piece = p;
                return true;
            }
        }

        piece = default;
        return false;
    }

    static string PieceName(DrumPiece piece) =>
        piece switch {
            DrumPiece.Kick => "kick",
            DrumPiece.Snare => "snare",
            DrumPiece.HiHat => "hiHat",
            DrumPiece.Toms => "toms",
            DrumPiece.Cymbals => "cymbals",
            _ => "percussion"
        };

    static PhraseRole? Role(string? name, string where, Action<bool, string> check) {
        if (name is null) {
            return null;
        }

        check(name is "intro" or "ending" or "pickup", $"{where}: '{name}' is not intro, ending or pickup.");
        return name switch {
            "intro" => PhraseRole.Intro,
            "ending" => PhraseRole.Ending,
            _ => PhraseRole.Pickup
        };
    }

    /// <summary>The full parameters from <paramref name="layers" /> (later ones win) over the player's defaults.</summary>
    static SessionPlayerParameters Parameters(
        string styleId,
        string? presetId,
        SessionPlayerKind player,
        IReadOnlyList<ParameterFile> layers,
        Dictionary<string, Rhythm> rhythms,
        Action<bool, string> check
    ) {
        var (drums, bass, keys) = DefaultSwitches(player);
        var parameters = new SessionPlayerParameters(
            styleId,
            presetId,
            new(0.5, 0.5),
            0.5,
            true,
            0,
            0,
            true,
            drums,
            bass,
            keys
        );
        foreach (var layer in layers) {
            parameters = parameters with {
                Pad = new(layer.Complexity ?? parameters.Pad.Complexity, layer.Intensity ?? parameters.Pad.Intensity),
                FillAmount = layer.FillAmount ?? parameters.FillAmount,
                FillAtSectionEnds = layer.FillAtSectionEnds ?? parameters.FillAtSectionEnds,
                Swing = layer.Swing ?? parameters.Swing,
                Feel = layer.Feel ?? parameters.Feel,
                FollowChords = layer.FollowChords ?? parameters.FollowChords,
                Drums = layer.Pieces is null ? parameters.Drums : Pieces(parameters.Drums, layer.Pieces, check),
                Bass = layer.Bass is null ? parameters.Bass : Bass(parameters.Bass, layer.Bass, check),
                Keys = layer.Keys is null ? parameters.Keys : Keys(parameters.Keys, layer.Keys, check)
            };
        }

        Validate(parameters, rhythms, check);
        return parameters;
    }

    static DrummerSwitches Pieces(
        DrummerSwitches? drums,
        IReadOnlyDictionary<string, PieceFile> pieces,
        Action<bool, string> check
    ) {
        check(drums is not null, "only a drum style sets pieces.");
        var settings = drums!.Pieces.ToList();
        foreach (var (name, setting) in pieces) {
            check(TryPiece(name, out var piece), $"'{name}' is not a kit piece.");
            var old = settings[(int)piece];
            settings[(int)piece] = new(piece, setting.On ?? old.On, setting.Variation ?? old.Variation);
        }

        return drums with { Pieces = settings };
    }

    static BassSwitches Bass(BassSwitches? bass, BassFile b, Action<bool, string> check) {
        check(bass is not null, "only a bass style sets bass switches.");
        var old = bass!;
        return new(
            b.LowNote ?? old.LowNote,
            b.HighNote ?? old.HighNote,
            b.Slides ?? old.Slides,
            b.DeadNotes ?? old.DeadNotes
        );
    }

    static KeysSwitches Keys(KeysSwitches? keys, KeysFile k, Action<bool, string> check) {
        check(keys is not null, "only a keyboard style sets keys switches.");
        var old = keys!;
        return new(
            k.Voicing is null ? old.Voicing : Voicing(k.Voicing, check),
            k.LowNote ?? old.LowNote,
            k.HighNote ?? old.HighNote,
            k.LeftHand is null ? old.LeftHand : LeftHand(k.LeftHand, check),
            k.RhythmId ?? old.RhythmId
        );
    }

    static void Validate(SessionPlayerParameters p, Dictionary<string, Rhythm> rhythms, Action<bool, string> check) {
        check(
            p.Pad.Complexity is >= 0 and <= 1 && p.Pad.Intensity is >= 0 and <= 1,
            "complexity and intensity are 0…1."
        );
        check(
            p.FillAmount is >= 0 and <= 1 && p.Swing is >= 0 and <= 1 && p.Feel is >= -1 and <= 1,
            "fillAmount and swing are 0…1, feel −1…1."
        );
        check(
            p.Drums is null || p.Drums.Pieces.All(static s => s.Variation is >= 0 and <= 3),
            "a piece's variation is 0…3."
        );
        check(
            p.Bass is null || p.Bass is { LowNote: >= 24, HighNote: <= 72 } && p.Bass.HighNote - p.Bass.LowNote >= 12,
            "the bass range is within 24…72, at least an octave."
        );
        check(
            p.Keys is null || p.Keys is { LowNote: >= 36, HighNote: <= 96 } && p.Keys.HighNote - p.Keys.LowNote >= 12,
            "the keys range is within 36…96, at least an octave."
        );
        check(p.Keys?.RhythmId is null || rhythms.ContainsKey(p.Keys.RhythmId), $"no rhythm '{p.Keys?.RhythmId}'.");
    }

    static KeysVoicing Voicing(string name, Action<bool, string> check) {
        check(name is "close" or "spread" or "shell", $"'{name}' is not a voicing.");
        return name switch {
            "close" => KeysVoicing.Close,
            "spread" => KeysVoicing.Spread,
            _ => KeysVoicing.Shell
        };
    }

    static KeysLeftHand LeftHand(string name, Action<bool, string> check) {
        check(name is "off" or "root" or "octaves" or "rootFifth", $"'{name}' is not a left-hand part.");
        return name switch {
            "off" => KeysLeftHand.Off,
            "root" => KeysLeftHand.Root,
            "octaves" => KeysLeftHand.Octaves,
            _ => KeysLeftHand.RootFifth
        };
    }

    /// <summary>
    ///     A lane's tokens: drums a character per step (<c>X x o g .</c>), bass and keys tokens separated by spaces;
    ///     <c>|</c> marks bars and beats and is ignored, as are spaces between drum characters.
    /// </summary>
    static string[] Tokens(
        string text,
        int steps,
        string lane,
        SessionPlayerKind player,
        string where,
        Action<bool, string> check
    ) {
        var cleaned = text.Replace("|", " ", StringComparison.Ordinal);
        var tokens = player == SessionPlayerKind.Drummer
            ? [..cleaned.Where(static c => !char.IsWhiteSpace(c)).Select(static c => c.ToString())]
            : cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        check(tokens.Length == steps, $"{where}: lane {lane} has {tokens.Length} steps, not {steps}.");
        var pattern = player switch {
            SessionPlayerKind.Drummer => DrumToken(),
            SessionPlayerKind.Bass => BassToken(),
            _ => lane == "lh" ? LeftToken() : RightToken()
        };
        foreach (var token in tokens) {
            check(token is "." or "-" || pattern.IsMatch(token), $"{where}: '{token}' is not a token of lane {lane}.");
        }

        check(player == SessionPlayerKind.Drummer || tokens[0] != "-", $"{where}: lane {lane} starts holding nothing.");
        return tokens;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[Xxog]$")]
    private static partial Regex DrumToken();

    [GeneratedRegex("^[Rr3578246AX][',]*[!?]?$")]
    private static partial Regex BassToken();

    [GeneratedRegex("^[CcTU1234][!?]?$")]
    private static partial Regex RightToken();

    [GeneratedRegex("^[R58][!?]?$")]
    private static partial Regex LeftToken();
}
