using Anode.Engine.Streaming;
using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

/// <summary>
///     The whole engine rendered offline (the disk streamer pumped before every block): a small model of mono and
///     stereo tracks with region offsets, gains, fades, pans, a mute and a cycle, compared bit for bit with a
///     reference sum computed here from the files' known samples.
/// </summary>
public sealed class OfflineRenderTests : IDisposable {
    const int Rate = 48_000;
    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

    sealed record RefTrack(int Id, bool Stereo, EngineStrip Strip, AudioPlaylistEntry[] Regions);

    [Theory]
    [InlineData(64, false)]
    [InlineData(64, true)]
    [InlineData(1024, true)]
    [InlineData(37, true)]
    public void A_small_model_renders_bit_exactly_against_a_reference_sum(int block, bool cycle) {
        var fileA = TestAudio.Write24(dir, "a.wav", 1, 3 * Rate, 1);
        var fileB = TestAudio.Write24(dir, "b.wav", 2, 2 * Rate, 10);
        Dictionary<int, (int Channels, int Seed)> files = new() { [0] = (1, 1), [1] = (2, 10) };
        RefTrack[] tracks = [
            new(
                1,
                false,
                new(1f, 0f, false, false),
                [
                    TestAudio.Region(4_800, 43_200, 0, 24_000, 0.5f, 480, 960),
                    TestAudio.Region(72_000, 48_000, 0)
                ]
            ),
            new(2, true, new(0.8f, -0.5f, false, false), [TestAudio.Region(0, 2 * Rate, 1)]),
            new(3, false, new(1f, 0.3f, false, false), [TestAudio.Region(12_000, 36_000, 0, 1_000, 0.75f, 100)]),
            new(4, false, new(1f, 0f, true, false), [TestAudio.Region(0, Rate, 0)])
        ];
        var master = new EngineStrip(0.9f, 0f, false, false);
        var models = tracks.Select(static t => new TrackModel(t.Id, [], t.Regions)).ToArray();
        using var segment = TestAudio.Segment(models, 1);
        using var controller = new EngineController(new(Rate, block));
        controller.LoadModel(
            TestAudio.Load(
                segment,
                1,
                true,
                tracks.Select(static t => TestAudio.Track(t.Id, t.Stereo, t.Strip)).ToArray(),
                [new(0, fileA, 1, Rate, 3 * Rate), new(1, fileB, 2, Rate, 2 * Rate)],
                dir,
                master
            )
        );
        using var driver = new OfflineDriver(controller, block);
        var c = new Cycle(cycle, 24_000, 84_000);
        driver.Send(new(EngineCommandKind.SetCycle, c.Start, c.End, c.Enabled));
        driver.PlayFrom(9_600);
        var frames = block * (3 * Rate / block);
        var (left, right) = driver.Render(frames);

        var (masterLeft, masterRight) = Strip.PanGains(0f, true);
        var mL = master.Gain * masterLeft;
        var mR = master.Gain * masterRight;
        var position = 9_600L;
        for (var n = 0; n < frames; n++) {
            float accL = 0, accR = 0;
            foreach (var t in tracks) {
                var (vL, vR) = TrackSample(t, files, position);
                var (pL, pR) = Strip.PanGains(t.Strip.Pan, t.Stereo);
                var gL = t.Strip.Mute ? 0f : t.Strip.Gain * pL;
                var gR = t.Strip.Mute ? 0f : t.Strip.Gain * pR;
                accL += vL * gL;
                accR += vR * gR;
            }

            var expectedLeft = accL * mL;
            var expectedRight = accR * mR;
            if (expectedLeft != left[n] || expectedRight != right[n]) {
                Assert.Fail(
                    $"frame {n} (position {position}): expected {expectedLeft}/{expectedRight}, got {left[n]}/{right[n]}"
                );
            }

            position = c.Advance(position, 1);
        }

        Assert.Equal(0, controller.Engine.Snapshot.Underruns);
        Assert.Contains(left, static v => v != 0);
    }

    /// <summary>The track's pre-strip sample at a position: what the disk thread renders.</summary>
    static (float L, float R) TrackSample(RefTrack t, Dictionary<int, (int Channels, int Seed)> files, long position) {
        foreach (var region in t.Regions) {
            if (position < region.Start || position >= region.Start + region.Length) {
                continue;
            }

            var r = position - region.Start;
            var level = region.FadeIn <= 0 && region.FadeOut <= 0 ? region.Gain : PlaylistRenderer.Level(region, r);
            var (channels, seed) = files[region.FileId];
            var frame = region.FileOffset + r;
            var s0 = TestAudio.Sample(frame, seed);
            var s1 = channels == 2 ? TestAudio.Sample(frame, seed + 1) : s0;
            return t.Stereo ? (s0 * level, s1 * level) : (s0 * level, s0 * level);
        }

        return (0f, 0f);
    }
}
