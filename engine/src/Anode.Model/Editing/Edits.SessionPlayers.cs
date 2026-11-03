using Anode.Contracts;
using Anode.Model.Time;

namespace Anode.Model.Editing;

/// <summary>
///     Phase 11's Session Players (docs/12-phase-11.md, docs/adr/0014-session-players-and-chord-id.md): Drummer, Bass
///     Player and Keyboard Player tracks (<c>addSessionPlayerTrack</c>) and their Session Player regions
///     (<c>addSessionPlayerRegion</c>, <c>setSessionPlayerRegion</c>, <c>regenerateSessionPlayer</c>, and Convert's
///     <c>convertSessionPlayerRegions</c>). A Drummer track is a Drum Machine Designer of kind <c>drummer</c> (its pads
///     its member instrument tracks); Bass and Keyboard Player tracks are instrument tracks. Styles, presets and the
///     section kinds' defaults come from the style library's catalogue (<see cref="SessionPlayerCatalog" />, the
///     server's from workstream D's library); without a style for a player its regions cannot be made. A region's notes
///     are not part of the project: the server generates them from its parameters and seed.
/// </summary>
public static partial class Edits {
    public const int MinBassNote = 24;
    public const int MaxBassNote = 72;
    public const int MinKeysNote = 36;
    public const int MaxKeysNote = 96;

    /// <summary>How many bars a Session Player region has when nothing else says.</summary>
    public const int DefaultPlayerBars = 8;

    /// <summary>No styles: Session Player regions cannot be made (the edits' catalogue when none is given).</summary>
    public static SessionPlayerCatalog NoStyles { get; } = new(string.Empty, []);

    /// <summary>A player's switches when a style's parameters leave them out.</summary>
    public static DrummerSwitches DefaultDrums { get; } =
        new([..Enum.GetValues<DrumPiece>().Select(static p => new DrumPieceSetting(p, true, 0))], null);

    public static BassSwitches DefaultBass { get; } = new(28, 60, false, false);

    public static KeysSwitches DefaultKeys { get; } = new(KeysVoicing.Close, 48, 84, KeysLeftHand.Off, null);

    static IReadOnlyList<DeltaOp>? SessionPlayerOps(
        ProjectState state,
        EditCommand command,
        IIdSource ids,
        PluginCatalog plugins,
        IPatchSource patches,
        SessionPlayerCatalog styles
    ) =>
        command switch {
            AddSessionPlayerTrackCommand c => AddSessionPlayerTrack(state, c, ids, plugins, patches, styles),
            AddSessionPlayerRegionCommand c => AddSessionPlayerRegion(state, c, ids, styles),
            SetSessionPlayerRegionCommand c => SetSessionPlayerRegion(state, c, styles),
            RegenerateSessionPlayerCommand c => RegenerateSessionPlayer(state, c, ids),
            ConvertSessionPlayerRegionsCommand c => ConvertSessionPlayerRegions(state, c, ids),
            _ => null
        };

    /// <summary>Whether a track of <paramref name="kind" /> plays Session Player regions.</summary>
    public static bool IsSessionPlayerKind(TrackKind kind) =>
        kind is TrackKind.Drummer or TrackKind.BassPlayer or TrackKind.KeyboardPlayer;

    /// <summary>
    ///     Whether a track of <paramref name="kind" /> is an instrument track: an instrument track, or a Bass or Keyboard
    ///     Player's.
    /// </summary>
    public static bool IsInstrumentKind(TrackKind kind) =>
        kind is TrackKind.Instrument or TrackKind.BassPlayer or TrackKind.KeyboardPlayer;

    /// <summary>
    ///     Whether a track of <paramref name="kind" /> is a stack (its members' strips offset by its own): a folder stack,
    ///     a Drum Machine Designer, or a Drummer (a DMD of its own kind).
    /// </summary>
    public static bool IsStackKind(TrackKind kind) => kind is TrackKind.Stack or TrackKind.Drummer;

