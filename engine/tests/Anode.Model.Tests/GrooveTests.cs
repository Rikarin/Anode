using Anode.Contracts;
using Anode.Model.Groove;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Anode.Model.Tests;

/// <summary>
///     Groove templates (docs/08-phase-7.md, "Grooves"): extraction and quantise worked by hand, and the vectors the
///     server and the editor share (<c>GrooveVectors.json</c>, rewritten with <c>ANODE_UPDATE_VECTORS=1</c>).
/// </summary>
public sealed class GrooveTests {
    const long Bar = 3_840;
    const long Sixteenth = 240;
    const long Eighth = 480;

    static Note N(long id, long start, int velocity = 100, int pitch = 60) =>
        new(id, pitch, velocity, 1, start, 120, 0);

    static GrooveOnsetTick O(long tick, double strength) => new(tick, strength);

    [Fact]
    public void Extraction_keeps_the_strongest_onset_per_step_relative_to_the_strongest() {
        // A bar of eighths from tick 7680, swung: the offbeats 80 ticks late; step 3 has two onsets; step 5 none.
        GrooveOnsetTick[] onsets = [
            O(7_680, 1), O(7_680 + 560, 0.5), O(7_680 + 960, 0.8), O(7_680 + 1_440 + 100, 0.25),
            O(7_680 + 1_440 + 60, 0.4), O(7_680 + 1_920, 0.8), O(7_680 + 2_880 + 3, 1), O(7_680 + 3_360 + 80, 0.5),
            O(7_680 + 3_840 + 10, 1) // the next bar's first: outside the template
        ];

        var groove = Grooves.FromOnsets("grv_a", "Swing", onsets, 7_680, Bar, Eighth);

        Assert.Equal(
            [
                new GroovePoint(0, 1), new(560, 0.5), new(960, 0.8), new(1_500, 0.4), new(1_920, 0.8),
                new(2_400, 1), new(2_883, 1), new(3_440, 0.5)
            ],
            groove.Points
        );
        Assert.Equal(("grv_a", "Swing", Bar, Eighth), (groove.Id, groove.Name, groove.LengthTicks, groove.StepTicks));
    }

    [Fact]
    public void An_onset_belongs_to_the_step_whose_half_open_window_holds_it() {
        GrooveOnsetTick[] onsets = [O(-120, 0.5), O(119, 0.25), O(120, 1), O(359, 0.75)];
        var groove = Grooves.FromOnsets("grv_b", "Edges", onsets, 0, 480, Sixteenth);
        // Step 0 holds −120…119 (−120 at 0.5 beats 119), step 1 holds 120…359 (120 at 1 wins).
        Assert.Equal([new GroovePoint(-120, 0.5), new(120, 1)], groove.Points);
    }

    [Fact]
    public void Equal_strengths_go_to_the_nearer_then_the_earlier() {
        GrooveOnsetTick[] onsets = [O(30, 1), O(-20, 1), O(260, 1), O(220, 1)];
        var groove = Grooves.FromOnsets("grv_c", "Ties", onsets, 0, 480, Sixteenth);
        Assert.Equal([new GroovePoint(-20, 1), new(220, 1)], groove.Points);
    }

    [Fact]
    public void A_template_from_notes_uses_their_starts_and_velocities() {
        Note[] notes = [N(1, 0, 127), N(2, 300, 64), N(3, 960, 32)];
        var groove = Grooves.FromNotes("grv_n", "Notes", notes, 15_360, Bar, Eighth);
        Assert.Equal(new(0, 1), groove.Points[0]);
        Assert.Equal(new(300, 64.0 / 127), groove.Points[1]);
        Assert.Equal(new(960, 32.0 / 127), groove.Points[2]);
        Assert.Equal(new(1_440, 1), groove.Points[3]);
    }

