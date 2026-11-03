using Anode.Dsp.Effects.Dynamics;
using Anode.Dsp.Effects.Eq;
using Anode.Dsp.Effects.Modulation;
using Anode.Dsp.Effects.Space;
using Anode.Dsp.Instruments.Synth;
using Anode.Ipc;
using Anode.Ipc.Segments;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Anode.Engine.Tests;

/// <summary>
///     Phase 10's promise to stereo projects (docs/11-phase-10.md, workstream A): a representative stereo project renders
///     bit for bit as it did before the engine became multichannel. The project (mono and stereo audio, an instrument,
///     buses with aux tracks, pre- and post-fader sends, a sidechain, built-in effects with and without latency, delay
///     compensation, volume, pan and insert automation, a device output pair and a master insert) is rendered three
///     ways: the device's outputs block by block, an offline master bounce and an offline stems bounce. Each is hashed
///     (SHA-256 of the samples) and compared with the hashes the same code produced at commit 52f2b5d (the Phase 10
///     contracts, before the multichannel graph), recorded per platform: floating-point library calls may round
///     differently on other platforms, where the test reports the hashes instead.
/// </summary>
public sealed class StereoReferenceTests : IDisposable {
    const int Rate = 48_000;
    const int Seconds = 3;
    const int Block = 128;

    /// <summary>The hashes at 52f2b5d: device outputs, master bounce, stems bounce (by RID).</summary>
    static readonly Dictionary<string, string[]> Recorded = new(StringComparer.Ordinal) {
        ["osx-arm64"] = [
            "701AF61812714F6655DB962349ADCAF98594E9E367AED4DB8B33F65521246AB1",
            "096E5B458726B271FDA0342E552D4CC5C89EC03ED1635AE778A0BD4490C15FD6",
            "E606FC96E4A8C444595EE043B924E7BF522AD4A26CECEF1C697AB028F5F7E145"
        ]
    };

    readonly string dir = TestAudio.TempDirectory();
    readonly string mono;
    readonly string stereo;

    public StereoReferenceTests() {
        mono = TestAudio.Write24(dir, "mono.wav", 1, Seconds * Rate, 1);
        stereo = TestAudio.Write24(dir, "stereo.wav", 2, Seconds * Rate, 10);
    }

    public void Dispose() => Directory.Delete(dir, true);

    static PlaybackEvent Automation(long position, int target, float value, int track, bool step = false) =>
        new() {
            SamplePos = position,
            Track = track,
            Kind = PlaybackEventKind.Automation,
            Data1 = step ? (byte)1 : (byte)0,
            Value = value,
            Target = target
        };

    static PlaybackEvent[] Sorted(params PlaybackEvent[] events) => [..events.OrderBy(static e => e.SamplePos)];

    static EngineInsert Insert(string id, string plugin, Dictionary<string, double>? parameters = null) =>
        new(id, plugin, false, parameters ?? []);