    /// <summary>The player of a Session Player track kind (null for the other kinds).</summary>
    public static SessionPlayerKind? PlayerOf(TrackKind kind) =>
        kind switch {
            TrackKind.Drummer => SessionPlayerKind.Drummer,
            TrackKind.BassPlayer => SessionPlayerKind.Bass,
            TrackKind.KeyboardPlayer => SessionPlayerKind.Keyboard,
            _ => null
        };

    static TrackKind TrackKindOf(SessionPlayerKind player) =>
        player switch {
            SessionPlayerKind.Drummer => TrackKind.Drummer,
            SessionPlayerKind.Bass => TrackKind.BassPlayer,
            _ => TrackKind.KeyboardPlayer
        };

    static string PlayerName(SessionPlayerKind player) =>
        player switch {
            SessionPlayerKind.Drummer => "Drummer",
            SessionPlayerKind.Bass => "Bass Player",
            _ => "Keyboard Player"
        };

    static List<DeltaOp> AddSessionPlayerTrack(
        ProjectState state,
        AddSessionPlayerTrackCommand c,
        IIdSource ids,
        PluginCatalog plugins,
        IPatchSource patches,
        SessionPlayerCatalog styles
    ) {
        if (!Enum.IsDefined(c.Player)) {
            throw new EditException($"Unknown Session Player {c.Player}.");
        }

        CheckTrackCount(state);
        var style = StyleFor(styles, c.Player, c.StyleId);
        var preset = c.PresetId is null ? null : PresetOf(style, c.PresetId);
        var count = state.Tracks.Count;
        var index = c.Index ?? count;
        if (index < 0 || index > count) {
            throw new EditException($"Track index {index} is outside 0…{count}.");
        }

        if (index < count && state.Tracks[index].ParentId is { } parent) {
            index = AfterStack(state, parent);
        }

        var kind = TrackKindOf(c.Player);
        var name = c.Name is null ? NextTrackName(state, kind) : Name(c.Name, "track name");
        var ops = new List<DeltaOp>();
        Track track;
        if (c.Player == SessionPlayerKind.Drummer) {
            if (c.PluginId is not null) {
                throw new EditException("A Drummer plays its kit: name a kit patch, not an instrument.");
            }

            var kit = Kit(patches, c.KitPatchId, style.KitPatchId);
            track = NewTrack(state, ids, TrackKind.Drummer, name) with { DrumMachine = new([]) };
            ops.Add(new TrackUpserted(track, index));
            var after = state.Apply(ops);
            foreach (var (note, padName, pad) in KitPads(kit)) {
                var step = NewPad(after, after.FindTrack(track.Id)!, note, padName, ids, plugins, pad);
                ops.AddRange(step);
                after = after.Apply(step);
            }
        } else {
            if (c.KitPatchId is not null) {
                throw new EditException("Only a Drummer takes a kit patch.");
            }

            // The style's patch (a Synth Player's synth, say) when the library has it, else the plugin asked for, else the
            // default instrument.
            var patch = c.PluginId is null && style.KitPatchId is { } patchId ? patches.Find(patchId) : null;
            track = NewTrack(state, ids, kind, name) with {
                Instrument = patch?.Instrument is { } slot
                    ? slot with { Parameters = PluginCatalog.Sorted(slot.Parameters) }
                    : NewInstrument(plugins, c.PluginId ?? PluginCatalog.DefaultInstrumentId, c.PluginId is not null),
                Strip = patch is null
                    ? ProjectDefaults.Strip
                    : ProjectDefaults.Strip with {
                        Inserts = [
                            ..patch.Inserts.Take(MaxInserts)
                                .Select(i => i with {
                                        Id = ids.Next(IdPrefix.Insert),
                                        Parameters = PluginCatalog.Sorted(i.Parameters).Remove(HostTempoParameter),
                                        Sidechain = null
                                    }
                                )
                        ]
                    }
            };
            ops.Add(new TrackUpserted(track, index));
        }

        if (c.Regions) {
            var sections = state.Sections.Values.OrderBy(static s => s.StartTick).ToArray();
            (long Start, long Length, string Name, SectionKind Kind)[] spans = sections.Length > 0
                ? [..sections.Select(static s => (s.StartTick, s.LengthTicks, s.Name, SectionKinds.Of(s.Name)))]
                : [(0, Bars(state, 0, DefaultPlayerBars), style.Name, SectionKind.Verse)];
            foreach (var (start, length, regionName, sectionKind) in spans) {
                var parameters = preset is null
                    ? Defaults(style, sectionKind, c.Player)
                    : Normalised(preset.Parameters with { StyleId = style.Id, PresetId = preset.Id }, c.Player);
                ops.Add(
                    new SessionPlayerRegionUpserted(
                        new(
                            ids.Next(IdPrefix.SessionPlayerRegion),
                            track.Id,
                            regionName,
                            start,
                            length,
                            false,
                            parameters,
                            IdPrefix.Seed(ids)
                        )
                    )
                );
            }
        }

        return ops;
    }

