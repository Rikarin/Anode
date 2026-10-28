using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

/// <summary>
///     Bounce in place (docs/06-phase-5.md): a freeze render of several tracks, each isolated (no other track, no sends,
///     not its mute or the solos), through its inserts unless they are bypassed, before its fader or through its fader
///     and pan, with a tail past its regions; compared bit for bit with the files' known samples.
/// </summary>
public sealed class BounceInPlaceTests : IDisposable {
    const int Rate = 48_000;
    const long Frames = Rate;
    readonly string dir = TestAudio.TempDirectory();
    readonly string mono;

    public BounceInPlaceTests() {
        mono = TestAudio.Write24(dir, "mono.wav", 1, Frames, 1);
    }

    public void Dispose() => Directory.Delete(dir, true);

    static CancellationToken Token => TestContext.Current.CancellationToken;

    static float M(long n) => n is >= 0 and < Frames ? TestAudio.Sample(n, 1) : 0f;

    /// <summary>
    ///     Track 1: the file through a 100-frame latent insert, fader 0.3 panned left, muted; track 2: the file 5,000
    ///     frames on through a ×0.5 insert, fader 0.8 panned right, soloed, with a send; track 3 plays the file too but is
    ///     not bounced.
    /// </summary>
    EngineController Controller() {
        var controller = new EngineController(new(Rate, 64), TestEffects.Registry());
        TrackModel[] models = [
            new(1, [], [TestAudio.Region(0, Frames, 0)]),
            new(2, [], [TestAudio.Region(0, Frames - 5_000, 0, 5_000)]),
            new(3, [], [TestAudio.Region(0, Frames, 0)])
        ];
        EngineTrack[] tracks = [
            new(
                1,
                -1,
                false,
                false,
                false,
                new(
                    0.3f,
                    -0.5f,
                    true,
                    false,
                    [TestEffects.Insert("d", TestEffects.Delay, new Dictionary<string, double> { ["frames"] = 100 })]
                )
            ),
            new(
                2,
                -1,
                false,
                false,
                false,
                new(
                    0.8f,
                    0.4f,
                    false,
                    true,
                    [TestEffects.Insert("g", TestEffects.Gain, new Dictionary<string, double> { ["gain"] = 0.5 })],
                    [new("s", 1, 1f, 0f, false)]
                )
            ),
            TestAudio.Track(3)
        ];
        using var segment = TestAudio.Segment(models, 1);
        controller.LoadModel(TestAudio.Load(segment, 1, true, tracks, [new(0, mono, 1, Rate, Frames)], dir));
        return controller;
    }

    static void Same(float expected, float actual, long frame, string what) {
        if (expected != actual) {
            Assert.Fail($"{what}, frame {frame}: expected {expected}, got {actual}");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Each_track_renders_isolated_with_or_without_effects_and_volume_and_pan(bool bypass, bool fader) {
        using var controller = Controller();
        const long end = Frames + Rate / 4; // a tail
        var path = Path.Combine(dir, $"bounce-{bypass}-{fader}");
        var files = await controller.RenderAsync(
            new(1, 0, end, path, [1, 2], true, IncludeVolumePan: fader, BypassInserts: bypass),
            null,
            Token
        );
        Assert.Equal([Path.Combine(path, "1.wav"), Path.Combine(path, "2.wav")], files);
        var one = RenderTests.ReadFloat(files[0], out _);
        var two = RenderTests.ReadFloat(files[1], out _);
        Assert.Equal(2 * end, one.Length);
        Assert.Equal(2 * end, two.Length);

        // An insert makes a mono track stereo, so its pan law is the stereo one; bypassed, it keeps the mono law.
        var (l1, r1) = fader ? Strip.PanGains(-0.5f, !bypass) : (1f, 1f);
        var (l2, r2) = fader ? Strip.PanGains(0.4f, !bypass) : (1f, 1f);
        var g1 = fader ? 0.3f : 1f;
        var g2 = fader ? 0.8f : 1f;
        var insert2 = bypass ? 1f : 0.5f;
        for (long n = 0; n < end; n++) {
            var s1 = M(n) * g1; // the delay insert's latency is removed, or it is bypassed
            var s2 = M(n + 5_000) * insert2 * g2;
            if (n >= Frames - 5_000) {
                s2 = 0f;
            }

            Same(s1 * l1, one[2 * n], n, "track 1 left");
            Same(s1 * r1, one[2 * n + 1], n, "track 1 right");
            Same(s2 * l2, two[2 * n], n, "track 2 left");
            Same(s2 * r2, two[2 * n + 1], n, "track 2 right");
        }

        // The inserts run again once the render is done.
        Assert.False(controller.IsRendering);
        var stems = await controller.RenderAsync(new(2, 0, 1_000, Path.Combine(dir, "after"), [2]), null, Token);
        var stem = RenderTests.ReadFloat(stems[0], out _);
        var (_, pr) = Strip.PanGains(0.4f, true);
        Same(M(5_500) * 0.5f * (0.8f * pr), stem[2 * 500 + 1], 500, "track 2 after the bounce");
    }

    [Fact]
    public async Task A_freeze_without_tracks_is_refused() {
        using var controller = Controller();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.RenderAsync(new(1, 0, 100, dir, [], true), null, Token)
        );
    }
}
