using Anode.Engine.Plugins;
using Anode.Ipc;
using Anode.Plugins;
using System.Buffers.Binary;

namespace Anode.Engine.Tests;

/// <summary>
///     Workstream E's sandboxed plugins through the engine (<see cref="SandboxPluginFactory" />): the in-repo test VST3
///     (AnodeTest Gain, built by native/build.sh) as an insert on two tracks, rendered offline in grouped sandboxes, is
///     bit-exact against the same session with the plugin in the engine's own process.
/// </summary>
public sealed class HostedPluginTests : IDisposable {
    const int Rate = 48_000;
    const string GainId = "vst3:414E4F44455447310000000100000001";
    readonly string dir = TestAudio.TempDirectory();
    readonly string mono;

    public HostedPluginTests() {
        mono = TestAudio.Write24(dir, "mono.wav", 1, 2 * Rate, 1);
    }

    public void Dispose() => Directory.Delete(dir, true);

    static CancellationToken Token => TestContext.Current.CancellationToken;

    static string Artifacts {
        get {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at is not null && !Directory.Exists(Path.Combine(at.FullName, "artifacts", "native", "vst3"))) {
                at = at.Parent;
            }

            return at is null
                ? throw new InvalidOperationException(
                    "engine/artifacts/native/vst3 not found: run engine/native/build.sh"
                )
                : Path.Combine(at.FullName, "artifacts", "native");
        }
    }

    /// <summary>The anode-plugin build beside this test project's.</summary>
    static string Sandbox {
        get {
            var dll = Path.Combine(
                AppContext.BaseDirectory.Replace(
                    Path.Combine("tests", "Anode.Engine.Tests"),
                    Path.Combine("src", "Anode.PluginSandbox"),
                    StringComparison.Ordinal
                ),
                "anode-plugin.dll"
            );
            Assert.True(File.Exists(dll), $"anode-plugin not built at {dll}");
            return dll;
        }
    }

    EngineController Controller(SandboxMode mode, string data, out SandboxPluginFactory factory) {
        var host = new PluginHost(
            new() {
                DataDirectory = data,
                SandboxExecutable = Sandbox,
                Mode = mode,
                Vst3Folders = [Path.Combine(Artifacts, "vst3")],
                ClapFolders = [],
                ScanAudioUnits = false
            }
        );
        host.Catalog.Scan(false, Token);
        factory = new(host);
        var registry = TestEffects.Registry();
        registry.Factory = factory;
        var controller = new EngineController(new(Rate, 64), registry);
        EngineInsert hosted = new("h", GainId, false, new Dictionary<string, double>());
        using var segment = TestAudio.Segment(
            [
                new(1, [], [TestAudio.Region(0, 2 * Rate, 0)]),
                new(2, [], [TestAudio.Region(0, Rate, 0, Rate)])
            ],
            1
        );
        controller.LoadModel(
            TestAudio.Load(
                segment,
                1,
                true,
                [
                    TestAudio.Track(1, false, new(0.8f, 0f, false, false, [hosted])),
                    TestAudio.Track(
                        2,
                        false,
                        new(
                            0.5f,
                            0.3f,
                            false,
                            false,
                            [
                                hosted,
                                TestEffects.Insert(
                                    "g",
                                    TestEffects.Gain,
                                    new Dictionary<string, double> { ["gain"] = 0.5 }
                                )
                            ]
                        )
                    )
                ],
                [new(0, mono, 1, Rate, 2 * Rate)],
                dir
            )
        );
        return controller;
    }

    static float[] Read(string path) {
        var bytes = File.ReadAllBytes(path);
        var at = 12;
        while (at + 8 <= bytes.Length) {
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            if (bytes.AsSpan(at, 4).SequenceEqual("data"u8)) {
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

    async Task<float[]> RenderAsync(SandboxMode mode) {
        var data = Path.Combine(dir, $"data-{mode}");
        var controller = Controller(mode, data, out var factory);
        try {
            Assert.Contains(controller.Plugins.Catalog(), static p => p is { Id: GainId, Format: "vst3" });
            var slot = controller.Engine.Root.Tracks[0].Channel.Config.Inserts[0].Slot;
            var hosted = Assert.IsAssignableFrom<IHostedPlugin>(slot.Effect);
            Assert.False(hosted.IsFailed);
            var path = Path.Combine(dir, $"{mode}.wav");
            await controller.RenderAsync(new(1, 0, Rate, path), null, Token);
            if (mode != SandboxMode.InHost) {
                Assert.True(hosted.Stats.Completed > 0);
                Assert.Equal(0, hosted.Stats.Misses);
            }

            return Read(path);
        } finally {
            controller.Dispose(); // the instances before the host that runs them
            factory.Dispose();
        }
    }

    [Fact]
    public async Task A_sandboxed_VST3_insert_renders_bit_exactly_against_the_in_process_one() {
        var sandboxed = await RenderAsync(SandboxMode.Grouped);
        var local = await RenderAsync(SandboxMode.InHost);
        Assert.Equal(2 * Rate, sandboxed.Length);
        Assert.Equal(local, sandboxed);
        Assert.Contains(sandboxed, static v => v != 0);
    }
}
