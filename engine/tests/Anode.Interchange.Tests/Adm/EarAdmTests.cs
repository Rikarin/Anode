using Anode.Contracts;
using Anode.Dsp.Spatial;
using Anode.Interchange.Adm;
using Anode.Model.Audio;
using System.Diagnostics;
using System.Globalization;
using Xunit.Sdk;
using SpeakerLayout = Anode.Dsp.Spatial.SpeakerLayout;

namespace Anode.Interchange.Tests.Adm;

/// <summary>
///     Anode's ADM BWF read and rendered by the EBU ADM Renderer (<c>ear-render --strict</c>, ITU-R BS.2127): it accepts
///     the files (BS.2076 and BS.2088 as it checks them), and its 7.1.4 render of the golden path's scene (a 7.1.4 bed
///     and a mono object moving from front left to rear right overhead) matches Anode's own, per speaker and per second,
///     within 1 dB of RMS. Opt-in: <c>python3 scripts/setup-ear.py</c> (or <c>ANODE_EAR</c>); skipped otherwise.
/// </summary>
public sealed class EarAdmTests(ITestOutputHelper output) : IDisposable {
    readonly AdmScenario scenario = new();

    public void Dispose() => scenario.Dispose();

    static string Python() {
        var named = Environment.GetEnvironmentVariable("ANODE_EAR");
        var python = !string.IsNullOrEmpty(named)
            ? named
            : Path.Combine(
                AdmScenario.RepositoryRoot(),
                "engine",
                "artifacts",
                "ear",
                OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python"
            );
        Assert.SkipUnless(File.Exists(python), "No ear: run python3 scripts/setup-ear.py (or set ANODE_EAR).");
        return python;
    }

