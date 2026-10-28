using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.Time;
using System.Text.Json;

namespace Anode.Model.Tests;

/// <summary>
///     Random projects and random edits for the properties: a project is built from a seed by applying random commands
///     (the ones that do not apply are skipped), so every state is one the server could reach.
/// </summary>
static class RandomProjects {
    /// <summary>Every command type of the contract, so a new one cannot go untested.</summary>
    public static IReadOnlyList<Type> CommandTypes { get; } = [
        ..typeof(EditCommand).GetCustomAttributes(
                typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute),
                false
            )
            .Cast<System.Text.Json.Serialization.JsonDerivedTypeAttribute>()
            .Select(static a => a.DerivedType)
    ];

    /// <summary>
    ///     A catalogue of plugins built here (not Anode.Dsp's, which another workstream fills): two built-in
    ///     instruments, a CLAP instrument with decimal parameter ids, and an effect that is not an instrument.
    /// </summary>
    public static PluginCatalog Plugins { get; } = new(
        [
            new(
                "anode.synth",
                "Synth",
                "Anode",
                "1.0",
                PluginFormat.Builtin,
                true,
                [],
                [
                    new("cutoff", "Cutoff", 20, 20_000, 8_000, "Hz", 0, null),
                    new("resonance", "Resonance", 0, 1, 0.2, string.Empty, 0, null),
                    new("wave", "Waveform", 0, 3, 0, string.Empty, 3, ["Saw", "Square", "Triangle", "Sine"]),
                    new("volume", "Volume", -60, 6, -6, "dB", 0, null)
                ],
                "Instrument",
                false,
                false
            ),
            new(
                "anode.sampler",
                "Sampler",
                "Anode",
                "1.0",
                PluginFormat.Builtin,
                true,
                [".sfz", ".wav"],
                [new("volume", "Volume", -60, 6, 0, "dB", 0, null), new("tune", "Tune", -100, 100, 0, "ct", 0, null)],
                "Instrument",
                false,
                false
            ),
            new(
                "com.example.sine",
                "Test Sine",
                "Example",
                "0.1",
                PluginFormat.Clap,
                true,
                [],
                [
                    new("1", "Gain", 0, 1, 0.5, string.Empty, 0, null),
                    new("2", "Detune", -1, 1, 0, string.Empty, 0, null)
                ],
                "Instrument",
                false,
                false
            ),
            new(
                "com.example.fx",
                "Test Delay",
                "Example",
                "0.1",
                PluginFormat.Clap,
                false,
                [],
                [],
                "Delay",
                false,
                false
            ),
            new(
                "anode.test-compressor",
                "Test Compressor",
                "Anode",
                "1.0",
                PluginFormat.Builtin,
                false,
                [],
                [
                    new("threshold", "Threshold", -60, 0, -20, "dB", 0, null),
                    new("ratio", "Ratio", 1, 20, 4, string.Empty, 0, null),
                    new("character", "Character", 0, 2, 0, string.Empty, 2, ["VCA", "FET", "Opto"])
                ],
                "Dynamics",
                true,
                false
            ),
            new(
                "com.example.vst3.gain",
                "Test Gain",
                "Example",
                "1.0",
                PluginFormat.Vst3,
                false,
                [],
                [
                    new("0", "Gain", -24, 24, 0, "dB", 0, null),
                    new("1", "Tilt", -1, 1, 0, string.Empty, 0, null),
                    new("tempo", "Tempo", 20, 300, 120, "bpm", 0, null)
                ],
                "Utility",
                false,
                true
            )
        ]
    );

    /// <summary>The analysis edits ask for (<see cref="FakeAnalysis" />).</summary>
    public static FakeAnalysis Analysis { get; } = new();

    /// <summary>The effects of <see cref="Plugins" />.</summary>
    public static IReadOnlyList<PluginInfo> Effects { get; } = [..Plugins.Plugins.Where(static p => !p.IsInstrument)];

    /// <summary>Two saved patches: an audio channel strip and an instrument one.</summary>
    public static TestPatches Patches { get; } = new(
        [
            new(
                "pat_strip",
                "Vocal Strip",
                "Vocals",
                TrackKind.Audio,
                null,
                [
                    new(
                        "ins_p1",
                        "anode.test-compressor",
                        "Test Compressor",
                        false,
                        new Dictionary<string, double> { ["threshold"] = -18, ["ratio"] = 3, ["character"] = 1 },
                        null
                    ),
                    new("ins_p2", "com.example.fx", "Test Delay", true, new Dictionary<string, double>(), "bus:3")
                ],
                [new("snd_p1", 2, -12, 0, false), new("snd_p2", 5, -20, 0.5, true)]
            ),
            new(
                "pat_synth",
                "Warm Pad",
                "Synths",
                TrackKind.Instrument,
                new(
                    "anode.synth",
                    "Synth",
                    new Dictionary<string, double> {
                        ["cutoff"] = 1200, ["resonance"] = 0.4, ["wave"] = 2, ["volume"] = -3
                    },
                    null
                ),
                [
                    new(
                        "ins_p3",
                        "com.example.vst3.gain",
                        "Test Gain",
                        false,
                        new Dictionary<string, double> { ["0"] = 3, ["1"] = 0 },
                        null
                    )
                ],
                []
            )
        ]
    );

    public sealed class TestPatches(IReadOnlyList<ChannelPatch> patches) : IPatchSource {
        public IReadOnlyList<ChannelPatch> All { get; } = patches;

        public ChannelPatch? Find(string id) => All.FirstOrDefault(p => p.Id == id);
    }

    public static (ProjectState State, SequentialIds Ids, Random Random) Build(int seed, int edits = 34) {
        var random = new Random(seed);
        var ids = new SequentialIds();
        var state = ProjectDefaults.NewProject("prj_test", "Test", 48_000);
        for (var i = 0; i < 3; i++) {
            state = state.Apply(
                new AudioFileUpserted(
                    new(
                        ids.Next(IdPrefix.File),
                        $"File {i}",
                        $"Audio Files/File {i}.wav",
                        48_000,
                        1 + i % 2,
                        random.Next(1_000, 400_000),
                        24,
                        null
                    )
                )
            );
        }

        for (var i = 0; i < edits; i++) {
            var type = i switch {
                < 4 => typeof(AddTrackCommand),
                < 7 => typeof(AddRegionCommand),
                < 10 => typeof(AddMidiRegionCommand),
                < 14 => typeof(AddNotesCommand),
                < 16 => typeof(AddControlsCommand),
                16 => typeof(PackTakeFolderCommand),
                17 => typeof(AddMarkerCommand),
                < 20 => typeof(AddSectionCommand),
                20 => typeof(CreateStackCommand),
                21 => typeof(AddAuxCommand),
                < 24 => typeof(AddInsertCommand),
                < 26 => typeof(AddSendCommand),
                26 => typeof(SetGroupCommand),
                27 => typeof(SetTrackGroupsCommand),
                28 => typeof(AddVcaCommand),
                29 => typeof(AssignVcaCommand),
                < 32 => typeof(SetAutomationCommand),
                32 => typeof(SetTrackFlexCommand),
                33 => Pick(
                    random,
                    [typeof(SetPitchNotesCommand), typeof(NewTrackAlternativeCommand), typeof(SetRegionFlexCommand)]
                ),
                _ => Pick(random, CommandTypes)
            };
            var command = i is >= 4 and < 7 && FirstAudioTrack(state) is { } audio && state.Files.Count > 0
                ? AddRegion(audio, Pick(random, [..state.Files.Values]), random)
                : i == 32 && FirstAudioTrack(state) is { } flexed && random.Next(2) == 0
                    ? new SetTrackFlexCommand(flexed, true, Pick(random, Enum.GetValues<FlexMode>()))
                    : Command(type, state, random);
            if (command is not null) {
                try {
                    state = Edits.Apply(state, command, ids, Plugins, Patches, Analysis).State;
                } catch (EditException) {
                    // not every random edit applies
                }
            }
        }

        // Half the time, a track is frozen (to a file of the pool, as a finished freeze job leaves it).
        if (edits > 20
            && random.Next(2) == 0
            && state.Tracks.Find(static t => t.Kind is TrackKind.Audio or TrackKind.Instrument) is { } frozen) {
            state = Edits.Freeze(state, frozen.Id, state.Files.Keys.First()).State;
        }

        return (state, ids, random);
    }

    static string? FirstAudioTrack(ProjectState state) => state.Tracks.Find(static t => t.Kind == TrackKind.Audio)?.Id;

    /// <summary>
    ///     Phase 6 commands (docs/07-phase-6.md) the model rejects until workstream B implements them; the identity
    ///     property skips them. (MIDI FX slots, the insert commands' <c>chain: midiEffects</c>, are rejected too, and the
    ///     generators never ask for them yet.)
    /// </summary>
    public static readonly IReadOnlySet<string> PendingPhase6 = new HashSet<string>(StringComparer.Ordinal) {
        nameof(AddPatternRegionCommand),
        nameof(SetPatternRegionCommand),
        nameof(SetPatternCommand),
        nameof(ConvertPatternToMidiCommand),
        nameof(ReseedPatternCommand),
        nameof(CreateDrumMachineCommand),
        nameof(AddDrumPadCommand),
        nameof(SetDrumPadCommand),
        nameof(RemoveDrumPadCommand),
        nameof(SetSmartControlsCommand),
        nameof(SetSmartControlCommand),
        nameof(SetSmartMappingsCommand),
        nameof(AutoMapSmartControlsCommand),
        nameof(CompareSmartControlsCommand),
        nameof(AddSceneCommand),
        nameof(RemoveSceneCommand),
        nameof(SetSceneCommand),
        nameof(SetCellCommand),
        nameof(DeleteCellsCommand),
        nameof(MoveCellCommand),
        nameof(SetLiveLoopsCommand),
        nameof(SetTrackActivationCommand)
    };

    /// <summary>A random command of <paramref name="type" /> for <paramref name="s" />, or null when none can be made.</summary>
    public static EditCommand? Command(Type type, ProjectState s, Random r) {
        var tracks = s.Tracks;
        var regions = s.Regions.Values.ToArray();
        var midiRegions = s.MidiRegions.Values.ToArray();
        var files = s.Files.Values.ToArray();
        var instruments = tracks.Where(static t => t.Kind == TrackKind.Instrument).ToArray();
        var midiTracks = tracks.Where(static t => Edits.IsMidiKind(t.Kind)).ToArray();
        var folders = s.TakeFolders.Values.ToArray();
        var markers = s.Markers.Values.ToArray();
        var sections = s.Sections.Values.ToArray();
        var stacks = tracks.Where(static t => t.Kind == TrackKind.Stack).ToArray();
        var plain = tracks.Where(static t => t.Kind != TrackKind.Stack).ToArray();
        var mixed = tracks.Where(static t => Edits.HasAudioStrip(t.Kind)).ToArray();
        var vcas = tracks.Where(static t => t.Kind == TrackKind.Vca).ToArray();
        var groups = s.Groups.Keys.ToArray();
        return type.Name switch {
            nameof(AddTrackCommand) => AddTrack(r, tracks.Count),
            nameof(RemoveTrackCommand) => Track() is { } t ? new RemoveTrackCommand(t) : null,
            nameof(MoveTrackCommand) => Track() is { } t ? new MoveTrackCommand(t, r.Next(0, tracks.Count)) : null,
            nameof(SetTrackCommand) => Track() is { } t
                ? new SetTrackCommand(
                    t,
                    MaybeText($"Renamed {r.Next(100)}"),
                    MaybeText("#123456"),
                    r.Next(2) == 0 ? null : new TrackInput(r.Next(0, 9), r.Next(2) == 0),
                    Maybe(r.Next(2) == 0),
                    Maybe(r.Next(2) == 0),
                    Maybe(r.Next(2) == 0),
                    MaybeText($"Notes {r.Next(100)}")
                )
                : null,
            nameof(SetStripCommand) => new SetStripCommand(
                r.Next(3) == 0 ? null : Track(),
                Maybe(Math.Round(r.NextDouble() * 150 - 144, 2)),
                Maybe(Math.Round(r.NextDouble() * 2 - 1, 3)),
                Maybe(r.Next(2) == 0),
                Maybe(r.Next(2) == 0)
            ),
            nameof(AddRegionCommand) => Track() is { } t && files.Length > 0 ? AddRegion(t, Pick(r, files), r) : null,
            nameof(MoveRegionCommand) => AnyRegion() is { } id
                ? new MoveRegionCommand(id, r.NextInt64(0, 960 * 64), r.Next(2) == 0 ? null : Track())
                : null,
            nameof(ResizeRegionCommand) => regions.Length > 0 ? Resize(Pick(r, regions), s, r) : null,
            nameof(SplitRegionCommand) => r.Next(2) == 0 && midiRegions.Length > 0
                ? SplitMidi(Pick(r, midiRegions), r)
                : regions.Length > 0
                    ? Split(Pick(r, regions), s, r)
                    : null,
            nameof(SetRegionCommand) => regions.Length > 0 ? SetRegion(Pick(r, regions), r) : null,
            nameof(DeleteRegionsCommand) => AnyRegion() is not null
                ? new DeleteRegionsCommand([..Enumerable.Range(0, r.Next(1, 4)).Select(_ => AnyRegion()!)])
                : null,
            nameof(SetTempoCommand) => new SetTempoCommand(Math.Round(40 + r.NextDouble() * 200, 1)),
            nameof(SetSignatureCommand) => new SetSignatureCommand(r.Next(1, 13), Pick(r, [2, 4, 8, 16])),
            nameof(SetCycleCommand) => Cycle(r),
            nameof(SetMetronomeCommand) => new SetMetronomeCommand(
                Maybe(r.Next(2) == 0),
                Maybe(r.Next(2) == 0),
                Maybe((double)r.Next(-40, 1))
            ),
            nameof(RenameProjectCommand) => new RenameProjectCommand($"Song {r.Next(1000)}"),
            nameof(SetInstrumentCommand) => Instrument() is { } t
                ? new SetInstrumentCommand(t.Id, Pick(r, Plugins.Plugins).Id)
                : null,
            nameof(SetParameterCommand) => Instrument() is { } t ? SetParameter(t, r) : null,
            nameof(LoadInstrumentFileCommand) => (Loader() ?? Instrument()) is { } t
                ? new LoadInstrumentFileCommand(t.Id, $"/samples/Kit {r.Next(10)}{Pick(r, [".sfz", ".wav", ".txt"])}")
                : null,
            nameof(SetMidiIoCommand) => MidiTrack() is { } t ? SetMidiIo(t, r) : null,
            nameof(AddMidiRegionCommand) => MidiTrack() is { } t
                ? new AddMidiRegionCommand(
                    t.Id,
                    r.Next(16) * 960L,
                    r.Next(1, 9) * 960L,
                    MaybeText($"Riff {r.Next(100)}")
                )
                : null,
            nameof(ResizeMidiRegionCommand) => MidiRegion() is { } m
                ? new ResizeMidiRegionCommand(
                    m.Id,
                    Math.Max(0, m.StartTick + r.Next(-4, 5) * 240L),
                    Math.Max(1, m.LengthTicks + r.Next(-4, 5) * 240L)
                )
                : null,
            nameof(SetMidiRegionCommand) => MidiRegion() is { } m
                ? new SetMidiRegionCommand(
                    m.Id,
                    MaybeText($"Riff {r.Next(100)}"),
                    Maybe(r.Next(-24, 25)),
                    Maybe(r.Next(-64, 65)),
                    Maybe(Pick(r, [0, 120, 240, 480, 960])),
                    Maybe(Math.Round(r.NextDouble(), 2)),
                    Maybe(Math.Round(r.NextDouble(), 2)),
                    Maybe(r.Next(2) == 0)
                )
                : null,
            nameof(AddNotesCommand) => MidiRegion() is { } m
                ? new AddNotesCommand(m.Id, [..Enumerable.Range(0, r.Next(1, 9)).Select(_ => NewNote(m, r))])
                : null,
            nameof(EditNotesCommand) => WithNotes() is { } m ? EditNotes(m, r) : null,
            nameof(DeleteNotesCommand) => WithNotes() is { } m
                ? new DeleteNotesCommand(m.Id, [..Some(m.Notes, r).Select(static n => n.Id)])
                : null,
            nameof(QuantizeNotesCommand) => WithNotes() is { } m
                ? new QuantizeNotesCommand(
                    m.Id,
                    Pick(r, [120, 160, 240, 480, 960]),
                    Math.Round(r.NextDouble(), 2),
                    r.Next(2) == 0 ? 0 : Math.Round(r.NextDouble(), 2),
                    r.Next(2) == 0 ? null : [..Some(m.Notes, r).Select(static n => n.Id)]
                )
                : null,
            nameof(AddControlsCommand) => MidiRegion() is { } m
                ? new AddControlsCommand(m.Id, [..Enumerable.Range(0, r.Next(1, 5)).Select(_ => NewControl(m, r))])
                : null,
            nameof(DeleteControlsCommand) => WithControls() is { } m
                ? new DeleteControlsCommand(m.Id, [..Some(m.Controls, r).Select(static e => e.Id)])
                : null,
            nameof(SetMidiRecordingCommand) => new SetMidiRecordingCommand(r.Next(2) == 0),
            nameof(SetTempoEventsCommand) => new SetTempoEventsCommand(TempoEvents(r)),
            nameof(SetSignaturesCommand) => new SetSignaturesCommand(Signatures(r)),
            nameof(SetKeysCommand) => new SetKeysCommand(
                [
                    new(0, r.Next(12), Pick(r, [KeyMode.Major, KeyMode.Minor])),
                    ..r.Next(2) == 0 ? [] : new[] { new KeyEvent(r.Next(1, 64) * 960L, r.Next(12), KeyMode.Minor) }
                ]
            ),
            nameof(AddMarkerCommand) => new AddMarkerCommand(
                $"Marker {r.Next(100)}",
                r.Next(64) * 480L,
                r.Next(3) == 0 ? r.Next(1, 8) * 960L : 0,
                MaybeText("#ffaa00")
            ),
            nameof(SetMarkerCommand) => markers.Length > 0
                ? new SetMarkerCommand(
                    Pick(r, markers).Id,
                    MaybeText($"Renamed {r.Next(100)}"),
                    r.Next(2) == 0 ? null : r.Next(64) * 480L,
                    r.Next(2) == 0 ? null : r.Next(4) * 960L,
                    MaybeText("#00aaff"),
                    MaybeText($"Text {r.Next(100)}")
                )
                : null,
            nameof(DeleteMarkersCommand) => markers.Length > 0
                ? new DeleteMarkersCommand([..Some(markers, r).Select(static m => m.Id)])
                : null,
            nameof(AddSectionCommand) => new AddSectionCommand(
                Pick(r, ["Intro", "Verse", "Chorus", "Bridge"]),
                r.Next(16) * 3840L,
                r.Next(1, 5) * 3840L,
                MaybeText("#5fa86b")
            ),
            nameof(SetSectionCommand) => sections.Length > 0
                ? new SetSectionCommand(
                    Pick(r, sections).Id,
                    MaybeText($"Part {r.Next(10)}"),
                    r.Next(2) == 0 ? null : r.Next(1, 5) * 1920L,
                    MaybeText("#a86b5f")
                )
                : null,
            nameof(DeleteSectionsCommand) => sections.Length > 0
                ? new DeleteSectionsCommand([..Some(sections, r).Select(static x => x.Id)], r.Next(2) == 0)
                : null,
            nameof(MoveSectionCommand) => sections.Length > 0
                ? new MoveSectionCommand(
                    Pick(r, sections).Id,
                    Pick(r, sections) is var to && r.Next(2) == 0 ? to.StartTick : to.StartTick + to.LengthTicks,
                    r.Next(3) == 0
                )
                : null,
            nameof(SetCompCommand) => folders.Length > 0 ? Comp(Pick(r, folders), r) : null,
            nameof(SelectTakeCommand) => folders.Length > 0 && Pick(r, folders) is var f
                ? new SelectTakeCommand(f.Id, Pick(r, f.Takes).Id)
                : null,
            nameof(SetTakeFolderCommand) => folders.Length > 0 && Pick(r, folders) is var f
                ? new SetTakeFolderCommand(
                    f.Id,
                    MaybeText($"Takes {r.Next(100)}"),
                    r.Next(2) == 0 ? null : r.Next(0, 4800),
                    Maybe(r.Next(2) == 0),
                    r.Next(2) == 0 ? null : Pick(r, f.Takes).Id,
                    null
                ) is var set
                && set.TakeId is not null
                && r.Next(2) == 0
                    ? set with { TakeName = $"Take {r.Next(100)}" }
                    : set
                : null,
            nameof(DeleteTakeCommand) => folders.Where(static x => x.Takes.Count > 1).ToArray() is { Length: > 0 } many
                && Pick(r, many) is var f
                    ? new DeleteTakeCommand(f.Id, Pick(r, f.Takes).Id)
                    : null,
            nameof(FlattenTakeFolderCommand) => folders.Length > 0
                ? new FlattenTakeFolderCommand(Pick(r, folders).Id)
                : null,
            nameof(PackTakeFolderCommand) => Pack(regions, r),
            nameof(SetRecordingCommand) => r.Next(32) is var bar
                ? new SetRecordingCommand(
                    r.Next(2) == 0 ? null : Pick(r, [CycleRecordMode.TakeFolder, CycleRecordMode.Regions]),
                    Maybe(r.Next(2) == 0),
                    Maybe(r.Next(2) == 0),
                    r.Next(3) == 0 ? null : bar * 3840L,
                    r.Next(3) == 0 ? null : (bar + r.Next(1, 8)) * 3840L
                )
                : null,
            nameof(CreateStackCommand) => plain.Length > 0
                ? new CreateStackCommand([..Some(plain, r).Select(static t => t.Id)], MaybeText($"Stack {r.Next(10)}"))
                : null,
            nameof(RemoveStackCommand) => stacks.Length > 0 ? new RemoveStackCommand(Pick(r, stacks).Id) : null,
            nameof(SetTrackParentCommand) => plain.Length > 0
                ? new SetTrackParentCommand(
                    Pick(r, plain).Id,
                    stacks.Length > 0 && r.Next(3) > 0 ? Pick(r, stacks).Id : null
                )
                : null,
            nameof(SetProjectNotesCommand) => new SetProjectNotesCommand($"Notes {r.Next(100)}"),
            nameof(JoinRegionsCommand) => Join(regions, midiRegions, r),
            nameof(CopyRegionsCommand) => AnyRegion() is not null
                ? new CopyRegionsCommand(
                    [..Enumerable.Range(0, r.Next(1, 3)).Select(_ => AnyRegion()!)],
                    r.Next(-4, 16) * 960L,
                    r.Next(3) == 0 ? Track() : null
                )
                : null,
            nameof(AddInsertCommand) => new AddInsertCommand(
                r.Next(12) == 0 ? Pick(r, Plugins.Plugins).Id : Pick(r, Effects).Id,
                r.Next(5) == 0 || mixed.Length == 0 ? null : Pick(r, mixed).Id,
                r.Next(2) == 0 ? null : r.Next(0, 3)
            ),
            nameof(RemoveInsertCommand) => WithInserts() is var (t, strip) && strip is not null
                ? new RemoveInsertCommand(Pick(r, strip.Inserts).Id, t)
                : null,
            nameof(MoveInsertCommand) => WithInserts() is var (t, strip) && strip is not null
                ? new MoveInsertCommand(
                    Pick(r, strip.Inserts).Id,
                    r.Next(0, strip.Inserts.Count + (r.Next(8) == 0 ? 1 : 0)),
                    t
                )
                : null,
            nameof(SetInsertCommand) => WithInserts() is var (t, strip) && strip is not null
                ? new SetInsertCommand(
                    Pick(r, strip.Inserts).Id,
                    t,
                    Maybe(r.Next(2) == 0),
                    r.Next(3) switch {
                        0 => null,
                        1 => Pick(r, [string.Empty, $"bus:{r.Next(1, 9)}", "bus:99"]),
                        _ => mixed.Length > 0 ? Pick(r, mixed).Id : null
                    }
                )
                : null,
            nameof(SetInsertParameterCommand) => WithInserts() is var (t, strip) && strip is not null
                ? InsertParameter(Pick(r, strip.Inserts), t, r)
                : null,
            nameof(AddSendCommand) => plain.Length > 0
                ? new AddSendCommand(
                    (mixed.Length > 0 && r.Next(6) > 0 ? Pick(r, mixed) : Pick(r, plain)).Id,
                    r.Next(16) == 0 ? 65 : r.Next(1, 9),
                    r.Next(2) == 0 ? null : Math.Round(r.NextDouble() * 50 - 44, 1),
                    Maybe(r.Next(2) == 0)
                )
                : null,
            nameof(SetSendCommand) => WithSends() is { } t
                ? new SetSendCommand(
                    t.Id,
                    Pick(r, t.Strip.Sends).Id,
                    Maybe(Math.Round(r.NextDouble() * 150 - 144, 1)),
                    Maybe(Math.Round(r.NextDouble() * 2 - 1, 2)),
                    Maybe(r.Next(2) == 0),
                    r.Next(4) == 0 ? r.Next(1, 9) : null
                )
                : null,
            nameof(RemoveSendCommand) => WithSends() is { } t
                ? new RemoveSendCommand(t.Id, Pick(r, t.Strip.Sends).Id)
                : null,
            nameof(SetOutputCommand) => mixed.Length > 0
                ? new SetOutputCommand(
                    Pick(r, mixed).Id,
                    Pick(r, ["master", $"bus:{r.Next(1, 9)}", $"output:{r.Next(1, 5)}", "bus:0", "speakers"])
                )
                : null,
            nameof(AddAuxCommand) => new AddAuxCommand(
                r.Next(2) == 0 ? null : r.Next(1, 9),
                MaybeText($"FX {r.Next(10)}")
            ),
            nameof(SetGroupCommand) => new SetGroupCommand(
                r.Next(16) == 0 ? 33 : r.Next(1, 5),
                MaybeText($"Group {r.Next(10)}"),
                MaybeText("#4f8ff7"),
                Maybe(r.Next(4) > 0),
                r.Next(2) == 0
                    ? null
                    : new(
                        r.Next(4) > 0,
                        r.Next(2) == 0,
                        r.Next(2) == 0,
                        r.Next(2) == 0,
                        r.Next(2) == 0,
                        r.Next(2) == 0,
                        r.Next(2) == 0,
                        r.Next(2) == 0
                    )
            ),
            nameof(DeleteGroupCommand) => groups.Length > 0 ? new DeleteGroupCommand(Pick(r, groups)) : null,
            nameof(SetTrackGroupsCommand) => plain.Length > 0
                ? new SetTrackGroupsCommand(Pick(r, plain).Id, groups.Length > 0 ? [..Some(groups, r)] : [])
                : null,
            nameof(AddVcaCommand) => new AddVcaCommand(MaybeText($"VCA {r.Next(10)}")),
            nameof(AssignVcaCommand) => plain.Length > 0
                ? new AssignVcaCommand(Pick(r, plain).Id, vcas.Length > 0 && r.Next(4) > 0 ? Pick(r, vcas).Id : null)
                : null,
            nameof(SetAutomationCommand) => Automation(s, r),
            nameof(SetAutomationModeCommand) => new SetAutomationModeCommand(
                Pick(r, Enum.GetValues<AutomationMode>()),
                r.Next(4) == 0 ? null : Track()
            ),
            nameof(SetMixerSettingsCommand) => new SetMixerSettingsCommand(
                r.Next(2) == 0 ? null : Pick(r, Enum.GetValues<PdcMode>()),
                Maybe(r.Next(2) == 0),
                Maybe(Math.Round(r.NextDouble() * 50, 1))
            ),
            nameof(ApplyPatchCommand) => mixed.Length > 0
                ? new ApplyPatchCommand(Pick(r, mixed).Id, Pick(r, ["pat_strip", "pat_synth", "pat_strip"]))
                : null,
            nameof(UnfreezeTrackCommand) => tracks.Where(static t => t.FrozenFileId is not null).ToArray() is {
                Length: > 0
            } frozen
                ? new UnfreezeTrackCommand(Pick(r, frozen).Id)
                : plain.Length > 0 && r.Next(4) == 0
                    ? new UnfreezeTrackCommand(Pick(r, plain).Id)
                    : null,
            // Phase 5 commands (docs/06-phase-5.md).
            nameof(SetTrackFlexCommand) => AudioTrack() is { } t
                ? new SetTrackFlexCommand(
                    t.Id,
                    r.Next(4) == 0 ? null : r.Next(3) > 0,
                    r.Next(2) == 0 ? null : Pick(r, Enum.GetValues<FlexMode>())
                )
                : null,
            nameof(SetRegionFlexCommand) => regions.Length > 0
                ? new SetRegionFlexCommand(
                    Pick(r, regions).Id,
                    r.Next(3) == 0 ? null : Pick(r, Enum.GetValues<FlexFollow>()),
                    r.Next(2) == 0 ? null : r.Next(-12, 13),
                    r.Next(2) == 0 ? null : r.Next(-50, 51)
                )
                : null,
            nameof(SetFlexMarkersCommand) => Flexed() is { } region ? Markers(s, region, r) : null,
            nameof(QuantizeAudioCommand) => regions.Length > 0
                ? new QuantizeAudioCommand(
                    [..Some(regions, r).Select(static x => x.Id)],
                    Pick(r, [120L, 240, 480, 960]),
                    Math.Round(r.NextDouble(), 2),
                    r.Next(2) == 0 ? 0 : Math.Round(r.NextDouble(), 2)
                )
                : null,
            nameof(SetPitchNotesCommand) => regions.Length > 0 ? PitchNotes(Pick(r, regions), r) : null,
            nameof(FlexPitchToMidiCommand) =>
                regions.Where(static x => x.PitchNotes.Count > 0).ToArray() is { Length: > 0 } pitched
                    ? new FlexPitchToMidiCommand(
                        Pick(r, pitched).Id,
                        r.Next(2) == 0 || midiTracks.Length == 0 ? null : Pick(r, midiTracks).Id
                    )
                    : null,
            nameof(SetFileTempoCommand) => files.Length > 0 ? FileTempo(Pick(r, files), r) : null,
            nameof(AdaptTempoCommand) => regions.Length > 0 ? new AdaptTempoCommand(Pick(r, regions).Id) : null,
            nameof(MapBeatsCommand) => MapBeats(s, r),
            nameof(StripSilenceCommand) => regions.Length > 0
                ? new StripSilenceCommand(
                    [..Some(regions, r).Select(static x => x.Id)],
                    -40 - r.Next(20),
                    r.Next(0, 200),
                    r.Next(0, 50),
                    r.Next(0, 100)
                )
                : null,
            nameof(NewTrackAlternativeCommand) => plain.Length > 0
                ? new NewTrackAlternativeCommand(Pick(r, plain).Id, MaybeText("Alt"), r.Next(2) == 0)
                : null,
            nameof(SelectTrackAlternativeCommand) => Alternating() is { } t
                ? new SelectTrackAlternativeCommand(t.Id, Pick(r, t.Alternatives).Id)
                : null,
            nameof(SetTrackAlternativeCommand) => Alternating() is { } t
                ? new SetTrackAlternativeCommand(t.Id, Pick(r, t.Alternatives).Id, $"Take {r.Next(10)}")
                : null,
            nameof(DeleteTrackAlternativeCommand) => Alternating() is { } t
                ? new DeleteTrackAlternativeCommand(t.Id, Pick(r, t.Alternatives).Id)
                : null,
            nameof(SetVarispeedCommand) => new SetVarispeedCommand(
                Maybe(r.Next(2) == 0),
                r.Next(2) == 0 ? null : Pick(r, Enum.GetValues<VarispeedMode>()),
                Maybe(Math.Round(0.4 + r.NextDouble() * 1.2, 2))
            ),
            nameof(SetSmartTempoCommand) => new SetSmartTempoCommand(
                r.Next(2) == 0 ? null : Pick(r, Enum.GetValues<ProjectTempoMode>()),
                r.Next(2) == 0 ? null : Pick(r, Enum.GetValues<FlexFollow>()),
                Maybe(r.Next(2) == 0)
            ),
            // Phase 6 commands (docs/07-phase-6.md): placeholders until the model implements them (workstream B
            // replaces these with generators that make commands that apply, and removes PendingPhase6).
            nameof(AddPatternRegionCommand) => Instrument() is { } t
                ? new AddPatternRegionCommand(t.Id, r.Next(0, 16) * 960L)
                : null,
            nameof(SetPatternRegionCommand) => new SetPatternRegionCommand("pat_none", Muted: r.Next(2) == 0),
            nameof(SetPatternCommand) => new SetPatternCommand("pat_none", new([], 0, r.Next())),
            nameof(ConvertPatternToMidiCommand) => new ConvertPatternToMidiCommand("pat_none"),
            nameof(ReseedPatternCommand) => new ReseedPatternCommand("pat_none", r.Next()),
            nameof(CreateDrumMachineCommand) => new CreateDrumMachineCommand(),
            nameof(AddDrumPadCommand) => Instrument() is { } t ? new AddDrumPadCommand(t.Id, 36 + r.Next(16)) : null,
            nameof(SetDrumPadCommand) => Instrument() is { } t
                ? new SetDrumPadCommand(t.Id, 36 + r.Next(16), "Kick")
                : null,
            nameof(RemoveDrumPadCommand) => Instrument() is { } t
                ? new RemoveDrumPadCommand(t.Id, 36 + r.Next(16))
                : null,
            nameof(SetSmartControlsCommand) => Track() is { } t
                ? new SetSmartControlsCommand(t, Pick(r, Enum.GetValues<SmartControlsLayout>()))
                : null,
            nameof(SetSmartControlCommand) => Track() is { } t
                ? new SetSmartControlCommand(t, "knob1", Math.Round(r.NextDouble(), 3))
                : null,
            nameof(SetSmartMappingsCommand) => Track() is { } t
                ? new SetSmartMappingsCommand(t, "knob1", [new("volume", -12, 0, false, [new(0, 0), new(1, 1)])])
                : null,
            nameof(AutoMapSmartControlsCommand) => Track() is { } t ? new AutoMapSmartControlsCommand(t) : null,
            nameof(CompareSmartControlsCommand) => Track() is { } t
                ? new CompareSmartControlsCommand(t, r.Next(2) == 0)
                : null,
            nameof(AddSceneCommand) => new AddSceneCommand(MaybeText("Scene")),
            nameof(RemoveSceneCommand) => new RemoveSceneCommand("scn_none"),
            nameof(SetSceneCommand) => new SetSceneCommand("scn_none", "Verse"),
            nameof(SetCellCommand) => Track() is { } t
                ? new SetCellCommand(t, "scn_none", PlayMode: Pick(r, Enum.GetValues<CellPlayMode>()))
                : null,
            nameof(DeleteCellsCommand) => new DeleteCellsCommand(["cel_none"]),
            nameof(MoveCellCommand) => Track() is { } t ? new MoveCellCommand("cel_none", t, "scn_none") : null,
            nameof(SetLiveLoopsCommand) => new SetLiveLoopsCommand(
                r.Next(2) == 0 ? null : Pick(r, Enum.GetValues<LaunchQuantize>()),
                Maybe(r.Next(2) == 0)
            ),
            nameof(SetTrackActivationCommand) => Track() is { } t
                ? new SetTrackActivationCommand([t], r.Next(2) == 0)
                : null,
            _ => throw new InvalidOperationException($"No generator for {type.Name}.")
        };
        string? Track() => tracks.Count == 0 ? null : Pick(r, tracks).Id;

        Track? AudioTrack() =>
            tracks.Where(static t => t.Kind == TrackKind.Audio).ToArray() is { Length: > 0 } audio
                ? Pick(r, audio)
                : null;

        AudioRegion? Flexed() =>
            regions.Where(x => s.FindTrack(x.TrackId)?.FlexOn == true).ToArray() is { Length: > 0 } flexed
                ? Pick(r, flexed)
                : null;

        Track? Alternating() =>
            tracks.Where(static t => t.Alternatives.Count > 0).ToArray() is { Length: > 0 } some ? Pick(r, some) : null;

        // A strip with inserts: a track's (its id) or the master's (null).
        (string? TrackId, ChannelStrip? Strip) WithInserts() {
            var with = mixed.Where(static t => t.Strip.Inserts.Count > 0).ToArray();
            if (s.Master.Inserts.Count > 0 && (with.Length == 0 || r.Next(4) == 0)) {
                return (null, s.Master);
            }

            return with.Length == 0 ? (null, null) : Pick(r, with) is var t ? (t.Id, t.Strip) : default;
        }

        Track? WithSends() =>
            mixed.Where(static t => t.Strip.Sends.Count > 0).ToArray() is { Length: > 0 } some ? Pick(r, some) : null;

        Track? Instrument() => instruments.Length == 0 ? null : Pick(r, instruments);

        Track? Loader() =>
            instruments.Where(static t => Plugins.Find(t.Instrument!.PluginId)?.FileTypes.Count > 0).ToArray() is {
                Length: > 0
            } some
                ? Pick(r, some)
                : null;

        Track? MidiTrack() => midiTracks.Length == 0 ? null : Pick(r, midiTracks);
        MidiRegion? MidiRegion() => midiRegions.Length == 0 ? null : Pick(r, midiRegions);

        MidiRegion? WithNotes() =>
            midiRegions.Where(static m => m.Notes.Count > 0).ToArray() is { Length: > 0 } some ? Pick(r, some) : null;

        MidiRegion? WithControls() =>
            midiRegions.Where(static m => m.Controls.Count > 0).ToArray() is { Length: > 0 } some
                ? Pick(r, some)
                : null;

        string? AnyRegion() =>
            regions.Length + midiRegions.Length + folders.Length == 0
                ? null
                : r.Next(regions.Length + midiRegions.Length + folders.Length) is var i && i < regions.Length
                    ? regions[i].Id
                    : i < regions.Length + midiRegions.Length
                        ? midiRegions[i - regions.Length].Id
                        : folders[i - regions.Length - midiRegions.Length].Id;

        T? Maybe<T>(T value) where T : struct => r.Next(2) == 0 ? null : value;
        string? MaybeText(string value) => r.Next(2) == 0 ? null : value;
    }

    /// <summary>Flex markers on a region of a flexed track: mostly valid (inside, in order), sometimes not.</summary>
    static SetFlexMarkersCommand Markers(ProjectState s, AudioRegion region, Random r) {
        var timing = RegionTiming.Of(s, region with { FlexMarkers = [] });
        var frames = Enumerable.Range(0, r.Next(0, 5))
            .Select(_ => region.FileOffset + r.NextInt64(1, Math.Max(2, region.Length)))
            .Distinct()
            .Order()
            .ToArray();
        var markers = new List<FlexMarker>();
        foreach (var frame in frames) {
            var tick = (long)Math.Round(timing.Base(frame) * (0.9 + 0.2 * r.NextDouble()));
            markers.Add(new(frame, r.Next(12) == 0 ? -tick : tick));
        }

        return new(region.Id, markers);
    }

    /// <summary>Pitch notes over parts of a region, mostly in range.</summary>
    static SetPitchNotesCommand PitchNotes(AudioRegion region, Random r) {
        var notes = new List<PitchNote>();
        var frame = region.FileOffset;
        var end = region.FileOffset + region.Length;
        for (var n = r.Next(0, 5); n > 0 && frame < end - 1; n--) {
            var start = frame + r.NextInt64(0, Math.Max(1, (end - frame) / 4));
            var stop = Math.Min(end, start + r.NextInt64(1, Math.Max(2, (end - frame) / 2)));
            if (stop <= start) {
                break;
            }

            var detected = r.Next(4_000, 8_000) + Math.Round(r.NextDouble() * 100 - 50, 1);
            notes.Add(
                new(
                    start,
                    stop,
                    detected,
                    r.Next(10) == 0 ? -1 : Math.Round(detected / 100) * 100 + r.Next(-3, 4) * 100,
                    Math.Round(r.NextDouble() * 2, 2),
                    Math.Round(r.NextDouble() * 2, 2),
                    r.Next(-200, 201),
                    r.Next(-6, 7)
                )
            );
            frame = stop;
        }

        return new(region.Id, notes);
    }

    static SetFileTempoCommand FileTempo(AudioFile file, Random r) {
        if (r.Next(5) == 0) {
            return new(file.Id, null);
        }

        var bpm = Math.Round(70 + r.NextDouble() * 100, 2);
        var step = (long)(60 * file.SampleRate / bpm);
        var first = r.NextInt64(0, step);
        long[] beats =
            [..Enumerable.Range(0, (int)Math.Max(0, (file.Frames - first) / step)).Select(i => first + i * step)];
        return new(file.Id, new(bpm, r.Next(2) == 0, beats, [..beats.Where(static (_, i) => i % 4 == 0)]));
    }

    /// <summary>One to three beats mapped near where they play now (sometimes impossibly).</summary>
    static MapBeatsCommand MapBeats(ProjectState s, Random r) {
        var map = s.TempoMap;
        var mappings = new List<BeatMapping>();
        for (var n = r.Next(1, 4); n > 0; n--) {
            var tick = r.Next(1, 64) * 480L;
            var sample = map.SampleAt(tick, s.SampleRate) + r.Next(-4_800, 4_800);
            mappings.Add(new(tick, r.Next(16) == 0 ? -1 : Math.Max(0, sample)));
        }

        return new(mappings);
    }

    /// <summary>
    ///     A deterministic analysis for the properties: transients every 1/16 at 100 bpm, nudged by the file's frames;
    ///     beats at 100 bpm (downbeats every fourth); sound in 20 000-frame stretches with 10 000 frames of silence.
    /// </summary>
    public sealed class FakeAnalysis : IAnalysisSource {
        public const double Bpm = 100;

        public FileAnalysis? Rhythm(AudioFile file) {
            var beat = (long)(60 * file.SampleRate / Bpm);
            var nudge = file.Frames % 97;
            long[] transients = [
                ..Enumerable.Range(0, (int)(file.Frames / (beat / 4)))
                    .Select(i => i * (beat / 4) + nudge * (i % 3))
                    .Where(f => f < file.Frames)
            ];
            long[] beats = [..Enumerable.Range(0, (int)(file.Frames / beat)).Select(i => i * beat)];
            return new(
                file.Id,
                240,
                transients,
                new(Bpm, file.Frames % 2 == 0, beats, [..beats.Where(static (_, i) => i % 4 == 0)]),
                [],
                [],
                false
            );
        }

        public IReadOnlyList<(long Start, long End)>? Silence(
            AudioFile file,
            double thresholdDb,
            double minTimeMs,
            double preAttackMs,
            double postReleaseMs
        ) =>
            [
                ..Enumerable.Range(0, (int)(file.Frames / 30_000) + 1)
                    .Select(i => (i * 30_000L, Math.Min(file.Frames, i * 30_000L + 20_000)))
                    .Where(static x => x.Item1 < x.Item2)
            ];
    }

    /// <summary>The project as canonical JSON: two states are the same project when these are equal.</summary>
    public static string Json(ProjectState state) =>
        JsonSerializer.Serialize(state.ToProject(string.Empty, 0, false), ContractsJsonContext.Default.Project);

    public static T Pick<T>(Random r, IReadOnlyList<T> items) => items[r.Next(items.Count)];

    /// <summary>A random non-empty selection of <paramref name="items" />.</summary>
    static IEnumerable<T> Some<T>(IReadOnlyList<T> items, Random r) {
        var chosen = items.Where(_ => r.Next(3) == 0).ToList();
        return chosen.Count > 0 ? chosen : [Pick(r, items)];
    }

    static AddTrackCommand AddTrack(Random r, int count) {
        TrackKind? kind = Pick<TrackKind?>(r, [null, TrackKind.Audio, TrackKind.Instrument, TrackKind.Midi]);
        var plugin = kind != TrackKind.Instrument
            ? null
            : r.Next(3) switch {
                0 => null,
                1 => "anode.sampler",
                _ => Pick(r, Plugins.Plugins).Id
            };
        return new(
            r.Next(2) == 0 ? null : $"Track {r.Next(100)}",
            r.Next(2) == 0 ? null : r.Next(0, count + 1),
            r.Next(2) == 0,
            kind,
            plugin
        );
    }

    static SetInsertParameterCommand InsertParameter(Insert insert, string? trackId, Random r) {
        var known = Plugins.Find(insert.PluginId)?.Parameters ?? [];
        if (known.Count == 0 || r.Next(10) == 0) {
            return new(insert.Id, insert.Parameters.Keys.FirstOrDefault() ?? "none", r.NextDouble(), trackId);
        }

        var p = Pick(r, known);
        var value = r.Next(8) == 0 ? p.Max + 1 : p.Min + Math.Round(r.NextDouble() * (p.Max - p.Min), 3);
        return new(insert.Id, p.Id, value, trackId);
    }

    /// <summary>A lane of a random strip and target: points in order within the target's range, or none (removal).</summary>
    static SetAutomationCommand? Automation(ProjectState s, Random r) {
        var strips = s.Tracks.Where(static t => t.Kind is not (TrackKind.Stack or TrackKind.Midi))
            .Select(static t => (Track?)t)
            .ToList();
        strips.Add(null);
        var track = Pick(r, strips);
        var strip = track?.Strip ?? s.Master;
        var targets = new List<string> { "volume", "mute" };
        if (track?.Kind != TrackKind.Vca) {
            targets.Add("pan");
            targets.AddRange(strip.Inserts.SelectMany(static i => i.Parameters.Keys.Select(p => $"insert:{i.Id}:{p}")));
        }

        if (track is not null && track.Kind != TrackKind.Vca) {
            targets.AddRange(strip.Sends.Select(static x => $"send:{x.Id}"));
        }

        if (track?.Instrument is { } slot) {
            targets.AddRange(slot.Parameters.Keys.Select(static p => $"instrument:{p}"));
        }

        var target = r.Next(20) == 0 ? "send:none" : Pick(r, targets);
        Edits.LaneRange range;
        try {
            range = Edits.TargetRangeOf(s, track?.Id, target, Plugins);
        } catch (EditException) {
            range = new(0, 1, false);
        }

        var (min, max) = (Math.Max(range.Min, -1000), Math.Min(range.Max, 1000));
        var tick = 0L;
        var points = new List<AutomationPoint>();
        for (var n = r.Next(0, 7); n > 0; n--) {
            tick += r.Next(3) == 0
                && points.Count > 0
                && points[^1].Tick == tick
                && (points.Count < 2 || points[^2].Tick != tick)
                    ? 0
                    : r.Next(1, 16) * 240L;
            var value = range.Toggle ? r.Next(2) : Math.Round(min + r.NextDouble() * (max - min), 3);
            points.Add(
                new(tick, r.Next(12) == 0 ? max + 1 : value, range.Toggle ? 0 : Math.Round(r.NextDouble() * 2 - 1, 2))
            );
        }

        return new(target, points, track?.Id);
    }

    static SetParameterCommand SetParameter(Track track, Random r) {
        var slot = track.Instrument!;
        var known = Plugins.Find(slot.PluginId)?.Parameters ?? [];
        if (known.Count == 0 || r.Next(10) == 0) {
            return new(track.Id, slot.Parameters.Keys.FirstOrDefault() ?? "none", r.NextDouble());
        }

        var p = Pick(r, known);
        // Mostly in range; sometimes just outside it.
        var value = r.Next(8) == 0 ? p.Max + 1 : p.Min + Math.Round(r.NextDouble() * (p.Max - p.Min), 3);
        return new(track.Id, p.Id, value);
    }

    static SetMidiIoCommand SetMidiIo(Track track, Random r) =>
        new(
            track.Id,
            r.Next(2) == 0 ? null : new(r.Next(2) == 0 ? null : $"port-{r.Next(4)}", r.Next(17)),
            r.Next(2) == 0 ? null : new($"port-{r.Next(4)}", r.Next(1, 17))
        );

    static NewNote NewNote(MidiRegion region, Random r) =>
        new(
            r.Next(24, 100),
            r.Next(1, 128),
            r.Next(4) == 0 ? r.Next(1, 17) : 1,
            r.NextInt64(0, Math.Max(1, region.LengthTicks + 480)),
            r.NextInt64(1, 1920)
        );

    static NewControlEvent NewControl(MidiRegion region, Random r) {
        var tick = r.NextInt64(0, Math.Max(1, region.LengthTicks));
        return r.Next(4) switch {
            0 => new(ControlKind.ControlChange, 1, r.Next(128), r.Next(128), tick),
            1 => new(ControlKind.PitchBend, 1, 0, r.Next(-8192, 8192), tick),
            2 => new(ControlKind.ChannelPressure, r.Next(1, 17), 0, r.Next(128), tick),
            _ => new(ControlKind.ProgramChange, 1, 0, r.Next(128), tick)
        };
    }

    static EditNotesCommand EditNotes(MidiRegion region, Random r) =>
        new(
            region.Id,
            [
                ..Some(region.Notes, r)
                    .Select(n => new NoteChange(
                            n.Id,
                            r.Next(2) == 0 ? null : Math.Clamp(n.Pitch + r.Next(-12, 13), 0, 127),
                            r.Next(2) == 0 ? null : r.Next(1, 128),
                            r.Next(4) == 0 ? r.Next(1, 17) : null,
                            r.Next(2) == 0 ? null : Math.Max(0, n.StartTick + r.Next(-4, 5) * 120L),
                            r.Next(2) == 0 ? null : r.NextInt64(1, 1920)
                        )
                    )
            ]
        );

    static AddRegionCommand AddRegion(string track, AudioFile file, Random r) {
        if (r.Next(2) == 0) {
            return new(track, file.Id, r.NextInt64(0, 960 * 64));
        }

        var offset = r.NextInt64(0, file.Frames / 2);
        return new(track, file.Id, r.NextInt64(0, 960 * 64), offset, r.NextInt64(1, file.Frames - offset + 1));
    }

    static ResizeRegionCommand Resize(AudioRegion region, ProjectState s, Random r) {
        var frames = s.Files[region.FileId].Frames;
        var offset = r.NextInt64(0, frames / 2);
        return new(region.Id, r.NextInt64(0, 960 * 64), offset, r.NextInt64(1, frames - offset + 1));
    }

    static SplitRegionCommand Split(AudioRegion region, ProjectState s, Random r) {
        var end = s.TempoMap.TickAtSample(Edits.EndSample(s, region), s.SampleRate);
        // Mostly inside, sometimes on or outside an edge.
        var at = r.Next(5) == 0 ? region.StartTick + r.NextInt64(-10, 10) : r.NextInt64(region.StartTick, end + 1);
        return new(region.Id, Math.Max(0, at));
    }

    static SplitRegionCommand SplitMidi(MidiRegion region, Random r) {
        var end = region.StartTick + region.LengthTicks;
        var at = r.Next(5) == 0 ? region.StartTick + r.NextInt64(-10, 10) : r.NextInt64(region.StartTick, end + 1);
        return new(region.Id, Math.Max(0, at));
    }

    static SetRegionCommand SetRegion(AudioRegion region, Random r) =>
        new(
            region.Id,
            r.Next(2) == 0 ? null : $"Region {r.Next(100)}",
            r.Next(2) == 0 ? null : Math.Round(r.NextDouble() * 48 - 24, 2),
            r.Next(2) == 0 ? null : r.NextInt64(0, region.Length / 2 + 1),
            r.Next(2) == 0 ? null : r.NextInt64(0, region.Length / 2 + 1),
            r.Next(2) == 0 ? null : Math.Round(r.NextDouble() * 2 - 1, 2),
            r.Next(2) == 0 ? null : Math.Round(r.NextDouble() * 2 - 1, 2),
            r.Next(2) == 0 ? null : r.Next(2) == 0
        );

    static TempoEvent[] TempoEvents(Random r) {
        var events = new List<TempoEvent> { new(0, Math.Round(40 + r.NextDouble() * 200, 1), Curve(r)) };
        for (var n = r.Next(0, 4); n > 0; n--) {
            events.Add(new(events[^1].Tick + r.Next(1, 16) * 960L, Math.Round(40 + r.NextDouble() * 200, 1), Curve(r)));
        }

        return [..events];

        static TempoCurve Curve(Random r) => r.Next(2) == 0 ? TempoCurve.Step : TempoCurve.Ramp;
    }

    static SignatureEvent[] Signatures(Random r) {
        var events = new List<SignatureEvent> { new(0, r.Next(1, 13), Pick(r, [2, 4, 8, 16])) };
        for (var n = r.Next(0, 3); n > 0; n--) {
            var previous = events[^1];
            events.Add(
                new(previous.Tick + r.Next(1, 8) * SignatureMap.BarTicks(previous), r.Next(1, 13), Pick(r, [4, 8]))
            );
        }

        return [..events];
    }

    static SetCompCommand Comp(TakeFolder folder, Random r) {
        var end = folder.StartTick + folder.LengthTicks;
        var cuts = Enumerable.Range(0, r.Next(0, 4))
            .Select(_ => r.NextInt64(folder.StartTick, end))
            .Append(folder.StartTick)
            .Append(end)
            .Distinct()
            .Order()
            .ToArray();
        var segments = new List<CompSegment>();
        for (var i = 0; i + 1 < cuts.Length; i++) {
            if (r.Next(4) > 0) {
                segments.Add(new(cuts[i], cuts[i + 1], Pick(r, folder.Takes).Id));
            }
        }

        return new(folder.Id, segments);
    }

    /// <summary>Some regions of the track with the most, to pack.</summary>
    static PackTakeFolderCommand? Pack(AudioRegion[] regions, Random r) {
        if (regions.Length == 0) {
            return null;
        }

        var track = regions.GroupBy(static x => x.TrackId).MaxBy(static g => g.Count())!.ToArray();
        return new([..Some(track, r).Select(static x => x.Id)], r.Next(2) == 0 ? null : $"Folder {r.Next(10)}");
    }

    /// <summary>Two or more regions of one track: MIDI ones join; audio ones mostly do not (they rarely meet).</summary>
    static JoinRegionsCommand? Join(AudioRegion[] regions, MidiRegion[] midiRegions, Random r) {
        if (r.Next(4) > 0
            && midiRegions.GroupBy(static x => x.TrackId).Where(static g => g.Count() > 1).ToArray() is {
                Length: > 0
            } midi) {
            return new([..Pick(r, midi).Select(static x => x.Id)]);
        }

        return regions.GroupBy(static x => x.TrackId).Where(static g => g.Count() > 1).ToArray() is {
            Length: > 0
        } audio
            ? new([..Pick(r, audio).Take(2).Select(static x => x.Id)])
            : null;
    }

    static SetCycleCommand Cycle(Random r) {
        var start = r.NextInt64(0, 960 * 32);
        return new(r.Next(2) == 0 ? null : r.Next(2) == 0, start, start + r.NextInt64(1, 960 * 32));
    }
}
