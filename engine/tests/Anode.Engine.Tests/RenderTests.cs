using Anode.Ipc;
using Anode.Ipc.Segments;
using System.Buffers.Binary;

namespace Anode.Engine.Tests;

/// <summary>
///     Offline renders (docs/05-phase-4.md, "Rendering"): the master, stems and a freeze, each 32-bit float WAV with
///     its path's latency removed, compared bit for bit with a reference; a frozen track playing its freeze file; and
///     progress and cancellation.
/// </summary>
public sealed class RenderTests : IDisposable {
    const int Rate = 48_000;
    const int Seconds = 3;
    readonly string dir = TestAudio.TempDirectory();
    readonly string mono;

    public RenderTests() {
        mono = TestAudio.Write24(dir, "mono.wav", 1, Seconds * Rate, 1);
    }

    public void Dispose() => Directory.Delete(dir, true);

    static float M(long n) => n is >= 0 and < Seconds * Rate ? TestAudio.Sample(n, 1) : 0f;

    static float N(long n) => n is >= 0 and < Seconds * Rate ? TestAudio.Sample(n + 5_000, 1) : 0f;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Track 1: the file through a 100-frame latent insert, fader 0.3; track 2: the file 5,000 frames on, ×0.5.</summary>
    EngineController Controller(string? freezeFile = null) {
        var controller = new EngineController(new(Rate, 64), TestEffects.Registry());
        TrackModel[] models = [
            new(1, [], [TestAudio.Region(0, Seconds * Rate, 0)]),
            new(2, [], [TestAudio.Region(0, Seconds * Rate - 5_000, 0, 5_000)])
        ];
        EngineTrack[] tracks = [
            new(
                1,
                -1,
                false,
                false,
                false,
                new(0.3f, 0f, false, false, [TestEffects.Insert("d", TestEffects.Delay, new Dictionary<string, double> { ["frames"] = 100 })]),
                FreezeFile: freezeFile
            ),
            new(2, -1, false, false, false, new(1f, 0f, false, false, [TestEffects.Insert("g", TestEffects.Gain, new Dictionary<string, double> { ["gain"] = 0.5 })]))
        ];
        using var segment = TestAudio.Segment(models, 1);
        controller.LoadModel(TestAudio.Load(segment, 1, true, tracks, [new(0, mono, 1, Rate, Seconds * Rate)], dir));
        return controller;
    }

    /// <summary>A 32-bit float WAV's interleaved samples.</summary>
    static float[] ReadFloat(string path, out int channels) {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        var at = 12;
        channels = 0;
        while (at + 8 <= bytes.Length) {
            var id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            if (id == "fmt ") {
                Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 8)));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 10));
                Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 22)));
            } else if (id == "data") {
                var samples = new float[size / 4];
                for (var i = 0; i < samples.Length; i++) {
                    samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 8 + 4 * i));
                }

                return samples;
            }

            at += 8 + size + (size & 1);
        }

        throw new InvalidDataException("no data chunk");
    }

    static void Same(float expected, float actual, int frame, string what) {
        if (expected != actual) {
            Assert.Fail($"{what}, frame {frame}: expected {expected}, got {actual}");
        }
    }

    [Fact]
    public async Task The_master_renders_offline_aligned_to_the_project_with_progress() {
        using var controller = Controller();
        const long start = 1_000, end = 21_000;
        var progress = new List<double>();
        var path = Path.Combine(dir, "Bounces", "mix.wav");
        var files = await controller.RenderAsync(new(1, start, end, path), progress.Add, Token);
        Assert.Equal([path], files);
        var data = ReadFloat(path, out var channels);
        Assert.Equal(2, channels);
        Assert.Equal(2 * (end - start), data.Length);
        for (var k = 0; k < end - start; k++) {
            var n = start + k;
            var expected = M(n) * 0.3f + N(n) * 0.5f; // track 1's latency is compensated and removed
            Same(expected, data[2 * k], (int)n, "left");
            Same(expected, data[2 * k + 1], (int)n, "right");
        }

        Assert.Equal(1, progress[^1]);
        Assert.False(controller.IsRendering);
        Assert.False(controller.Engine.IsOffline);
    }

    [Fact]
    public async Task Stems_are_each_tracks_post_fader_output_and_a_freeze_its_source_and_inserts_before_the_fader() {
        using var controller = Controller();
        const long start = 0, end = 12_000;
        var stems = Path.Combine(dir, "stems");
        var files = await controller.RenderAsync(new(2, start, end, stems, [1, 2]), null, Token);
        Assert.Equal([Path.Combine(stems, "1.wav"), Path.Combine(stems, "2.wav")], files);
        var one = ReadFloat(files[0], out _);
        var two = ReadFloat(files[1], out _);
        for (var n = 0; n < end; n++) {
            Same(M(n) * 0.3f, one[2 * n], n, "stem 1");
            Same(N(n) * 0.5f, two[2 * n + 1], n, "stem 2");
        }

        var freezeDir = Path.Combine(dir, "freeze");
        var frozen = await controller.RenderAsync(new(3, 0, Seconds * Rate, freezeDir, [1], true), null, Token);
        var freeze = ReadFloat(frozen[0], out _);
        Assert.Equal(2 * Seconds * Rate, freeze.Length);
        for (var n = 0; n < Seconds * Rate; n++) {
            Same(M(n), freeze[2 * n], n, "freeze"); // pre-fader, the insert's latency removed
        }

        // The frozen track plays its file into its fader, without its inserts (and their latency).
        using var thawed = Controller(frozen[0]);
        using var driver = new OfflineDriver(thawed, 64);
        driver.PlayFrom(0);
        var (left, _) = driver.Render(9_600);
        for (var n = 0; n < 9_600; n++) {
            Same(M(n) * 0.3f + N(n) * 0.5f, left[n], n, "frozen playback");
        }
    }

    [Fact]
    public async Task A_cancelled_render_deletes_its_file_and_the_engine_plays_on() {
        using var controller = Controller();
        using var cancel = new CancellationTokenSource();
        var path = Path.Combine(dir, "cancelled.wav");
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.RenderAsync(
                new(4, 0, 600L * Rate, path),
                _ => {
                    if (++calls == 2) {
                        cancel.Cancel();
                    }
                },
                cancel.Token
            )
        );
        Assert.False(File.Exists(path));
        Assert.False(controller.Engine.IsOffline);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.RenderAsync(new(5, 0, 100, dir, [99]), null, Token)
        );
    }
}