    static void Render(string input, string output, string system) {
        var start = new ProcessStartInfo(Python()) {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in new[] { "-I", "-m", "ear.cmdline.render_file", "--strict", "-s", system, input, output }) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"ear-render failed: {error.Result}");
        // ear notes that BS.2127 standardises the rendering of BS.2076-1 files only; any other warning is a finding.
        var warnings = error.Result.Split('\n')
            .Where(static l => l.Contains("warning", StringComparison.OrdinalIgnoreCase))
            .Where(static l => !l.Contains("is not standardised", StringComparison.Ordinal))
            .ToList();
        Assert.True(warnings.Count == 0, string.Join('\n', warnings));
    }

    static float[][] ReadAll(string path) {
        using var reader = Bw64Reader.Open(path);
        var channels = new float[reader.Channels][];
        for (var c = 0; c < reader.Channels; c++) {
            channels[c] = new float[reader.Frames];
        }

        var block = new float[4096 * reader.Channels];
        var at = 0;
        int frames;
        while ((frames = reader.Read(block)) > 0) {
            for (var f = 0; f < frames; f++) {
                for (var c = 0; c < reader.Channels; c++) {
                    channels[c][at + f] = block[f * reader.Channels + c];
                }
            }

            at += frames;
        }

        return channels;
    }

    /// <summary>Anode's render of a scene to 7.1.4: beds pass through, objects through the object renderer block by block.</summary>
    static float[][] AnodeRender(AdmScene scene) {
        var layout = SpeakerLayout.Of(SpeakerFormat.Surround714);
        var result = new float[12][];
        for (var c = 0; c < 12; c++) {
            result[c] = new float[scene.Frames];
        }

        foreach (var bed in scene.Beds) {
            var channels = ReadAll(bed.AudioPath);
            for (var c = 0; c < 12; c++) {
                channels[c].CopyTo(result[c], 0);
            }
        }

        var renderer = new ObjectRenderer();
        var longest = (int)scene.Objects.SelectMany(static o => o.Blocks).Max(static b => b.DurationFrames);
        renderer.Prepare(layout, scene.Objects.Count, scene.SampleRate, longest);
        for (var o = 0; o < scene.Objects.Count; o++) {
            var signal = ReadAll(scene.Objects[o].AudioPath);
            var output = new float[12 * longest];
            foreach (var block in scene.Objects[o].Blocks) {
                var frames = (int)block.DurationFrames;
                var input = new float[signal.Length * frames];
                for (var c = 0; c < signal.Length; c++) {
                    signal[c].AsSpan((int)block.StartFrame, frames).CopyTo(input.AsSpan(c * frames));
                }

                if (!block.Interpolate) {
                    renderer.Reset(o);
                }

                renderer.SetPosition(o, new((float)block.Azimuth, (float)block.Elevation, (float)block.Distance, (float)block.Size));
                Array.Clear(output);
                renderer.Render(o, new(input, signal.Length, frames), new(output, 12, frames));
                for (var c = 0; c < 12; c++) {
                    var destination = result[c].AsSpan((int)block.StartFrame, frames);
                    var source = output.AsSpan(c * frames, frames);
                    for (var n = 0; n < frames; n++) {
                        destination[n] += source[n];
                    }
                }
            }
        }

        return result;
    }

    static double RmsDb(ReadOnlySpan<float> signal) {
        var sum = 0.0;
        foreach (var s in signal) {
            sum += s * s;
        }

        return 10 * Math.Log10(Math.Max(sum / signal.Length, 1e-20));
    }

    [Fact]
    public void Ear_renders_the_golden_path_scene_as_anode_does_within_1_db_per_speaker_and_second() {
        const int seconds = 4;
        var scene = scenario.GoldenPath(AdmScenario.Rate * seconds);
        var adm = scenario.PathOf("golden.wav");
        AdmWriter.Write(scene, adm);
        var rendered = scenario.PathOf("golden-ear.wav");
        Render(adm, rendered, "4+7+0");

        var ear = ReadAll(rendered);
        var anode = AnodeRender(scene);
        Assert.Equal(12, ear.Length);
        var layout = SpeakerLayout.Of(SpeakerFormat.Surround714);
        var worst = 0.0;
        for (var second = 0; second < seconds; second++) {
            var line = new List<string>();
            for (var c = 0; c < 12; c++) {
                var a = RmsDb(anode[c].AsSpan(second * AdmScenario.Rate, AdmScenario.Rate));
                var e = RmsDb(ear[c].AsSpan(second * AdmScenario.Rate, AdmScenario.Rate));
                line.Add(string.Create(CultureInfo.InvariantCulture, $"{layout.Label(c)} {a:F2}/{e:F2}"));
                worst = Math.Max(worst, Math.Abs(a - e));
                Assert.True(Math.Abs(a - e) <= 1, $"second {second}, {layout.Label(c)}: Anode {a:F2} dB, ear {e:F2} dB");
            }

            output.WriteLine($"second {second}: {string.Join(", ", line)}");
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"largest difference {worst:F3} dB"));

        // The object's energy moves from L to the top rear right speaker.
        static int Loudest(float[][] channels, int start) =>
            Enumerable.Range(0, 12).MaxBy(c => RmsDb(channels[c].AsSpan(start, 4800)));
        Assert.Equal(0, Loudest(ear, 0));
        Assert.Equal(11, Loudest(ear, AdmScenario.Rate * seconds - 4800));
    }

    [Fact]
    public void Ear_accepts_every_bed_layout_and_stereo_objects() {
        foreach (var layout in SpeakerLayouts.All) {
            var count = layout.ChannelCount;
            var audio = scenario.Noise($"bed-{layout.Name}.wav", count, 4_800, [..Enumerable.Repeat(0.1f, count)], count);
            var pad = scenario.Noise($"pad-{layout.Name}.wav", 2, 4_800, [0.2f, 0.2f], 99);
            var path = scenario.PathOf($"adm-{layout.Name}.wav");
            AdmWriter.Write(
                new(
                    layout.Name,
                    AdmScenario.Rate,
                    4_800,
                    [new("Bed", layout.Format, audio)],
                    [new("Pad", pad, 2, [new(0, 2_400, 60, 20, 1, 0.3, false), new(2_400, 2_400, -60, 20, 1, 0.1, true)])]
                ),
                path
            );
            Render(path, scenario.PathOf($"ear-{layout.Name}.wav"), "4+7+0");
        }
    }
}
