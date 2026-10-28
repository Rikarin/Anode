using System.Collections.Concurrent;
using System.Diagnostics;
using static Anode.Plugins.Tests.PluginFixtures;

namespace Anode.Plugins.Tests;

/// <summary>Crash isolation, hangs and deadline misses: the caller keeps processing.</summary>
public sealed class IsolationTests : IDisposable {
    const int Frames = 64;
    readonly TempDirectory data = new();

    public void Dispose() => data.Dispose();

    [Fact]
    public void A_crashing_plugin_is_bypassed_reported_and_the_caller_keeps_processing() {
        using var host = Host(data, SandboxMode.PerInstance);
        host.Processing = ProcessingMode.Offline;
        var problems = new ConcurrentQueue<PluginProblem>();
        host.Problem += problems.Enqueue;
        using var crasher = host.CreateEffect(MisbehaveId, new(1, "a"))!;
        using var survivor = host.CreateEffect(GainId, new(2, "b"))!;
        crasher.Prepare(48000, 512);
        survivor.Prepare(48000, 512);
        survivor.SetParameter(0, 1f);

        // Fine at first: a pass-through.
        var (l, r) = Signal(Frames);
        var dry = (float[])l.Clone();
        crasher.Process(l, r, [], []);
        Assert.Equal(dry, l);

        crasher.SetParameter(0, 1f); // "Crash": the sandbox dies in the next block
        var watch = Stopwatch.StartNew();
        for (var b = 0; b < 200; b++) {
            var (x, y) = Signal(Frames, b * Frames);
            var expected = (float[])x.Clone();
            crasher.Process(x, y, [], []);
            Assert.Equal(expected, x); // bypassed: dry
            var (u, v) = Signal(Frames, b * Frames);
            survivor.Process(u, v, [], []);
            Assert.Equal(expected[5] * MathF.Pow(10, 12 / 20f), u[5], 4); // the other sandbox keeps playing
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"processing around the crash took {watch.Elapsed}");
        SpinWait.SpinUntil(() => !problems.IsEmpty, TimeSpan.FromSeconds(5));
        var problem = Assert.Single(problems);
        Assert.Equal(new(1, "a"), problem.Owner);
        Assert.Equal(MisbehaveId, problem.PluginId);
        Assert.Contains("crashed", problem.Message, StringComparison.Ordinal);
        var hosted = (IHostedPlugin)crasher;
        Assert.True(hosted.IsFailed);
        Assert.False(((IHostedPlugin)survivor).IsFailed);
        Assert.Null(crasher.SaveState()); // nothing to save from a dead sandbox
        Assert.Equal(-1, hosted.OpenEditor(true));

        // Re-adding the insert starts a new sandbox.
        using var again = host.CreateEffect(MisbehaveId, new(1, "a"))!;
        again.Prepare(48000, 512);
        var (p, q) = Signal(Frames);
        var before = (float[])p.Clone();
        again.Process(p, q, [], []);
        Assert.Equal(before, p);
        Assert.False(((IHostedPlugin)again).IsFailed);
        Assert.NotEqual(hosted.Stats.SandboxPid, ((IHostedPlugin)again).Stats.SandboxPid);
    }

    [Fact]
    public void A_crash_takes_down_the_plugins_sharing_its_sandbox() {
        using var host = Host(data);
        host.Processing = ProcessingMode.Offline;
        var problems = new ConcurrentQueue<PluginProblem>();
        host.Problem += problems.Enqueue;
        using var crasher = host.CreateEffect(MisbehaveId, new(1, "a"))!;
        using var neighbour = host.CreateEffect(GainId, new(2, "b"))!;
        crasher.Prepare(48000, 512);
        neighbour.Prepare(48000, 512);
        crasher.SetParameter(0, 1f);
        var (l, r) = Signal(Frames);
        crasher.Process(l, r, [], []);
        SpinWait.SpinUntil(() => problems.Count == 2, TimeSpan.FromSeconds(5));
        Assert.Equal([1, 2], problems.Select(p => p.Owner.TrackId).Order());
        Assert.True(((IHostedPlugin)neighbour).IsFailed);
        var (x, y) = Signal(Frames);
        var dry = (float[])x.Clone();
        neighbour.Process(x, y, [], []);
        Assert.Equal(dry, x);
    }

