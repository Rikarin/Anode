using Anode.Contracts;
using Anode.Model.Editing;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Anode.Model.Tests;

/// <summary>
///     The <c>transform</c> and groove-quantise commands against workstream E's shared vectors
///     (<c>TransformVectors.json</c>, <c>GrooveVectors.json</c>; docs/08-phase-7.md): each case runs through the command
///     on a MIDI region (at the project start for Transform, whose vectors are in project ticks; at the case's
///     <c>regionStartTick</c> for quantise), so the command's path (project ticks, regions, ids, checks) gives E's
///     events exactly. The algorithms themselves are E's tests.
/// </summary>
public sealed class Phase7VectorTests {
    const string Region = "mid_vectors";

    static readonly JsonSerializerOptions Json = new(ContractsJsonContext.Default.Options) {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(ContractsJsonContext.Default, new DefaultJsonTypeInfoResolver())
    };

    [Fact]
    public void The_transform_command_gives_the_Transform_vectors_events() {
        var root = Vectors("TransformVectors.json");
        var notes = root["notes"].Deserialize<Note[]>(Json)!;
        var controls = root["controls"].Deserialize<ControlEvent[]>(Json)!;
        var run = 0;
        foreach (var c in root["cases"]!.AsArray().OfType<JsonNode>()) {
            var set = c["set"].Deserialize<TransformSet>(Json)!;
            if (set.Mode == TransformMode.SelectOnly) {
                continue; // the editor's own; the command refuses it
            }

            var state = Project(0, notes, controls);
            if (c["signatures"]?.Deserialize<SignatureEvent[]>(Json) is { Length: > 0 } signatures) {
                state = state with { Settings = state.Settings with { Signatures = signatures } };
            }

            var after = Edits.Apply(state, new TransformCommand([Region], set), new SequentialIds()).State;
            var expected = c["expected"]!;
            Same(Edits.Sorted(expected["notes"].Deserialize<Note[]>(Json)!), after.MidiRegions[Region].Notes, c);
            Same(
                Edits.Sorted(expected["controls"].Deserialize<ControlEvent[]>(Json)!),
                after.MidiRegions[Region].Controls,
                c
            );
            run++;
        }

        Assert.True(run > 10, $"Only {run} Transform vectors ran.");
    }

    [Fact]
    public void Groove_quantise_gives_the_groove_vectors_notes() {
        var root = Vectors("GrooveVectors.json");
        var cases = root["quantize"]!.AsArray().OfType<JsonNode>().ToArray();
        Assert.NotEmpty(cases);
        foreach (var c in cases) {
            var groove = c["groove"].Deserialize<GrooveTemplate>(Json)!;
            var state = Project(c["regionStartTick"]!.GetValue<long>(), c["notes"].Deserialize<Note[]>(Json)!, [])
                .Apply(new GrooveUpserted(groove));
            var after = Edits.Apply(
                    state,
                    new QuantizeNotesCommand(
                        Region,
                        0,
                        c["strength"]!.GetValue<double>(),
                        GrooveId: groove.Id,
                        GrooveVelocity: c["velocity"]!.GetValue<bool>()
                    ),
                    new SequentialIds()
                )
                .State;
            Same(Edits.Sorted(c["expected"].Deserialize<Note[]>(Json)!), after.MidiRegions[Region].Notes, c);
        }
    }

    static JsonNode Vectors(string name, [CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, name)))!;

    /// <summary>A project with one external MIDI track and a region from <paramref name="start" /> holding the events.</summary>
    static ProjectState Project(long start, IReadOnlyList<Note> notes, IReadOnlyList<ControlEvent> controls) {
        var state = Edits.Apply(
                ProjectDefaults.NewProject("prj_v", "Vectors", 48_000),
                new AddTrackCommand("Vectors", Kind: TrackKind.Midi),
                new SequentialIds()
            )
            .State;
        return state.Apply(
            new MidiRegionUpserted(
                new(
                    Region,
                    state.Tracks[0].Id,
                    "Vectors",
                    start,
                    3840 * 16,
                    ProjectDefaults.Playback,
                    Edits.Sorted(notes),
                    Edits.Sorted(controls),
                    false
                )
            )
        );
    }

    /// <summary>The events expected (in the region's canonical order) are the region's.</summary>
    static void Same<T>(IEnumerable<T> expected, IReadOnlyList<T> actual, JsonNode c) =>
        Assert.True(
            expected.Select(static x => JsonSerializer.Serialize(x, Json))
                .SequenceEqual(actual.Select(static x => JsonSerializer.Serialize(x, Json))),
            $"Vector '{c["name"]}' differs."
        );
}
