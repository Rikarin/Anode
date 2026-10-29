using Anode.Contracts;
using Anode.Model.Audio;
using Anode.Model.Midi;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Anode.ContentBuilder;

/// <summary>A pack's identity, as its <c>pack.json</c> says.</summary>
public sealed record PackSpec(string Id, string Name, string Version, string Author, string Description);

/// <summary>
///     Assembles a content pack in a staging folder (loops with their sidecars, pattern loops, kits under
///     <c>patches/</c>, the manifest) and zips it into a <c>.anodepack</c> (docs/07-phase-6.md, "Content packs").
/// </summary>
public sealed class PackBuilder(PackSpec spec, string staging) {
    public const int SampleRate = 48_000;

    readonly List<string> written = [];

    /// <summary>Every file written so far, relative to the pack's root.</summary>
    public IReadOnlyList<string> Written => written;

    /// <summary>An audio loop (stereo float WAV) and its sidecar; tempo null for a one-shot.</summary>
    public void AudioLoop(
        string folder,
        string name,
        float[] left,
        float[] right,
        double? tempo,
        double? beats,
        int? key,
        LoopScale scale,
        IReadOnlyList<string> tags,
        IReadOnlyList<long>? transients = null
    ) {
        var path = File(folder, name + ".wav");
        using (var writer = new WavWriter(path, 2, SampleRate, WavEncoding.Pcm24)) {
            var interleaved = new float[left.Length * 2];
            for (var i = 0; i < left.Length; i++) {
                interleaved[2 * i] = left[i];
                interleaved[2 * i + 1] = right[i];
            }

            writer.Write(interleaved);
        }

        Sidecar(
            path + ".anodeloop.json",
            new(1, LoopKind.Audio, name, tempo, beats, key, scale, tags, tempo is null, transients)
        );
    }

    /// <summary>A MIDI loop (format 0) and its sidecar.</summary>
    public void MidiLoop(
        string folder,
        string name,
        IReadOnlyList<Note> notes,
        double tempo,
        double beats,
        int? key,
        LoopScale scale,
        IReadOnlyList<string> tags
    ) {
        var path = File(folder, name + ".mid");
        System.IO.File.WriteAllBytes(
            path,
            StandardMidiFileWriter.Write(notes, null, tempo, name, (long)Math.Round(beats * 960))
        );
        Sidecar(path + ".anodeloop.json", new(1, LoopKind.Midi, name, tempo, beats, key, scale, tags, false));
    }

    /// <summary>A pattern loop: its sidecar alone, holding the pattern.</summary>
    public void PatternLoop(
        string folder,
        string name,
        Pattern pattern,
        double tempo,
        double beats,
        IReadOnlyList<string> tags
    ) {
        var path = File(folder, name + ".anodeloop.json");
        Sidecar(
            path,
            new(1, LoopKind.Pattern, name, tempo, beats, null, LoopScale.Neither, tags, false, null, pattern)
        );
    }

    /// <summary>A drum kit: a patch of kind stack with its pads (the server's patch file, under <c>patches/</c>).</summary>
    public void Kit(string id, string name, IReadOnlyList<(int Note, string Name, InstrumentSlot Instrument)> pads) {
        var path = File("patches", id + ".json");
        var json = new JsonObject {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["name"] = name,
            ["category"] = "Drum Kits",
            ["kind"] = "stack",
            ["inserts"] = new JsonArray(),
            ["sends"] = new JsonArray(),
            ["pads"] = new JsonArray(
                [
                    ..pads.Select(static p => (JsonNode)new JsonObject {
                            ["note"] = p.Note,
                            ["name"] = p.Name,
                            ["instrument"] = JsonSerializer.SerializeToNode(
                                p.Instrument,
                                ContractsJsonContext.Default.InstrumentSlot
                            ),
                            ["inserts"] = new JsonArray()
                        }
                    )
                ]
            )
        };
        System.IO.File.WriteAllText(path, json.ToJsonString(new() { WriteIndented = true }));
    }

    /// <summary>Writes the manifest and zips the staging folder into <paramref name="output" /> (replacing it).</summary>
    public void Finish(string output) {
        var manifest = new JsonObject {
            ["id"] = spec.Id,
            ["name"] = spec.Name,
            ["version"] = spec.Version,
            ["author"] = spec.Author,
            ["description"] = spec.Description
        };
        System.IO.File.WriteAllText(
            Path.Combine(staging, "pack.json"),
            manifest.ToJsonString(new() { WriteIndented = true })
        );
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        if (System.IO.File.Exists(output)) {
            System.IO.File.Delete(output);
        }

        ZipFile.CreateFromDirectory(staging, output, CompressionLevel.Optimal, false);
    }

    string File(string folder, string name) {
        var directory = Path.Combine(staging, folder);
        Directory.CreateDirectory(directory);
        written.Add(Path.Combine(folder, name).Replace('\\', '/'));
        return Path.Combine(directory, name);
    }

    static void Sidecar(string path, LoopSidecar sidecar) =>
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(sidecar, ContractsJsonContext.Default.LoopSidecar));
}