    [Fact]
    public void A_late_block_is_bypassed_by_the_deadline_and_the_plugin_catches_up() {
        using var host = Host(data);
        host.Processing = ProcessingMode.RealTime;
        host.ConfigureDevice(48000, Frames, null);
        using var slow = host.CreateEffect(MisbehaveId, new(1, "a"))!;
        slow.Prepare(48000, 512);
        var hosted = (IHostedPlugin)slow;

        // Warm up without a deadline (the test's sandbox is a JIT build), then real time.
        host.Processing = ProcessingMode.Offline;
        for (var b = 0; b < 20; b++) {
            var (l, r) = Signal(Frames, b * Frames);
            slow.Process(l, r, [], []);
        }

        Assert.Equal(20, hosted.Stats.Completed);
        host.Processing = ProcessingMode.RealTime;
        var missesBefore = hosted.Stats.Misses;
        slow.SetParameter(1, 0.2f); // "Stall": 20 ms per block, 15 periods
        var worst = TimeSpan.Zero;
        for (var b = 0; b < 40; b++) {
            var (l, r) = Signal(Frames, b * Frames);
            var dry = (float[])l.Clone();
            var watch = Stopwatch.StartNew();
            host.BeginCycle();
            slow.Process(l, r, [], []);
            worst = watch.Elapsed > worst ? watch.Elapsed : worst;
            Assert.Equal(dry, l); // the pass-through is bypassed with the dry signal: identical either way
            Thread.Sleep(1); // the device's period
        }

        // Never waited past the deadline (80 % of 1.33 ms), give or take scheduling of this non-real-time test thread.
        Assert.True(worst < TimeSpan.FromMilliseconds(5), $"worst block {worst.TotalMilliseconds:0.00} ms");
        var stats = hosted.Stats;
        Log($"misses {stats.Misses - missesBefore}, busy skips {stats.BusySkips}, completed {stats.Completed}");
        Assert.True(stats.Misses - missesBefore >= 30);
        Assert.True(stats.BusySkips > 0, "a late sandbox is not signalled again until it has caught up");

        // Back to normal: it catches up and completes blocks again.
        slow.SetParameter(1, 0f);
        var completed = stats.Completed;
        var until = Stopwatch.StartNew();
        while (hosted.Stats.Completed < completed + 10 && until.Elapsed < TimeSpan.FromSeconds(5)) {
            var (l, r) = Signal(Frames);
            host.BeginCycle();
            slow.Process(l, r, [], []);
            Thread.Sleep(1);
        }

        Assert.True(hosted.Stats.Completed >= completed + 10);
        Assert.False(hosted.IsFailed);
    }

    [Fact]
    public void A_plugin_that_hangs_is_killed_as_hung_and_bypassed() {
        var options = Options(data.Path);
        options.HangTimeout = TimeSpan.FromSeconds(1);
        using var host = new PluginHost(options);
        host.Catalog.Scan(false, TestContext.Current.CancellationToken);
        var problems = new ConcurrentQueue<PluginProblem>();
        host.Problem += problems.Enqueue;
        using var stuck = host.CreateEffect(MisbehaveId, new(1, "a"))!;
        stuck.Prepare(48000, 512);
        stuck.SetParameter(1, 1f); // "Stall" at 1: the plugin never returns
        var watch = Stopwatch.StartNew();
        while (problems.IsEmpty && watch.Elapsed < TimeSpan.FromSeconds(10)) {
            var (l, r) = Signal(Frames);
            var dry = (float[])l.Clone();
            host.BeginCycle();
            stuck.Process(l, r, [], []);
            Assert.Equal(dry, l);
            Thread.Sleep(1);
        }

        var problem = Assert.Single(problems);
        Assert.Contains("hung", problem.Message, StringComparison.Ordinal);
        Assert.True(((IHostedPlugin)stuck).IsFailed);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(5));
    }
}
