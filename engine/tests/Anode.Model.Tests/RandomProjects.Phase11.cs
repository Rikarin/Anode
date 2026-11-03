using Anode.Contracts;
using Anode.Model.Editing;

namespace Anode.Model.Tests;

/// <summary>
///     Phase 11's random content and commands (docs/12-phase-11.md): a style catalogue of two styles per player (a
///     stand-in for workstream D's library), chords and scales, Session Player tracks and regions, Pitch Source and
///     keys, a Mastering Assistant with an analysis, and an Environment with cables.
/// </summary>
static partial class RandomProjects {
    /// <summary>Two styles per player, each with two presets, its section defaults and (keys) two rhythms.</summary>
    public static SessionPlayerCatalog Styles { get; } = new(
        "test-1",
        [
            Style("drums.rock", SessionPlayerKind.Drummer, "pat_kit"),
            Style("drums.funk", SessionPlayerKind.Drummer, "pat_kit"),
            Style("bass.pop", SessionPlayerKind.Bass, null),
            Style("bass.synth", SessionPlayerKind.Bass, "pat_synth"),
            Style("keys.ballad", SessionPlayerKind.Keyboard, null),
            Style("keys.synth", SessionPlayerKind.Keyboard, "pat_synth", true)
        ]
    );

    static SessionPlayerStyle Style(string id, SessionPlayerKind player, string? patch, bool synth = false) {
        SessionPlayerParameters Parameters(double complexity, double intensity, string? preset) =>
            new(
                id,
                preset,
                new(complexity, intensity),
                0.5,
                true,
                0.2,
                0,
                true,
                player == SessionPlayerKind.Drummer ? Edits.DefaultDrums : null,
                player == SessionPlayerKind.Bass ? new(28, 52, true, false) : null,
                player == SessionPlayerKind.Keyboard ? new(KeysVoicing.Spread, 48, 79, KeysLeftHand.Root, null) : null
            );

        return new(
            id,
            id,
            player,
            "Test",
            "A test style.",
            synth,
            patch,
            player == SessionPlayerKind.Keyboard ? [new("eighths", "Eighths"), new("whole", "Whole Notes")] : [],
            [new($"{id}.soft", "Soft", Parameters(0.3, 0.3, $"{id}.soft")), new($"{id}.busy", "Busy", Parameters(0.9, 0.8, $"{id}.busy"))],
            [
                ..Enum.GetValues<SectionKind>()
                    .Select((k, i) => new SectionDefaults(k, Parameters(0.2 + 0.15 * i, 0.3 + 0.1 * i, null)))
            ]
        );
    }

    /// <summary>The Phase 11 command types (<see cref="Phase11" /> makes them).</summary>
    static readonly HashSet<string> Phase11Names = [
        nameof(AddChordsCommand), nameof(EditChordsCommand), nameof(DeleteChordsCommand), nameof(SetScalesCommand),
        nameof(ApplyChordIdCommand), nameof(SetPitchSourceCommand), nameof(SetRegionKeyCommand),
        nameof(AddSessionPlayerTrackCommand), nameof(AddSessionPlayerRegionCommand),
        nameof(SetSessionPlayerRegionCommand), nameof(RegenerateSessionPlayerCommand),
        nameof(ConvertSessionPlayerRegionsCommand), nameof(SetMasteringCommand), nameof(SetMasteringAnalysisCommand),
        nameof(AddEnvironmentLayerCommand), nameof(SetEnvironmentLayerCommand), nameof(RemoveEnvironmentLayerCommand),
        nameof(AddEnvironmentObjectCommand), nameof(SetEnvironmentObjectCommand),
        nameof(RemoveEnvironmentObjectsCommand), nameof(ConnectEnvironmentCommand),
        nameof(DisconnectEnvironmentCommand)
    ];

    /// <summary>The Phase 11 commands a random project is given (when it is built with more than 20 edits).</summary>
    static IEnumerable<Type> Phase11Content(Random random) => [
        typeof(AddChordsCommand),
        typeof(AddChordsCommand),
        typeof(SetScalesCommand),
        typeof(AddSessionPlayerTrackCommand),
        typeof(AddSessionPlayerTrackCommand),
        typeof(AddSessionPlayerRegionCommand),
        typeof(SetPitchSourceCommand),
        typeof(SetPitchSourceCommand),
        typeof(SetRegionKeyCommand),
        typeof(AddEnvironmentLayerCommand),
        ..Enumerable.Repeat(typeof(AddEnvironmentObjectCommand), random.Next(3, 7)),
        typeof(ConnectEnvironmentCommand),
        typeof(ConnectEnvironmentCommand),
        typeof(ConnectEnvironmentCommand)
    ];

