using System.Diagnostics;
using static Anode.Plugins.Tests.PluginFixtures;

namespace Anode.Plugins.Tests;

/// <summary>The out-of-process scanner: what it finds, its cache and its blocklist.</summary>
public sealed class ScanTests : IDisposable {
    readonly TempDirectory data = new();

    public void Dispose() => data.Dispose();

    [Fact]
    public void Scan_describes_the_test_plugins() {
        using var host = Host(data);
        var catalog = host.CatalogEntries();
        Assert.Equal(4, catalog.Count);

        var gain = Assert.Single(catalog, p => p.Id == GainId);
        Assert.Equal("Anode Test Gain", gain.Name);
        Assert.Equal("Anode", gain.Vendor);
        Assert.Equal("vst3", gain.Format);
        Assert.False(gain.IsInstrument);
        Assert.Equal("Utility", gain.Category);
        Assert.True(gain.HasEditor);
        Assert.False(gain.HasSidechain);
        Assert.Equal(["Gain", "Tone"], gain.Parameters.Select(p => p.Name));
        Assert.Equal(24.0 / 36.0, gain.Parameters[0].Default, 9);

        var delay = Assert.Single(catalog, p => p.Id == DelayId);
        Assert.Equal("Delay", delay.Category);
        Assert.True(delay.HasSidechain);
        Assert.False(delay.HasEditor);
        Assert.Equal(64, host.Catalog.Find(DelayId)!.Description.Latency);

        var misbehave = Assert.Single(catalog, p => p.Id == MisbehaveId);
        Assert.Equal(1, misbehave.Parameters[0].Steps);

        var synth = Assert.Single(catalog, p => p.Id == SynthId);
        Assert.True(synth.IsInstrument);
        Assert.Equal("", synth.Category);
    }

    [Fact]
    public void A_second_scan_reuses_the_cache_and_a_new_host_starts_from_it() {
        using (var host = Host(data)) {
            Assert.Equal(4, host.Catalog.Entries.Count);
        }

        Assert.True(File.Exists(Path.Combine(data.Path, "plugins", "cache.json")));
        using var again = new PluginHost(Options(data.Path));
        Assert.Equal(4, again.Catalog.Entries.Count); // from the cache, before any scan

        // Unchanged bundles are not opened again: no scanner process for them.
        var lines = new List<string>();
        var options = Options(data.Path);
        options.Log = lines.Add;
        using var third = new PluginHost(options);
        var watch = Stopwatch.StartNew();
        third.Catalog.Scan(false, TestContext.Current.CancellationToken);
        Assert.Equal(4, third.Catalog.Entries.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"cached scan took {watch.Elapsed}");
    }

