using Anode.Server.Engine;

namespace Anode.Server.Tests;

/// <summary>The machine's cores, from a known <c>/proc/stat</c> and from the real system.</summary>
public sealed class CoreLoadsTests {
    const string Before = """
                          cpu  300 0 300 2400 0 0 0 0 0 0
                          cpu0 100 0 100 800 0 0 0 0 0 0
                          cpu1 200 0 200 1600 0 0 0 0 0 0
                          intr 123
                          """;

    const string After = """
                         cpu  500 0 400 3100 0 0 0 0 0 0
                         cpu0 175 0 125 900 0 0 0 0 0 0
                         cpu1 225 0 225 2100 100 0 0 0 0 0
                         intr 456
                         """;

    [Fact]
    public void A_proc_stat_gives_a_load_per_core_with_idle_and_iowait_as_not_busy() {
        var loads = CoreLoads.Between(CoreLoads.ParseProcStat(Before), CoreLoads.ParseProcStat(After));
        Assert.Equal(2, loads.Length);
        Assert.Equal(0.5, loads[0], 6); // 100 of 200 ticks busy
        Assert.Equal(50.0 / 650, loads[1], 6); // 50 busy in 650, iowait among the rest
    }

    [Fact]
    public void Readings_of_different_cores_or_no_time_give_nothing_to_divide() {
        var cores = CoreLoads.ParseProcStat(Before);
        Assert.Empty(CoreLoads.Between([], cores));
        Assert.Equal([0d, 0d], CoreLoads.Between(cores, cores));
    }

    [Fact]
    public async Task This_system_reports_its_cores_where_it_can() {
        Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux(), "No per-core source here yet.");
        var cores = new CoreLoads();
        Assert.Empty(cores.Sample());
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var loads = cores.Sample();
        Assert.Equal(Environment.ProcessorCount, loads.Count);
        Assert.All(loads, static load => Assert.InRange(load, 0, 1));
    }
}
