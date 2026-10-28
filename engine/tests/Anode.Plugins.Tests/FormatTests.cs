using Anode.Dsp.Instruments;
using static Anode.Plugins.Tests.PluginFixtures;

namespace Anode.Plugins.Tests;

/// <summary>CLAP and Audio Units through the same factory and sandbox as VST3.</summary>
public sealed class FormatTests : IDisposable {
    const int Frames = 64;
    readonly TempDirectory data = new();

    public void Dispose() => data.Dispose();

    PluginHostOptions ClapOptions(SandboxMode mode = SandboxMode.Grouped) {
        var options = Options(data.Path, mode);
        options.Vst3Folders = [];
        options.ClapFolders = [ClapFolder];
        return options;
    }

    PluginHostOptions AuOptions(SandboxMode mode = SandboxMode.Grouped) {
        var options = Options(data.Path, mode);
        options.Vst3Folders = [];
        options.ScanAudioUnits = true;
        return options;
    }

    [Fact]
    public void A_CLAP_instrument_is_scanned_and_plays_in_a_sandbox_bit_exact_against_in_process() {
        using var sandboxed = new PluginHost(ClapOptions());
        sandboxed.Catalog.Scan(false, TestContext.Current.CancellationToken);
        var entry = Assert.Single(sandboxed.CatalogEntries());
        Assert.Equal(ClapSineId, entry.Id); // the CLAP id itself, as Phase 2 projects store it
        Assert.Equal("clap", entry.Format);
        Assert.True(entry.IsInstrument);
        Assert.Equal(["Gain", "Octave"], entry.Parameters.Select(p => p.Name));
        Assert.Equal(["-2", "-1", "0", "+1", "+2"], entry.Parameters[1].ValueNames!);

        sandboxed.Processing = ProcessingMode.Offline;
        using var local = new PluginHost(ClapOptions(SandboxMode.InHost));
        local.Catalog.Scan(false, TestContext.Current.CancellationToken);
        using var a = sandboxed.CreateInstrument(ClapSineId, new(1, null))!;
        using var b = local.CreateInstrument(ClapSineId, new(1, null))!;
        a.Prepare(48000, 512);
        b.Prepare(48000, 512);
        a.SetParameter(1, 1f); // an octave up
        b.SetParameter(1, 1f);

        MidiEvent[] Events(int block) =>
            block switch {
                2 => [new(5, 0x90, 57, 110)],
                20 => [new(30, 0x80, 57, 0)],
                _ => []
            };

        var x = Render(a, 40, Frames, Events);
        var y = Render(b, 40, Frames, Events);
        Assert.Equal(y.Left, x.Left);
        Assert.Contains(x.Left, v => MathF.Abs(v) > 0.01f);

        var state = a.SaveState()!;
        using var c = sandboxed.CreateInstrument(ClapSineId, new(1, null))!;
        c.Prepare(48000, 512);
        Assert.True(c.LoadState(state));
        Assert.Equal(1f, c.GetParameter(1));
    }

    [Theory]
    [InlineData("au:aufx.dely.appl")]
    [InlineData("au:aufx.hshf.appl")]
    [InlineData("au:aufx.lmtr.appl")]
    public void An_Audio_Unit_runs_in_a_sandbox_bit_exact_against_in_process(string pluginId) {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Audio Units are macOS only");
        using var sandboxed = new PluginHost(AuOptions());
        sandboxed.Catalog.Scan(false, TestContext.Current.CancellationToken);
        sandboxed.Processing = ProcessingMode.Offline;
        using var local = new PluginHost(AuOptions(SandboxMode.InHost));
        local.Catalog.Scan(false, TestContext.Current.CancellationToken);
        using var a = sandboxed.CreateEffect(pluginId, new(1, "a"))!;
        using var b = local.CreateEffect(pluginId, new(1, "a"))!;
        a.Prepare(48000, 512);
        b.Prepare(48000, 512);
        Log($"{pluginId}: latency {a.LatencySamples} at 48 kHz, {a.Descriptor.Parameters.Count} parameters");
        var p = a.Descriptor.Parameters[0];
        var value = p.Min + 0.25f * (p.Max - p.Min);
        a.SetParameter(0, value);
        b.SetParameter(0, value);
        var x = Render(a, 100, Frames);
        var y = Render(b, 100, Frames);
        Assert.Equal(y.Left, x.Left);
        Assert.Equal(y.Right, x.Right);
        Assert.Contains(x.Left, v => MathF.Abs(v) > 0.01f);
        Assert.Equal(value, a.GetParameter(0), 4);

        // State: the parameter travels in the AU's ClassInfo.
        var state = a.SaveState()!;
        using var c = sandboxed.CreateEffect(pluginId, new(1, "a"))!;
        c.Prepare(48000, 512);
        Assert.True(c.LoadState(state));
        Assert.Equal(value, c.GetParameter(0), 4);
    }

    [Fact]
    public void The_peak_limiter_reports_its_latency() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Audio Units are macOS only");
        using var host = new PluginHost(AuOptions());
        host.Catalog.Scan(false, TestContext.Current.CancellationToken);
        using var limiter = host.CreateEffect("au:aufx.lmtr.appl", new(1, "a"))!;
        limiter.Prepare(48000, 512);
        Assert.True(limiter.LatencySamples > 0, $"latency {limiter.LatencySamples}");
    }
}
