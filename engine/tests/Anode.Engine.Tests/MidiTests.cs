using Anode.Audio.Midi;
using Anode.Dsp.Instruments;
using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

/// <summary>
///     Instrument and external MIDI tracks rendered offline: region events reach the instrument at their frames, the
///     note bookkeeping (stop, locate, cycle wrap, recompile), live MIDI and its routing, parameter changes, instrument
///     swaps, external MIDI out, MIDI recording, activity batches and port hotplug.
/// </summary>
public sealed class MidiTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    readonly string dir = TestAudio.TempDirectory();
    readonly FakeMidi midi = new();
    readonly List<TestInstrument> created;
    readonly EngineController controller;
    readonly OfflineDriver driver;

    public MidiTests() {
        (var registry, created) = MidiFixtures.Registry();
        controller = new(new(Rate, Block), registry, midi);
        driver = new(controller, Block);
    }

    public void Dispose() {
        driver.Dispose();
        controller.Dispose();
        Directory.Delete(dir, true);
    }

    TestInstrument Instrument(int trackId) =>
        (TestInstrument)controller.Engine.Root.Tracks[controller.Engine.Root.IndexOf(trackId)].Instrument!.Instrument;

    void Load(long version, EngineTrack[] tracks, TrackModel[] models, bool replace = true) {
        using var segment = TestAudio.Segment(models, version);
        controller.LoadModel(TestAudio.Load(segment, version, replace, tracks, [], dir));
    }

    /// <summary>Plays from <paramref name="from" /> for <paramref name="frames" />; returns the first playing block.</summary>
    long Play(TestInstrument instrument, long from, int frames) {
        driver.PlayFrom(from);
        var first = instrument.Processed;
        for (var i = 0; i < frames / Block; i++) {
            driver.Process();
        }

        return first;
    }

    /// <summary>Each logged event at its absolute project sample, for a pass that started at <paramref name="from" />.</summary>
    static List<(long Sample, MidiEvent Event)> Timeline(TestInstrument instrument, long firstBlock, long from) =>
    [
        ..instrument.Log.Take(instrument.Logged)
            .Where(e => e.Block >= firstBlock)
            .Select(e => (from + (e.Block - firstBlock) * Block + e.Event.Frame, e.Event))
    ];

    [Fact]
    public void Region_notes_reach_the_instrument_at_their_samples_and_it_sounds() {
        Load(
            1,
            [MidiFixtures.InstrumentTrack(1)],
            [new(1, MidiFixtures.Notes((1_000, 2_000, 60), (5_000, 100, 64), (5_000, 300, 67)), [])]
        );
        var instrument = Instrument(1);
        Assert.True(instrument.Prepared);
        var first = Play(instrument, 0, 8_000);
        var timeline = Timeline(instrument, first, 0);
        Assert.Equal(
            [(1_000L, 0x90, 60), (3_000L, 0x80, 60), (5_000L, 0x90, 64), (5_000L, 0x90, 67), (5_100L, 0x80, 64),
                (5_300L, 0x80, 67)],
            timeline.Select(static t => (t.Sample, (int)t.Event.Status, (int)t.Event.Data1))
        );
        Assert.Equal(100, timeline[0].Event.Data2);

        // The instrument's output reaches the master (stereo, at unity).
        driver.Send(new(EngineCommandKind.Locate, 1_000));
        driver.Process();
        var (left, right) = driver.Render(1_024);
        Assert.True(left.Max(Math.Abs) > 0.05f);
        Assert.Equal(left, right);
    }

    [Fact]
    public void Cycle_wrap_releases_held_notes_and_replays_them() {
        Load(1, [MidiFixtures.InstrumentTrack(1)], [new(1, MidiFixtures.Notes((2_000, 5_000, 60)), [])]);
        var instrument = Instrument(1);
        driver.Send(new(EngineCommandKind.SetCycle, 0, 3_000, true));
        var first = Play(instrument, 0, 6_400);
        var timeline = Timeline(instrument, first, 0)
            .Select(static t => (t.Sample, (int)t.Event.Status, (int)t.Event.Data1))
            .ToList();
        // On at 2,000; the wrap at 3,000 releases it; on again 2,000 frames into the second lap (5,000 rendered).
        Assert.Equal([(2_000L, 0x90, 60), (3_000L, 0x80, 60), (5_000L, 0x90, 60), (6_000L, 0x80, 60)], timeline);
    }

    [Fact]
    public void Stop_and_locate_reset_the_instrument_and_external_tracks_get_note_offs() {
        Load(
            1,
            [MidiFixtures.InstrumentTrack(1), MidiFixtures.ExternalTrack(2, "out-a", 3)],
            [
                new(1, MidiFixtures.Notes((100, 48_000, 60)), []),
                new(2, MidiFixtures.Notes((100, 48_000, 62)), [])
            ]
        );
        var instrument = Instrument(1);
        var resets = instrument.Resets;
        Play(instrument, 0, 1_280);
        Assert.Equal(1, instrument.Sounding);
        driver.Send(new(EngineCommandKind.Stop));
        driver.Process();
        Assert.Equal(resets + 2, instrument.Resets); // the play's locate, then the stop
        Assert.Equal(0, instrument.Sounding);
        controller.Midi.Pump();
        var sent = midi.SentNow();
        Assert.Equal(
            [(0x92, 62, 100), (0x82, 62, 0)],
            sent.Select(static s => ((int)s.Message[0], (int)s.Message[1], (int)s.Message[2]))
        );
        Assert.All(sent, static s => Assert.Equal("out-a", s.Uid));
        Assert.All(sent, static s => Assert.True(s.TimeNs > 0));

        driver.Send(new(EngineCommandKind.Locate, 10_000));
        driver.Process();
        Assert.Equal(resets + 3, instrument.Resets);
    }

    [Fact]
    public void A_recompile_releases_notes_held_by_the_old_regions() {
        Load(1, [MidiFixtures.InstrumentTrack(1)], [new(1, MidiFixtures.Notes((100, 48_000, 60)), [])]);
        var instrument = Instrument(1);
        Play(instrument, 0, 640);
        Assert.Equal(1, instrument.Sounding);
        Load(2, [], [new(1, [], [])], false); // the note was deleted
        driver.Process();
        Assert.Equal(0, instrument.Sounding);
        Assert.Equal((0x80, 60), (instrument.Events.Last().Status, instrument.Events.Last().Data1));
    }

    [Fact]
    public void Live_midi_plays_on_the_named_track_while_stopped() {
        Load(1, [MidiFixtures.InstrumentTrack(1), MidiFixtures.InstrumentTrack(2)], [new(1, [], []), new(2, [], [])]);
        Assert.True(controller.LiveMidi(2, [new(0x91, 64, 90), new(0x81, 64, 0)]));
        Assert.False(controller.LiveMidi(9, [new(0x90, 64, 90)]));
        driver.Process();
        driver.Process();
        Assert.Empty(Instrument(1).Events);
        Assert.Equal([(0x91, 64, 90), (0x81, 64, 0)], Instrument(2).Events.Select(static e => ((int)e.Status, (int)e.Data1, (int)e.Data2)));
        Assert.All(Instrument(2).Events, static e => Assert.InRange(e.Frame, 0, Block - 1));
    }

    [Fact]
    public void Parameters_go_through_the_queue_to_the_audio_thread() {
        Load(1, [MidiFixtures.InstrumentTrack(1, MidiFixtures.Instrument(gain: 0.25))], [new(1, [], [])]);
        var instrument = Instrument(1);
        Assert.Equal(0.25f, instrument.GetParameter(0));
        Assert.True(controller.SetParameter(1, "gain", 0.75));
        Assert.Equal(0.25f, instrument.GetParameter(0)); // not until the audio thread runs
        driver.Process();
        Assert.Equal(0.75f, instrument.GetParameter(0));
        Assert.False(controller.SetParameter(1, "nope", 1));
        Assert.False(controller.SetParameter(7, "gain", 1));
    }

    [Fact]
    public void Set_instrument_builds_swaps_and_disposes() {
        Load(1, [new(1, -1, false, false, false, TestAudio.Unity)], [new(1, [], [])]);
        Assert.Null(controller.Engine.Root.Tracks[0].Instrument);
        var state = Convert.ToBase64String([1, 2, 3]);
        controller.SetInstrument(1, MidiFixtures.Instrument(gain: 0.3, file: "/x/a.txt", state: state));
        var first = Instrument(1);
        Assert.True(first.Prepared);
        Assert.Equal("/x/a.txt", first.File);
        Assert.Equal([1, 2, 3], first.State);
        Assert.Equal(0.3f, first.GetParameter(0));

        // Same plugin, file and state: only the parameters change, through the queue.
        controller.SetInstrument(1, MidiFixtures.Instrument(gain: 0.6, file: "/x/a.txt"));
        Assert.Same(first, Instrument(1));
        driver.Process();
        Assert.Equal(0.6f, first.GetParameter(0));

        // Another file: a new instance that keeps the old one's state.
        first.State = [9];
        controller.SetInstrument(1, MidiFixtures.Instrument(gain: 0.6, file: "/x/b.txt"));
        var second = Instrument(1);
        Assert.NotSame(first, second);
        Assert.True(first.Disposed);
        Assert.Equal([9], second.State);
        Assert.Equal("/x/b.txt", second.File);

        controller.SetInstrument(1, null);
        Assert.Null(controller.Engine.Root.Tracks[0].Instrument);
        Assert.True(second.Disposed);
        Assert.Throws<InvalidOperationException>(() => controller.SetInstrument(1, MidiFixtures.Instrument("no.such")));
        Assert.Throws<InvalidOperationException>(() => controller.SetInstrument(5, MidiFixtures.Instrument()));
    }

    [Fact]
    public void Removing_a_track_disposes_its_instrument_and_states_are_reported() {
        Load(
            1,
            [MidiFixtures.InstrumentTrack(1), MidiFixtures.InstrumentTrack(2), TestAudio.Track(3)],
            [new(1, [], []), new(2, [], []), new(3, [], [])]
        );
        Instrument(1).State = [7, 7];
        var states = controller.InstrumentStates();
        Assert.Equal([(1, Convert.ToBase64String([7, 7])), (2, (string?)null)], states.Select(static s => (s.TrackId, s.State)));
        var gone = Instrument(2);
        Load(2, [MidiFixtures.InstrumentTrack(1)], [new(1, [], [])]);
        Assert.True(gone.Disposed);
        Assert.False(Instrument(1).Disposed);
    }

    [Fact]
    public void A_stereo_change_keeps_the_instrument() {
        Load(1, [MidiFixtures.InstrumentTrack(1) with { Stereo = false }], [new(1, [], [])]);
        var instrument = Instrument(1);
        Assert.True(controller.SetTrackIo(1, -1, true, false, false));
        Assert.Same(instrument, Instrument(1));
        Assert.False(instrument.Disposed);
    }

    [Fact]
    public void Device_input_goes_to_matching_armed_tracks_else_to_the_thru_track() {
        Load(
            1,
            [
                MidiFixtures.InstrumentTrack(1, armed: true, midi: new("in-a", 0, null, 1)),
                MidiFixtures.InstrumentTrack(2, armed: true, midi: new("in-b", 2, null, 1)),
                MidiFixtures.InstrumentTrack(3)
            ],
            [new(1, [], []), new(2, [], []), new(3, [], [])]
        );
        var batches = new List<MidiActivityMessage>();
        controller.Midi.Activity = batches.Add;
        controller.SetMidiThru(3);
        midi.Play(0, 0x90, 60, 100); // in-a: track 1
        midi.Play(1, 0x91, 61, 100); // in-b, channel 2: track 2
        midi.Play(1, 0x92, 62, 100); // in-b, channel 3: nobody armed matches, so the thru track
        midi.Play(0, 0xF8, 0, 0); // clock: dropped
        controller.Midi.Pump();
        driver.Process();
        Assert.Equal([60], Instrument(1).Events.Select(static e => (int)e.Data1));
        Assert.Equal([61], Instrument(2).Events.Select(static e => (int)e.Data1));
        Assert.Equal([62], Instrument(3).Events.Select(static e => (int)e.Data1));
        var batch = Assert.Single(batches);
        Assert.Equal(
            [(1, "in-a", 60), (2, "in-b", 61), (3, "in-b", 62)],
            batch.Events.Select(static e => (e.TrackId, e.Device, e.Data1))
        );

        // Disarmed, everything goes to the thru track; without one it plays nowhere.
        Assert.True(controller.SetTrackIo(1, -1, true, false, false));
        Assert.True(controller.SetTrackIo(2, -1, true, false, false));
        midi.Play(0, 0x90, 70, 100);
        controller.Midi.Pump();
        controller.SetMidiThru(-1);
        midi.Play(0, 0x90, 71, 100);
        controller.Midi.Pump();
        driver.Process();
        Assert.Equal([62, 70], Instrument(3).Events.Select(static e => (int)e.Data1));
        Assert.Single(Instrument(1).Events);
    }

    [Fact]
    public void Activity_comes_in_batches_at_most_thirty_a_second() {
        Load(1, [MidiFixtures.InstrumentTrack(1)], [new(1, [], [])]);
        var batches = new List<(DateTime At, MidiActivityMessage Batch)>();
        controller.Midi.Activity = b => batches.Add((DateTime.UtcNow, b));
        var until = DateTime.UtcNow.AddMilliseconds(500);
        while (DateTime.UtcNow < until) {
            controller.LiveMidi(1, [new(0x90, 60, 1)]);
            controller.Midi.Pump();
            Thread.Sleep(1);
        }

        Assert.InRange(batches.Count, 10, 17);
        for (var i = 1; i < batches.Count; i++) {
            Assert.True((batches[i].At - batches[i - 1].At).TotalMilliseconds >= 30);
        }

        Assert.All(batches, static b => Assert.All(b.Batch.Events, static e => Assert.Equal(1, e.TrackId)));
    }

    [Fact]
    public void Ports_are_listed_and_hotplug_is_noticed() {
        var lists = new List<IReadOnlyList<EngineMidiPort>>();
        using var hub = new Midi.MidiHub(controller.Engine, midi) { ScanInterval = TimeSpan.Zero, PortsChanged = lists.Add };
        hub.Pump();
        Assert.Equal(
            [new("in-a", "Keys A", true, false), new("in-b", "Keys B", true, false), new("out-a", "Synth", false, true)],
            Assert.Single(lists)
        );
        hub.Pump();
        Assert.Single(lists); // nothing changed
        midi.SetPorts([new("in-a", "Keys A", true), new("in-c", "Pads", true), new("out-a", "Synth", false), new("out-c", "Pads", false)]);
        hub.Pump();
        Assert.Equal(2, lists.Count);
        Assert.Equal(["in-a", "in-c", "out-a", "out-c"], lists[1].Select(static p => p.Id));
        Assert.Equal(lists[1], hub.Ports);
    }

    [Fact]
    public void External_tracks_send_on_their_channel_timestamped_to_the_output_time() {
        using var late = new EngineController(new(Rate, Block, 0, 4_800), null, midi);
        using var lateDriver = new OfflineDriver(late, Block);
        using (var segment = TestAudio.Segment([new(4, MidiFixtures.Notes((640, 64, 50)), [])], 1)) {
            late.LoadModel(TestAudio.Load(segment, 1, true, [MidiFixtures.ExternalTrack(4, "out-a", 10)], [], dir));
        }

        lateDriver.PlayFrom(0);
        var before = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1e9 / System.Diagnostics.Stopwatch.Frequency));
        for (var i = 0; i < 20; i++) {
            lateDriver.Process();
        }

        late.Midi.Pump();
        var sent = midi.SentNow();
        Assert.Equal([(0x99, 50), (0x89, 50)], sent.Select(static s => ((int)s.Message[0], (int)s.Message[1])));
        // At least the output latency (100 ms) after the callback that played it started.
        Assert.True(sent[0].TimeNs >= before + 100_000_000L);
        Assert.InRange(sent[1].TimeNs - sent[0].TimeNs, 0, 5_000_000L);

        // A backend that cannot schedule gets each message once it is due.
        midi.Sent.Clear();
        midi.SchedulesOutput = false;
        lateDriver.Send(new(EngineCommandKind.Locate, 0));
        lateDriver.Process();
        lateDriver.Send(new(EngineCommandKind.Play));
        for (var i = 0; i < 20; i++) {
            lateDriver.Process();
        }

        late.Midi.Pump();
        Assert.Empty(midi.SentNow()); // due 100 ms from now
        Thread.Sleep(150);
        late.Midi.Pump();
        Assert.Equal(2, midi.SentNow().Length);
    }

    [Fact]
    public async Task Recording_captures_live_midi_of_armed_tracks_shifted_by_the_latency() {
        Load(
            1,
            [
                MidiFixtures.InstrumentTrack(1, armed: true) with { InputChannel = 0 },
                MidiFixtures.InstrumentTrack(2),
                TestAudio.Track(3, input: 0, armed: true)
            ],
            [new(1, [], []), new(2, [], []), new(3, [], [])]
        );
        using var stop = new CancellationTokenSource();
        var callbacks = Task.Run(
            () => {
                while (!stop.IsCancellationRequested) {
                    lock (driver) {
                        driver.Process();
                    }

                    Thread.Sleep(1);
                }
            },
            TestContext.Current.CancellationToken
        );
        try {
            await controller.RecordAsync(48_000, TestContext.Current.CancellationToken);
            await WaitFor(() => controller.Engine.Snapshot.Position > 48_000 + 1_000);
            Assert.True(controller.LiveMidi(1, [new(0x90, 60, 100)]));
            Assert.True(controller.LiveMidi(2, [new(0x90, 61, 100)])); // not armed: plays, not recorded
            await WaitFor(() => Instrument(1).Events.Any());
            var heard = controller.Engine.Snapshot.Position;
            Assert.True(controller.LiveMidi(1, [new(0x80, 60, 0), new(0x90, 64, 80)])); // 64 is still held at the stop
            await WaitFor(() => Instrument(1).Events.Count() == 3);
            await WaitFor(() => controller.Engine.Snapshot.Position > heard + 2_000);
            var result = controller.Stop();
            Assert.NotNull(result);
            var take = Assert.Single(result.MidiTakes);
            Assert.Equal(1, take.TrackId);
            Assert.Equal(48_000, take.StartSample);
            Assert.True(take.EndSample > heard);
            Assert.Equal(
                [(0x90, 60), (0x80, 60), (0x90, 64), (0x80, 64)],
                take.Events.Select(static e => (e.Status, e.Data1))
            );
            Assert.InRange(take.Events[0].Sample, 48_000 + 1_000 - Block, heard);
            Assert.Equal(take.EndSample, take.Events[3].Sample);
            Assert.All(take.Events, e => Assert.InRange(e.Sample, take.StartSample, take.EndSample));
            // The armed audio track records audio; the armed instrument track does not.
            Assert.Equal([3], result.Takes.Select(static t => t.TrackId));
        } finally {
            await stop.CancelAsync();
            await callbacks;
        }
    }

    static async Task WaitFor(Func<bool> condition) {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }
    }
}
