using Anode.Contracts;
using Anode.Model.Storage;
using CsCheck;
using System.Text.Json.Nodes;

namespace Anode.Model.Tests;

/// <summary>
///     The migration chain against real files. <c>Fixtures/Schemas/SchemaN.anode</c> were written by the Anode that
///     wrote schema N, built from the last commit before schema N + 1 with one small program, which made the same song
///     through that build's own edit commands, each schema adding what it brought. They are never regenerated: a new
///     schema adds a folder, and <see cref="Every_schema_has_a_fixture" /> fails until it does. Schema 1 to 11 came from
///     f0ee395~1, 6e96cda~1, a911c56~1, 8243598~1, a612505~1, f5aa076~1, d0670db~1, 15524a3~1, 97807c7~1, 18dfef2~1 and
///     97dbdbc. The audio file is named but absent (fixtures are about the document), so it reads as offline.
/// </summary>
public sealed class MigrationFixtureTests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("anode-migration-").FullName;

    static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Schemas");

    public void Dispose() => Directory.Delete(folder, true);

    public static TheoryData<int> Schemas => [..Enumerable.Range(1, PackageMigrations.CurrentSchemaVersion)];

    void Copy(int schema) => CopyTo(schema, folder);

    static void CopyTo(int schema, string target) {
        var source = Path.Combine(Fixtures, $"Schema{schema}.anode");
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            var copy = Path.Combine(target, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(path, copy, true);
        }
    }

    IEnumerable<string> Documents() =>
        Directory.EnumerateFiles(Path.Combine(folder, ProjectPackage.TracksFolder), "*.json")
            .Append(Path.Combine(folder, ProjectPackage.ProjectFileName));

    [Fact]
    public void Every_schema_has_a_fixture() {
        var names = Directory.EnumerateDirectories(Fixtures).Select(Path.GetFileName).Order().ToArray();

        Assert.Equal(
            Enumerable.Range(1, PackageMigrations.CurrentSchemaVersion).Select(static n => $"Schema{n}.anode").Order(),
            names
        );
    }

    [Theory]
    [MemberData(nameof(Schemas))]
    public void A_fixture_is_really_of_its_schema(int schema) {
        Copy(schema);

        Assert.All(
            Documents(),
            path => {
                var version = JsonNode.Parse(File.ReadAllText(path))!["schemaVersion"];
                Assert.Equal(schema, version?.GetValue<int>() ?? 1);
            }
        );
    }

    [Theory]
    [MemberData(nameof(Schemas))]
    public void A_fixture_opens_with_everything_its_schema_held(int schema) {
        Copy(schema);

        var state = ProjectPackage.Load(folder);

        // Schema 1: audio.
        Assert.Equal(($"prj_schema{schema}", $"Schema {schema}", 48_000), (state.Id, state.Name, state.SampleRate));
        Assert.Empty(state.UnreadableTracks);
        var vocal = state.Tracks.Single(static t => t.Name == "Vocal");
        Assert.Equal(TrackKind.Audio, vocal.Kind);
        Assert.Equal((-2.5, -0.25), (vocal.Strip.VolumeDb, vocal.Strip.Pan));
        var file = Assert.Single(state.Files.Values);
        Assert.Equal(("Tone", 48_000, 48_000L), (file.Name, file.SampleRate, file.Frames));
        var region = Assert.Single(state.Regions.Values);
        Assert.Equal((vocal.Id, file.Id, 1920L), (region.TrackId, region.FileId, region.StartTick));
        Assert.Equal((48_000L, 480L, 960L, -1.0), (region.Length, region.FadeIn, region.FadeOut, region.GainDb));

        // Schema 2: instruments and MIDI.
        if (schema >= 2) {
            var keys = state.Tracks.Single(static t => t.Name == "Keys");
            Assert.Equal(TrackKind.Instrument, keys.Kind);
            Assert.NotNull(keys.Instrument);
            var midi = Assert.Single(state.MidiRegions.Values);
            Assert.Equal(keys.Id, midi.TrackId);
            Assert.Equal([60, 64], midi.Notes.Select(static n => n.Pitch));
            Assert.Equal([0L, 480], midi.Notes.Select(static n => n.StartTick));
        } else {
            Assert.Empty(state.MidiRegions);
        }

        // Schema 3: the global tracks, markers, sections and notes.
        Assert.Equal(schema >= 3 ? 100 : 120, state.Settings.Tempo);
        Assert.Equal(schema >= 3 ? 100 : 120, state.Settings.TempoEvents[0].Bpm);
        Assert.Equal(schema >= 3 ? "Intro" : null, state.Sections.Values.SingleOrDefault()?.Name);
        Assert.Equal(schema >= 3 ? $"Written by schema {schema}." : string.Empty, state.Notes);
        Assert.Equal(schema >= 3, state.Markers.Values.Any(static m => m.Name == "Verse"));

        // Schema 4: the mixer and automation.
        if (schema >= 4) {
            var aux = state.Tracks.Single(static t => t.Name == "Reverb");
            Assert.Equal((TrackKind.Aux, 1), (aux.Kind, aux.InputBus));
            var send = Assert.Single(vocal.Strip.Sends);
            Assert.Equal((1, -9.0), (send.Bus, send.LevelDb));
            Assert.Equal("anode.synth.fx", Assert.Single(vocal.Strip.Inserts).PluginId);
            var lane = Assert.Single(state.Automation.Values);
            Assert.Equal((vocal.Id, "volume"), (lane.TrackId, lane.Target));
            Assert.Equal([0L, 3840], lane.Points.Select(static p => p.Tick));
            Assert.Equal("Vocals", Assert.Single(state.Groups.Values).Name);
        } else {
            Assert.Empty(vocal.Strip.Sends);
            Assert.Equal("master", vocal.Strip.Output);
            Assert.Empty(state.Automation);
        }

        // Schema 5: flex.
        Assert.Equal(schema >= 5, vocal.FlexOn);
        Assert.Equal(schema >= 5 ? 2 : 0, region.Transpose);
        Assert.Equal(1, state.Settings.Varispeed.Speed);

        // Schema 6: Live Loops and patterns.
        Assert.Equal(schema >= 6 ? ["Scene A"] : [], state.LiveLoops.Scenes.Select(static s => s.Name));
        Assert.Equal(schema >= 6 ? 1 : 0, state.PatternRegions.Count);

        // Schema 7: grooves.
        Assert.Equal(schema >= 7, state.Tracks.Exists(static t => t.MatchGroove));
        Assert.Equal(0, file.Version);

        // Schema 8: sync.
        Assert.Equal(schema >= 8 ? MtcFrameRate.Fps30 : MtcFrameRate.Fps25, state.Settings.Sync.FrameRate);

        // Schema 9: the score.
        Assert.Equal(schema >= 9, state.Tracks.Exists(static t => t.StaffStyleId == StaffStyleIds.Piano));
        Assert.All(state.MidiRegions.Values, static r => Assert.NotNull(r.Notation));

        // Schema 10: surround and scene markers.
        Assert.Equal(ChannelFormat.Stereo, state.Settings.SurroundFormat);
        Assert.Equal(ChannelFormat.Stereo, vocal.Strip.InputFormat);
        Assert.Equal(schema >= 10, state.Markers.Values.Any(static m => m.Kind == MarkerKind.Scene));

        // Schema 11: keys and the chord track.
        Assert.Empty(state.ChordTrack.Chords);
        Assert.Equal(schema >= 11, state.MidiRegions.Values.Any(static r => r.Key is not null));
    }

    [Theory]
    [MemberData(nameof(Schemas))]
    public void A_fixture_saves_as_the_current_schema_and_reads_back_the_same(int schema) {
        Copy(schema);
        var state = ProjectPackage.Load(folder);

        ProjectPackage.Save(folder, state);

        Assert.All(
            Documents(),
            static path => Assert.Equal(
                PackageMigrations.CurrentSchemaVersion,
                JsonNode.Parse(File.ReadAllText(path))!["schemaVersion"]!.GetValue<int>()
            )
        );
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(ProjectPackage.Load(folder)));
    }

    /// <summary>
    ///     Each step only fills in what is missing: a file that already has everything comes through the chain from any
    ///     version as it went in, whatever the project held.
    /// </summary>
    [Fact]
    public void The_chain_from_any_schema_leaves_what_is_there_alone() =>
        Gen.Select(Gen.Int[0, 500], Gen.Int[1, PackageMigrations.CurrentSchemaVersion])
            .Sample(
                static sample => {
                    var (seed, schema) = sample;
                    var target = Directory.CreateTempSubdirectory("anode-chain-").FullName;
                    try {
                        var (state, _, _) = RandomProjects.Build(seed, 30);
                        ProjectPackage.Save(target, state);
                        foreach (var path in Directory.EnumerateFiles(target, "*.json", SearchOption.AllDirectories)) {
                            var node = JsonNode.Parse(File.ReadAllText(path))!;
                            node["schemaVersion"] = schema;
                            File.WriteAllText(path, node.ToJsonString());
                        }

                        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(ProjectPackage.Load(target)));
                    } finally {
                        Directory.Delete(target, true);
                    }
                },
                iter: 60,
                threads: 1
            );

    /// <summary>
    ///     A real file with any of its properties gone, as a damaged one, a hand-edited one or one from a build nobody
    ///     tested: it is read or refused with <see cref="PackageException" />, never anything else, and what is read saves
    ///     and reads back the same.
    /// </summary>
    [Fact]
    public void A_fixture_with_a_property_taken_out_is_read_or_refused_and_nothing_else() =>
        Gen.Select(Gen.Int[1, PackageMigrations.CurrentSchemaVersion], Gen.Int[0, 100_000], Gen.Int[0, 3])
            .Sample(
                static sample => {
                    var (schema, pick, damaged) = sample;
                    var target = Directory.CreateTempSubdirectory("anode-damage-").FullName;
                    try {
                        CopyTo(schema, target);
                        var documents = Directory.EnumerateFiles(target, "*.json", SearchOption.AllDirectories)
                            .Order(StringComparer.Ordinal)
                            .ToArray();
                        var path = documents[damaged % documents.Length];
                        var node = JsonNode.Parse(File.ReadAllText(path))!;
                        var properties = Properties(node).ToArray();
                        var (parent, key) = properties[pick % properties.Length];
                        ((JsonObject)parent).Remove(key);
                        File.WriteAllText(path, node.ToJsonString());

                        ProjectState state;
                        try {
                            state = ProjectPackage.Load(target);
                        } catch (PackageException) {
                            return;
                        }

                        ProjectPackage.Save(target, state);
                        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(ProjectPackage.Load(target)));
                    } finally {
                        Directory.Delete(target, true);
                    }
                },
                iter: 400,
                threads: 1
            );

    static IEnumerable<(JsonNode Parent, string Key)> Properties(JsonNode node) {
        switch (node) {
            case JsonObject map:
                foreach (var (key, value) in map) {
                    yield return (map, key);
                    if (value is not null) {
                        foreach (var inner in Properties(value)) {
                            yield return inner;
                        }
                    }
                }

                break;
            case JsonArray list:
                foreach (var item in list.OfType<JsonNode>()) {
                    foreach (var inner in Properties(item)) {
                        yield return inner;
                    }
                }

                break;
        }
    }
}