    /// <summary>A random Phase 11 command of <paramref name="name" />, or null (another phase's, or none can be made).</summary>
    static EditCommand? Phase11(string name, ProjectState s, Random r) {
        var chords = s.ChordTrack.Chords;
        var players = s.SessionPlayerRegions.Values.ToArray();
        var playerTracks = s.Tracks.Where(static t => Edits.IsSessionPlayerKind(t.Kind)).ToArray();
        var environment = s.Environment;
        string[] following = [..s.MidiRegions.Keys, ..s.Regions.Keys];
        return name switch {
            nameof(AddChordsCommand) => new AddChordsCommand(
                [..Enumerable.Range(0, r.Next(1, 5)).Select(_ => NewChord(r))]
            ),
            nameof(EditChordsCommand) => chords.Count == 0
                ? null
                : new EditChordsCommand(
                    [
                        ..Some(chords, r)
                            .Select(c => c with {
                                    Tick = r.Next(3) == 0 ? r.Next(0, 32) * 960L : c.Tick,
                                    Root = r.Next(2) == 0 ? Name(r) : c.Root,
                                    Quality = r.Next(2) == 0 ? Pick(r, ChordQualities.All) : c.Quality,
                                    Bass = r.Next(3) == 0 ? Name(r) : r.Next(3) == 0 ? null : c.Bass
                                }
                            )
                    ]
                ),
            nameof(DeleteChordsCommand) => chords.Count == 0
                ? null
                : new DeleteChordsCommand([..Some(chords, r).Select(static c => c.Id)]),
            nameof(SetScalesCommand) => new SetScalesCommand(
                [
                    ..Enumerable.Range(0, r.Next(0, 4))
                        .Select(i => new ScaleChange(i * 15_360L, Name(r), Pick(r, Enum.GetValues<ScaleKind>())))
                ]
            ),
            nameof(ApplyChordIdCommand) => ChordId(s, r, following),
            nameof(SetPitchSourceCommand) => following.Length == 0
                ? null
                : new SetPitchSourceCommand(
                    [..Some(following, r)],
                    Pick(r, [PitchSourceMode.Off, PitchSourceMode.Root, PitchSourceMode.Root, PitchSourceMode.Chords])
                ),
            nameof(SetRegionKeyCommand) => following.Length == 0
                ? null
                : new SetRegionKeyCommand(
                    [..Some(following, r)],
                    r.Next(3) == 0 ? null : new(Name(r), Pick(r, Enum.GetValues<KeyMode>()))
                ),
            nameof(AddSessionPlayerTrackCommand) => AddPlayer(s, r),
            nameof(AddSessionPlayerRegionCommand) => playerTracks.Length == 0 ? null : AddPlayerRegion(s, Pick(r, playerTracks), r),
            nameof(SetSessionPlayerRegionCommand) => players.Length == 0 ? null : SetPlayer(s, Pick(r, players), r),
            nameof(RegenerateSessionPlayerCommand) => players.Length == 0
                ? null
                : new RegenerateSessionPlayerCommand(
                    [..Some(players, r).Select(static p => p.Id)],
                    r.Next(2) == 0 ? null : r.NextInt64(0, long.MaxValue)
                ),
            nameof(ConvertSessionPlayerRegionsCommand) => players.Length == 0
                ? null
                : new ConvertSessionPlayerRegionsCommand([..Some(players, r).Select(p => Converted(s, p, r))]),
            nameof(SetMasteringCommand) => new SetMasteringCommand(
                r.Next(5) == 0 ? "ins_none" : null,
                r.Next(2) == 0 ? Pick(r, Enum.GetValues<MasteringCharacter>()) : null,
                r.Next(2) == 0 ? -r.Next(6, 25) : null,
                r.Next(2) == 0 ? -r.Next(0, 4) * 0.5 : null,
                r.Next(2) == 0 ? Math.Round(r.NextDouble(), 2) : null,
                r.Next(2) == 0 ? Math.Round(r.NextDouble(), 2) : null,
                r.Next(2) == 0 ? Math.Round(r.NextDouble() * 2, 2) : null,
                r.Next(2) == 0 ? r.Next(2) == 0 : null
            ),
            nameof(SetMasteringAnalysisCommand) => Edits.MasteringAssistantOf(s) is { } assistant
                ? new SetMasteringAnalysisCommand(assistant.Id, MasteringAnalysis(r))
                : null,
            nameof(AddEnvironmentLayerCommand) => new AddEnvironmentLayerCommand(r.Next(2) == 0 ? null : $"Layer {r.Next(100)}"),
            nameof(SetEnvironmentLayerCommand) => environment.Layers.Count == 0
                ? null
                : new SetEnvironmentLayerCommand(
                    Pick(r, environment.Layers).Id,
                    r.Next(2) == 0 ? null : $"Renamed {r.Next(100)}",
                    r.Next(2) == 0 ? null : r.Next(0, environment.Layers.Count)
                ),
            nameof(RemoveEnvironmentLayerCommand) => environment.Layers.Count == 0
                ? null
                : new RemoveEnvironmentLayerCommand(Pick(r, environment.Layers).Id),
            nameof(AddEnvironmentObjectCommand) => AddObject(s, r),
            nameof(SetEnvironmentObjectCommand) => environment.Objects.Count == 0 ? null : SetObject(s, Pick(r, environment.Objects), r),
            nameof(RemoveEnvironmentObjectsCommand) => environment.Objects.Count == 0
                ? null
                : new RemoveEnvironmentObjectsCommand([..Some(environment.Objects, r).Select(static o => o.Id)]),
            nameof(ConnectEnvironmentCommand) => environment.Objects.Count < 2 ? null : Connect(environment, r),
            nameof(DisconnectEnvironmentCommand) => environment.Cables.Count == 0
                ? null
                : new DisconnectEnvironmentCommand([..Some(environment.Cables, r).Select(static c => c.Id)]),
            _ => null
        };
    }