    [Fact]
    public void Quantise_moves_to_the_templates_point_of_the_step_by_the_strength() {
        var groove = new GrooveTemplate(
            "grv_q",
            "Q",
            1_920,
            Eighth,
            [new(10, 1), new(560, 0.5), new(950, 1), new(1_460, 0.8)]
        );
        // Tick 4000: step 8 (nearest multiple of 480: 3840), template step 0 of cycle 2, landing at 3840 + 10.
        Assert.Equal((3_850L, 1.0), Grooves.Target(groove, 4_000, 1));
        Assert.Equal((3_925L, 1.0), Grooves.Target(groove, 4_000, 0.5));
        // Tick 4100: step 9 (4320), template step 1, landing at 3840 + 560 = 4400.
        Assert.Equal((4_400L, 0.5), Grooves.Target(groove, 4_100, 1));
        // A tie (240) goes to the earlier step 0.
        Assert.Equal((10L, 1.0), Grooves.Target(groove, 240, 1));
        Assert.Equal((5L, 1.0), Grooves.Target(groove, 0, 0.5)); // half of the way to 10
    }

    [Fact]
    public void Quantise_keeps_order_and_scales_velocities_only_when_asked() {
        var groove = new GrooveTemplate("grv_q", "Q", 960, Eighth, [new(0, 1), new(560, 0.5)]);
        Note[] notes = [N(1, 500), N(2, 10, 127)];

        var plain = Grooves.Quantize(notes, 960, groove, 1, false);
        Assert.Equal([N(1, 560), N(2, 0, 127)], plain);

        var both = Grooves.Quantize(notes, 960, groove, 0.5, true);
        // Note 1: 1460 → 1520 by half: 1490; velocity 100 × (1 + 0.5 × (0.5 − 1)) = 75. Note 2: 970 → 960 by half: 965.
        Assert.Equal([N(1, 530, 75), N(2, 5, 127)], both);
    }

    [Fact]
    public void A_template_must_hold_one_point_per_step() {
        Assert.Throws<ArgumentException>(static () => Grooves.FromOnsets("g", "g", [], 0, 1_000, 480));
        Assert.Throws<ArgumentException>(static () =>
            Grooves.Target(new GrooveTemplate("g", "g", 960, 480, [new(0, 1)]), 0, 1)
        );
    }

    // ---- The shared vectors ----

