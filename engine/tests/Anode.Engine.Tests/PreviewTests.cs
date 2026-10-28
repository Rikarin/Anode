namespace Anode.Engine.Tests;

/// <summary>The preview channel: a file streamed to the master whatever the transport does; start, replace, stop.</summary>
public sealed class PreviewTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    readonly string dir = TestAudio.TempDirectory();
    readonly EngineController controller = new(new(Rate, Block));
    readonly OfflineDriver driver;

    public PreviewTests() {
        driver = new(controller, Block);
    }

    public void Dispose() {
        driver.Dispose();
        controller.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void A_file_plays_from_its_start_at_its_gain_and_ends_in_silence() {
        var mono = TestAudio.Write24(dir, "mono.wav", 1, 30_000, 2);
        controller.Preview(mono, 0.5f);
        var (left, right) = driver.Render(40_000);
        for (var f = 0; f < 30_000; f++) {
            Assert.Equal(TestAudio.Sample(f, 2) * 0.5f, left[f]);
            Assert.Equal(left[f], right[f]);
        }

        Assert.All(left[30_000..], static v => Assert.Equal(0f, v));
        Assert.True(controller.Engine.Preview!.Ended);
    }

    [Fact]
    public void A_new_file_replaces_the_playing_one_and_null_stops_it() {
        var first = TestAudio.Write24(dir, "first.wav", 1, 4 * Rate, 3);
        var stereo = TestAudio.Write24(dir, "stereo.wav", 2, 4 * Rate, 8);
        controller.Preview(first, 1f);
        var before = driver.Render(Block * 10).Left;
        Assert.Equal(TestAudio.Sample(Block * 10 - 1, 3), before[^1]);
        controller.Preview(stereo, 0.25f);
        var (left, right) = driver.Render(Block * 400);
        for (var f = 0; f < left.Length; f++) {
            Assert.Equal(TestAudio.Sample(f, 8) * 0.25f, left[f]);
            Assert.Equal(TestAudio.Sample(f, 9) * 0.25f, right[f]);
        }

        controller.Preview(null, 1f);
        Assert.Null(controller.Engine.Preview);
        Assert.All(driver.Render(Block * 4).Left, static v => Assert.Equal(0f, v));
    }

    [Fact]
    public void It_plays_beside_the_tracks_while_the_transport_rolls() {
        var track = TestAudio.Write24(dir, "track.wav", 1, 4 * Rate, 4);
        var preview = TestAudio.Write24(dir, "preview.wav", 1, 4 * Rate, 6);
        using var segment = TestAudio.Segment([new(1, [], [TestAudio.Region(0, 4 * Rate, 0)])], 1);
        controller.LoadModel(
            TestAudio.Load(segment, 1, true, [TestAudio.Track(1)], [new(0, track, 1, Rate, 4 * Rate)], dir)
        );
        driver.PlayFrom(1_000);
        controller.Preview(preview, 1f);
        var left = driver.Render(Block * 100).Left;
        for (var f = 0; f < left.Length; f++) {
            Assert.Equal(TestAudio.Sample(1_000 + f, 4) + TestAudio.Sample(f, 6), left[f]);
        }

        Assert.Equal(0, controller.Engine.Snapshot.Underruns);
    }

    [Fact]
    public void A_file_it_cannot_stream_is_refused_and_the_preview_plays_on() {
        var good = TestAudio.Write24(dir, "good.wav", 1, 4 * Rate, 5);
        controller.Preview(good, 1f);
        var text = Path.Combine(dir, "notes.txt");
        File.WriteAllText(text, "not audio");
        Assert.Throws<InvalidDataException>(() => controller.Preview(text, 1f));
        Assert.ThrowsAny<IOException>(() => controller.Preview(Path.Combine(dir, "missing.wav"), 1f));
        Assert.NotNull(controller.Engine.Preview);
        Assert.Equal(TestAudio.Sample(0, 5), driver.Render(Block).Left[0]);
    }
}
