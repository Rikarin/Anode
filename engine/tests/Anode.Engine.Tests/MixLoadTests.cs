using Anode.Ipc;
using Anode.Ipc.Segments;
using System.Diagnostics;

namespace Anode.Engine.Tests;

/// <summary>
///     The mixer under load (docs/05-phase-4.md, workstream A, "Constraints"): a session of 64 tracks, each with 4
///     inserts and 2 sends, 4 aux buses with inserts and master inserts, with automation, sidechains, a latent insert
///     (delay compensation) and a device output pair. The callback must not allocate, and its cost is measured.
/// </summary>
public sealed class MixLoadTests : IDisposable {
    const int Rate = 48_000;
    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

    static long Least(Action action) {
        action();
        var least = long.MaxValue;
        for (var i = 0; i < 3 && least > 0; i++) {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
    }

    /// <summary>The session; <paramref name="biquads" /> uses the biquad test effect for the 4 inserts (the benchmark).</summary>
    EngineController Session(bool biquads) {
        var file = TestAudio.Write24(dir, "mono.wav", 1, 30 * Rate, 1);
        var models = new List<TrackModel>();
        var tracks = new List<EngineTrack>();
        var targets = new List<EngineAutomationTarget>();
        _ = new List<PlaybackEvent>();
        for (var t = 0; t < 64; t++) {
            EngineInsert[] inserts = biquads
                ? [..Enumerable.Range(0, 4).Select(static i => TestEffects.Insert($"i{i}", TestEffects.Biquad))]
                : [
                    TestEffects.Insert("i0", TestEffects.Gain),
                    TestEffects.Insert("i1", TestEffects.Biquad),
                    TestEffects.Insert("i2", t == 5 ? TestEffects.Delay : TestEffects.Gain, bypassed: t == 7),
                    TestEffects.Insert(
                        "i3",
                        TestEffects.Sidechain,
                        sidechainTrack: t == 3 ? 40 : -1,
                        sidechainBus: t == 4 ? 2 : 0
                    )
                ];
            EngineSend[] sends =
                [new("a", 1 + t % 4, 0.3f, 0.1f, t % 2 == 0), new("b", 1 + (t + 1) % 4, 0.2f, -0.2f, false)];
            var output = t == 63 ? -2 : 0;
            tracks.Add(
                new(t, -1, t % 3 == 0, false, false, new(0.5f, (t % 5 - 2) / 4f, false, false, inserts, sends, output))
            );
            var mine = new List<PlaybackEvent>();
            if (t % 4 == 0) {
                var index = targets.Count;
                targets.Add(new(index, t, "volume"));
                targets.Add(new(index + 1, t, "send", "a"));
                targets.Add(new(index + 2, t, "insert", "i0", biquads ? "frequency" : "gain"));
                for (var p = 0; p < 30; p++) {
                    mine.Add(Point(p * Rate, t, index, p % 2 == 0 ? 0.3f : 0.8f));
                    mine.Add(Point(p * Rate, t, index + 1, p % 2 == 0 ? 0.1f : 0.5f));
                    mine.Add(
                        Point(p * Rate, t, index + 2, biquads ? 500 + p * 10 : p % 2 == 0 ? 0.5f : 1f, p % 3 == 0)
                    );
                }
            }

            models.Add(
                new(t, [..mine.OrderBy(static e => e.SamplePos)], [TestAudio.Region(0, 30L * Rate, 0, t * 10L)])
            );
        }

        for (var b = 1; b <= 4; b++) {
            tracks.Add(
                new(
                    100 + b,
                    -1,
                    true,
                    false,
                    false,
                    new(
                        0.7f,
                        0f,
                        false,
                        false,
                        [
                            TestEffects.Insert("r", biquads ? TestEffects.Biquad : TestEffects.Gain),
                            TestEffects.Insert("s", TestEffects.Biquad)
                        ]
                    ),
                    InputBus: b
                )
            );
        }

        var masterIndex = targets.Count;
        targets.Add(new(masterIndex, -1, "volume"));
        models.Add(new(-1, [Point(0, -1, masterIndex, 1f), Point(20L * Rate, -1, masterIndex, 0.5f)], []));
        using var segment = TestAudio.Segment(models, 1);
        var controller = new EngineController(new(Rate, 64), TestEffects.Registry());
        controller.LoadModel(
            TestAudio.Load(
                segment,
                1,
                true,
                tracks,
                [new(0, file, 1, Rate, 30L * Rate)],
                dir,
                new(
                    0.9f,
                    0f,
                    false,
                    false,
                    [
                        TestEffects.Insert("m1", TestEffects.Biquad),
                        TestEffects.Insert("m2", biquads ? TestEffects.Biquad : TestEffects.Gain)
                    ]
                )
            ) with {
                AutomationTargets = targets
            }
        );
        return controller;
    }

    static PlaybackEvent Point(long position, int track, int target, float value, bool step = false) =>
        new() {
            SamplePos = position,
            Track = track,
            Kind = PlaybackEventKind.Automation,
            Data1 = step ? (byte)1 : (byte)0,
            Value = value,
            Target = target
        };

    [Fact]
    public void The_mixer_with_inserts_sends_aux_sidechains_compensation_and_automation_allocates_nothing() {
        using var controller = Session(false);
        using var driver = new OfflineDriver(controller, 64, 2, 4);
        controller.SetMixer("all", false, 0);

        void Callbacks() {
            for (var i = 0; i < 100; i++) {
                driver.Process(false);
            }
        }

        driver.Send(new(EngineCommandKind.SetCycle, 2 * Rate, 4 * Rate, true));
        driver.PlayFrom(Rate);
        var bytes = Least(Callbacks);
        for (var round = 0; round < 20; round++) {
            driver.Pump();
            controller.SetInsertParameter(10, "i0", "gain", 0.5 + round % 3 * 0.1); // a held parameter
            controller.SetSendLevel(11, "b", 0.1f * (round % 4), 0f);
            controller.SetStrip(12, new(0.1f * (round % 5), 0f, round % 7 == 0, round % 9 == 0));
            if (round == 10) {
                controller.SetMixer("tracks", true, 1); // low-latency mode cuts nothing here: nothing is live
            }

            bytes = Math.Max(bytes, Least(Callbacks));
        }

        Assert.Equal(0, bytes);
        Assert.Equal(0, controller.Engine.Snapshot.Underruns);
    }

    [Fact]
    public void The_callback_cost_of_64_tracks_with_4_inserts_and_2_sends_4_aux_buses_and_master_inserts() {
        using var controller = Session(true);
        const int block = 64;
        using var driver = new OfflineDriver(controller, block);
        driver.PlayFrom(Rate);
        for (var i = 0; i < 200; i++) {
            driver.Process(); // warm up
        }

        var times = new List<double>();
        for (var i = 0; i < 3_000; i++) {
            driver.Pump();
            var start = Stopwatch.GetTimestamp();
            driver.Process(false);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
        }

        times.Sort();
        var period = 1e6 * block / Rate;
        var p50 = times[times.Count / 2];
        var p99 = times[times.Count * 99 / 100];
        TestContext.Current.SendDiagnosticMessage(
            "mixer callback, 64 tracks × (4 biquad inserts + 2 sends), 4 aux buses × 2 inserts, master × 2 inserts, "
            + $"{block} frames ({period:0} µs): p50 {p50:0.0} µs, p99 {p99:0.0} µs, max {times[^1]:0.0} µs"
        );
        Console.WriteLine(
            $"mixer callback p50 {p50:0.0} µs, p99 {p99:0.0} µs, max {times[^1]:0.0} µs of {period:0} µs"
        );
        Assert.True(p50 < period, $"p50 {p50:0.0} µs exceeds the {period:0} µs period");
        Assert.Equal(0, controller.Engine.Snapshot.Underruns);
    }
}
