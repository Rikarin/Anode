using Anode.Engine.Recording;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;
using System.Runtime.InteropServices;

namespace Anode.Engine.Tests;

/// <summary>
///     The audio callback must never allocate (a collection would stop it; docs/adr/0001-process-model.md), and neither
///     may the threads that run at a steady rate beside it: the disk thread's passes and the telemetry writes. Measured
///     per thread, so everything runs synchronously on the test's thread. Each figure is the least of three
///     measurements, as in Cathode's HostAllocationTests: the runtime now and then does one-off work on the measuring
///     thread (a tier-up, a type load) that shows in one measurement, while a real allocation shows in all three.
/// </summary>
public sealed unsafe class AllocationTests : IDisposable {
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

    [Fact]
    public void Callbacks_disk_passes_and_telemetry_allocate_nothing() {
        var mono = TestAudio.Write24(dir, "mono.wav", 1, 20 * Rate, 1);
        var stereo = TestAudio.Write24(dir, "stereo.wav", 2, 20 * Rate, 2);
        EngineFile[] files = [new(0, mono, 1, Rate, 20 * Rate), new(1, stereo, 2, Rate, 20 * Rate)];
        var models = new List<TrackModel>();
        var tracks = new List<EngineTrack>();
        for (var t = 0; t < 8; t++) {
            var regions = Enumerable.Range(0, 40)
                .Select(r => TestAudio.Region(r * 12_000L, 11_000, t % 2, r * 3_000L, 0.7f, 200, 300))
                .ToArray();
            models.Add(new(t, [], regions));
            tracks.Add(TestAudio.Track(t, t % 2 == 1, new(0.8f, t / 8f - 0.5f, t == 5, t == 3), t % 2, t == 2, t < 4));
        }

        models.Add(new(TrackEntry.MetronomeTrack, Enumerable.Range(0, 400).Select(i => TestAudio.Click(i * 6_000L, i % 4 == 0)).ToArray(), []));
        using var segment = TestAudio.Segment(models, 1);
        using var controller = new EngineController(new(Rate, 64));
        controller.LoadModel(TestAudio.Load(segment, 1, true, tracks, files, dir));
        controller.Engine.SetMetronome(true, 0.5f);
        using var driver = new OfflineDriver(controller, 64);
        using var take = new RecordTake(2, Path.Combine(dir, "2-take.wav"), 1, Rate, 0);
        controller.Engine.Root.Tracks[2].Take = take;
        var region = NativeMemory.AlignedAlloc((nuint)TelemetryLayout.Size, 64);
        using var telemetry = new TelemetryWriter(controller.Engine, (byte*)region, () => 0);
        var scratch = new float[8192];
        try {
            driver.Send(new(EngineCommandKind.SetCycle, 30_000, 150_000, true));
            driver.PlayFrom(20_000);
            driver.Send(new(EngineCommandKind.Record));
            var callbacks = Least(() => {
                    for (var i = 0; i < 100; i++) {
                        driver.Process(false);
                    }
                }
            );
            var pumps = 0L;
            var callbackBytes = callbacks;
            var telemetryBytes = 0L;
            for (var round = 0; round < 20; round++) {
                // Cross the cycle end a few times, and let the playlists' regions come and go.
                pumps = Math.Max(pumps, Least(driver.Pump));
                take.Drain(scratch);
                callbackBytes = Math.Max(
                    callbackBytes,
                    Least(() => {
                            for (var i = 0; i < 100; i++) {
                                driver.Process(false);
                            }
                        }
                    )
                );
                telemetryBytes = Math.Max(telemetryBytes, Least(telemetry.Write));
            }

            Assert.Equal(0, callbackBytes);
            Assert.Equal(0, pumps);
            Assert.Equal(0, telemetryBytes);
            Assert.True(take.Pushed > 0);
            Assert.Equal(0, take.Dropped);
            Assert.Equal(0, controller.Engine.Snapshot.Underruns);
        } finally {
            controller.Engine.Root.Tracks[2].Take = null;
            NativeMemory.AlignedFree(region);
        }
    }
}