    /// <summary>The kit patch a new Drummer gets: the one asked for (it must exist), else the style's when it does.</summary>
    static ChannelPatch? Kit(IPatchSource patches, string? asked, string? style) {
        if (asked is not null) {
            return patches.Find(asked) is { Pads: not null } kit ? kit : throw new EditException($"No drum kit '{asked}'.");
        }

        return style is not null && patches.Find(style) is { Pads: not null } found ? found : null;
    }

    /// <summary>
    ///     A Drummer's pads: the kit's, else one Drum Synth pad on each note the Drummer plays
    ///     (<see cref="DrumKitNotes" />), so the kit is multi-output whatever content is installed.
    /// </summary>
    static IEnumerable<(int Note, string? Name, KitPad? Pad)> KitPads(ChannelPatch? kit) =>
        kit?.Pads is { } pads
            ? pads.Where(static p => p.Note is >= 0 and <= 127)
                .DistinctBy(static p => p.Note)
                .Select(static p => (p.Note, (string?)p.Name, (KitPad?)p))
            : Enum.GetValues<DrumPiece>()
                .SelectMany(DrumKitNotes.Of)
                .Order()
                .Select(static n => (n, (string?)null, (KitPad?)null));

    static List<DeltaOp> AddSessionPlayerRegion(
        ProjectState state,
        AddSessionPlayerRegionCommand c,
        IIdSource ids,
        SessionPlayerCatalog styles
    ) {
        var (track, player) = SessionPlayerTrackOf(state, c.TrackId);
        CheckTick(c.StartTick, "start");
        var section = SectionAt(state, c.StartTick);
        var length = c.LengthTicks
            ?? (section is null
                ? Bars(state, c.StartTick, DefaultPlayerBars)
                : section.StartTick + section.LengthTicks - c.StartTick);
        CheckLength(length, "region");
        CheckTick(c.StartTick + length, "end");
        SessionPlayerParameters parameters;
        string styleName;
        if (c.Parameters is { } given) {
            parameters = CheckParameters(state, styles, player, track.Id, given);
            styleName = StyleFor(styles, player, parameters.StyleId).Name;
        } else {
            var style = TrackStyle(state, track, c.StartTick, styles, player);
            parameters = Defaults(style, SectionKinds.Of(section?.Name), player);
            styleName = style.Name;
        }

        return [
            new SessionPlayerRegionUpserted(
                new(
                    ids.Next(IdPrefix.SessionPlayerRegion),
                    track.Id,
                    c.Name is null ? section?.Name ?? styleName : Name(c.Name, "region name"),
                    c.StartTick,
                    length,
                    false,
                    parameters,
                    c.Seed ?? IdPrefix.Seed(ids)
                )
            )
        ];
    }

