using Anode.Engine.Recording;
using Anode.Ipc;

namespace Anode.Engine.Tests;

/// <summary>
///     Flashback Capture, offline: an armed audio track's input and a thru instrument track's live MIDI while playing,
///     written as takes at the positions they were played (through cycle wraps, as passes), bounded by the buffer, and
///     only since playback last started.
/// </summary>
public sealed unsafe class FlashbackTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    const int Seed = 30;
    readonly string dir = TestAudio.TempDirectory();
    EngineController? controller;
    OfflineDriver? driver;
    long frame;

    EngineController Controller => controller!;

    OfflineDriver Driver => driver!;

    public void Dispose() {
        driver?.Dispose();
        controller?.Dispose();
        Directory.Delete(dir, true);
    }

    /// <summary>Track 1: armed stereo audio on inputs 0–1; track 2: an instrument (the MIDI thru track).</summary>
    void Start(int latency = 0, double seconds = 600) {
        var (registry, _) = MidiFixtures.Registry();
        controller = new(new(Rate, Block, latency, FlashbackSeconds: seconds), registry);
        using var segment = TestAudio.Segment([new(1, [], []), new(2, [], [])], 1);
        Controller.LoadModel(
            TestAudio.Load(
                segment,
                1,
                true,
                [TestAudio.Track(1, true, input: 0, armed: true), MidiFixtures.InstrumentTrack(2)],
                [],
                dir
            )
        );
        Controller.SetMidiThru(2);
        driver = new(Controller, Block);
    }

    void Blocks(long count) {
        for (var b = 0; b < count; b++) {
            for (var i = 0; i < Block; i++) {
                Driver.Input(0)[i] = TestAudio.Sample(frame + i, Seed);
                Driver.Input(1)[i] = TestAudio.Sample(frame + i, Seed + 1);
            }

            Driver.Process();
            frame += Block;
        }
    }

    static void AssertInput(int[] samples, long from, long input, long count) {
        for (long f = 0; f < count; f++) {
            if (samples[2 * (from + f)] != TestAudio.Pattern(input + f, Seed)
                || samples[2 * (from + f) + 1] != TestAudio.Pattern(input + f, Seed + 1)) {
                Assert.Fail($"file frame {from + f}: expected input frame {input + f}");
            }
        }
    }

    [Fact]
    public void Only_armed_audio_tracks_keep_a_buffer_and_disarming_frees_it() {
        Start(seconds: 600);
        var root = Controller.Engine.Root;
        var memory = Assert.IsType<FlashbackBuffer>(root.Tracks[0].Flashback);
        Assert.Equal(600L * Rate, memory.Capacity); // ten minutes
        Assert.Equal(2, memory.Channels);
        Assert.Null(root.Tracks[1].Flashback); // an instrument track keeps MIDI instead
        Assert.True(Controller.SetTrackIo(1, 0, true, false, false));
        Assert.Null(root.Tracks[0].Flashback);
        Assert.True(Controller.SetTrackIo(1, 0, false, true, false)); // armed again, now mono
        Assert.Equal(1, Controller.Engine.Root.Tracks[0].Flashback!.Channels); // a new track state
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public void A_capture_writes_what_was_played_with_cycle_passes(int latency) {
        Start(latency);
        Driver.Send(new(EngineCommandKind.SetCycle, 20_000, 30_000, true));
        Driver.PlayFrom(14_000);
        var played = frame;
        Blocks(600); // 38,400 frames: 6,000 to the cycle end, three whole passes, and 2,400 into a fifth
        Driver.Send(new(EngineCommandKind.Stop));
        Blocks(10); // stopped: nothing more is kept
        var result = Controller.FlashbackCapture();
        var take = Assert.Single(result.Takes);
        Assert.Equal(1, take.TrackId);
        Assert.Equal(2, take.Channels);
        Assert.Equal(38_400, take.Frames);
        var start = 14_000L - latency;
        Assert.Equal(start, take.StartSample);
        var first = 30_000 - start;
        List<RecordedPass> expected = [new(start, 0, first)];
        for (var offset = first; offset < 38_400; offset += 10_000) {
            expected.Add(new(20_000, offset, Math.Min(10_000, 38_400 - offset)));
        }

        Assert.Equal(expected, take.Passes!);
        Assert.EndsWith("-flashback.wav", take.Path);
        AssertInput(TestAudio.Read24(take.Path, out _), 0, played, 38_400);
        Assert.Empty(result.MidiTakes);
    }

    [Fact]
    public void A_capture_keeps_only_what_the_buffer_still_holds() {
        Start(seconds: 4);
        Driver.PlayFrom(0);
        var played = frame;
        Blocks(6 * Rate / Block);
        var take = Assert.Single(Controller.FlashbackCapture().Takes);
        var kept = 3 * Rate; // four seconds less the quarter kept clear of the audio thread
        Assert.Equal(kept, take.Frames);
        Assert.Equal(6L * Rate - kept, take.StartSample);
        Assert.Null(take.Passes);
        AssertInput(TestAudio.Read24(take.Path, out _), 0, played + 6 * Rate - kept, kept);
    }

    [Fact]
    public void A_capture_covers_playback_since_it_last_started() {
        Start();
        Assert.Empty(Controller.FlashbackCapture().Takes); // nothing played yet
        Driver.PlayFrom(0);
        Blocks(100);
        Driver.Send(new(EngineCommandKind.Stop));
        Blocks(2);
        Driver.Send(new(EngineCommandKind.Play)); // resumes where it stopped: a new run all the same
        var resumed = frame;
        Blocks(50);
        var take = Assert.Single(Controller.FlashbackCapture().Takes);
        Assert.Equal(100 * Block, take.StartSample);
        Assert.Equal(50 * Block, take.Frames);
        AssertInput(TestAudio.Read24(take.Path, out _), 0, resumed, 50 * Block);

        // A recording ends the run: there is nothing to capture until playback starts again.
        Driver.Send(new(EngineCommandKind.Record));
        Blocks(5);
        Driver.Send(new(EngineCommandKind.Stop));
        Blocks(1);
        Assert.Empty(Controller.FlashbackCapture().Takes);
    }

    [Fact]
    public void Live_midi_on_the_thru_track_is_captured_where_it_was_played_and_merged_across_wraps() {
        Start();
        Driver.Send(new(EngineCommandKind.SetCycle, 10_000, 20_000, true));
        Driver.PlayFrom(10_000);
        Blocks(20); // 1,280
        Assert.True(Controller.LiveMidi(2, [new(0x90, 60, 100)]));
        Blocks(20); // 2,560
        Assert.True(Controller.LiveMidi(2, [new(0x80, 60, 0)]));
        Blocks(200); // 15,360: wrapped once, now at 15,360 − 10,000 + 10,000
        Assert.True(Controller.LiveMidi(2, [new(0x90, 72, 90)])); // still held at the capture
        Blocks(10);
        var result = Controller.FlashbackCapture();
        Assert.DoesNotContain(result.Takes, static t => t.TrackId == 2);
        var take = Assert.Single(result.MidiTakes);
        Assert.Equal(2, take.TrackId);
        Assert.Equal((10_000, 20_000), (take.StartSample, take.EndSample)); // the cycle, wrapped
        Assert.Equal(
            [(0x90, 60), (0x80, 60), (0x90, 72), (0x80, 72)],
            take.Events.Select(static e => (e.Status, e.Data1))
        );
        // Each lands within a block of where the playhead was when it was sent (one block of shift back).
        Assert.InRange(take.Events[0].Sample, 10_000 + 1_280 - 2 * Block, 10_000 + 1_280 + Block);
        Assert.InRange(take.Events[1].Sample, 10_000 + 2_560 - 2 * Block, 10_000 + 2_560 + Block);
        Assert.InRange(take.Events[2].Sample, 10_000 + 5_360 - 2 * Block, 10_000 + 5_360 + Block);
        Assert.Equal(10_000 + 6_000, take.Events[3].Sample); // closed where the capture was taken
    }
}