    static PitchName Name(Random r) => new(Pick(r, Enum.GetValues<PitchStep>()), r.Next(-1, 2));

    static NewChord NewChord(Random r) =>
        new(
            r.Next(0, 32) * 960L,
            Name(r),
            r.Next(20) == 0 ? "min" : Pick(r, ChordQualities.All),
            r.Next(4) == 0 ? Name(r) : null
        );

    static ApplyChordIdCommand ChordId(ProjectState s, Random r, string[] following) {
        var start = r.Next(0, 8) * 3840L;
        var end = start + r.Next(1, 8) * 3840L;
        return new(
            new(
                start,
                end,
                Pick(r, Enum.GetValues<ChordIdWrite>()),
                [
                    ..Enumerable.Range(0, r.Next(0, 6))
                        .Select(_ => new DetectedChord(
                                start + r.Next(0, (int)((end - start) / 960)) * 960L,
                                960,
                                Name(r),
                                Pick(r, ChordQualities.All),
                                r.Next(4) == 0 ? Name(r) : null,
                                Math.Round(r.NextDouble(), 2)
                            )
                        )
                ],
                following.Length == 0 || r.Next(3) == 0
                    ? []
                    : [
                        ..Some(following, r)
                            .Select(id => new DetectedKey(id, new(Name(r), Pick(r, Enum.GetValues<KeyMode>())), 0.8))
                    ]
            )
        );
    }

    static AddSessionPlayerTrackCommand AddPlayer(ProjectState s, Random r) {
        var player = Pick(r, Enum.GetValues<SessionPlayerKind>());
        var styles = Styles.Styles.Where(st => st.Player == player).ToArray();
        var style = r.Next(2) == 0 ? null : Pick(r, styles);
        return new(
            player,
            style?.Id,
            style is not null && r.Next(2) == 0 ? Pick(r, style.Presets).Id : null,
            player == SessionPlayerKind.Drummer && r.Next(4) == 0 ? "pat_kit" : null,
            player != SessionPlayerKind.Drummer && r.Next(4) == 0 ? "anode.sampler" : null,
            r.Next(2) == 0 ? null : $"Player {r.Next(100)}",
            r.Next(3) == 0 ? r.Next(0, s.Tracks.Count + 1) : null,
            r.Next(4) != 0
        );
    }

    static AddSessionPlayerRegionCommand AddPlayerRegion(ProjectState s, Track track, Random r) {
        var player = Edits.PlayerOf(track.Kind)!.Value;
        var style = Pick(r, Styles.Styles.Where(st => st.Player == player).ToArray());
        return new(
            track.Id,
            r.Next(0, 32) * 960L,
            r.Next(2) == 0 ? null : r.Next(1, 9) * 3840L,
            r.Next(2) == 0 ? null : Pick(r, style.Presets).Parameters,
            r.Next(2) == 0 ? null : $"Groove {r.Next(100)}",
            r.Next(2) == 0 ? null : r.NextInt64(0, long.MaxValue)
        );
    }

