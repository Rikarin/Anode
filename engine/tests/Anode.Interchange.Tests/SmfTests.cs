using Anode.Contracts;
using Anode.Interchange.Smf;
using Anode.Model;
using Anode.Model.Editing;
using Anode.Model.Midi;
using CsCheck;

namespace Anode.Interchange.Tests;

/// <summary>
///     The Standard MIDI File project mapping (docs/10-phase-9.md, workstream B): a project written as a file and read
///     back as an import's content gives the same events, tempo map, signatures, keys and markers.
/// </summary>
public sealed class SmfTests {
    static readonly PluginCatalog Plugins = PluginCatalog.Empty;

    /// <summary>A project with two instrument tracks, a tempo map, signatures, keys and markers.</summary>
    static ProjectState Project() {
        var ids = new SequentialIds();
        var state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);
        state = Edits.Apply(
                state,
                [
                    new AddTrackCommand("Piano", Kind: TrackKind.Instrument),
                    new AddTrackCommand("Bass", Kind: TrackKind.Instrument),
                    new SetTempoEventsCommand([new(0, 120, TempoCurve.Step), new(7680, 96.5, TempoCurve.Step)]),
                    new SetSignaturesCommand([new(0, 4, 4), new(7680, 3, 4), new(10560, 6, 8)]),
                    new SetKeysCommand(
                        [new(0, 2, KeyMode.Major), new(7680, 9, KeyMode.Minor), new(10560, 6, KeyMode.Major)]
                    ),
                    new AddMarkerCommand("Verse", 0),
                    new AddMarkerCommand("Chorus", 7680)
                ],
                ids,
                Plugins
            )
            .State;
        var piano = state.Tracks[0].Id;
        var bass = state.Tracks[1].Id;
        state = Edits.Apply(
                state,
                [new AddMidiRegionCommand(piano, 0, 7680), new AddMidiRegionCommand(bass, 3840, 7680)],
                ids
            )
            .State;
        var regions = state.MidiRegions.Values.OrderBy(static r => r.StartTick).ToArray();
        state = Edits.Apply(
                state,
                [
                    new AddNotesCommand(
                        regions[0].Id,
                        [
                            new(60, 100, 1, 0, 960), new(64, 80, 1, 0, 960), new(67, 90, 2, 1920, 480),
                            new(72, 127, 1, 7000, 680)
                        ]
                    ),
                    new AddControlsCommand(
                        regions[0].Id,
                        [
                            new(ControlKind.ControlChange, 1, 64, 127, 0), new(ControlKind.ProgramChange, 1, 0, 5, 0),
                            new(ControlKind.PitchBend, 1, 0, -8192, 480), new(ControlKind.PitchBend, 2, 0, 8191, 960),
                            new(ControlKind.ChannelPressure, 1, 0, 64, 1200),
                            new(ControlKind.PolyPressure, 1, 60, 30, 1300)
                        ]
                    ),
                    new AddNotesCommand(regions[1].Id, [new(36, 110, 3, 0, 240), new(43, 70, 3, 3840, 3840)])
                ],
                ids
            )
            .State;
        return state;
    }

    static (IReadOnlyList<
        (int, int, int, long, long)> Notes, IReadOnlyList<(ControlKind, int, int, int, long)> Controls)
        Events(ProjectState state, string trackId) =>
        (
            [
                ..state.MidiRegionsOf(trackId)
                    .SelectMany(static r => r.Notes.Select(n => (n.Pitch, n.Velocity, n.Channel,
                                r.StartTick + n.StartTick, n.LengthTicks)
                        )
                    )
                    .Order()
            ],
            [
                ..state.MidiRegionsOf(trackId)
                    .SelectMany(static r => r.Controls.Select(c => (c.Kind, c.Channel, c.Number, c.Value,
                                r.StartTick + c.Tick)
                        )
                    )
                    .Order()
            ]);

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void A_project_written_and_imported_has_the_same_events_tempo_map_and_markers(int format) {
        var state = Project();
        var bytes = SmfExport.Write(state, new("Song", Format: format));
        var file = StandardMidiFile.Read(bytes);
        Assert.Equal(format, file.Format);
        Assert.Equal(format == 1 ? 3 : 1, file.Tracks.Count);

        // Into a new project, with the tempo map and markers: one step.
        var ids = new SequentialIds();
        var empty = ProjectDefaults.NewProject("prj_2", "New", 48_000);
        var content = SmfImport.ToContent(file, empty, new("/tmp/Song.mid", Merge: format == 0));
        var result = Edits.Apply(empty, new ImportTracksCommand(content), ids, Plugins);
        var imported = result.State;
        Assert.Equal(state.Settings.TempoEvents, imported.Settings.TempoEvents);
        Assert.Equal(state.Settings.Signatures, imported.Settings.Signatures);
        Assert.Equal(state.Settings.Keys, imported.Settings.Keys);
        Assert.Equal(
            state.Markers.Values.Select(static m => (m.Name, m.Tick)).Order(),
            imported.Markers.Values.Select(static m => (m.Name, m.Tick)).Order()
        );
        if (format == 1) {
            Assert.Equal(["Piano", "Bass"], imported.Tracks.Select(static t => t.Name));
            for (var i = 0; i < 2; i++) {
                var (notes, controls) = Events(state, state.Tracks[i].Id);
                var back = Events(imported, imported.Tracks[i].Id);
                Assert.Equal(notes, back.Notes);
                Assert.Equal(controls, back.Controls);
            }
        } else {
            var track = Assert.Single(imported.Tracks);
            var expected = (
                state.Tracks.SelectMany(t => Events(state, t.Id).Notes).Order().ToArray(),
                state.Tracks.SelectMany(t => Events(state, t.Id).Controls).Order().ToArray());
            var actual = Events(imported, track.Id);
            Assert.Equal(expected.Item1, actual.Notes);
            Assert.Equal(expected.Item2, actual.Controls);
        }

        Assert.Equal(
            RandomJson(empty),
            RandomJson(imported.Apply(result.Inverse))
        );
    }

    static string RandomJson(ProjectState state) =>
        System.Text.Json.JsonSerializer.Serialize(
            state.ToProject(string.Empty, 0, false),
            ContractsJsonContext.Default.Project
        );

    [Fact]
    public void Tracks_or_a_region_alone_and_playback_as_it_plays() {
        var state = Project();
        var piano = state.Tracks[0].Id;
        var region = state.MidiRegionsOf(piano).Single();
        // Transpose +2, velocity −10, a quarter-note grid; and the note past the region's end is cut there.
        state = Edits.Apply(
                state,
                new SetMidiRegionCommand(region.Id, Transpose: 2, Velocity: -10, QuantizeTicks: 960),
                new SequentialIds()
            )
            .State;
        var document = SmfExport.Document(state, new("Piano", TrackIds: [piano]));
        var track = Assert.Single(document.Tracks);
        Assert.Equal("Piano", track.Name);
        Assert.Contains(track.Notes, static n => n is { Pitch: 69, Velocity: 80, StartTick: 1920 });
        Assert.Contains(track.Notes, static n => n is { Pitch: 74, StartTick: 6720, LengthTicks: 680 });
        Assert.Contains(track.Notes, static n => n is { Pitch: 62, Velocity: 90, StartTick: 0 });

        var alone = SmfExport.Document(
            state,
            new("Region", RegionId: state.MidiRegionsOf(state.Tracks[1].Id).Single().Id)
        );
        Assert.Equal([36, 43], Assert.Single(alone.Tracks).Notes.Select(static n => n.Pitch));
        Assert.Throws<ArgumentException>(() => SmfExport.Write(state, new("Bad", Format: 2)));
        Assert.Throws<ArgumentException>(() => SmfExport.Write(state, new("Bad", TrackIds: ["trk_none"])));
    }

    [Fact]
    public void Import_options_leave_the_tempo_and_markers_or_merge_the_tracks() {
        var state = Project();
        var file = StandardMidiFile.Read(SmfExport.Write(state, new("Song")));
        var target = ProjectDefaults.NewProject("prj_2", "New", 48_000);
        var content = SmfImport.ToContent(file, target, new("Song.mid", false, false));
        Assert.Null(content.TempoEvents);
        Assert.Null(content.Signatures);
        Assert.Null(content.Keys);
        Assert.Null(content.Markers);
        Assert.Equal(2, content.Tracks.Count);
        // Regions start at the bar of their first event, by the project's own signatures when the file's are not taken.
        Assert.Equal([0L, 3840L], content.Tracks.Select(static t => t.MidiRegions[0].StartTick));
        var merged = SmfImport.ToContent(file, target, new("Song.mid", Merge: true));
        Assert.Equal("Piano", Assert.Single(merged.Tracks).Name);
        Assert.Equal(6, merged.Tracks[0].MidiRegions[0].Notes.Count);
        Assert.Throws<FormatException>(() =>
            SmfImport.ToContent(StandardMidiFile.Read(StandardMidiFileWriter.Write([])), target, new("Empty.mid"))
        );
    }

    [Fact]
    public void Random_notes_and_controllers_survive_a_round_trip() =>
        Gen.Select(
                Gen.Int[0, 127],
                Gen.Int[1, 127],
                Gen.Int[1, 16],
                Gen.Long[0, 40_000],
                Gen.Long[1, 5_000]
            )
            .Select(static (p, v, c, t, l) => new Note(0, p, v, c, t, l, 0))
            .Array[1, 30]
            .Sample(
                static notes => {
                    // Notes of one pitch and channel do not overlap in a file: one of each.
                    Note[] distinct = [..notes.DistinctBy(static n => (n.Pitch, n.Channel))];
                    var document = new SmfDocument(
                        1,
                        [new(0, 120, TempoCurve.Step)],
                        [new(0, 4, 4)],
                        [],
                        [],
                        [new("T", distinct, [])]
                    );
                    var read = StandardMidiFile.Read(StandardMidiFileWriter.Write(document));
                    var track = read.Tracks.Single(static t => t.Notes.Count > 0);
                    Assert.Equal(
                        distinct.Select(static n => (n.Pitch, n.Velocity, n.Channel, n.StartTick, n.LengthTicks))
                            .Order(),
                        track.Notes.Select(static n => (n.Pitch, n.Velocity, n.Channel, n.StartTick, n.LengthTicks))
                            .Order()
                    );
                },
                iter: 200
            );

    [Fact]
    public void Tempo_ramps_are_written_as_steps_and_text_as_UTF_8() {
        var document = new SmfDocument(
            1,
            [new(0, 100, TempoCurve.Ramp), new(1920, 140, TempoCurve.Step)],
            [new(0, 4, 4)],
            [new(0, 0, KeyMode.Minor)],
            [new(960, "Café")],
            [new("Señor", [new(0, 60, 90, 1, 0, 480, 0)], [])],
            "Name"
        );
        var read = StandardMidiFile.Read(StandardMidiFileWriter.Write(document));
        Assert.Equal(9, read.Tempo.Count);
        Assert.Equal([0L, 240, 480, 720, 960, 1200, 1440, 1680, 1920], read.Tempo.Select(static t => t.Tick));
        Assert.True(read.Tempo[0].Bpm > 100 && read.Tempo[7].Bpm < 140);
        Assert.Equal("Café", Assert.Single(read.Markers).Name);
        Assert.Equal(new(0, 0, KeyMode.Minor), Assert.Single(read.Keys));
        Assert.Contains(read.Tracks, static t => t.Name == "Señor");
    }
}