    /// <summary>
    ///     A region's changes (docs/12-phase-11.md): a preset (or a new style's first) applies first, then the parameters
    ///     given, any of which clears the preset; place, name and mute are not parameters; the seed stays.
    /// </summary>
    static DeltaOp[] SetSessionPlayerRegion(
        ProjectState state,
        SetSessionPlayerRegionCommand c,
        SessionPlayerCatalog styles
    ) {
        var region = SessionPlayerRegionOf(state, c.RegionId);
        var player = PlayerOf(TrackOf(state, region.TrackId).Kind)!.Value;
        var p = region.Parameters;
        var newStyle = c.StyleId is not null && c.StyleId != p.StyleId;
        if (newStyle || c.PresetId is not null) {
            var style = StyleFor(styles, player, c.StyleId ?? p.StyleId);
            var preset = c.PresetId is not null ? PresetOf(style, c.PresetId) : FirstPreset(style);
            p = preset is null
                ? Normalised(p with { StyleId = style.Id, PresetId = null }, player)
                : Normalised(preset.Parameters with { StyleId = style.Id, PresetId = preset.Id }, player);
        }

        var edited = p with {
            Pad = new(c.Complexity ?? p.Pad.Complexity, c.Intensity ?? p.Pad.Intensity),
            FillAmount = c.FillAmount ?? p.FillAmount,
            FillAtSectionEnds = c.FillAtSectionEnds ?? p.FillAtSectionEnds,
            Swing = c.Swing ?? p.Swing,
            Feel = c.Feel ?? p.Feel,
            FollowChords = c.FollowChords ?? p.FollowChords,
            Drums = c.Drums ?? p.Drums,
            Bass = c.Bass ?? p.Bass,
            Keys = c.Keys ?? p.Keys
        };
        if (edited != p) {
            p = edited with { PresetId = null };
        }

        var start = c.StartTick ?? region.StartTick;
        var length = c.LengthTicks ?? region.LengthTicks;
        CheckTick(start, "start");
        CheckLength(length, "region");
        CheckTick(start + length, "end");
        return [
            new SessionPlayerRegionUpserted(
                region with {
                    StartTick = start,
                    LengthTicks = length,
                    Name = c.Name is null ? region.Name : Name(c.Name, "region name"),
                    Muted = c.Muted ?? region.Muted,
                    Parameters = CheckParameters(state, styles, player, region.TrackId, p)
                }
            )
        ];
    }

    static List<DeltaOp> RegenerateSessionPlayer(
        ProjectState state,
        RegenerateSessionPlayerCommand c,
        IIdSource ids
    ) {
        if (c.RegionIds is not { Count: > 0 }) {
            throw new EditException("Name at least one Session Player region.");
        }

        return [
            ..c.RegionIds.Distinct(StringComparer.Ordinal)
                .Select(id => SessionPlayerRegionOf(state, id))
                .ToArray()
                .Select(r => new SessionPlayerRegionUpserted(r with { Seed = c.Seed ?? IdPrefix.Seed(ids) }))
        ];
    }

