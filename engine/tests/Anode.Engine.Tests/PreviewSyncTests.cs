using Anode.Engine.Recording;

namespace Anode.Engine.Tests;

/// <summary>
///     The loop preview in sync with the project (docs/07-phase-6.md, "Loops", <c>setPreviewSync</c>): the preview channel
///     renders the file through the Stretch renderer at a tempo ratio and a transpose, and, while the transport plays,
///     starts on the next bar line of the tempo map.
/// </summary>
public sealed class PreviewSyncTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;

    /// <summary>The file frames of the bursts in <see cref="FlexPlaybackTests.WriteBursts" />'s file.</summary>
    static readonly long[] Bursts = [4_800, 14_400, 24_000, 33_600, 43_200, 52_800, 62_400];

    readonly string dir = TestAudio.TempDirectory();
    readonly EngineController controller = new(new(Rate, Block));
    readonly OfflineDriver driver;

    public PreviewSyncTests() {
        driver = new(controller, Block);
    }

    public void Dispose() {
        driver.Dispose();
        controller.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void A_loop_plays_at_the_tempo_ratio_from_the_next_bar_while_playing() {
        var bursts = FlexPlaybackTests.WriteBursts(dir, "bursts.wav", 2);
        // 100 bpm: a bar is 115,200 samples.
        using (var segment = TestAudio.Segment([new(1, [], [])], 1, tempo: [TestAudio.Tempo(0, 0, 100)])) {
            controller.LoadModel(TestAudio.Load(segment, 1, true, [TestAudio.Track(1)], [], dir));
        }

        driver.PlayFrom(30_000);
        driver.Process();
        controller.SetPreviewSync(1.25, 0, true); // the loop is 80 bpm: it plays at 125 %
        controller.Preview(bursts, 1f);
        Assert.Equal(64_000, controller.Engine.Preview!.TotalFrames);
        var origin = 30_000 + Block;
        var (left, _) = driver.Render(200_000);
        var bar = 115_200 - origin;
        Assert.All(left[..bar], static v => Assert.Equal(0f, v));
        var onsets = FlexPlaybackTests.Onsets(left);
        Assert.Equal(Bursts.Length, onsets.Count);
        for (var k = 0; k < Bursts.Length; k++) {
            var expected = bar + Bursts[k] / 1.25;
            Assert.InRange(onsets[k], expected - Rate * 0.003, expected + Rate * 0.003);
        }

        Assert.All(left[(bar + 64_000)..], static v => Assert.Equal(0f, v));
    }

    [Fact]
    public void A_transpose_shifts_the_pitch_and_a_stopped_transport_starts_it_at_once() {
        var path = Path.Combine(dir, "sine.wav");
        using (var writer = new WavWriter(path, 1, Rate)) {
            writer.Write(
                [
                    ..Enumerable.Range(0, 2 * Rate)
                        .Select(static n => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * n / Rate)))
                ]
            );
        }

        controller.SetPreviewSync(1, 12, true);
        controller.Preview(path, 1f);
        var (left, _) = driver.Render(Rate);
        Assert.NotEqual(0f, left[100]); // stopped: at once
        Assert.InRange(Frequency(left.AsSpan(Rate / 4, Rate / 2)), 880 * 0.99, 880 * 1.01);

        // A new sync restarts the playing file that way.
        controller.SetPreviewSync(1, 0, false);
        Assert.Equal(1, controller.Engine.Preview!.Sync.TempoRatio);
        var (again, _) = driver.Render(Rate);
        Assert.InRange(Frequency(again.AsSpan(Rate / 4, Rate / 2)), 440 * 0.99, 440 * 1.01);
        Assert.Throws<ArgumentException>(() => controller.SetPreviewSync(0, 0, false));
    }

    /// <summary>The frequency of a sine from its rising zero crossings.</summary>
    static double Frequency(ReadOnlySpan<float> signal) {
        int first = -1, last = -1, count = 0;
        for (var i = 1; i < signal.Length; i++) {
            if (signal[i - 1] < 0 && signal[i] >= 0) {
                if (first < 0) {
                    first = i;
                }

                last = i;
                count++;
            }
        }

        return (count - 1) * (double)Rate / (last - first);
    }
}