    EngineController Build() {
        EngineFile[] files = [new(0, mono, 1, Rate, Seconds * Rate), new(1, stereo, 2, Rate, Seconds * Rate)];
        EngineAutomationTarget[] targets = [
            new(0, 1, "volume"),
            new(1, 1, "pan"),
            new(2, 3, "insert", "dly", "mix"),
            new(3, 2, "send", "post"),
            new(4, -1, "volume")
        ];
        var notes = MidiFixtures.Notes(
            [..Enumerable.Range(0, 40).Select(static n => (n * 3_000L, 2_400L, 48 + n * 5 % 24))]
        );
        TrackModel[] models = [
            new(
                1,
                Sorted(
                    Automation(0, 0, 0.9f, 1),
                    Automation(30_000, 0, 0.3f, 1),
                    Automation(60_000, 0, 1f, 1, true),
                    Automation(0, 1, -0.5f, 1),
                    Automation(90_000, 1, 0.7f, 1)
                ),
                [TestAudio.Region(0, Seconds * Rate, 0)]
            ),
            new(
                2,
                [Automation(0, 3, 0.8f, 2), Automation(50_000, 3, 0.1f, 2)],
                [TestAudio.Region(0, Seconds * Rate, 1)]
            ),
            new(3, Sorted([..notes, Automation(0, 2, 20f, 3), Automation(70_000, 2, 80f, 3)]), []),
            new(4, [], [TestAudio.Region(1_000, Seconds * Rate - 1_000, 0, 500, 0.7f, 300, 400)]),
            new(5, [], [TestAudio.Region(0, Seconds * Rate, 1, 0, 0.9f)]),
            new(-1, [Automation(0, 4, 0.9f, -1), Automation(100_000, 4, 0.6f, -1)], [])
        ];
        EngineTrack[] tracks = [
            TestAudio.Track(
                1,
                false,
                new(
                    0.8f,
                    -0.3f,
                    false,
                    false,
                    [Insert("eq", ChannelEq.Id, new() { ["peak2Gain"] = 6, ["lowShelfGain"] = -3 })],
                    [new("pre", 1, 0.5f, 0.2f, true)]
                )
            ),
            TestAudio.Track(
                2,
                true,
                new(
                    0.7f,
                    0.25f,
                    false,
                    false,
                    [
                        Insert(
                            "comp",
                            Compressor.Id,
                            new() { ["threshold"] = -30, ["ratio"] = 6, ["sidechain"] = 1 }
                        ) with {
                            SidechainTrack = 1
                        }
                    ],
                    [new("post", 2, 0.6f, -0.4f, false)]
                )
            ),
            MidiFixtures.InstrumentTrack(3, MidiFixtures.Instrument(AnodeSynth.Id, 0.4)) with {
                Strip = new(
                    0.9f,
                    0.1f,
                    false,
                    false,
                    [Insert("dly", Anode.Dsp.Effects.Delay.StereoDelay.Id, new() { ["sync"] = 0 })],
                    Output: 1
                )
            },
            TestAudio.Track(4, false, new(1f, 0.5f, false, false, [Insert("lin", LinearPhaseEq.Id)])),
            TestAudio.Track(
                5,
                true,
                new(0.6f, 0f, false, false, [Insert("lim", AdaptiveLimiter.Id)], Output: -2)
            ),
            TestAudio.Track(10, true, new(0.9f, 0f, false, false, [Insert("verb", Reverb.Id)])) with { InputBus = 1 },
            TestAudio.Track(11, true, new(1f, -0.2f, false, false, [Insert("cho", Chorus.Id)])) with { InputBus = 2 }
        ];
        var master = new EngineStrip(0.9f, 0f, false, false, [Insert("mlim", Limiter.Id)]);
        var controller = new EngineController(new(Rate, Block));
        using var segment = TestAudio.Segment(models, 1);
        controller.LoadModel(
            TestAudio.Load(segment, 1, true, tracks, files, dir, master) with { AutomationTargets = targets }
        );
        controller.SetMixer("all", false, 0);
        return controller;
    }

    static string Hash(IEnumerable<float[]> channels) {
        using var sha = SHA256.Create();
        foreach (var channel in channels) {
            var bytes = MemoryMarshal.AsBytes(channel.AsSpan()).ToArray();
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    static string HashFiles(IEnumerable<string> paths) {
        using var sha = SHA256.Create();
        foreach (var path in paths) {
            var bytes = File.ReadAllBytes(path);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    string[] RenderAll() {
        string device;
        using (var controller = Build()) {
            using var driver = new OfflineDriver(controller, Block, 2, 4);
            driver.PlayFrom(0);
            var frames = 2 * Rate / Block * Block;
            var outputs = Enumerable.Range(0, 4).Select(_ => new float[frames]).ToArray();
            for (var at = 0; at < frames; at += Block) {
                driver.Process();
                for (var c = 0; c < 4; c++) {
                    driver.Output(c).CopyTo(outputs[c].AsSpan(at));
                }
            }

            Assert.Equal(0, controller.Engine.Snapshot.Underruns);
            Assert.Contains(outputs[0], static v => v != 0);
            Assert.Contains(outputs[2], static v => v != 0);
            device = Hash(outputs);
        }

        string master;
        using (var controller = Build()) {
            var path = Path.Combine(dir, "master.wav");
            var written = controller.RenderAsync(new(1, 0, 2 * Rate, path)).GetAwaiter().GetResult();
            master = HashFiles(written);
        }

        string stems;
        using (var controller = Build()) {
            var written = controller.RenderAsync(new(2, 0, 2 * Rate, Path.Combine(dir, "stems"), [1, 2, 3, 10, 5]))
                .GetAwaiter()
                .GetResult();
            stems = HashFiles(written.Order(StringComparer.Ordinal));
        }

        return [device, master, stems];
    }

    [Fact]
    public void A_representative_stereo_project_renders_as_it_did_before_the_multichannel_graph() {
        var hashes = RenderAll();
        var rid = RuntimeInformation.RuntimeIdentifier;
        var message = $"{rid}: device {hashes[0]}, master {hashes[1]}, stems {hashes[2]}";
        TestContext.Current.SendDiagnosticMessage(message);
        Console.WriteLine(message);
        if (!Recorded.TryGetValue(rid, out var expected) || expected.Length == 0) {
            Assert.Skip($"No reference recorded for {rid}: {message}");
        }

        Assert.Equal(expected[0], hashes[0]);
        Assert.Equal(expected[1], hashes[1]);
        Assert.Equal(expected[2], hashes[2]);
    }

    [Fact]
    public void Rendering_the_reference_project_twice_gives_the_same_bits() => Assert.Equal(RenderAll(), RenderAll());
}
