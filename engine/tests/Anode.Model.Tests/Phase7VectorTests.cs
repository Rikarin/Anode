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
///     on a region at the project start, so the command's path (project ticks, regions, ids) gives E's exact events.
///     Skipped until E's vectors are there. A case is
///     <c>
///         { name, set | groove, notes, controls?, strength?, velocity?,
///         expected: { notes, controls? } }
///     </c>
///     , the events in the contract's JSON.
/// </summary>
public sealed class Phase7VectorTests {
    static readonly JsonSerializerOptions Json = new(ContractsJsonContext.Default.Options) {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(ContractsJsonContext.Default, new DefaultJsonTypeInfoResolver())
    };

    [Fact]
    public void The_transform_command_gives_the_Transform_vectors_events() {
        var cases = Cases("TransformVectors.json");
        foreach (var c in cases) {
            var (state, region) = Project(c);
            var set = c["set"].Deserialize<TransformSet>(Json)!;
            if (set.Mode == TransformMode.SelectOnly) {
                continue; // the editor's own
            }

            var after = Edits.Apply(state, new TransformCommand([region], set), new SequentialIds()).State;
            Expect(c, after.MidiRegions[region]);
        }
    }

    [Fact]
    public void Groove_quantise_gives_the_groove_vectors_notes() {
        var cases = Cases("GrooveVectors.json");
        foreach (var c in cases) {
            if (c["groove"] is null || c["expected"] is null) {
                continue; // an extraction case
            }

            var (state, region) = Project(c);
            var groove = c["groove"].Deserialize<GrooveTemplate>(Json)!;
            state = state.Apply(new GrooveUpserted(groove));
            var after = Edits.Apply(
                    state,
                    new QuantizeNotesCommand(
                        region,
                        groove.StepTicks > int.MaxValue ? 0 : (int)groove.StepTicks,
                        c["strength"]?.GetValue<double>() ?? 1,
                        GrooveId: groove.Id,
                        GrooveVelocity: c["velocity"]?.GetValue<bool>() ?? false
                    ),
                    new SequentialIds()
                )
                .State;
            Expect(c, after.MidiRegions[region]);
        }
    }

    static List<JsonNode> Cases(string name, [CallerFilePath] string source = "") {
        var path = Path.Combine(Path.GetDirectoryName(source)!, name);
        Assert.SkipUnless(File.Exists(path), $"{name} is workstream E's: not there yet.");
        var root = JsonNode.Parse(File.ReadAllText(path));
        var cases = (root as JsonArray ?? root?["cases"] as JsonArray ?? [])
            .OfType<JsonNode>()
            .Where(static c => c["notes"] is not null)
            .ToList();
        Assert.NotEmpty(cases);
        return cases;
    }

    /// <summary>A project with one MIDI region at the start holding the case's notes and controls, as given.</summary>
    static (ProjectState State, string Region) Project(JsonNode c) {
        var notes = c["notes"].Deserialize<Note[]>(Json) ?? [];
        var controls = c["controls"]?.Deserialize<ControlEvent[]>(Json) ?? [];
        var end = notes.Select(static n => n.StartTick + n.LengthTicks)
            .Concat(controls.Select(static e => e.Tick + 1))
            .DefaultIfEmpty(3840)
            .Max();
        var region = new MidiRegion(
            "mid_vectors",
            "trk_vectors",
            "Vectors",
            0,
            Math.Max(3840, end + 3840),
            ProjectDefaults.Playback,
            notes,
            controls,
            false
        );
        var state = ProjectDefaults.NewProject("prj_v", "Vectors", 48_000)
            .Apply(
                new TrackUpserted(
                    Edits.Apply(
                        ProjectDefaults.NewProject("prj_v", "Vectors", 48_000),
                        new AddTrackCommand("Vectors", Kind: TrackKind.Midi),
                        new SequentialIds()
                    )
                    .State.Tracks[0] with {
                        Id = "trk_vectors"
                    },
                    0
                )
            )
            .Apply(new MidiRegionUpserted(region));
        return (state, region.Id);
    }

    static void Expect(JsonNode c, MidiRegion region) {
        var expected = c["expected"]!;
        var notes = expected["notes"].Deserialize<Note[]>(Json) ?? [];
        Assert.Equal(
            Edits.Sorted(notes).Select(static n => JsonSerializer.Serialize(n, Json)),
            region.Notes.Select(static n => JsonSerializer.Serialize(n, Json))
        );
        if (expected["controls"]?.Deserialize<ControlEvent[]>(Json) is { } controls) {
            Assert.Equal(
                Edits.Sorted(controls).Select(static e => JsonSerializer.Serialize(e, Json)),
                region.Controls.Select(static e => JsonSerializer.Serialize(e, Json))
            );
        }
    }
}