    /// <summary>
    ///     Convert's result: each Session Player region replaced by a MIDI region of its notes, or a Drummer's by a
    ///     pattern region of its pattern, at its place, with its name and mute.
    /// </summary>
    static List<DeltaOp> ConvertSessionPlayerRegions(
        ProjectState state,
        ConvertSessionPlayerRegionsCommand c,
        IIdSource ids
    ) {
        if (c.Regions is not { Count: > 0 }) {
            throw new EditException("Name at least one converted region.");
        }

        if (c.Regions.DistinctBy(static r => r?.RegionId).Count() != c.Regions.Count) {
            throw new EditException("A region converts once.");
        }

        var ops = new List<DeltaOp>();
        var next = state.LastEventId;
        foreach (var converted in c.Regions) {
            var source = SessionPlayerRegionOf(state, converted?.RegionId);
            ops.Add(new SessionPlayerRegionRemoved(source.Id));
            if (converted!.Target == SessionPlayerConvertTarget.Pattern) {
                if (state.FindTrack(source.TrackId)?.Kind != TrackKind.Drummer) {
                    throw new EditException($"Region '{source.Id}' is not a Drummer's: it converts to MIDI only.");
                }

                ops.Add(
                    new PatternRegionUpserted(
                        new(
                            ids.Next(IdPrefix.PatternRegion),
                            source.TrackId,
                            source.Name,
                            source.StartTick,
                            source.LengthTicks,
                            source.Muted,
                            CheckPattern(converted.Pattern ?? throw new EditException("A pattern conversion has a pattern."))
                        )
                    )
                );
                continue;
            }

            if (converted.Target != SessionPlayerConvertTarget.Midi || converted.Notes is null) {
                throw new EditException("A conversion is to MIDI (with its notes) or to a pattern.");
            }

            if (converted.Notes.Count > MaxNotesPerCommand) {
                throw new EditException($"A region converts with at most {MaxNotesPerCommand} notes.");
            }

            var midi = new MidiRegion(
                ids.Next(IdPrefix.MidiRegion),
                source.TrackId,
                source.Name,
                source.StartTick,
                source.LengthTicks,
                ProjectDefaults.Playback,
                [],
                [],
                source.Muted,
                ProjectDefaults.Notation,
                PitchSourceMode.Off,
                null
            );
            var notes = new List<Note>(converted.Notes.Count);
            foreach (var n in converted.Notes) {
                var note = new Note(
                    ++next,
                    n?.Pitch ?? throw new EditException("A note is null."),
                    n.Velocity,
                    n.Channel,
                    n.StartTick,
                    n.LengthTicks,
                    n.ArticulationId ?? 0
                );
                CheckNote(midi, note, true);
                notes.Add(note);
            }

            ops.Add(new MidiRegionUpserted(midi with { Notes = Sorted(notes) }));
        }

        return ops;
    }

    /// <summary>
    ///     Parameters as a region keeps them: of a style of the player (and a preset of it, when named), in their ranges,
    ///     with the player's switches (the defaults when left out) and no other player's.
    /// </summary>
    public static SessionPlayerParameters CheckParameters(
        ProjectState state,
        SessionPlayerCatalog styles,
        SessionPlayerKind player,
        string trackId,
        SessionPlayerParameters? parameters
    ) {
        if (parameters?.Pad is null) {
            throw new EditException("Session Player parameters have a style and a pad.");
        }

        var style = StyleFor(styles, player, parameters.StyleId ?? throw new EditException("Name a style."));
        if (parameters.PresetId is { } presetId) {
            _ = PresetOf(style, presetId);
        }

        CheckUnit(parameters.Pad.Complexity, "complexity");
        CheckUnit(parameters.Pad.Intensity, "intensity");
        CheckUnit(parameters.FillAmount, "fill amount");
        CheckUnit(parameters.Swing, "swing");
        if (!(parameters.Feel is >= -1 and <= 1)) {
            throw new EditException($"The feel is −1…1, not {parameters.Feel}.");
        }

        if ((player != SessionPlayerKind.Drummer && parameters.Drums is not null)
            || (player != SessionPlayerKind.Bass && parameters.Bass is not null)
            || (player != SessionPlayerKind.Keyboard && parameters.Keys is not null)) {
            throw new EditException($"A {PlayerName(player)} region takes its own player's switches only.");
        }

        var normalised = Normalised(parameters, player);
        switch (player) {
            case SessionPlayerKind.Drummer:
                return normalised with { Drums = CheckDrums(state, trackId, normalised.Drums!) };
            case SessionPlayerKind.Bass:
                CheckRange(normalised.Bass!.LowNote, normalised.Bass.HighNote, MinBassNote, MaxBassNote, "bass");
                return normalised;
            default:
                var keys = normalised.Keys!;
                if (!Enum.IsDefined(keys.Voicing) || !Enum.IsDefined(keys.LeftHand)) {
                    throw new EditException("Unknown voicing or left-hand part.");
                }

                CheckRange(keys.LowNote, keys.HighNote, MinKeysNote, MaxKeysNote, "keys");
                if (keys.RhythmId is { } rhythm && style.Rhythms.All(r => r.Id != rhythm)) {
                    throw new EditException($"Style '{style.Name}' has no rhythm '{rhythm}'.");
                }

                return normalised;
        }
    }

