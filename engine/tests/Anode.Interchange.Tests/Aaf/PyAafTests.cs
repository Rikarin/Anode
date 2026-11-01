using Anode.Interchange.Aaf;
using Anode.Model.Audio;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Anode.Interchange.Tests.Aaf;

/// <summary>
///     Anode's AAF exports read by an independent reader, pyaaf2 (<c>scripts/aaf-check.py</c>): every clip at its
///     position, source offset and length to the sample, the crossfade as a transition, the fades, the clip gain, the
///     embedded essence's bytes and the linked media's locators. Opt-in: run <c>python3 scripts/setup-pyaaf2.py</c> (or
///     set <c>ANODE_PYAAF2</c> to a python with pyaaf2); skipped otherwise.
/// </summary>
public sealed class PyAafTests : IDisposable {
    readonly AafScenario scenario = new();

    public void Dispose() => scenario.Dispose();

    [Fact]
    public void Pyaaf2_finds_every_clip_transition_gain_and_the_embedded_media() {
        var python = Python();
        var path = Path.Combine(scenario.Exports, "Song.aaf");
        AafWriter.Write(scenario.State, scenario.Folder, new("Song"), path);
        var result = Check(python, path);

        Assert.Equal("Song", (string?)result["name"]);
        var tracks = result["tracks"]!.AsArray();
        Assert.Equal(["Drums", "Keys"], tracks.Select(static t => (string?)t!["name"]));
        Assert.All(tracks, static t => Assert.Equal("[48000,1]", t!["editRate"]!.ToJsonString()));
        var drums = tracks[0]!["components"]!.AsArray();
        Assert.Equal(
            ["Filler", "OperationGroup", "Transition", "SourceClip", "Filler", "OperationGroup"],
            drums.Select(static c => (string?)c!["kind"])
        );
        AssertClip(drums[1]!, 24_000, 30_000, 1_000, 500, null, 0.501187);
        Assert.Equal(
            (49_200L, 4_800L, 2_400L),
            ((long)drums[2]!["position"]!, (long)drums[2]!["length"]!,
                (long)drums[2]!["cutPoint"]!)
        );
        Assert.Equal("Mono Audio Dissolve", (string?)drums[2]!["operation"]);
        AssertClip(drums[3]!, 49_200, 20_000, 2_000, null, 1_000, null);
        Assert.Equal((69_200L, 50_800L), ((long)drums[4]!["position"]!, (long)drums[4]!["length"]!));
        AssertClip(drums[5]!, 120_000, 10_000, 0, null, null, 1.412538);
        Assert.Equal(130_000, (long)tracks[0]!["length"]!);
        var keys = tracks[1]!["components"]!.AsArray();
        AssertClip(Assert.Single(keys)!, 0, 40_000, 100, null, null, 0.841395);

        var kick = drums[1]!["media"]!;
        Assert.Equal(
            ("Kick", "PCMDescriptor", 1, 16, 96_000L),
            ((string?)kick["master"], (string?)kick["class"],
                (int)kick["channels"]!, (int)kick["bits"]!, (long)kick["frames"]!)
        );
        Assert.Equal(Sha256(PcmOf("Kick.wav")), (string?)kick["essenceSha256"]);
        var pad = drums[3]!["media"]!;
        Assert.Equal((2, 24), ((int)pad["channels"]!, (int)pad["bits"]!));
        Assert.Equal(Sha256(PcmOf("Pad.wav")), (string?)pad["essenceSha256"]);
        var strings = keys[0]!["media"]!;
        Assert.Equal(
            (2, 24, 48_000L * 2 * 3),
            ((int)strings["channels"]!, (int)strings["bits"]!,
                (long)strings["essenceBytes"]!)
        );
        Assert.Equal(Sha256(Float24("Strings.wav")), (string?)strings["essenceSha256"]);
        Assert.Equal("AAF", (string?)kick["container"]);

        var markers = result["markers"]!.AsArray();
        Assert.Equal([48_000L, 96_000L], markers.Select(static m => (long)m!["position"]!));
        Assert.Equal(["Verse", "Chorus"], markers.Select(static m => (string?)m!["comment"]));
        Assert.Equal("DescriptiveMarker", (string?)markers[0]!["class"]);
        Assert.Equal(24_000, (long)markers[0]!["length"]!);
        Assert.Equal("first verse", (string?)markers[0]!["userComments"]!["Comment"]);
    }