    static readonly JsonSerializerOptions Indented = new() {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    const string About =
        """Groove template vectors (docs/08-phase-7.md, "Grooves"): Anode.Model's Grooves computes these; the server """
        + "and the editor must reproduce them exactly (velocity scales to 1e-12). Ticks: 960 per quarter note. "
        + "EXTRACTION (`extract`: onsets in project ticks with strengths 0…1, from startTick, lengthTicks, stepTicks; "
        + "lengthTicks a positive multiple of stepTicks, N = lengthTicks / stepTicks points): step n's grid tick is "
        + "g = startTick + n * stepTicks and it holds the onsets with g - step/2 <= tick < g + step/2 (exactly: n = "
        + "floor((2 * (tick - startTick) + step) / (2 * step)), onsets with n outside 0…N-1 are ignored). Its winner is "
        + "the strongest; ties go to the nearer the grid, then the earlier. Point n is (winner.tick - startTick, "
        + "winner.strength / S), S the greatest winner strength; a step without onsets, or every step when S = 0, is "
        + "(n * stepTicks, 1). From notes (`fromNotes`: notes relative to the region's start startTick): each note is "
        + "an onset at startTick + note.startTick with strength velocity / 127. From audio the server turns "
        + "GrooveFromAudio onsets (frames, strengths) into project ticks with the tempo map first. "
        + "TARGET (`targets`: tick, strength): t <= 0 is step k = 0, else k = floor((2t + step - 1) / (2 * step)) (the "
        + "nearest multiple of stepTicks; a tie goes to the earlier); n = k mod N, cycle c = floor(k / N); the landing "
        + "is L = c * lengthTicks + points[n].tick (the template repeats from the project start); the result is "
        + "max(0, t + round((L - t) * strength)) (rounded half away from zero; never before the project start) and "
        + "points[n].velocityScale. "
        + "QUANTIZE (`quantize`: notes relative to regionStartTick): each note's project tick is regionStartTick + "
        + "startTick; its new startTick is target.tick - regionStartTick (not clamped to the region); with velocity, its velocity v "
        + "becomes round(v * (1 + strength * (scale - 1))) half away from zero, clamped to 1…127; other fields and the "
        + "order stay.";

    static GrooveTemplate Swing16 =>
        new(
            "grv_swing16",
            "16th swing",
            Bar,
            Sixteenth,
            [
                ..Enumerable.Range(0, 16)
                    .Select(static i => new GroovePoint(
                            i * Sixteenth + ((i & 1) == 1 ? 80 : 0),
                            i % 4 == 0 ? 1 : i % 2 == 0 ? 0.75 : 0.5
                        )
                    )
            ]
        );

    static GrooveTemplate Drunk8 =>
        new(
            "grv_drunk8",
            "Drunk 8ths",
            2 * Bar,
            Eighth,
            [
                ..Enumerable.Range(0, 16)
                    .Select(static i => new GroovePoint(
                            i * Eighth + i * 37 % 61 - 30,
                            0.4 + i * 7 % 13 / 20.0
                        )
                    )
            ]
        );

    static IEnumerable<
        (string Name, IReadOnlyList<GrooveOnsetTick> Onsets, long Start, long Length, long Step)> Extractions() {
        yield return ("swung eighths from bar 3, two onsets in one step, one step empty, one after the end", [
            O(7_680, 1), O(7_680 + 560, 0.5), O(7_680 + 960, 0.8), O(7_680 + 1_540, 0.25), O(7_680 + 1_500, 0.4),
            O(7_680 + 1_920, 0.8), O(7_680 + 2_883, 1), O(7_680 + 3_440, 0.5), O(7_680 + 3_850, 1)
        ], 7_680, Bar, Eighth);
        yield return ("window edges: -step/2 belongs to step 0, +step/2 to step 1", [
            O(-120, 0.5), O(119, 0.25), O(120, 1), O(359, 0.75), O(360, 0.9)
        ], 0, 480, Sixteenth);
        yield return ("ties: equal strengths go to the nearer, then the earlier", [
            O(30, 1), O(-20, 1), O(260, 1), O(220, 1), O(500, 0.5), O(460, 0.5)
        ], 0, 720, Sixteenth);
        yield return ("silence: every strength 0", [O(10, 0), O(250, 0)], 0, 480, Sixteenth);
        yield return ("no onsets", [], 960, 960, Sixteenth);
        yield return ("two bars of triplet eighths (320) with humanised hits", [
            ..Enumerable.Range(0, 24).Select(static i => O(1_000 + i * 320 + i * 53 % 41 - 20, 0.3 + i % 3 * 0.3))
        ], 1_000, 2 * Bar, 320);
    }

    static IEnumerable<(string Name, IReadOnlyList<Note> Notes, long Start, long Length, long Step)> FromNotes() {
        yield return ("a swung MIDI hi-hat bar at bar 5", [
            ..Enumerable.Range(0, 8)
                .Select(static i => N(i + 1, i * Eighth + ((i & 1) == 1 ? 100 : -3), i % 2 == 0 ? 110 : 70))
        ], 4 * Bar, Bar, Eighth);
        yield return ("velocity 127 is scale 1; chords keep the loudest", [
            N(1, 0, 127, 36), N(2, 2, 40, 42), N(3, 245, 90, 38), N(4, 250, 91, 42), N(5, 500, 64, 36)
        ], 0, 960, Sixteenth);
    }

    static IEnumerable<(string Name, GrooveTemplate Groove, long Tick, double Strength)> Targets() {
        foreach (var tick in (long[])[0, 1, 119, 120, 121, 240, 3_839, 3_840, 3_950, 7_800, 123_456]) {
            yield return (FormattableString.Invariant($"16th swing, tick {tick}, strength 1"), Swing16, tick, 1);
        }

        foreach (var strength in (double[])[0, 0.25, 0.5, 0.75, 1]) {
            yield return (FormattableString.Invariant($"16th swing, tick 250, strength {strength}"), Swing16, 250,
                strength);
            yield return (FormattableString.Invariant($"drunk 8ths, tick 5000, strength {strength}"), Drunk8, 5_000,
                strength);
        }

        yield return ("drunk 8ths, tick 0 (step 0's point is 30 early: clamped to the project start)", Drunk8, 0, 1);
        yield return ("drunk 8ths, the second cycle", Drunk8, 2 * 2 * Bar + 7 * Eighth + 30, 1);
        yield return ("negative tick is step 0", Swing16, -50, 1);
        yield return ("241 half way to 320: 39.5 rounds away from zero to 40", Swing16, 241, 0.5);
    }

    static IEnumerable<(string Name, IReadOnlyList<Note> Notes, long RegionStart, GrooveTemplate Groove, double
        Strength, bool Velocity)> Quantizes() {
        IReadOnlyList<Note> straight = [
            ..Enumerable.Range(0, 16)
                .Select(static i => N(i + 1, i * Sixteenth + i * 17 % 23 - (i == 0 ? 0 : 11), 100, 60 + i))
        ];
        yield return ("straight 16ths to 16th swing, full strength, no velocity", straight, 0, Swing16, 1, false);
        yield return ("straight 16ths to 16th swing, strength 0.6 with velocity", straight, 0, Swing16, 0.6, true);
        yield return ("a region at bar 2 to drunk 8ths, full strength with velocity", straight, Bar, Drunk8, 1, true);
        yield return ("a region at tick 100 (not on the grid) to drunk 8ths, strength 0.3", straight, 100, Drunk8, 0.3,
            true);
        yield return ("velocity clamps to 1…127 and the order stays", [N(5, 3_840, 127), N(1, 960, 1), N(9, 0, 127)],
            0, new GrooveTemplate("grv_x", "x", 1_920, 960, [new(0, 1.5), new(970, 0.001)]), 1, true);
    }

    [Fact]
    public void The_groove_vectors_are_current() {
        var extract = new JsonArray();
        foreach (var (name, onsets, start, length, step) in Extractions()) {
            extract.Add(
                new JsonObject {
                    ["name"] = name,
                    ["onsets"] = new JsonArray(
                        [
                            ..onsets.Select(static o => (JsonNode)new JsonObject {
                                    ["tick"] = o.Tick, ["strength"] = o.Strength
                                }
                            )
                        ]
                    ),
                    ["startTick"] = start,
                    ["lengthTicks"] = length,
                    ["stepTicks"] = step,
                    ["expected"] = Node(Grooves.FromOnsets("grv_test", name, onsets, start, length, step))
                }
            );
        }

        var fromNotes = new JsonArray();
        foreach (var (name, notes, start, length, step) in FromNotes()) {
            fromNotes.Add(
                new JsonObject {
                    ["name"] = name,
                    ["notes"] = Nodes(notes),
                    ["startTick"] = start,
                    ["lengthTicks"] = length,
                    ["stepTicks"] = step,
                    ["expected"] = Node(Grooves.FromNotes("grv_test", name, notes, start, length, step))
                }
            );
        }

        var targets = new JsonArray();
        foreach (var (name, groove, tick, strength) in Targets()) {
            var (at, scale) = Grooves.Target(groove, tick, strength);
            targets.Add(
                new JsonObject {
                    ["name"] = name,
                    ["grooveId"] = groove.Id,
                    ["tick"] = tick,
                    ["strength"] = strength,
                    ["expectedTick"] = at,
                    ["expectedVelocityScale"] = scale
                }
            );
        }

        var quantize = new JsonArray();
        foreach (var (name, notes, regionStart, groove, strength, velocity) in Quantizes()) {
            quantize.Add(
                new JsonObject {
                    ["name"] = name,
                    ["groove"] = Node(groove),
                    ["regionStartTick"] = regionStart,
                    ["strength"] = strength,
                    ["velocity"] = velocity,
                    ["notes"] = Nodes(notes),
                    ["expected"] = Nodes(Grooves.Quantize(notes, regionStart, groove, strength, velocity))
                }
            );
        }

        var document = new JsonObject {
            ["about"] = About,
            ["grooves"] = new JsonArray([Node(Swing16), Node(Drunk8)]),
            ["extract"] = extract,
            ["fromNotes"] = fromNotes,
            ["targets"] = targets,
            ["quantize"] = quantize
        };
        var json = document.ToJsonString(Indented) + "\n";
        var path = VectorsPath();
        if (Environment.GetEnvironmentVariable("ANODE_UPDATE_VECTORS") == "1") {
            File.WriteAllText(path, json);
        }

        Assert.Equal(json, File.ReadAllText(path));
    }

    /// <summary>Reads the vectors as a client does and checks every case, as the server's and editor's tests do.</summary>
    [Fact]
    public void The_groove_vectors_replay() {
        var document = JsonNode.Parse(File.ReadAllText(VectorsPath()))!;
        var grooves = document["grooves"]
            .Deserialize(ContractsJsonContext.Default.IReadOnlyListGrooveTemplate)!
                .ToDictionary(static g => g.Id);
        var count = 0;
        foreach (var e in document["extract"]!.AsArray()) {
            var onsets = e!["onsets"]!.AsArray()
                .Select(static o => new GrooveOnsetTick((long)o!["tick"]!, (double)o["strength"]!))
                .ToArray();
            var actual = Grooves.FromOnsets(
                "grv_test",
                (string)e["name"]!,
                onsets,
                (long)e["startTick"]!,
                (long)e["lengthTicks"]!,
                (long)e["stepTicks"]!
            );
            Same(e["expected"].Deserialize(ContractsJsonContext.Default.GrooveTemplate)!, actual);
            count++;
        }

        foreach (var e in document["fromNotes"]!.AsArray()) {
            var actual = Grooves.FromNotes(
                "grv_test",
                (string)e!["name"]!,
                e["notes"].Deserialize(ContractsJsonContext.Default.IReadOnlyListNote)!,
                (long)e["startTick"]!,
                (long)e["lengthTicks"]!,
                (long)e["stepTicks"]!
            );
            Same(e["expected"].Deserialize(ContractsJsonContext.Default.GrooveTemplate)!, actual);
            count++;
        }

        foreach (var e in document["targets"]!.AsArray()) {
            var (tick, scale) =
                Grooves.Target(grooves[(string)e!["grooveId"]!], (long)e["tick"]!, (double)e["strength"]!);
            Assert.Equal((long)e["expectedTick"]!, tick);
            Assert.Equal((double)e["expectedVelocityScale"]!, scale, 1e-12);
            count++;
        }

        foreach (var e in document["quantize"]!.AsArray()) {
            var actual = Grooves.Quantize(
                e!["notes"].Deserialize(ContractsJsonContext.Default.IReadOnlyListNote)!,
                (long)e["regionStartTick"]!,
                e["groove"].Deserialize(ContractsJsonContext.Default.GrooveTemplate)!,
                (double)e["strength"]!,
                (bool)e["velocity"]!
            );
            Assert.Equal(e["expected"].Deserialize(ContractsJsonContext.Default.IReadOnlyListNote)!, actual);
            count++;
        }

        Assert.True(count >= 30, $"{count} cases");
    }

    static void Same(GrooveTemplate expected, GrooveTemplate actual) {
        Assert.Equal((expected.LengthTicks, expected.StepTicks), (actual.LengthTicks, actual.StepTicks));
        Assert.Equal(expected.Points.Count, actual.Points.Count);
        for (var i = 0; i < expected.Points.Count; i++) {
            Assert.Equal(expected.Points[i].Tick, actual.Points[i].Tick);
            Assert.Equal(expected.Points[i].VelocityScale, actual.Points[i].VelocityScale, 1e-12);
        }
    }

    static JsonNode Node(GrooveTemplate groove) =>
        JsonSerializer.SerializeToNode(groove, ContractsJsonContext.Default.GrooveTemplate)!;

    static JsonArray Nodes(IEnumerable<Note> notes) =>
        [..notes.Select(static n => JsonSerializer.SerializeToNode(n, ContractsJsonContext.Default.Note))];

    static string VectorsPath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "GrooveVectors.json");
}
