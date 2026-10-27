using Anode.Engine.Recording;
using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

public sealed unsafe class MetronomeTests : IDisposable {
    const int Rate = 48_000;
    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

    /// <summary>
    ///     Clicks every beat from 0 for <paramref name="bars" /> bars of <paramref name="beats" /> beats of
    ///     <paramref name="beatFrames" />.
    /// </summary>
    static PlaybackEvent[] Clicks(int bars, int beats, long beatFrames, long from = 0) =>
        [..Enumerable.Range(0, bars * beats).Select(i => TestAudio.Click(from + i * beatFrames, i % beats == 0))];

    static (long, int) CountIn(PlaybackEvent[] events, long position) {
        fixed (PlaybackEvent* p = events.AsSpan()) {
            return Metronome.CountInFor(new(p, events.Length, null, 0), position, Rate);
        }
    }

    [Fact]
    public void The_count_in_is_timed_from_the_models_clicks() {
        var fourFour = Clicks(8, 4, 24_000); // 120 bpm
        Assert.Equal((96_000L, 4), CountIn(fourFour, 100_000));
        Assert.Equal((96_000L, 4), CountIn(fourFour, 0));
        var threeFour = Clicks(8, 3, 16_000); // 180 bpm
        Assert.Equal((48_000L, 3), CountIn(threeFour, 50_000));
        var late = Clicks(4, 6, 12_000, 48_000); // starts after the record position
        Assert.Equal((72_000L, 6), CountIn(late, 0));
        Assert.Equal((96_000L, 4), CountIn([], 5_000)); // nothing to measure: 4/4 at 120 bpm
        Assert.Equal((96_000L, 4), CountIn(Clicks(1, 4, 24_000), 5_000)); // a single bar has no second downbeat
    }

    EngineController Controller(PlaybackEvent[] clicks, long length = Rate * 10) {
        var controller = new EngineController(new(Rate, 64));
        using var segment = TestAudio.Segment([new(TrackEntry.MetronomeTrack, clicks, [])], 1, length);
        controller.LoadModel(TestAudio.Load(segment, 1, true, [], [], dir));
        return controller;
    }

    [Fact]
    public void Clicks_start_on_their_sample_and_cut_the_previous_one() {
        using var controller = Controller(
            [TestAudio.Click(1_000, true), TestAudio.Click(1_500, false), TestAudio.Click(25_000, false)]
        );
        controller.Engine.SetMetronome(true, 0.5f);
        using var driver = new OfflineDriver(controller, 64);
        driver.PlayFrom(0);
        var (left, right) = driver.Render(Rate);
        var accent = controller.Engine.Metronome.Accent;
        var beat = controller.Engine.Metronome.Beat;
        var expected = new float[Rate];
        for (var i = 1_000; i < 1_500; i++) {
            expected[i] = accent[i - 1_000] * 0.5f;
        }

        for (var i = 1_500; i < 1_500 + beat.Length; i++) {
            expected[i] = beat[i - 1_500] * 0.5f;
        }

        for (var i = 25_000; i < 25_000 + beat.Length; i++) {
            expected[i] = beat[i - 25_000] * 0.5f;
        }

        Assert.Equal(expected, left);
        Assert.Equal(expected, right);
    }

    [Fact]
    public void Clicks_follow_the_cycle_and_a_disabled_metronome_is_silent() {
        using var controller = Controller([TestAudio.Click(0, true), TestAudio.Click(12_000, false)]);
        using var driver = new OfflineDriver(controller, 64);
        driver.Send(new(EngineCommandKind.SetCycle, 0, 24_000, true));
        driver.PlayFrom(0);
        Assert.All(driver.Render(Rate).Left, static v => Assert.Equal(0f, v));

        controller.Engine.SetMetronome(true, 1f);
        var left = driver.Render(Rate).Left; // positions 0..24000 twice: the clicks at 0 and 12000 in each lap
        var accent = controller.Engine.Metronome.Accent;
        foreach (var lap in new[] { 0, 24_000 }) {
            Assert.Equal(0f, left[lap]);
            Assert.Equal(accent[1], left[lap + 1]);
            Assert.Equal(accent[100], left[lap + 100]);
        }
    }

    [Fact]
    public void A_count_in_holds_the_playhead_for_a_bar_then_recording_starts() {
        using var controller = Controller(Clicks(16, 4, 2_400)); // short beats keep the test small
        controller.Engine.SetMetronome(false, 1f); // the count-in sounds even with the metronome off
        using var driver = new OfflineDriver(controller, 64);
        var take = new RecordTake(1, Path.Combine(dir, "count-in.wav"), 1, Rate, 0);
        using var segment = TestAudio.Segment([new(1, [], [])], 2);
        controller.LoadModel(TestAudio.Load(segment, 2, false, [TestAudio.Track(1, input: 0, armed: true)], [], dir));
        controller.Engine.Root.Tracks[0].Take = take;
        driver.PlayFrom(0);
        driver.Send(new(EngineCommandKind.Stop));
        driver.Process();
        driver.Send(new(EngineCommandKind.Locate, 48_000));
        var (frames, beats) = CountIn(Clicks(16, 4, 2_400), 48_000);
        Assert.Equal((9_600L, 4), (frames, beats));
        driver.Send(new(EngineCommandKind.Record, frames + 1, beats)); // a count-in that ends mid-block
        driver.Process();
        var first = driver.Output(0).ToArray();
        var snapshot = controller.Engine.Snapshot;
        Assert.Equal(TransportState.Recording, snapshot.State);
        Assert.True(snapshot.CountIn);
        Assert.Equal(48_000, snapshot.Position);

        var left = first.Concat(driver.Render(64 * 160).Left).ToArray();
        var accent = controller.Engine.Metronome.Accent;
        var beat = controller.Engine.Metronome.Beat;
        Assert.Equal(accent[5], left[5]);
        foreach (var k in new[] { 1, 2, 3 }) {
            var at = (int)((frames + 1) * k / beats);
            Assert.Equal(0f, left[at]);
            Assert.Equal(beat[5], left[at + 5]);
        }

        Assert.Equal(48_000 + 64 * 161 - (frames + 1), controller.Engine.Snapshot.Position + 64);

        Assert.Equal(48_000, take.StartPosition); // recording began where the count-in held the playhead
        Assert.Equal(64 * 161 - (frames + 1), take.Pushed); // from the count-in's last frame on, mid-block
        controller.Engine.Root.Tracks[0].Take = null;
        take.Dispose();
    }
}