    static SetSessionPlayerRegionCommand SetPlayer(ProjectState s, SessionPlayerRegion region, Random r) {
        var player = Edits.PlayerOf(s.FindTrack(region.TrackId)!.Kind)!.Value;
        var styles = Styles.Styles.Where(st => st.Player == player).ToArray();
        var style = r.Next(3) == 0 ? Pick(r, styles) : null;
        return new(
            region.Id,
            r.Next(4) == 0 ? r.Next(0, 32) * 960L : null,
            r.Next(4) == 0 ? r.Next(1, 9) * 3840L : null,
            r.Next(4) == 0 ? $"Renamed {r.Next(100)}" : null,
            r.Next(4) == 0 ? r.Next(2) == 0 : null,
            style?.Id,
            r.Next(4) == 0 ? Pick(r, (style ?? styles.First(st => st.Id == region.Parameters.StyleId)).Presets).Id : null,
            r.Next(2) == 0 ? Math.Round(r.NextDouble(), 2) : null,
            r.Next(2) == 0 ? Math.Round(r.NextDouble(), 2) : null,
            r.Next(4) == 0 ? Math.Round(r.NextDouble(), 2) : null,
            r.Next(4) == 0 ? r.Next(2) == 0 : null,
            r.Next(4) == 0 ? Math.Round(r.NextDouble() * 1.2, 2) : null,
            r.Next(4) == 0 ? Math.Round(r.NextDouble() * 2 - 1, 2) : null,
            r.Next(4) == 0 ? r.Next(2) == 0 : null,
            player == SessionPlayerKind.Drummer && r.Next(3) == 0
                ? new(
                    [..Enum.GetValues<DrumPiece>().Select(p => new DrumPieceSetting(p, r.Next(4) != 0, r.Next(0, 4)))],
                    r.Next(2) == 0 ? null : Pick(r, s.Tracks).Id
                )
                : null,
            player == SessionPlayerKind.Bass && r.Next(3) == 0 ? new(r.Next(24, 40), r.Next(50, 73), r.Next(2) == 0, r.Next(2) == 0) : null,
            player == SessionPlayerKind.Keyboard && r.Next(3) == 0
                ? new(
                    Pick(r, Enum.GetValues<KeysVoicing>()),
                    r.Next(36, 60),
                    r.Next(72, 97),
                    Pick(r, Enum.GetValues<KeysLeftHand>()),
                    r.Next(2) == 0 ? null : Pick(r, ["eighths", "whole", "none"])
                )
                : null
        );
    }

    static ConvertedSessionPlayerRegion Converted(ProjectState s, SessionPlayerRegion region, Random r) {
        var drummer = s.FindTrack(region.TrackId)?.Kind == TrackKind.Drummer;
        NewNote[] notes = [
            ..Enumerable.Range(0, r.Next(0, 9))
                .Select(i => new NewNote(drummer ? DrumKitNotes.Kick : 36 + r.Next(0, 24), r.Next(40, 128), 1, i * 240L, 240))
        ];
        return drummer && r.Next(2) == 0
            ? new(
                region.Id,
                SessionPlayerConvertTarget.Pattern,
                [],
                new(
                    [
                        new(
                            "row_kick",
                            PatternRowKind.Note,
                            DrumKitNotes.Kick,
                            "Kick",
                            16,
                            null,
                            PatternPlayMode.Forward,
                            false,
                            [..Enumerable.Range(0, 16).Select(i => new PatternStep(i % 4 == 0, 100, 0.5, 1, 1, 0))]
                        )
                    ],
                    0,
                    1
                )
            )
            : new(region.Id, SessionPlayerConvertTarget.Midi, notes, null);
    }

    public static MasteringAnalysis MasteringAnalysis(Random r) =>
        new(
            0,
            r.Next(1, 16) * 3840L,
            r.Next(0, 100),
            Math.Round(-30 + r.NextDouble() * 20, 2),
            Math.Round(-25 + r.NextDouble() * 20, 2),
            Math.Round(r.NextDouble() * 12, 2),
            Math.Round(-6 + r.NextDouble() * 6, 2),
            Math.Round(6 + r.NextDouble() * 12, 2),
            Math.Round(r.NextDouble(), 2),
            [..MasteringBands.CentresHz.Select(_ => Math.Round(r.NextDouble() * 12 - 6, 1))],
            [..MasteringBands.CentresHz.Select(_ => Math.Round(r.NextDouble() * 6 - 3, 1))]
        );

