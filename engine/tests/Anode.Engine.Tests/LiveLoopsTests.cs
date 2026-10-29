using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

/// <summary>
///     The Live Loops launcher (docs/07-phase-6.md, "Live Loops"): launches and stops land sample-accurately on the next
///     quantise boundary of the tempo map (across a tempo ramp too, a request within 1 ms of a boundary taking the next),
///     a scene is one decision, the play modes (loop, one shot, retrigger), track activation, audio cells played from
///     memory (warped at load), and the state changes pushed for the server.
/// </summary>
public sealed class LiveLoopsTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;

    // A 120 bpm quarter note, and the first launch boundaries the tests use.
    const int Quarter = Rate / 2;

    readonly string dir = TestAudio.TempDirectory();
    readonly EngineController controller = new(new(Rate, Block), TestMidiEffects.Registry());
    readonly OfflineDriver driver;
    readonly List<CellStateMessage> states = [];

    public LiveLoopsTests() {
        driver = new(controller, Block);
        controller.CellState = states.Add;
    }

    public void Dispose() {
        driver.Dispose();
        controller.Dispose();
        Directory.Delete(dir, true);
    }

    // ---- The tempo map the tests launch against --------------------------------------------------------------------

    /// <summary>
    ///     120 bpm for 4 quarter notes (to sample 96,000), then a ramp of 10 bpm per quarter note for 8 quarter notes
    ///     (to 200 bpm), then 200 bpm: 4/4 from the start.
    /// </summary>
    static TempoMapPoint[] Ramp() {
        var rampSeconds = 60 / 10.0 * Math.Log(1 + 10 * 8 / 120.0);
        var end = 96_000 + (long)Math.Round(rampSeconds * Rate);
        var growth = Math.Exp(10 * ((end - 96_000) / (double)Rate) / 60);
        return [
            TestAudio.Tempo(0, 0, 120), TestAudio.Tempo(96_000, 4, 120, 10),
            TestAudio.Tempo(end, 4 + 120 / 10.0 * (growth - 1), 120 * growth)
        ];
    }

    /// <summary>The quarter-note position at <paramref name="sample" />, worked out independently of the engine.</summary>
    static double QuarterAt(TempoMapPoint[] map, double sample) {
        var p = map.Last(m => m.Sample <= sample);
        var seconds = (sample - p.Sample) / Rate;
        return p.Slope == 0
            ? p.Quarter + p.Bpm * seconds / 60
            : p.Quarter + p.Bpm / p.Slope * (Math.Exp(p.Slope * seconds / 60) - 1);
    }

    /// <summary>
    ///     The first sample at or after <paramref name="from" /> whose position is on a multiple of
    ///     <paramref name="step" /> quarters.
    /// </summary>
    static long Expected(TempoMapPoint[] map, long from, double step) {
        var boundary = Math.Ceiling(QuarterAt(map, from) / step - 1e-9) * step;
        long lo = from, hi = from + 100L * Rate;
        while (lo < hi) {
            var mid = (lo + hi) / 2;
            if (QuarterAt(map, mid) >= boundary - 1e-9) {
                hi = mid;
            } else {
                lo = mid + 1;
            }
        }

        return lo;
    }

    static double Step(EngineQuantize quantize) =>
        quantize switch {
            EngineQuantize.Sixteenth => 0.25,
            EngineQuantize.Eighth => 0.5,
            EngineQuantize.Quarter => 1,
            EngineQuantize.Half => 2,
            EngineQuantize.Bar => 4,
            EngineQuantize.TwoBars => 8,
            EngineQuantize.FourBars => 16,
            _ => 0
        };

    // ---- Fixtures --------------------------------------------------------------------------------------------------

    static CellModel MidiCell(
        int index,
        int trackId,
        long length,
        CellMode mode = CellMode.Loop,
        int note = 60,
        long noteLength = 1_000
    ) =>
        new(index, trackId, mode, length, MidiFixtures.Notes((0, noteLength, note)), []);

    static EngineTrack Instrument(int id, bool active = true) =>
        MidiFixtures.InstrumentTrack(id) with { LiveLoopsActive = active };

    void Load(
        TrackModel[] models,
        EngineTrack[] tracks,
        CellModel[] cells,
        TempoMapPoint[]? tempo = null,
        EngineFile[]? files = null,
        long version = 1,
        bool replace = true
    ) {
        using var segment = TestAudio.Segment(models, version, tempo: tempo, cells: cells);
        controller.LoadModel(TestAudio.Load(segment, version, replace, tracks, files ?? [], dir));
    }

    TestInstrument InstrumentOf(int trackId) =>
        (TestInstrument)controller.Engine.Root.Tracks[controller.Engine.Root.IndexOf(trackId)].Instrument!.Instrument;

    /// <summary>
    ///     The note-ons <paramref name="instrument" /> received at or after rendered frame <paramref name="since" />, as
    ///     project samples from <paramref name="origin" />.
    /// </summary>
    static List<(long Sample, int Note)> NoteOns(TestInstrument instrument, long since, long origin) => [
        ..instrument.Log.Take(instrument.Logged)
            .Where(e => e.At >= since && (e.Event.Status & 0xF0) == 0x90 && e.Event.Data2 > 0)
            .Select(e => (origin + e.At - since, (int)e.Event.Data1))
    ];

    void Render(int frames) {
        for (var i = 0; i < frames / Block; i++) {
            driver.Process();
        }
    }

    List<CellStateMessage> Pump() {
        states.Clear();
        controller.PumpCellStates();
        return [..states];
    }

    /// <summary>
    ///     Plays from <paramref name="from" /> until the next block starts at <paramref name="from" /> + the frames
    ///     given.
    /// </summary>
    void Roll(long from, int frames = Block) {
        driver.PlayFrom(from);
        Render(frames);
    }

    /// <summary>Where the next block starts while rolling (the snapshot holds the last callback's start).</summary>
    long Next() => controller.Engine.Snapshot.Position + Block;

    // ---- Tests -----------------------------------------------------------------------------------------------------

    public static TheoryData<EngineQuantize, long> Quantizes() {
        var data = new TheoryData<EngineQuantize, long>();
        foreach (var quantize in Enum.GetValues<EngineQuantize>()) {
            data.Add(quantize, 30_000); // at 120 bpm
            data.Add(quantize, 101_234); // inside the ramp
            data.Add(quantize, 180_000); // at 200 bpm, after the ramp
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Quantizes))]
    public void A_launch_starts_on_the_next_boundary_within_a_sample(EngineQuantize quantize, long at) {
        var map = Ramp();
        Load([new(1, [], [])], [Instrument(1)], [MidiCell(0, 1, 2 * Quarter)], map);
        var instrument = InstrumentOf(1);
        Roll(at - Block);
        var position = Next();
        var rendered = instrument.Rendered;
        Pump();
        controller.LaunchCells([new(0, quantize)]);
        var log = new List<CellStateMessage>();
        for (var i = 0; i < 40 && !log.Exists(static s => s.State == EngineCellState.Playing); i++) {
            Render(Rate / 4);
            log.AddRange(Pump());
        }

        var expected = quantize == EngineQuantize.Off
            ? position
            : Expected(map, position + controller.Engine.LateFrames, Step(quantize));
        var queued = log[0];
        var playing = log[1];
        Assert.Equal((0, EngineCellState.Queued), (queued.Cell, queued.State));
        Assert.Equal((0, EngineCellState.Playing), (playing.Cell, playing.State));
        Assert.InRange(playing.Sample, expected - 1, expected + 1);
        Assert.Equal(playing.Sample, queued.Sample); // as announced
        // The cell's first note sounds on the very sample the launcher reported.
        Assert.Equal((playing.Sample, 60), NoteOns(instrument, rendered, position)[0]);
    }

    [Fact]
    public void A_request_within_1_ms_of_a_boundary_takes_the_next_one() {
        Load([new(1, [], [])], [Instrument(1)], [MidiCell(0, 1, Quarter)]);
        // The block where the launch is applied starts 40 frames (less than 1 ms) before a sixteenth note.
        Roll(12_000 - 40 - Block);
        controller.LaunchCells([new(0, EngineQuantize.Sixteenth)]);
        Render(Rate / 2);
        Assert.Equal(18_000, Pump().Single(static s => s.State == EngineCellState.Playing).Sample);

        controller.Stop();
        driver.Process();
        Roll(12_000 - 49 - Block); // 49 frames: in time
        controller.LaunchCells([new(0, EngineQuantize.Sixteenth)]);
        Render(Rate / 2);
        Assert.Equal(12_000, Pump().Single(static s => s.State == EngineCellState.Playing).Sample);
    }

    [Fact]
    public void A_launch_while_stopped_waits_and_starts_with_the_transport_on_its_boundary() {
        Load([new(1, [], [])], [Instrument(1)], [MidiCell(0, 1, Quarter)]);
        driver.Send(new(EngineCommandKind.Locate, 10_000));
        driver.Process();
        controller.LaunchCells([new(0, EngineQuantize.Quarter)]);
        driver.Process();
        Assert.Equal([new CellStateMessage(0, EngineCellState.Queued, 24_000)], Pump());
        Render(Rate); // stopped: nothing happens
        Assert.Empty(Pump());
        driver.Send(new(EngineCommandKind.Play));
        Render(Rate);
        Assert.Equal([new CellStateMessage(0, EngineCellState.Playing, 24_000)], Pump());

        // Stopping the transport stops the cell.
        controller.Stop();
        driver.Process();
        Assert.Equal(EngineCellState.Stopped, Assert.Single(Pump()).State);
    }

    [Fact]
    public void A_scene_starts_its_cells_together_and_a_new_scene_replaces_and_stops_on_the_bar() {
        Load(
            [new(1, [], []), new(2, [], [])],
            [Instrument(1), Instrument(2)],
            [
                MidiCell(0, 1, 4 * Quarter, note: 60), MidiCell(1, 2, 4 * Quarter, note: 64),
                MidiCell(2, 1, 2 * Quarter, note: 67)
            ]
        );
        Roll(10_000);
        // Scene 1: cells 0 and 1, on the next bar.
        controller.LaunchCells([new(0, EngineQuantize.Bar), new(1, EngineQuantize.Bar)]);
        Render(2 * Rate);
        var first = Pump();
        Assert.Equal(
            [
                (0, EngineCellState.Queued, 96_000L), (1, EngineCellState.Queued, 96_000L),
                (0, EngineCellState.Playing, 96_000L), (1, EngineCellState.Playing, 96_000L)
            ],
            first.Select(static s => (s.Cell, s.State, s.Sample))
        );

        // Scene 2: cell 2 on track 1, and a stop of track 2 (no cell in that scene), on the next bar: one decision.
        var at = Next();
        Assert.InRange(at, 96_000, 192_000 - Rate / 10);
        var one = InstrumentOf(1);
        var two = InstrumentOf(2);
        var (renderedOne, renderedTwo) = (one.Rendered, two.Rendered);
        controller.LaunchCells([new(2, EngineQuantize.Bar)], [new(2, EngineQuantize.Bar)]);
        Render(3 * Rate);
        Assert.Equal(
            [
                (1, EngineCellState.Stopping, 192_000L), (2, EngineCellState.Queued, 192_000L),
                (0, EngineCellState.Stopped, 192_000L), (2, EngineCellState.Playing, 192_000L),
                (1, EngineCellState.Stopped, 192_000L)
            ],
            Pump().Select(static s => (s.Cell, s.State, s.Sample))
        );

        // Track 1 switched cells on the bar (cell 2 loops every half note), track 2 fell silent there.
        var ones = NoteOns(one, renderedOne, at);
        Assert.Equal([(192_000L, 67), (240_000L, 67)], ones.Where(static n => n.Sample >= 192_000));
        Assert.DoesNotContain(ones, static n => n.Sample >= 192_000 && n.Note == 60);
        Assert.DoesNotContain(NoteOns(two, renderedTwo, at), static n => n.Sample >= 192_000);
        Assert.Equal(0, two.Sounding);
    }

    [Fact]
    public void A_one_shot_stops_at_its_end_and_a_retrigger_cell_restarts_when_launched_again() {
        Load(
            [new(1, [], []), new(2, [], [])],
            [Instrument(1), Instrument(2)],
            [
                MidiCell(0, 1, 30_000, CellMode.OneShot, noteLength: 50_000), // its note outlasts it
                MidiCell(1, 2, 60_000, CellMode.Retrigger, 62)
            ]
        );
        Roll(0, 0);
        controller.LaunchCells([new(0, EngineQuantize.Off), new(1, EngineQuantize.Off)]);
        Render(20_032);
        Assert.Equal(1, InstrumentOf(1).Sounding);
        Render(12_032);
        Assert.Equal(0, InstrumentOf(1).Sounding); // the one-shot ended at 30,000 and released its note
        var log = Pump();
        Assert.Contains(new(0, EngineCellState.Stopped, 30_000), log);

        // Relaunching the retrigger cell restarts it on the next quarter note.
        var retrigger = InstrumentOf(2);
        var rendered = retrigger.Rendered;
        var at = Next();
        controller.LaunchCells([new(1, EngineQuantize.Quarter)]);
        Render(Rate / 2);
        Assert.Contains(new(1, EngineCellState.Playing, 48_000), Pump());
        Assert.Equal([(48_000L, 62)], NoteOns(retrigger, rendered, at));

        // A loop cell launched again while it plays carries on (no restart).
        Load([new(1, [], [])], [Instrument(1)], [MidiCell(3, 1, Quarter)], version: 2);
        Roll(0, 0);
        controller.LaunchCells([new(3, EngineQuantize.Off)]);
        Render(10_048);
        Pump();
        controller.LaunchCells([new(3, EngineQuantize.Sixteenth)]);
        Render(Rate);
        Assert.DoesNotContain(Pump(), static s => s.State != EngineCellState.Playing && s.Cell == 3);
    }

    [Fact]
    public void An_active_track_plays_its_cell_and_an_inactive_one_its_regions() {
        TrackModel[] models = [new(1, MidiFixtures.Notes((1_000, 500, 48), (25_000, 500, 48)), [])];
        CellModel[] cells = [MidiCell(0, 1, Quarter, note: 72, noteLength: 500)];
        Load(models, [Instrument(1, false)], cells);
        var instrument = InstrumentOf(1);
        Roll(0, 0);
        var rendered = instrument.Rendered;
        controller.LaunchCells([new(0, EngineQuantize.Off)]);
        Render(Rate);
        // Inactive: its regions only (the cell plays, unheard).
        Assert.Equal([(1_000L, 48), (25_000L, 48)], NoteOns(instrument, rendered, 0));
        Assert.Contains(Pump(), static s => s is { Cell: 0, State: EngineCellState.Playing });

        // Activated (a model load with its settings): the cell, in time with where it started, and not the regions.
        Load(models, [Instrument(1)], cells, version: 2, replace: false);
        controller.Stop();
        driver.Process();
        Roll(0, 0);
        rendered = instrument.Rendered;
        controller.LaunchCells([new(0, EngineQuantize.Off)]);
        Render(Rate);
        Assert.Equal([(0L, 72), (24_000L, 72)], NoteOns(instrument, rendered, 0));
    }

    [Fact]
    public void An_audio_cell_plays_from_memory_looping_at_its_length() {
        var file = TestAudio.Write24(dir, "loop.wav", 1, Rate, 7);
        const int length = 20_000;
        Load(
            [new(1, [], [])],
            [TestAudio.Track(1) with { LiveLoopsActive = true }],
            [new(0, 1, CellMode.Loop, length, [], [TestAudio.Region(0, 15_000, 0, 1_000)])],
            files: [new(0, file, 1, Rate, Rate)]
        );
        var cell = controller.Engine.Root.Cells[0]!;
        Assert.Equal((1, (long)length), (cell.Channels, cell.Frames));
        Roll(5_000, 0);
        controller.LaunchCells([new(0, EngineQuantize.Off)]);
        var (left, right) = driver.Render(3 * length);
        for (var f = 0; f < left.Length; f++) {
            var offset = f % length;
            var expected = offset < 15_000 ? TestAudio.Sample(1_000 + offset, 7) : 0f;
            Assert.Equal(expected, left[f]);
            Assert.Equal(expected, right[f]);
        }
    }

    [Fact]
    public void A_warped_audio_cell_is_rendered_at_load_as_the_disk_thread_renders_the_region() {
        var bursts = FlexPlaybackTests.WriteBursts(dir, "bursts.wav", 2);
        EngineFile[] files = [new(0, bursts, 2, Rate, 80_000)];
        var entry = FlexPlaybackTests.Warped(0, 0, FlexAlgorithm.Polyphonic);
        var length = FlexPlaybackTests.MapLength;
        Load(
            [new(1, [], [entry], FlexPlaybackTests.Map())],
            [TestAudio.Track(1, true)],
            [new(0, 1, CellMode.OneShot, length, [], [entry], FlexPlaybackTests.Map())],
            files: files
        );
        driver.PlayFrom(0);
        var track = driver.Render((int)length).Left;
        var cell = controller.Engine.Root.Cells[0]!.Audio!;
        Assert.Equal(2 * length, cell.Length);
        for (var f = 0; f < length; f++) {
            Assert.Equal(track[f], cell[2 * f], 1e-5f);
        }

        // The bursts sit where the warp map puts them (within 3 ms).
        var onsets = FlexPlaybackTests.Onsets([..Enumerable.Range(0, (int)length).Select(f => cell[2 * f])]);
        Assert.Equal(FlexPlaybackTests.MapTargets.Count, onsets.Count);
        for (var k = 0; k < onsets.Count; k++) {
            Assert.InRange(onsets[k], FlexPlaybackTests.MapTargets[k] - 144, FlexPlaybackTests.MapTargets[k] + 144);
        }
    }

    [Fact]
    public void A_recompile_that_removes_a_playing_cell_stops_it_and_unchanged_audio_is_kept() {
        var file = TestAudio.Write24(dir, "loop.wav", 1, Rate, 3);
        EngineFile[] files = [new(0, file, 1, Rate, Rate)];
        CellModel audio = new(4, 1, CellMode.Loop, 10_000, [], [TestAudio.Region(0, 10_000, 0)]);
        Load([new(1, [], [])], [TestAudio.Track(1) with { LiveLoopsActive = true }], [audio], files: files);
        var rendered = controller.Engine.Root.Cells[4]!.Audio;
        Roll(0, 0);
        controller.LaunchCells([new(4, EngineQuantize.Off)]);
        Render(4_096);
        Pump();

        // The same cell again (a per-track recompile): its audio is not rendered again.
        Load([new(1, [], [])], [], [audio], version: 2, replace: false);
        Assert.Same(rendered, controller.Engine.Root.Cells[4]!.Audio);
        Render(1_024);
        Assert.Empty(Pump());

        // Without it: it stops.
        Load([new(1, [], [])], [], [], version: 3, replace: false);
        Assert.Null(controller.Engine.Root.Cells[4]);
        Render(Block);
        Assert.Equal(EngineCellState.Stopped, Assert.Single(Pump()).State);
    }
}