    static DrummerSwitches CheckDrums(ProjectState state, string trackId, DrummerSwitches drums) {
        if (drums.Pieces is null
            || drums.Pieces.Count != Enum.GetValues<DrumPiece>().Length
            || drums.Pieces.Any(static p => p is null || !Enum.IsDefined(p.Piece) || p.Variation is < 0 or > 3)
            || drums.Pieces.DistinctBy(static p => p.Piece).Count() != drums.Pieces.Count) {
            throw new EditException("The Drummer's switches name every kit piece once, each with a variation 0…3.");
        }

        if (drums.RhythmSourceTrackId is { } source) {
            var track = state.FindTrack(source) ?? throw new EditException($"No track '{source}' to follow.");
            if (track.Id == trackId || !(track.Kind == TrackKind.Audio || HoldsMidi(track))) {
                throw new EditException($"Track '{track.Name}' is no rhythm source: follow another audio or MIDI track.");
            }
        }

        return drums with { Pieces = [..drums.Pieces.OrderBy(static p => p.Piece)] };
    }

    static void CheckRange(int low, int high, int min, int max, string what) {
        if (low < min || high > max || high - low < 12) {
            throw new EditException($"The {what} range is at least an octave within {min}…{max}, not {low}…{high}.");
        }
    }

    /// <summary>
    ///     Parameters as a package reads them (no style library at hand): every value clamped to its range, the player's
    ///     switches whole (its defaults where they are not) and no other player's. The rhythm source is checked once the
    ///     tracks are read.
    /// </summary>
    public static SessionPlayerParameters StoredParameters(SessionPlayerParameters p, SessionPlayerKind player) {
        static double Unit(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
        var normalised = Normalised(
            p with {
                Pad = new(Unit(p.Pad.Complexity), Unit(p.Pad.Intensity)),
                FillAmount = Unit(p.FillAmount),
                Swing = Unit(p.Swing),
                Feel = double.IsFinite(p.Feel) ? Math.Clamp(p.Feel, -1, 1) : 0
            },
            player
        );
        return player switch {
            SessionPlayerKind.Drummer => normalised with {
                Drums = normalised.Drums!.Pieces is { } pieces
                    && pieces.Count == DefaultDrums.Pieces.Count
                    && pieces.All(static x => x is not null && Enum.IsDefined(x.Piece) && x.Variation is >= 0 and <= 3)
                    && pieces.DistinctBy(static x => x.Piece).Count() == pieces.Count
                        ? normalised.Drums with { Pieces = [..pieces.OrderBy(static x => x.Piece)] }
                        : DefaultDrums with { RhythmSourceTrackId = normalised.Drums.RhythmSourceTrackId }
            },
            SessionPlayerKind.Bass => normalised.Bass is { } b
                && b.LowNote >= MinBassNote
                && b.HighNote <= MaxBassNote
                && b.HighNote - b.LowNote >= 12
                    ? normalised
                    : normalised with { Bass = DefaultBass },
            _ => normalised.Keys is { } k
                && Enum.IsDefined(k.Voicing)
                && Enum.IsDefined(k.LeftHand)
                && k.LowNote >= MinKeysNote
                && k.HighNote <= MaxKeysNote
                && k.HighNote - k.LowNote >= 12
                    ? normalised
                    : normalised with { Keys = DefaultKeys }
        };
    }

    /// <summary>Parameters with the player's switches (its defaults when left out) and none of the other players'.</summary>
    static SessionPlayerParameters Normalised(SessionPlayerParameters parameters, SessionPlayerKind player) =>
        parameters with {
            Drums = player == SessionPlayerKind.Drummer ? parameters.Drums ?? DefaultDrums : null,
            Bass = player == SessionPlayerKind.Bass ? parameters.Bass ?? DefaultBass : null,
            Keys = player == SessionPlayerKind.Keyboard ? parameters.Keys ?? DefaultKeys : null
        };

    /// <summary>
    ///     A style's defaults for a region over a section of <paramref name="kind" />: its section defaults, else its
    ///     first preset's, else the pad's centre.
    /// </summary>
    static SessionPlayerParameters Defaults(SessionPlayerStyle style, SectionKind kind, SessionPlayerKind player) {
        var parameters = style.Sections.FirstOrDefault(s => s.Kind == kind)?.Parameters
            ?? FirstPreset(style)?.Parameters
            ?? new(style.Id, null, new(0.5, 0.5), 0.5, true, 0, 0, true, null, null, null);
        return Normalised(parameters with { StyleId = style.Id }, player);
    }

    /// <summary>The style <paramref name="styleId" /> of the player (null: its first), from the catalogue.</summary>
    static SessionPlayerStyle StyleFor(SessionPlayerCatalog styles, SessionPlayerKind player, string? styleId) {
        foreach (var style in styles.Styles) {
            if (style.Player == player && (styleId is null || style.Id == styleId)) {
                return style;
            }
        }

        throw new EditException(
            styleId is null ? $"No {PlayerName(player)} style is installed." : $"No {PlayerName(player)} style '{styleId}'."
        );
    }

    static SessionPlayerPreset? FirstPreset(SessionPlayerStyle style) =>
        style.Presets.Count > 0 ? style.Presets[0] : null;

    static SessionPlayerPreset PresetOf(SessionPlayerStyle style, string presetId) =>
        style.Presets.FirstOrDefault(p => p.Id == presetId)
        ?? throw new EditException($"Style '{style.Name}' has no preset '{presetId}'.");

    /// <summary>
    ///     The style a new region of a track plays (tracks keep none of their own): its region before
    ///     <paramref name="tick" />, else its first, else the player's first style.
    /// </summary>
    static SessionPlayerStyle TrackStyle(
        ProjectState state,
        Track track,
        long tick,
        SessionPlayerCatalog styles,
        SessionPlayerKind player
    ) {
        var regions = state.SessionPlayerRegionsOf(track.Id).ToArray();
        var near = regions.LastOrDefault(r => r.StartTick <= tick) ?? regions.FirstOrDefault();
        return near is not null && styles.Styles.FirstOrDefault(s => s.Player == player && s.Id == near.Parameters.StyleId)
            is { } kept
            ? kept
            : StyleFor(styles, player, null);
    }

    /// <summary>The arrangement section <paramref name="tick" /> is in, or null.</summary>
    public static ArrangementMarker? SectionAt(ProjectState state, long tick) =>
        state.Sections.Values.FirstOrDefault(s => tick >= s.StartTick && tick < s.StartTick + s.LengthTicks);

    /// <summary><paramref name="bars" /> bars from <paramref name="tick" />, in ticks (by the signature track).</summary>
    static long Bars(ProjectState state, long tick, int bars) {
        var map = state.SignatureMap;
        var position = map.Position(tick);
        return map.TickOf(position with { Bar = position.Bar + bars }) - tick;
    }

    static (Track Track, SessionPlayerKind Player) SessionPlayerTrackOf(ProjectState state, string? id) {
        var track = TrackOf(state, id ?? string.Empty);
        return PlayerOf(track.Kind) is { } player
            ? (track, player)
            : throw new EditException($"Track '{track.Name}' is not a Drummer, Bass Player or Keyboard Player track.");
    }

    /// <summary>Track <paramref name="trackId" /> when it is a track of <paramref name="region" />'s player.</summary>
    static Track SamePlayerTrack(ProjectState state, SessionPlayerRegion region, string trackId) {
        var track = TrackOf(state, trackId);
        return track.Kind == state.FindTrack(region.TrackId)?.Kind
            ? track
            : throw new EditException($"Track '{track.Name}' is not a track of region '{region.Id}''s player.");
    }

    static SessionPlayerRegion SessionPlayerRegionOf(ProjectState state, string? id) =>
        state.SessionPlayerRegions.TryGetValue(id ?? string.Empty, out var region)
            ? region
            : throw new EditException($"No Session Player region '{id}'.");
}