    static AddEnvironmentObjectCommand AddObject(ProjectState s, Random r) {
        var kind = Pick(r, Enum.GetValues<EnvironmentObjectKind>());
        var midi = s.Tracks.Where(Edits.HoldsMidi).ToArray();
        var strips = s.Tracks.Where(static t => Edits.HasAudioStrip(t.Kind)).ToArray();
        var target = kind == EnvironmentObjectKind.ControlTarget && strips.Length > 0 && r.Next(2) == 0 ? Pick(r, strips) : null;
        return new(
            kind,
            r.Next(0, 800),
            r.Next(0, 600),
            s.Environment.Layers.Count > 0 && r.Next(2) == 0 ? Pick(r, s.Environment.Layers).Id : null,
            r.Next(2) == 0 ? null : $"Object {r.Next(100)}",
            kind switch {
                EnvironmentObjectKind.Fader => new Dictionary<string, double> {
                    ["number"] = r.Next(0, 128), ["channel"] = r.Next(1, 17)
                },
                EnvironmentObjectKind.Keyboard => new Dictionary<string, double> { ["velocity"] = r.Next(1, 128) },
                EnvironmentObjectKind.Arpeggiator => new Dictionary<string, double> { ["rate"] = r.Next(0, 8) },
                _ => null
            },
            kind is EnvironmentObjectKind.PhysicalInput or EnvironmentObjectKind.MidiPort && r.Next(2) == 0
                ? $"Port {r.Next(4)}"
                : null,
            kind == EnvironmentObjectKind.TrackInput && midi.Length > 0
                ? Pick(r, midi).Id
                : target?.Id,
            kind == EnvironmentObjectKind.ControlTarget ? Pick(r, ["volume", "pan", "mute"]) : null,
            kind == EnvironmentObjectKind.Transformer
                ? [new(TransformField.Status, TransformConditionOp.Equal, 144), new(TransformField.Pitch, TransformConditionOp.Inside, 36, 84)]
                : null,
            kind == EnvironmentObjectKind.Transformer ? [new(TransformField.Pitch, TransformOperationKind.Add, 12)] : null,
            kind == EnvironmentObjectKind.DelayLine
                ? [..Enumerable.Range(1, r.Next(1, 4)).Select(i => new DelayTap(i * 240L, r.Next(-12, 13), -r.Next(0, 30)))]
                : null,
            kind == EnvironmentObjectKind.ChordMemorizer && r.Next(2) == 0 ? [0, 4, 7] : null
        );
    }

    static SetEnvironmentObjectCommand SetObject(ProjectState s, EnvironmentObject o, Random r) =>
        new(
            o.Id,
            r.Next(3) == 0 ? $"Renamed {r.Next(100)}" : null,
            r.Next(4) == 0 && s.Environment.Layers.Count > 0 ? Pick(r, s.Environment.Layers).Id : null,
            r.Next(2) == 0 ? r.Next(0, 800) : null,
            r.Next(2) == 0 ? r.Next(0, 600) : null,
            o.Kind == EnvironmentObjectKind.Fader && r.Next(2) == 0
                ? new Dictionary<string, double> { ["value"] = r.Next(0, 128) }
                : null,
            o.Kind is EnvironmentObjectKind.PhysicalInput or EnvironmentObjectKind.MidiPort && r.Next(3) == 0
                ? r.Next(2) == 0 ? string.Empty : $"Port {r.Next(4)}"
                : null,
            null,
            o.Kind == EnvironmentObjectKind.ControlTarget && r.Next(3) == 0 ? Pick(r, ["volume", "pan", string.Empty]) : null,
            null,
            o.Kind == EnvironmentObjectKind.Transformer && r.Next(2) == 0
                ? [new(TransformField.Velocity, TransformOperationKind.Fix, r.Next(1, 128))]
                : null,
            o.Kind == EnvironmentObjectKind.DelayLine && r.Next(2) == 0 ? [new(480, 7, 0)] : null,
            o.Kind == EnvironmentObjectKind.ChordMemorizer && r.Next(2) == 0 ? r.Next(2) == 0 ? [] : [0, 3, 7] : null
        );

    static ConnectEnvironmentCommand Connect(MidiEnvironment environment, Random r) {
        var from = Pick(r, environment.Objects);
        var to = Pick(r, environment.Objects);
        var outlets = EnvironmentObjectKinds.Of(from.Kind).Outlets.Count;
        var inlets = EnvironmentObjectKinds.Of(to.Kind).Inlets.Count;
        return new(from.Id, outlets == 0 ? 0 : r.Next(0, outlets), to.Id, inlets == 0 ? 0 : r.Next(0, inlets));
    }
}
