using Anode.Contracts;
using Anode.Model.Editing;
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
                ]
            ),
            new(
                "anode.sampler",
                "Sampler",
                "Anode",
                "1.0",
                PluginFormat.Builtin,
                true,
                [".sfz", ".wav"],
                [new("volume", "Volume", -60, 6, 0, "dB", 0, null), new("tune", "Tune", -100, 100, 0, "ct", 0, null)]
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
                ]
            ),
            new("com.example.fx", "Test Delay", "Example", "0.1", PluginFormat.Clap, false, [], [])
        ]
    );

    public static (ProjectState State, SequentialIds Ids, Random Random) Build(int seed, int edits = 24) {
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
                        24
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
                _ => Pick(random, CommandTypes)
            };
            if (Command(type, state, random) is { } command) {
                try {
                    state = Edits.Apply(state, command, ids, Plugins).State;
                } catch (EditException) {
                    // not every random edit applies
                }
            }
        }

        return (state, ids, random);
    }

    /// <summary>A random command of <paramref name="type" /> for <paramref name="s" />, or null when none can be made.</summary>
    public static EditCommand? Command(Type type, ProjectState s, Random r) {
        var tracks = s.Tracks;
        var regions = s.Regions.Values.ToArray();
        var midiRegions = s.MidiRegions.Values.ToArray();
        var files = s.Files.Values.ToArray();
        var instruments = tracks.Where(static t => t.Kind == TrackKind.Instrument).ToArray();
        var midiTracks = tracks.Where(static t => Edits.IsMidiKind(t.Kind)).ToArray();
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
                    Maybe(r.Next(2) == 0)
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
                    Maybe(Math.Round(r.NextDouble(), 2))
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
            _ => throw new InvalidOperationException($"No generator for {type.Name}.")
        };
        string? Track() => tracks.Count == 0 ? null : Pick(r, tracks).Id;
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
            regions.Length + midiRegions.Length == 0
                ? null
                : r.Next(regions.Length + midiRegions.Length) is var i && i < regions.Length
                    ? regions[i].Id
                    : midiRegions[i - regions.Length].Id;

        T? Maybe<T>(T value) where T : struct => r.Next(2) == 0 ? null : value;
        string? MaybeText(string value) => r.Next(2) == 0 ? null : value;
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
            r.Next(2) == 0 ? null : r.NextInt64(0, region.Length / 2 + 1)
        );

    static SetCycleCommand Cycle(Random r) {
        var start = r.NextInt64(0, 960 * 32);
        return new(r.Next(2) == 0 ? null : r.Next(2) == 0, start, start + r.NextInt64(1, 960 * 32));
    }
}