    [Fact]
    public void A_plugin_that_crashes_the_scanner_is_blocklisted_and_the_rest_are_found() {
        var folder = Path.Combine(data.Path, "vst3");
        CopyBundle(TestBundle, Path.Combine(folder, "AnodeTestCrashOnScan.vst3"));
        CopyBundle(TestBundle, Path.Combine(folder, "ZAnodeTest.vst3"));
        var options = Options(data.Path);
        options.Vst3Folders = [folder];
        using var host = new PluginHost(options);
        host.Catalog.Scan(false, TestContext.Current.CancellationToken);

        // The good copy (scanned after the crash, in a new scanner) is found; the crashing one is blocklisted.
        Assert.Equal(4, host.Catalog.Entries.Count);
        Assert.All(host.Catalog.Entries, e => Assert.EndsWith("ZAnodeTest.vst3", e.Path, StringComparison.Ordinal));
        var (item, reason) = Assert.Single(host.Catalog.Blocklist);
        Assert.EndsWith("AnodeTestCrashOnScan.vst3", item, StringComparison.Ordinal);
        Assert.Contains("crashed", reason, StringComparison.Ordinal);

        // A rescan skips it without starting a scanner for it.
        using var again = new PluginHost(options);
        var watch = Stopwatch.StartNew();
        again.Catalog.Scan(false, TestContext.Current.CancellationToken);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"rescan took {watch.Elapsed}");
        Assert.Equal(4, again.Catalog.Entries.Count);
    }

    [Fact]
    public void A_plugin_that_hangs_the_scanner_is_blocklisted() {
        var folder = Path.Combine(data.Path, "vst3");
        CopyBundle(TestBundle, Path.Combine(folder, "AnodeTestHangOnScan.vst3"));
        var options = Options(data.Path);
        options.Vst3Folders = [folder];
        options.ScanTimeout = TimeSpan.FromSeconds(2);
        using var host = new PluginHost(options);
        host.Catalog.Scan(false, TestContext.Current.CancellationToken);
        Assert.Empty(host.Catalog.Entries);
        var (_, reason) = Assert.Single(host.Catalog.Blocklist);
        Assert.Contains("hung", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Installed_plugins_are_scanned() {
        // Whatever this machine has (on macOS at least Apple's Audio Units): the scanner gets through all of it.
        var options = Options(data.Path);
        options.Vst3Folders = null;
        options.ClapFolders = null;
        options.ScanAudioUnits = OperatingSystem.IsMacOS();
        using var host = new PluginHost(options);
        var watch = Stopwatch.StartNew();
        host.Catalog.Scan(false, TestContext.Current.CancellationToken);
        Log($"scanned in {watch.Elapsed.TotalSeconds:0.0} s");
        foreach (var e in host.Catalog.Entries) {
            Log(
                $"{e.PluginId,-48} {e.Description.Name,-28} {e.Description.Vendor,-12} {(e.Description.IsInstrument ? "instrument" : e.Description.Category),-10} " +
                $"params={e.Description.Parameters.Count} sidechain={e.Description.HasSidechain} editor={e.Description.HasEditor} latency={e.Description.Latency}"
            );
        }

        foreach (var (item, reason) in host.Catalog.Blocklist) {
            Log($"blocklisted {item}: {reason}");
        }

        if (OperatingSystem.IsMacOS()) {
            Assert.Contains(host.Catalog.Entries, e => e.PluginId == AuDelayId);
            Assert.Contains(host.Catalog.Entries, e => e.PluginId == "au:aufx.hshf.appl");
        }
    }

    [Fact]
    public void Installed_plugins_load_and_process_in_sandboxes() {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("ANODE_TEST_INSTALLED") == "1",
            "loads every installed plugin: set ANODE_TEST_INSTALLED=1"
        );
        var options = Options(data.Path);
        options.Vst3Folders = null;
        options.ClapFolders = null;
        options.ScanAudioUnits = OperatingSystem.IsMacOS();
        using var host = new PluginHost(options);
        host.Catalog.Scan(false, TestContext.Current.CancellationToken);
        host.Processing = ProcessingMode.Offline;
        foreach (var e in host.Catalog.Entries) {
            var watch = Stopwatch.StartNew();
            var hosted = e.Description.IsInstrument
                ? (IDisposable?)host.CreateInstrument(e.PluginId, new(1, null))
                : host.CreateEffect(e.PluginId, new(1, "x"));
            using var _ = hosted;
            var plugin = (IHostedPlugin)hosted!;
            var peak = 0f;
            if (hosted is Anode.Dsp.Effects.IAudioEffect fx) {
                fx.Prepare(48000, 512);
                var (l, _) = Render(fx, 50, 64);
                peak = l.Max(MathF.Abs);
            } else if (hosted is Anode.Dsp.Instruments.IInstrument inst) {
                inst.Prepare(48000, 512);
                var (l, _) = Render(inst, 50, 64, b => b == 1 ? [new(0, 0x90, 60, 100)] : []);
                peak = l.Max(MathF.Abs);
            }

            Log(
                $"loaded {e.PluginId,-48} {(plugin.IsFailed ? "FAILED " + plugin.Failure : "ok")} in {watch.ElapsedMilliseconds} ms; " +
                $"blocks {plugin.Stats.Completed}, peak {peak:0.000}"
            );
        }
    }

    static void CopyBundle(string from, string to) {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
