using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

/// <summary>What happens when the model, the cycle or the playhead changes while playing.</summary>
public sealed class PlaybackChangeTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    readonly string dir = TestAudio.TempDirectory();
    readonly EngineController controller = new(new(Rate, Block));
    readonly OfflineDriver driver;
    readonly string file;

    public PlaybackChangeTests() {
        driver = new(controller, Block);
        file = TestAudio.Write24(dir, "a.wav", 1, 4 * Rate, 5);
    }

    public void Dispose() {
        driver.Dispose();
        controller.Dispose();
        Directory.Delete(dir, true);
    }

    void Load(long version, bool replace, float gain) {
        using var segment = TestAudio.Segment([new(1, [], [TestAudio.Region(0, 4 * Rate, 0, 0, gain)])], version);
        controller.LoadModel(
            TestAudio.Load(segment, version, replace, [TestAudio.Track(1)], [new(0, file, 1, Rate, 4 * Rate)], dir)
        );
    }

    [Fact]
    public void An_edit_while_playing_splices_in_without_a_gap() {
        Load(1, true, 1f);
        driver.PlayFrom(0);
        var before = driver.Render(Rate / 2).Left;
        for (var n = 0; n < before.Length; n++) {
            Assert.Equal(TestAudio.Sample(n, 5), before[n]);
        }

        Load(2, false, 0.5f); // a per-track segment: the region's gain changed
        var after = driver.Render(Rate / 2).Left;
        var switched = -1;
        for (var n = 0; n < after.Length; n++) {
            var position = before.Length + n;
            var sample = TestAudio.Sample(position, 5);
            if (switched < 0 && after[n] == sample) {
                continue;
            }

            if (switched < 0) {
                switched = n;
            }

            Assert.Equal(sample * 0.5f, after[n]);
        }

        Assert.InRange(switched, 1, controller.Streamer.LeadFrames + Block);
        Assert.Equal(0, controller.Engine.Snapshot.Underruns);
    }

    [Fact]
    public void A_cycle_set_while_playing_wraps_seamlessly() {
        Load(1, true, 1f);
        driver.PlayFrom(0);
        var first = driver.Render(Rate / 2).Left;
        var cycle = new Cycle(true, 38_400, 43_200); // 0.8 s to 0.9 s: after the new fill's lead
        driver.Send(new(EngineCommandKind.SetCycle, cycle.Start, cycle.End, true));
        var rest = driver.Render(Rate).Left;
        long position = first.Length;
        foreach (var sample in rest) {
            Assert.Equal(TestAudio.Sample(position, 5), sample);
            position = cycle.Advance(position, 1);
        }

        Assert.Equal(0, controller.Engine.Snapshot.Underruns);
    }

    [Fact]
    public void A_locate_while_playing_continues_from_the_new_position_after_one_silent_block() {
        Load(1, true, 1f);
        driver.PlayFrom(0);
        var first = driver.Render(Rate / 4).Left;
        Assert.Equal(TestAudio.Sample(first.Length - 1, 5), first[^1]);
        driver.Send(new(EngineCommandKind.Locate, 2 * Rate));
        var rest = driver.Render(Rate / 4).Left;
        Assert.All(rest[..Block], static v => Assert.Equal(0f, v)); // the disk had nothing for the new position yet
        for (var n = Block; n < rest.Length; n++) {
            Assert.Equal(TestAudio.Sample(2 * Rate + n, 5), rest[n]);
        }
    }

    [Fact]
    public void A_stop_and_play_resumes_where_it_stopped() {
        Load(1, true, 1f);
        driver.PlayFrom(1_000);
        var first = driver.Render(Rate / 8).Left;
        driver.Send(new(EngineCommandKind.Stop));
        var stopped = driver.Render(Block * 10).Left;
        Assert.All(stopped, static v => Assert.Equal(0f, v));
        driver.Send(new(EngineCommandKind.Play));
        var resumed = driver.Render(Block * 10).Left;
        for (var n = 0; n < resumed.Length; n++) {
            Assert.Equal(TestAudio.Sample(1_000 + first.Length + n, 5), resumed[n]);
        }
    }

    [Fact]
    public void Removing_a_track_releases_its_segment_and_a_mute_silences_it() {
        Load(1, true, 1f);
        driver.PlayFrom(0);
        driver.Render(Block * 4);
        Assert.True(controller.SetStrip(1, new(1f, 0f, true, false)));
        driver.Render(Block); // the mute ramps over one block
        Assert.All(driver.Render(Block * 4).Left, static v => Assert.Equal(0f, v));

        using var empty = TestAudio.Segment([new(TrackEntry.MetronomeTrack, [], [])], 2);
        controller.LoadModel(TestAudio.Load(empty, 2, true, [], [], dir));
        Assert.Empty(controller.Engine.Root.Tracks);
        Assert.Equal(2, controller.MappedSegments); // the two pooled whole-model slots stay mapped
        Assert.False(controller.SetStrip(1, TestAudio.Unity));
    }
}