    [Fact]
    public void Pyaaf2_finds_the_linked_media_by_their_locators() {
        var python = Python();
        var path = Path.Combine(scenario.Exports, "Linked.aaf");
        AafWriter.Write(scenario.State, scenario.Folder, new("Linked", EmbedMedia: false), path);
        var result = Check(python, path);
        var drums = result["tracks"]![0]!["components"]!.AsArray();
        AssertClip(drums[1]!, 24_000, 30_000, 1_000, 500, null, 0.501187);
        AssertClip(drums[3]!, 49_200, 20_000, 2_000, null, 1_000, null);
        var media = Path.Combine(scenario.Exports, "Linked Media");
        foreach (var (component, file) in new[] { (drums[1]!, "Kick.wav"), (drums[3]!, "Pad.wav") }) {
            var found = component["media"]!;
            Assert.Null(found["essenceSha256"]);
            Assert.Equal("RIFFWAVE", (string?)found["container"]);
            var url = (string?)Assert.Single(found["urls"]!.AsArray());
            Assert.Equal(new Uri(Path.Combine(media, file)).AbsoluteUri, url);
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(scenario.Folder, "Audio Files", file)),
                File.ReadAllBytes(new Uri(url!).LocalPath)
            );
        }
    }

    internal static string Python() {
        var named = Environment.GetEnvironmentVariable("ANODE_PYAAF2");
        var python = !string.IsNullOrEmpty(named)
            ? named
            : Path.Combine(
                AafScenario.RepositoryRoot(),
                "engine",
                "artifacts",
                "pyaaf2",
                OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python"
            );
        Assert.SkipUnless(File.Exists(python), "No pyaaf2: run python3 scripts/setup-pyaaf2.py (or set ANODE_PYAAF2).");
        return python;
    }

    static JsonNode Check(string python, string path) {
        var start = new ProcessStartInfo(python) {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(Path.Combine(AafScenario.RepositoryRoot(), "scripts", "aaf-check.py"));
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"pyaaf2 could not read {path}:\n{error.Result}");
        return JsonNode.Parse(output)!;
    }

    static void AssertClip(
        JsonNode clip,
        long position,
        long length,
        long start,
        long? fadeIn,
        long? fadeOut,
        double? gain
    ) {
        Assert.Equal((position, length, start), ((long)clip["position"]!, (long)clip["length"]!, (long)clip["start"]!));
        Assert.Equal(fadeIn, (long?)clip["fadeIn"]);
        Assert.Equal(fadeOut, (long?)clip["fadeOut"]);
        if (gain is null) {
            Assert.Null(clip["gain"]);
        } else {
            Assert.Equal(gain.Value, (double)clip["gain"]!, 5);
        }
    }

    byte[] PcmOf(string file) {
        var path = Path.Combine(scenario.Folder, "Audio Files", file);
        var info = WavReader.Probe(path);
        using var stream = File.OpenRead(path);
        stream.Position = info.DataOffset;
        var data = new byte[info.Frames * info.BytesPerFrame];
        stream.ReadExactly(data);
        return data;
    }

    byte[] Float24(string file) {
        using var reader = WavReader.Open(Path.Combine(scenario.Folder, "Audio Files", file));
        var samples = new float[reader.Info.Frames * reader.Info.Channels];
        for (var at = 0; at < samples.Length;) {
            var frames = reader.Read(samples.AsSpan(at));
            Assert.NotEqual(0, frames);
            at += frames * reader.Info.Channels;
        }

        var data = new byte[samples.Length * 3];
        for (var i = 0; i < samples.Length; i++) {
            var v = (int)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 8388607);
            (data[i * 3], data[i * 3 + 1], data[i * 3 + 2]) = ((byte)v, (byte)(v >> 8), (byte)(v >> 16));
        }

        return data;
    }

    static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
