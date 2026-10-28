using Anode.Engine.Recording;
using Anode.Engine.Streaming;
using Anode.Ipc;
using Anode.Ipc.Segments;
using System.Diagnostics;

namespace Anode.Engine.Tests;

/// <summary>
///     Warped playback on the disk thread (docs/06-phase-5.md, workstream A): a warp map's onsets landing where it says,
///     renders independent of where a fill starts, cycles over warped regions, crossfades between warped and unwarped
///     regions with the unwarped audio bit-exact, and the disk thread's cost with 64 warped stereo tracks. The tests
///     hold for every renderer that keeps <c>IFlexRenderer</c>'s rules, not only the reference one: onsets within 3 ms,
///     renders compared within 1e-4.
/// </summary>
public sealed class FlexPlaybackTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    const long RegionStart = 10_000;
    const float Threshold = 0.2f;
    const int Tolerance = 3 * Rate / 1000; // 3 ms

    /// <summary>The file frames of the bursts in <see cref="WriteBursts" />'s file.</summary>
    static readonly long[] Bursts = [4_800, 14_400, 24_000, 33_600, 43_200, 52_800, 62_400];

    /// <summary>Where the warp map puts each burst, as an offset from the region's start: rates from 0.69 to 1.37.</summary>
    static readonly long[] Targets = [6_000, 18_000, 25_000, 34_600, 48_600, 56_600, 67_600];

    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

    static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     A file of 80,000 frames, silent but for a 10 ms decaying 1 kHz burst at each of <see cref="Bursts" /> (on every
    ///     channel).
    /// </summary>
    public static string WriteBursts(string directory, string name, int channels) {
        var path = Path.Combine(directory, name);
        var samples = new float[80_000 * channels];
        foreach (var at in Bursts) {
            for (var n = 0; n < 480; n++) {
                var s = (float)(0.8 * Math.Sin(2 * Math.PI * 1_000 * n / Rate) * Math.Exp(-n / 160.0));
                for (var c = 0; c < channels; c++) {
                    samples[(at + n) * channels + c] = s;
                }
            }
        }

        using var writer = new WavWriter(path, channels, Rate);
        writer.Write(samples);
        return path;
    }

    /// <summary>The warp map: file frame 0 at offset 0, each burst at its target, and 9,600 frames at rate 1 after the last.</summary>
    public static WarpPoint[] Map() {
        List<WarpPoint> points = [new() { Offset = 0, FileFrame = 0 }];
        for (var k = 0; k < Bursts.Length; k++) {
            points.Add(new() { Offset = Targets[k], FileFrame = Bursts[k] });
        }

        points.Add(new() { Offset = Targets[^1] + 9_600, FileFrame = Bursts[^1] + 9_600 });
        return [..points];
    }

    public static long MapLength => Targets[^1] + 9_600;

    /// <summary>Where <see cref="Map" /> puts the bursts, as offsets from the region's start.</summary>
    public static IReadOnlyList<long> MapTargets => Targets;

    /// <summary>A warped entry over <see cref="Map" />'s points from index <paramref name="warpStart" /> of its track's.</summary>
    public static AudioPlaylistEntry Warped(
        long start,
        int file,
        FlexAlgorithm algorithm,
        int warpStart = 0,
        float gain = 1f,
        int fadeIn = 0,
        int fadeOut = 0
    ) =>
        TestAudio.Region(start, MapLength, file, 0, gain, fadeIn, fadeOut) with {
            WarpStart = warpStart, WarpCount = Bursts.Length + 2, Algorithm = algorithm
        };

    /// <summary>The project positions where <paramref name="signal" /> rises past the threshold after 50 ms below it.</summary>
    public static List<long> Onsets(ReadOnlySpan<float> signal, long origin = 0) {
        var onsets = new List<long>();
        var lastAbove = long.MinValue / 2;
        for (var i = 0; i < signal.Length; i++) {
            if (Math.Abs(signal[i]) < Threshold) {
                continue;
            }

            if (i - lastAbove > Rate / 20) {
                onsets.Add(origin + i);
            }

            lastAbove = i;
        }

        return onsets;
    }

    /// <summary>Loads the tracks into a new controller.</summary>
    EngineController Load(TrackModel[] models, EngineTrack[] tracks, EngineFile[] files) {
        using var segment = TestAudio.Segment(models, 1);
        var controller = new EngineController(new(Rate, Block));
        controller.LoadModel(TestAudio.Load(segment, 1, true, tracks, files, dir));
        return controller;
    }

    /// <summary>Plays <paramref name="frames" /> frames from <paramref name="from" /> (with a cycle, if any).</summary>
    (float[] Left, float[] Right) Play(
        TrackModel[] models,
        EngineTrack[] tracks,
        EngineFile[] files,
        long from,
        int frames,
        Cycle cycle = default
    ) {
        using var controller = Load(models, tracks, files);
        using var driver = new OfflineDriver(controller, Block);
        if (cycle.Enabled) {
            driver.Send(new(EngineCommandKind.SetCycle, cycle.Start, cycle.End, true));
        }

        driver.PlayFrom(from);
        var result = driver.Render(frames);
        Assert.Equal(0, controller.Engine.Snapshot.Underruns);
        return result;
    }

    (TrackModel[] Models, EngineTrack[] Tracks, EngineFile[] Files) BurstTrack(
        FlexAlgorithm algorithm,
        bool stereo = true
    ) {
        var path = WriteBursts(dir, $"bursts-{algorithm}-{stereo}.wav", stereo ? 2 : 1);
        return (
            [new(1, [], [Warped(RegionStart, 0, algorithm)], Map())],
            [TestAudio.Track(1, stereo)],
            [new(0, path, stereo ? 2 : 1, Rate, 80_000)]
        );
    }

    static void Close(
        ReadOnlySpan<float> expected,
        ReadOnlySpan<float> actual,
        long origin,
        string what,
        float within = 1e-4f
    ) {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++) {
            if (!(Math.Abs(expected[i] - actual[i]) <= within)) {
                Assert.Fail($"{what}, position {origin + i}: expected {expected[i]}, got {actual[i]}");
            }
        }
    }

    [Theory]
    [InlineData(FlexAlgorithm.Polyphonic)]
    [InlineData(FlexAlgorithm.Monophonic)]
    [InlineData(FlexAlgorithm.Slicing)]
    [InlineData(FlexAlgorithm.Rhythmic)]
    [InlineData(FlexAlgorithm.Speed)]
    [InlineData(FlexAlgorithm.Pitch)]
    public void A_warp_maps_onsets_land_where_it_puts_them(FlexAlgorithm algorithm) {
        var (models, tracks, files) = BurstTrack(algorithm);
        var (left, right) = Play(models, tracks, files, 0, (int)(RegionStart + MapLength + Rate / 10));
        var expected = Targets.Select(static t => RegionStart + t).ToArray();
        foreach (var channel in new[] { left, right }) {
            var onsets = Onsets(channel);
            Assert.Equal(expected.Length, onsets.Count);
            for (var k = 0; k < expected.Length; k++) {
                Assert.InRange(onsets[k], expected[k] - Tolerance, expected[k] + Tolerance);
            }
        }
    }

    [Fact]
    public void A_mono_file_plays_on_both_sides_of_a_stereo_track_and_a_stereo_file_is_averaged_on_a_mono_one() {
        var stereoPath = Path.Combine(dir, "opposed.wav");
        using (var writer = new WavWriter(stereoPath, 2, Rate)) {
            var mono = WriteBursts(dir, "for-opposed.wav", 1);
            using var file = new StreamFile(0, mono);
            var frames = new float[80_000];
            file.Read(0, 80_000, new byte[StreamFile.ScratchBytes(80_000, 3)], frames);
            var opposed = new float[160_000];
            for (var f = 0; f < 80_000; f++) {
                opposed[2 * f] = frames[f];
                opposed[2 * f + 1] = -frames[f]; // the average is silence
            }

            writer.Write(opposed);
        }

        var monoPath = WriteBursts(dir, "mono.wav", 1);
        TrackModel[] models = [
            new(1, [], [Warped(RegionStart, 0, FlexAlgorithm.Polyphonic)], Map()),
            new(2, [], [Warped(RegionStart, 1, FlexAlgorithm.Polyphonic)], Map())
        ];
        EngineFile[] files = [new(0, stereoPath, 2, Rate, 80_000), new(1, monoPath, 1, Rate, 80_000)];
        var frames2 = (int)(RegionStart + MapLength);
        var (left, _) = Play(
            models,
            [TestAudio.Track(1), TestAudio.Track(2, true, new(1f, 0f, true, false))],
            files,
            0,
            frames2
        );
        Assert.All(left, static v => Assert.InRange(v, -1e-4f, 1e-4f)); // track 1, mono: L + R = 0; track 2 muted
        var (soloLeft, soloRight) = Play(models[1..], [TestAudio.Track(2, true)], files[1..], 0, frames2);
        Close(soloLeft, soloRight, 0, "mono file, stereo track", 1e-5f);
        Assert.Equal(Bursts.Length, Onsets(soloLeft).Count);
    }

    [Theory]
    [InlineData(FlexAlgorithm.Polyphonic)]
    [InlineData(FlexAlgorithm.Slicing)]
    [InlineData(FlexAlgorithm.Speed)]
    public void A_render_does_not_depend_on_where_its_fill_started(FlexAlgorithm algorithm) {
        var (models, tracks, files) = BurstTrack(algorithm);
        const int frames = 24_000;
        var continuous = Play(models, tracks, files, 0, (int)(RegionStart + MapLength)).Left;
        foreach (var from in new long[] { RegionStart - 100, 23_456, 41_001, 70_000 }) {
            var located = Play(models, tracks, files, from, frames).Left;
            var length = (int)Math.Min(frames, continuous.Length - from);
            Close(continuous.AsSpan((int)from, length), located.AsSpan(0, length), from, $"played from {from}");
        }
    }

    [Theory]
    [InlineData(FlexAlgorithm.Polyphonic)]
    [InlineData(FlexAlgorithm.Slicing)]
    public void A_cycle_over_a_warped_region_repeats_what_it_plays_through(FlexAlgorithm algorithm) {
        var (models, tracks, files) = BurstTrack(algorithm);
        var continuous = Play(models, tracks, files, 0, (int)(RegionStart + MapLength)).Left;
        var cycle = new Cycle(true, 20_000, 50_000);
        const long from = 15_000;
        var frames = 100_032; // three laps and more
        var looped = Play(models, tracks, files, from, frames, cycle).Left;
        var position = from;
        var laps = 0;
        for (var n = 0; n < frames; n++) {
            if (!(Math.Abs(continuous[position] - looped[n]) <= 1e-4f)) {
                Assert.Fail(
                    $"frame {n} (position {position}, lap {laps}): expected {continuous[position]}, got {looped[n]}"
                );
            }

            var next = cycle.Advance(position, 1);
            laps += next < position ? 1 : 0;
            position = next;
        }

        Assert.True(laps >= 3);
        Assert.Equal(3, Onsets(looped.AsSpan(5_000, 30_000)).Count); // bursts at 28,000, 35,000 and 44,600 each lap
    }

    [Fact]
    public void Warped_and_unwarped_regions_crossfade_and_the_unwarped_audio_is_bit_exact() {
        var noise = TestAudio.Write24(dir, "noise.wav", 2, 4 * Rate, 5);
        var bursts = WriteBursts(dir, "bursts.wav", 2);
        EngineFile[] files = [new(0, noise, 2, Rate, 4 * Rate), new(1, bursts, 2, Rate, 80_000)];
        var a = TestAudio.Region(0, 40_000, 0, 1_000, 0.9f, 0, 6_000) with { FadeOutCurve = 0.5f };
        var b = Warped(34_000, 1, FlexAlgorithm.Polyphonic, 0, 1f, 6_000, 5_000) with { FadeInCurve = -0.5f };
        var bEnd = b.Start + b.Length;
        var c = TestAudio.Region(bEnd - 5_000, 30_000, 0, 50_000, 0.7f, 5_000);
        var strip = new EngineStrip(0.8f, 0.25f, false, false);
        EngineTrack[] tracks = [TestAudio.Track(1, true, strip)];
        var frames = (int)(bEnd + 30_000);

        float[] Solo(AudioPlaylistEntry entry) =>
            Play([new(1, [], [entry], entry.WarpCount > 0 ? Map() : null)], tracks, files, 0, frames).Left;

        using var controller = Load([new(1, [], [a, b, c], Map())], tracks, files);
        Assert.Equal([0, 1, 0], controller.Engine.Root.Lanes[0]!);
        Assert.NotNull(controller.Engine.Root.Tracks[0].Crossfade);
        using var driver = new OfflineDriver(controller, Block);
        driver.PlayFrom(0);
        var together = driver.Render(frames).Left;
        var (soloA, soloB, soloC) = (Solo(a), Solo(b), Solo(c));
        for (var n = 0; n < frames; n++) {
            if (n < b.Start) {
                Assert.Equal(soloA[n], together[n]); // the unwarped path, as Phase 4 plays it
            } else if (n >= bEnd) {
                Assert.Equal(soloC[n], together[n]);
            } else if (!(Math.Abs(soloA[n] + soloB[n] + soloC[n] - together[n]) <= 1e-5f)) {
                Assert.Fail($"position {n}: {soloA[n]} + {soloB[n]} + {soloC[n]} ≠ {together[n]}");
            }
        }

        // The warped region fades in from silence and out to silence.
        Assert.Equal(0f, soloB[(int)b.Start]);
        Assert.InRange(soloB[(int)bEnd - 1], -1e-3f, 1e-3f);
        Assert.Equal(Bursts.Length, Onsets(soloB).Count);
        Assert.Equal(0, controller.Engine.Snapshot.Underruns);
    }

    /// <summary>
    ///     The disk thread's cost (docs/06-phase-5.md, workstream A): 64 stereo tracks, each a Polyphonic region warped
    ///     at varying rates, filled from a locate (every stream's 2 s read-ahead at once) and then kept full while
    ///     playing. Reported, with the renderer in use; the steady state must keep up with real time.
    /// </summary>
    [Fact]
    public void The_disk_thread_cost_of_64_warped_stereo_tracks() {
        var noise = TestAudio.Write24(dir, "noise.wav", 2, 40 * Rate, 9);
        var models = new List<TrackModel>();
        var tracks = new List<EngineTrack>();
        for (var t = 0; t < 64; t++) {
            // 30 s of project time over 27 s of file: one warp point a second, rates between 0.8 and 1.0.
            var warp = new List<WarpPoint>();
            double frame = t * 1_000;
            for (var s = 0; s <= 30; s++) {
                warp.Add(new() { Offset = (long)s * Rate, FileFrame = frame });
                frame += Rate * (0.8 + 0.2 * ((s + t) % 5) / 4);
            }

            models.Add(
                new(
                    t,
                    [],
                    [
                        TestAudio.Region(0, 30L * Rate, 0) with {
                            WarpCount = warp.Count, Algorithm = FlexAlgorithm.Polyphonic
                        }
                    ],
                    [..warp]
                )
            );
            tracks.Add(TestAudio.Track(t, true));
        }

        using var controller = Load([..models], [..tracks], [new(0, noise, 2, Rate, 40 * Rate)]);
        using var driver = new OfflineDriver(controller, Block);
        driver.Send(new(EngineCommandKind.Locate, 5L * Rate));
        driver.Process(false);
        var clock = Stopwatch.StartNew();
        driver.Pump(); // every stream's read-ahead, from a locate: primes and renders 64 × 2 s
        var fill = clock.Elapsed;
        driver.Send(new(EngineCommandKind.Play));
        var steady = TimeSpan.Zero;
        const int seconds = 4;
        for (var i = 0; i < seconds * Rate / Block; i++) {
            if (i % 32 == 0) {
                clock.Restart();
                driver.Pump();
                steady += clock.Elapsed;
            }

            driver.Process(false);
        }

        var readAhead = controller.Engine.Root.Tracks[0].Stream.TargetFrames / (double)Rate;
        var message =
            $"disk thread, 64 warped stereo tracks (Polyphonic): filling every {readAhead:0.#} s read-ahead from a locate "
            + $"{fill.TotalMilliseconds:0} ms; playing, {steady.TotalMilliseconds / seconds:0} ms of disk time per second";
        TestContext.Current.SendDiagnosticMessage(message);
        Console.WriteLine(message);
        Assert.Equal(0, controller.Engine.Snapshot.Underruns);
        Assert.True(steady.TotalSeconds < seconds, message);
    }

    [Fact]
    public async Task Bounce_in_place_renders_a_warped_track_with_its_onsets_where_the_map_puts_them() {
        var (models, tracks, files) = BurstTrack(FlexAlgorithm.Polyphonic, false);
        using var controller = Load(models, tracks, files);
        var end = RegionStart + MapLength + Rate / 2; // and a tail
        var written = await controller.RenderAsync(
            new(1, 0, end, Path.Combine(dir, "bounce"), [1], true, IncludeVolumePan: true),
            null,
            Token
        );
        var data = RenderTests.ReadFloat(Assert.Single(written), out var channels);
        Assert.Equal(2, channels);
        Assert.Equal(2 * end, data.Length);
        var left = Enumerable.Range(0, (int)end).Select(f => data[2 * f]).ToArray();
        var onsets = Onsets(left);
        Assert.Equal(Targets.Length, onsets.Count);
        for (var k = 0; k < Targets.Length; k++) {
            Assert.InRange(onsets[k], RegionStart + Targets[k] - Tolerance, RegionStart + Targets[k] + Tolerance);
        }
    }
}
