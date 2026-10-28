using Anode.Dsp.Effects;
using Anode.Plugins.Native;
using static Anode.Plugins.Tests.PluginFixtures;

namespace Anode.Plugins.Tests;

/// <summary>
///     Round trips through a sandbox on a null clock: a real-time thread drives 64-frame cycles at 48 kHz, as the
///     engine's callback would, through one or more sandboxed effects. Reports the percentiles (set
///     <c>ANODE_PLUGIN_TEST_LOG</c> to keep them; <c>ANODE_PLUGIN_SANDBOX</c> runs a published NativeAOT sandbox).
/// </summary>
public sealed class TimingTests : IDisposable {
    const int Frames = 64;
    const long PeriodNs = Frames * 1_000_000_000L / 48000;
    readonly TempDirectory data = new();

    public void Dispose() => data.Dispose();

    [Theory]
    [InlineData(1, SandboxMode.Grouped, false)]
    [InlineData(8, SandboxMode.Grouped, false)]
    [InlineData(8, SandboxMode.Grouped, true)]
    [InlineData(4, SandboxMode.PerInstance, false)]
    [InlineData(4, SandboxMode.PerInstance, true)]
    public void Sandboxed_blocks_meet_the_deadline_on_a_real_time_clock(int instances, SandboxMode mode, bool split) {
        using var host = Host(data, mode);
        host.ConfigureDevice(48000, Frames, null);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var first = host.CreateEffect(GainId, new(0, "x"))!; // starts its sandbox
        first.Prepare(48000, 512);
        Log($"timing: sandbox start and first load {watch.Elapsed.TotalMilliseconds:0} ms");
        var effects = Enumerable.Range(1, instances - 1)
            .Select(i => host.CreateEffect(GainId, new(i, "x"))!)
            .Prepend(first)
            .ToList();
        try {
            foreach (var e in effects) {
                e.Prepare(48000, 512);
            }

            // Warm-up without a deadline, then 2 s of cycles.
            host.Processing = ProcessingMode.Offline;
            Run(host, effects, 200, false, split, out _);
            host.Processing = ProcessingMode.RealTime;
            var cycles = 1500;
            Run(host, effects, cycles, true, split, out var times);
            Array.Sort(times);
            var misses = effects.Sum(e => ((IHostedPlugin)e).Stats.Misses);

            string P(double q) =>
                (times[(int)(q * (times.Length - 1))] / 1000.0).ToString(
                    "0.0",
                    System.Globalization.CultureInfo.InvariantCulture
                );

            Log(
                $"timing {instances} x gain ({mode}, {host.Sandboxes.Count} sandbox(es){(split ? ", split-phase" : string.Empty)}), 64 frames: cycle p50 {P(0.5)} / "
                + $"p99 {P(0.99)} / max {P(1)} µs; misses {misses} / {cycles * instances}"
            );
            // Generous: this is a test thread on a busy machine, not CoreAudio's; S4 measured the protocol itself.
            Assert.True(misses <= cycles * instances / 50, $"{misses} misses");
        } finally {
            foreach (var e in effects) {
                e.Dispose();
            }
        }
    }

    static void Run(
        PluginHost host,
        List<IAudioEffect> effects,
        int cycles,
        bool realTime,
        bool split,
        out long[] times
    ) {
        var result = new long[cycles];
        var thread = new Thread(() => {
                if (realTime) {
                    Assert.Equal(0, IpcWake.RtPromote(PeriodNs, PeriodNs / 2, PeriodNs));
                }

                var l = new float[Frames];
                var r = new float[Frames];
                var next = IpcWake.Now() + PeriodNs;
                for (var c = 0; c < cycles; c++) {
                    if (realTime) {
                        IpcWake.SleepUntil(next);
                        next += PeriodNs;
                    }

                    var start = IpcWake.Now();
                    host.BeginCycle();
                    if (split) {
                        // Independent chains (one insert per track): hand every block over, then collect them.
                        foreach (var e in effects) {
                            ((ISplitProcessing)e).Begin(l, r, [], []);
                        }

                        foreach (var e in effects) {
                            ((ISplitProcessing)e).End(l, r);
                        }
                    } else {
                        foreach (var e in effects) {
                            e.Process(l, r, [], []);
                        }
                    }

                    result[c] = IpcWake.Now() - start;
                }
            }
        ) { Name = "null clock" };
        thread.Start();
        thread.Join();
        times = result;
    }
}
