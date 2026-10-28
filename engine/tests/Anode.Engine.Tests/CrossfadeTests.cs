using Anode.Engine.Streaming;
using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

/// <summary>
///     Segment format 2 in the engine: fade curves (docs/04-phase-3.md: gain x^(2^(−2c)) for a fade-in, mirrored for a
///     fade-out) and crossfades (two playlist entries sounding at once, through a second stream per track, summed
///     sample-exactly), rendered offline and compared bit for bit with a reference computed here from the files' known
///     samples, through cycle wraps, a locate and an edit while playing.
/// </summary>
public sealed class CrossfadeTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

    /// <summary>The fade rule, written out independently of the engine.</summary>
    static float Shape(long step, int length, float curve) =>
        curve == 0f ? (float)step / length : (float)Math.Pow((double)step / length, Math.Pow(2, -2.0 * curve));

    /// <summary>A region's level at frame r from its start: gain × fade-in × fade-out.</summary>
    static float ReferenceLevel(in AudioPlaylistEntry e, long r) {
        var level = e.Gain;
        if (e.FadeIn > 0 && r < e.FadeIn) {
            level *= Shape(r, e.FadeIn, e.FadeInCurve);
        }

        if (e.FadeOut > 0 && e.Length - 1 - r < e.FadeOut) {
            level *= Shape(e.Length - 1 - r, e.FadeOut, e.FadeOutCurve);
        }

        return level;
    }

    static AudioPlaylistEntry Entry(
        long start,
        long length,
        int file,
        long offset,
        float gain,
        int fadeIn,
        float inCurve,
        int fadeOut,
        float outCurve
    ) =>
        TestAudio.Region(start, length, file, offset, gain, fadeIn, fadeOut) with {
            FadeInCurve = inCurve, FadeOutCurve = outCurve
        };

    [Theory]
    [InlineData(0.25, 0.5f, 0.5)] // c = 0.5: √x
    [InlineData(0.5, -0.5f, 0.25)] // c = −0.5: x²
    [InlineData(0.0625, 1f, 0.5)] // c = 1: x^¼
    [InlineData(0.5, -1f, 0.0625)] // c = −1: x⁴
    [InlineData(0.3, 0f, 0.3)] // linear
    public void A_fade_curve_maps_x_to_x_to_the_two_to_the_minus_two_c(double x, float curve, double gain) {
        const int length = 10_000;
        var step = (long)(x * length);
        Assert.Equal((float)gain, PlaylistRenderer.FadeShape(step, length, curve), 6);
        var entry = Entry(0, 100_000, 0, 0, 1f, length, curve, length, curve);
        Assert.Equal(PlaylistRenderer.FadeShape(step, length, curve), PlaylistRenderer.Level(entry, step));
        // The fade-out is the mirror: the same gain the same distance from the region's last frame.
        Assert.Equal(
            PlaylistRenderer.FadeShape(step, length, curve),
            PlaylistRenderer.Level(entry, 100_000 - 1 - step)
        );
    }

    [Fact]
    public void Linear_fades_run_from_and_to_zero() {
        var entry = TestAudio.Region(0, 100, 0, 0, 0.5f, 10, 20);
        Assert.Equal(0f, PlaylistRenderer.Level(entry, 0));
        Assert.Equal(0.25f, PlaylistRenderer.Level(entry, 5));
        Assert.Equal(0.5f, PlaylistRenderer.Level(entry, 10));
        Assert.Equal(0.5f, PlaylistRenderer.Level(entry, 79));
        Assert.Equal(0.5f * (19f / 20), PlaylistRenderer.Level(entry, 80));
        Assert.Equal(0f, PlaylistRenderer.Level(entry, 99));
    }

    [Fact]
    public void Lanes_alternate_along_runs_of_crossfades() {
        AudioPlaylistEntry[] playlist = [
            TestAudio.Region(0, 1_000, 0, 0, 1f, 0, 100), TestAudio.Region(900, 1_000, 0, 0, 1f, 100, 100),
            TestAudio.Region(1_800, 1_000, 0, 0, 1f, 100), TestAudio.Region(3_000, 500, 0),
            TestAudio.Region(3_500, 500, 0, 0, 1f, 0, 50), TestAudio.Region(3_950, 500, 0, 0, 1f, 50)
        ];
        unsafe {
            fixed (AudioPlaylistEntry* p = playlist) {
                Assert.Equal([0, 1, 0, 0, 0, 1], PlaylistLanes.Of(new(null, 0, p, playlist.Length))!);
                Assert.Null(PlaylistLanes.Of(new(null, 0, p + 3, 2))); // butted, not overlapping
            }
        }
    }

    sealed record RefTrack(int Id, bool Stereo, EngineStrip Strip, AudioPlaylistEntry[] Regions);

    /// <summary>
    ///     Tracks with chains of curved crossfades (a comp), mono and stereo, a crossfade across the cycle's start and
    ///     one across its end, plus a plain track.
    /// </summary>
    (RefTrack[] Tracks, Dictionary<int, (int Channels, int Seed)> Files, EngineFile[] EngineFiles) Model() {
        var a = TestAudio.Write24(dir, "a.wav", 1, 4 * Rate, 1);
        var b = TestAudio.Write24(dir, "b.wav", 2, 4 * Rate, 10);
        RefTrack[] tracks = [
            new(
                1,
                false,
                TestAudio.Unity,
                [
                    Entry(1_000, 30_000, 0, 5_000, 0.9f, 300, 0.4f, 2_400, -0.6f),
                    Entry(28_600, 20_000, 1, 100, 0.8f, 2_400, 0.7f, 960, 0f),
                    Entry(47_640, 40_000, 0, 60_000, 1f, 960, -1f, 0, 0f),
                    Entry(90_000, 10_000, 0, 0, 0.5f, 100, 1f, 100, -0.25f)
                ]
            ),
            new(
                2,
                true,
                new(0.7f, 0.4f, false, false),
                [
                    Entry(0, 24_100, 1, 0, 1f, 0, 0f, 4_000, 0.25f), // over the cycle's start at 24,000
                    Entry(20_100, 66_000, 0, 9_000, 0.6f, 4_000, -0.25f, 4_100, 0f),
                    Entry(82_000, 20_000, 1, 33_000, 1f, 4_100, 0.5f, 0, 0f) // over the cycle's end at 84,000
                ]
            ),
            new(3, false, new(1f, -0.3f, false, false), [TestAudio.Region(0, 3 * Rate, 0, 1_234, 0.5f, 480, 480)])
        ];
        return (
            tracks,
            new() { [0] = (1, 1), [1] = (2, 10) },
            [new(0, a, 1, Rate, 4 * Rate), new(1, b, 2, Rate, 4 * Rate)]
        );
    }

    static (float L, float R) TrackSample(RefTrack t, Dictionary<int, (int Channels, int Seed)> files, long position) {
        var sum = (L: 0f, R: 0f);
        var sounding = 0;
        foreach (var region in t.Regions) {
            if (position < region.Start || position >= region.Start + region.Length) {
                continue;
            }

            var r = position - region.Start;
            var level = ReferenceLevel(region, r);
            var (channels, seed) = files[region.FileId];
            var frame = region.FileOffset + r;
            var s0 = TestAudio.Sample(frame, seed);
            var s1 = channels == 2 ? TestAudio.Sample(frame, seed + 1) : s0;
            var mono = channels == 2 ? (s0 + s1) / 2 : s0;
            var (l, rr) = t.Stereo ? (s0 * level, s1 * level) : (mono * level, mono * level);
            sum = (sum.L + l, sum.R + rr);
            sounding++;
        }

        Assert.InRange(sounding, 0, 2);
        return sum;
    }

    static (float L, float R) Mix(RefTrack[] tracks, Dictionary<int, (int, int)> files, long position) {
        float accL = 0, accR = 0;
        foreach (var t in tracks) {
            var (vL, vR) = TrackSample(t, files, position);
            var (pL, pR) = Strip.PanGains(t.Strip.Pan, t.Stereo);
            accL += vL * (t.Strip.Gain * pL);
            accR += vR * (t.Strip.Gain * pR);
        }

        return (accL, accR);
    }

    EngineController Load(RefTrack[] tracks, EngineFile[] files, int block, long version = 1) {
        var models = tracks.Select(static t => new TrackModel(t.Id, [], t.Regions)).ToArray();
        using var segment = TestAudio.Segment(models, version);
        var controller = new EngineController(new(Rate, block));
        controller.LoadModel(
            TestAudio.Load(
                segment,
                version,
                true,
                tracks.Select(static t => TestAudio.Track(t.Id, t.Stereo, t.Strip)).ToArray(),
                files,
                dir
            )
        );
        return controller;
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(64, true)]
    [InlineData(37, true)]
    [InlineData(1024, true)]
    public void Crossfades_and_curves_render_bit_exactly_against_a_reference(int block, bool cycle) {
        var (tracks, files, engineFiles) = Model();
        using var controller = Load(tracks, engineFiles, block);
        Assert.NotNull(controller.Engine.Root.Lanes[0]);
        Assert.NotNull(controller.Engine.Root.Tracks[1].Crossfade);
        Assert.Null(controller.Engine.Root.Lanes[2]);
        Assert.Null(controller.Engine.Root.Tracks[2].Crossfade); // no second stream where nothing overlaps
        using var driver = new OfflineDriver(controller, block);
        var c = new Cycle(cycle, 24_000, 84_000);
        driver.Send(new(EngineCommandKind.SetCycle, c.Start, c.End, c.Enabled));
        driver.PlayFrom(500);
        var frames = block * (3 * Rate / block);
        var (left, right) = driver.Render(frames);
        var position = 500L;
        for (var n = 0; n < frames; n++) {
            var (l, r) = Mix(tracks, files, position);
            if (l != left[n] || r != right[n]) {
                Assert.Fail($"frame {n} (position {position}): expected {l}/{r}, got {left[n]}/{right[n]}");
            }

            position = c.Advance(position, 1);
        }

        Assert.Equal(0, controller.Engine.Snapshot.Underruns);
    }

    [Fact]
    public void A_locate_into_a_crossfade_resumes_exactly_after_one_block() {
        var (tracks, files, engineFiles) = Model();
        using var controller = Load(tracks, engineFiles, Block);
        using var driver = new OfflineDriver(controller, Block);
        driver.PlayFrom(0);
        driver.Render(Rate / 4);
        driver.Send(new(EngineCommandKind.Locate, 28_000)); // just before track 1's crossfade at 28,600
        var (left, right) = driver.Render(Rate / 4);
        Assert.All(left[..Block], static v => Assert.Equal(0f, v)); // the disk had nothing for the new position yet
        for (var n = Block; n < left.Length; n++) {
            var (l, r) = Mix(tracks, files, 28_000 + n);
            Assert.Equal(l, left[n]);
            Assert.Equal(r, right[n]);
        }
    }

    [Fact]
    public void An_edit_that_adds_a_crossfade_while_playing_splices_both_streams_on_one_frame() {
        var file = TestAudio.Write24(dir, "c.wav", 1, 4 * Rate, 3);
        Dictionary<int, (int, int)> files = new() { [0] = (1, 3) };
        EngineFile[] engineFiles = [new(0, file, 1, Rate, 4 * Rate)];
        var before = new RefTrack(1, false, TestAudio.Unity, [TestAudio.Region(0, 2 * Rate, 0)]);
        var after = before with {
            Regions = [
                Entry(0, 40_000, 0, 0, 0.9f, 0, 0f, 12_000, 0.5f),
                Entry(28_000, 2 * Rate, 0, 77_000, 1f, 12_000, -0.5f, 0, 0f)
            ]
        };
        using var controller = Load([before], engineFiles, Block);
        using var driver = new OfflineDriver(controller, Block);
        driver.PlayFrom(0);
        var first = driver.Render(Block * 100).Left;
        using (var patch = TestAudio.Segment([new(1, [], after.Regions)], 2)) {
            controller.LoadModel(TestAudio.Load(patch, 2, false, [], [], dir));
        }

        var rest = driver.Render(Rate / 2).Left;
        var switched = -1;
        for (var n = 0; n < rest.Length; n++) {
            var position = first.Length + n;
            var old = Mix([before], files, position).L;
            var edited = Mix([after], files, position).L;
            if (switched < 0 && rest[n] == old && rest[n] != edited) {
                continue;
            }

            if (switched < 0) {
                switched = n;
            }

            Assert.Equal(edited, rest[n]);
        }

        Assert.InRange(switched, 1, controller.Streamer.LeadFrames + Block);
        Assert.True(first.Length + switched < 28_000); // the whole crossfade plays from the new fills
        Assert.Equal(0, controller.Engine.Snapshot.Underruns);
    }
}
