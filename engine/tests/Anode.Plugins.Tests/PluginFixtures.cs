using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;

namespace Anode.Plugins.Tests;

/// <summary>Where the test plug-ins and the sandbox are, and helpers to drive hosted plugins.</summary>
public static class PluginFixtures {
    public const string GainId = "vst3:414E4F44455447310000000100000001";
    public const string DelayId = "vst3:414E4F44455447310000000200000001";
    public const string MisbehaveId = "vst3:414E4F44455447310000000300000001";
    public const string SynthId = "vst3:414E4F44455447310000000400000001";
    public const string AuDelayId = "au:aufx.dely.appl";
    public const string ClapSineId = "org.anode.test-sine";

    /// <summary>engine/artifacts/native/vst3, where native/build.sh puts AnodeTest.vst3.</summary>
    public static string Vst3Folder {
        get {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "artifacts", "native"))) {
                dir = dir.Parent;
            }

            return dir is null
                ? throw new InvalidOperationException("engine/artifacts/native not found: run engine/native/build.sh")
                : Path.Combine(dir.FullName, "artifacts", "native", "vst3");
        }
    }

    public static string TestBundle => Path.Combine(Vst3Folder, "AnodeTest.vst3");

    /// <summary>engine/artifacts/native/clap, where native/build.sh puts the Phase 2 test CLAP instrument.</summary>
    public static string ClapFolder => Path.Combine(Path.GetDirectoryName(Vst3Folder)!, "clap");

    /// <summary>
    ///     The anode-plugin build next to this test project's (run with dotnet exec), or <c>ANODE_PLUGIN_SANDBOX</c>
    ///     (a published NativeAOT one).
    /// </summary>
    public static string Sandbox {
        get {
            if (Environment.GetEnvironmentVariable("ANODE_PLUGIN_SANDBOX") is { Length: > 0 } published) {
                return published;
            }

            var dll = Path.Combine(
                AppContext.BaseDirectory.Replace(
                    Path.Combine("tests", "Anode.Plugins.Tests"),
                    Path.Combine("src", "Anode.PluginSandbox"),
                    StringComparison.Ordinal
                ),
                "anode-plugin.dll"
            );
            Assert.True(File.Exists(dll), $"anode-plugin not built at {dll}");
            return dll;
        }
    }

    /// <summary>Options for a host over the test bundle only, with its own data directory.</summary>
    public static PluginHostOptions Options(string dataDirectory, SandboxMode mode = SandboxMode.Grouped) =>
        new() {
            DataDirectory = dataDirectory,
            SandboxExecutable = Sandbox,
            Mode = mode,
            Vst3Folders = [Vst3Folder],
            ClapFolders = [],
            ScanAudioUnits = false,
            Log = Log
        };

    public static void Log(string line) {
        // ANODE_PLUGIN_TEST_LOG=<file> keeps what passing tests report (scan results, timings).
        if (Environment.GetEnvironmentVariable("ANODE_PLUGIN_TEST_LOG") is { Length: > 0 } file) {
            lock (typeof(PluginFixtures)) {
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }

        try {
            TestContext.Current.TestOutputHelper?.WriteLine(line);
        } catch (InvalidOperationException) {
            // no test running
        }
    }

    /// <summary>A scanned host over the test bundle.</summary>
    public static PluginHost Host(TempDirectory data, SandboxMode mode = SandboxMode.Grouped) {
        var host = new PluginHost(Options(data.Path, mode));
        host.Catalog.Scan(false, TestContext.Current.CancellationToken);
        return host;
    }

    /// <summary>A stereo test signal: a sine left, a different one right, 0.5 peak.</summary>
    public static (float[] Left, float[] Right) Signal(int frames, int offset = 0) {
        var l = new float[frames];
        var r = new float[frames];
        for (var i = 0; i < frames; i++) {
            var n = i + offset;
            l[i] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * n / 48000f);
            r[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * n / 48000f);
        }

        return (l, r);
    }

    /// <summary>Processes <paramref name="blocks" /> blocks of the test signal; the output, concatenated.</summary>
    public static (float[] Left, float[] Right) Render(
        IAudioEffect effect,
        int blocks,
        int frames,
        Action<int>? before = null
    ) {
        var left = new float[blocks * frames];
        var right = new float[blocks * frames];
        for (var b = 0; b < blocks; b++) {
            before?.Invoke(b);
            var (l, r) = Signal(frames, b * frames);
            effect.Process(l, r, [], []);
            l.CopyTo(left, b * frames);
            r.CopyTo(right, b * frames);
        }

        return (left, right);
    }

    public static (float[] Left, float[] Right) Render(
        IInstrument instrument,
        int blocks,
        int frames,
        Func<int, MidiEvent[]> events
    ) {
        var left = new float[blocks * frames];
        var right = new float[blocks * frames];
        for (var b = 0; b < blocks; b++) {
            var l = new float[frames];
            var r = new float[frames];
            instrument.Process(events(b), l, r);
            l.CopyTo(left, b * frames);
            r.CopyTo(right, b * frames);
        }

        return (left, right);
    }
}

/// <summary>A temporary directory, deleted on disposal.</summary>
public sealed class TempDirectory : IDisposable {
    public TempDirectory() {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "anode-plugins-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose() {
        try {
            Directory.Delete(Path, true);
        } catch (IOException) {
            // best effort
        }
    }
}
