using Anode.Contracts;
using System.Diagnostics;

namespace Anode.Stems.Tests;

/// <summary>
///     The exported HTDemucs on the test mix (docs/09-phase-8.md, step 9; docs/adr/0010-stem-splitter.md). They run when
///     the models are in <c>models/</c> at the repository root (or <c>ANODE_MODELS</c>; <c>node
///     scripts/fetch-models.mjs</c>) and the mix in <c>engine/artifacts/content/stem-mix/</c> (<c>node
///     scripts/build-content.mjs --stems</c>), and are skipped otherwise.
/// </summary>
public sealed class RealModelTests {
    static readonly StemKind[] Four = [StemKind.Vocals, StemKind.Drums, StemKind.Bass, StemKind.Other];

    static string Models() =>
        Environment.GetEnvironmentVariable("ANODE_MODELS") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Signals.RepositoryRoot(), "models");

    static string Model(StemModel model) {
        var path = Path.Combine(Models(), StemSeparators.FileName(model));
        Assert.SkipUnless(File.Exists(path), $"No {path}: run node scripts/fetch-models.mjs.");
        return path;
    }

    static string Mix() {
        var folder = Path.Combine(Signals.RepositoryRoot(), "engine", "artifacts", "content", "stem-mix");
        Assert.SkipUnless(
            File.Exists(Path.Combine(folder, "mix.wav")),
            $"No {folder}: run node scripts/build-content.mjs --stems."
        );
        return folder;
    }

    [Theory]
    [InlineData(StemModel.FourStems, "htdemucs")]
    [InlineData(StemModel.SixStems, "htdemucs_6s")]
    public void The_network_with_the_csharp_transforms_matches_demucs_on_one_segment(StemModel model, string name) {
        var path = Model(model);
        var mixFile = Path.Combine(Models(), name + ".check-mix.npy");
        Assert.SkipUnless(File.Exists(mixFile), $"No {mixFile}: scripts/export-demucs.py writes it.");
        var mix = Signals.ReadNpy(mixFile);
        var expected = Signals.ReadNpy(Path.Combine(Models(), name + ".check-out.npy"));
        using var network = OnnxSegmentModel.Load(path, StemSeparatorOptions.Default);
        var length = network.SegmentLength;
        var spectrogram = new Spectrogram(length);
        var spectrum = new float[spectrogram.SpectrumLength];
        spectrogram.Forward(mix.AsSpan(0, length), 0, spectrum);
        spectrogram.Forward(mix.AsSpan(length, length), 1, spectrum);
        var stems = network.Stems.Count;
        var spectrumOut = new float[stems * spectrum.Length];
        var waveOut = new float[stems * 2 * length];
        network.Run(mix, spectrum, spectrumOut, waveOut, CancellationToken.None);
        var max = 0.0;
        var stem = new float[length];
        for (var s = 0; s < stems; s++) {
            for (var c = 0; c < 2; c++) {
                waveOut.AsSpan((s * 2 + c) * length, length).CopyTo(stem);
                spectrogram.InverseAdd(spectrumOut.AsSpan(s * spectrum.Length, spectrum.Length), c, stem);
                var reference = expected.AsSpan((s * 2 + c) * length, length);
                for (var n = 0; n < length; n++) {
                    max = Math.Max(max, Math.Abs(stem[n] - reference[n]));
                }
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{name}: max |Δ| against demucs {max:G3}");
        Assert.True(max < 1e-3, $"max |Δ| {max}");
    }

    [Fact]
    public void Four_stems_separate_the_test_mix() {
        var path = Model(StemModel.FourStems);
        var folder = Mix();
        var (mix, rate) = Signals.ReadWav(Path.Combine(folder, "mix.wav"));
        using var separator = StemSeparators.Open(StemModel.FourStems, path);
        var clock = Stopwatch.StartNew();
        var stems = separator.Separate(mix, rate, Four, null, TestContext.Current.CancellationToken);
        var seconds = clock.Elapsed.TotalSeconds;
        var minutes = mix[0].Length / (double)rate / 60;
        var report = new List<string> {
            $"CPU: {seconds:F1} s for {minutes * 60:F1} s, {seconds / minutes:F1} s per minute"
        };
        var sdr = new Dictionary<StemKind, double>();
        foreach (var kind in Four) {
            var (reference, _) = Signals.ReadWav(Path.Combine(folder, kind.ToString().ToLowerInvariant() + ".wav"));
            sdr[kind] = Signals.Sdr(reference, stems[kind]);
            report.Add($"{kind}: SDR {sdr[kind]:F2} dB");
        }

        foreach (var line in report) {
            TestContext.Current.TestOutputHelper?.WriteLine(line);
        }

        Assert.True(sdr[StemKind.Drums] >= 6, string.Join("; ", report));
        Assert.True(sdr[StemKind.Bass] >= 6, string.Join("; ", report));
        Assert.True(sdr[StemKind.Vocals] >= 3, string.Join("; ", report));
        Assert.True(sdr[StemKind.Other] >= 3, string.Join("; ", report));
    }

    [Fact]
    public void Six_stems_run_on_the_test_mix() {
        var path = Model(StemModel.SixStems);
        var folder = Mix();
        var (mix, rate) = Signals.ReadWav(Path.Combine(folder, "mix.wav"));
        using var separator = StemSeparators.Open(StemModel.SixStems, path);
        var clock = Stopwatch.StartNew();
        var stems = separator.Separate(mix, rate, separator.Stems, null, TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine($"six stems, CPU: {clock.Elapsed.TotalSeconds:F1} s");
        foreach (var kind in Four) {
            var (reference, _) = Signals.ReadWav(Path.Combine(folder, kind.ToString().ToLowerInvariant() + ".wav"));
            TestContext.Current.TestOutputHelper?.WriteLine($"{kind}: SDR {Signals.Sdr(reference, stems[kind]):F2} dB");
        }

        Assert.Equal(6, stems.Count);
        Assert.All(stems.Values, s => Assert.Equal(mix[0].Length, s[0].Length));
    }

    [Fact]
    public void CoreMl_times_the_test_mix() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "CoreML is macOS only.");
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("ANODE_STEMS_COREML") == "1",
            "Set ANODE_STEMS_COREML=1 to time CoreML against the CPU (it compiles the model first)."
        );
        var path = Model(StemModel.FourStems);
        var (mix, rate) = Signals.ReadWav(Path.Combine(Mix(), "mix.wav"));
        var (reference, _) = Signals.ReadWav(Path.Combine(Mix(), "drums.wav"));
        foreach (var provider in new[] { StemExecutionProvider.Cpu, StemExecutionProvider.CoreMl }) {
            var load = Stopwatch.StartNew();
            using var separator = StemSeparators.Open(
                StemModel.FourStems,
                path,
                new() { Provider = provider }
            );
            var loaded = load.Elapsed.TotalSeconds;
            var clock = Stopwatch.StartNew();
            var stems = separator.Separate(mix, rate, [StemKind.Drums], null, TestContext.Current.CancellationToken);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{provider}: load {loaded:F1} s, separate {clock.Elapsed.TotalSeconds:F1} s, drums SDR "
                + $"{Signals.Sdr(reference, stems[StemKind.Drums]):F2} dB"
            );
        }
    }
}
