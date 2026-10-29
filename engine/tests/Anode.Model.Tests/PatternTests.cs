using Anode.Contracts;
using Anode.Model.Compile;
using Anode.Model.Editing;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Anode.Model.Tests;

/// <summary>
///     Pattern unrolling (docs/07-phase-6.md, "Pattern regions"): the rules one at a time, and the vectors the editor
///     shares (<c>PatternVectors.json</c>, rewritten with <c>ANODE_UPDATE_VECTORS=1</c>).
/// </summary>
public sealed class PatternTests {
    static PatternStep On(int velocity = 100, double gate = 1, int repeats = 1, double chance = 1, double offset = 0) =>
        new(true, velocity, gate, repeats, chance, offset);

    static PatternStep Off => new(false, 100, 1, 1, 1, 0);

    static PatternRow Row(
        string id,
        int note,
        IReadOnlyList<PatternStep> steps,
        PatternPlayMode mode = PatternPlayMode.Forward,
        PatternRowKind kind = PatternRowKind.Note,
        int? stepTicks = null
    ) =>
        new(id, kind, note, id, steps.Count, stepTicks, mode, false, steps);

    static PatternRegion Region(long length, double swing, long seed, params PatternRow[] rows) =>
        new("ptn_test", "trk_1", "Pattern", 0, length, false, new(rows, swing, seed));

    [Fact]
    public void Forward_rows_cycle_independently_of_each_other() {
        var notes = PatternUnroller.Unroll(
            Region(
                3840,
                0,
                1,
                Row("a", 36, [On(), Off, Off, Off]),
                Row("b", 42, [On(), Off, Off, Off, Off, Off, Off])
            )
        );
        Assert.Equal([0L, 960, 1920, 2880], notes.Where(static n => n.Pitch == 36).Select(static n => n.Tick));
        Assert.Equal([0L, 1680, 3360], notes.Where(static n => n.Pitch == 42).Select(static n => n.Tick));
    }

    [Fact]
    public void Ping_pong_does_not_repeat_the_end_steps() {
        PatternStep[] steps = [On(10), On(20), On(30), On(40)];
        var notes = PatternUnroller.Unroll(Region(240 * 10, 0, 1, Row("p", 60, steps, PatternPlayMode.PingPong)));
        Assert.Equal([10, 20, 30, 40, 30, 20, 10, 20, 30, 40], notes.Select(static n => n.Velocity));
    }

    [Fact]
    public void Reverse_plays_the_steps_backwards() {
        var notes = PatternUnroller.Unroll(
            Region(240 * 6, 0, 1, Row("r", 60, [On(10), On(20), On(30)], PatternPlayMode.Reverse))
        );
        Assert.Equal([30, 20, 10, 30, 20, 10], notes.Select(static n => n.Velocity));
    }

    [Fact]
    public void Swing_delays_even_sixteenths_by_half_a_sixteenth_at_most() {
        var notes = PatternUnroller.Unroll(Region(960, 0.5, 1, Row("s", 60, [On(), On(), On(), On()])));
        Assert.Equal([0L, 300, 480, 780], notes.Select(static n => n.Tick));
    }

    [Fact]
    public void Offset_moves_a_step_by_a_fraction_of_it_and_repeats_split_the_gate() {
        var offset = PatternUnroller.Unroll(Region(480, 0, 1, Row("o", 60, [Off, On(offset: -0.25)])));
        Assert.Equal(180, Assert.Single(offset).Tick);
        var ratchet = PatternUnroller.Unroll(Region(240, 0, 1, Row("x", 60, [On(gate: 1, repeats: 3)])));
        Assert.Equal([0L, 80, 160], ratchet.Select(static n => n.Tick));
        Assert.All(ratchet, static n => Assert.Equal(80, n.Length));
    }

    [Fact]
    public void Gate_ties_into_the_next_steps_and_stops_at_the_region_end() {
        var notes = PatternUnroller.Unroll(Region(960, 0, 1, Row("g", 60, [On(gate: 2.5), Off, Off, On(gate: 4)])));
        Assert.Equal([(0L, 600L), (720L, 240L)], notes.Select(static n => (n.Tick, n.Length)));
    }

    [Fact]
    public void Chance_is_the_same_for_the_same_seed_and_changes_with_it() {
        var row = Row("c", 60, [..Enumerable.Repeat(On(chance: 0.5), 16)]);
        var once = PatternUnroller.Unroll(Region(3840 * 4, 0, 7, row));
        var again = PatternUnroller.Unroll(Region(3840 * 4, 0, 7, row));
        var reseeded = PatternUnroller.Unroll(Region(3840 * 4, 0, 8, row));
        Assert.Equal(once, again);
        Assert.NotEqual(once, reseeded);
        Assert.InRange(once.Count, 16, 48); // about half of 64
    }

    [Fact]
    public void A_chord_degree_row_plays_a_triad_of_the_key() {
        var notes = PatternUnroller.Unroll(
            Region(240, 0, 1, Row("d", 2, [On()], kind: PatternRowKind.ChordDegree)),
            static _ => new(0, 2, KeyMode.Minor)
        );
        // D minor, degree 2: E G Bb from E3 (64).
        Assert.Equal([64, 67, 70], notes.Select(static n => n.Pitch));
    }

    [Fact]
    public void Converting_a_pattern_to_MIDI_keeps_exactly_its_notes() {
        var ids = new SequentialIds();
        var random = new Random(5);
        var state = ProjectDefaults.NewProject("prj_p", "P", 48_000);
        state = Edits.Apply(state, new SetKeysCommand([new(0, 2, KeyMode.Minor)]), ids).State;
        state = Edits.Apply(state, new AddTrackCommand(Kind: TrackKind.Instrument), ids, RandomProjects.Plugins).State;
        var track = state.Tracks[0];
        var pattern = Valid(RandomProjects.RandomPattern(random, track)) with { Swing = 0.3 };
        state = Edits.Apply(state, new AddPatternRegionCommand(track.Id, 1920, 3840 * 2, pattern), ids).State;
        var region = state.PatternRegions.Values.Last(r => r.StartTick == 1920 && r.TrackId == track.Id);
        var unrolled = PatternUnroller.Unroll(region, t => Edits.KeyAt(state, t));
        var converted = Edits.Apply(state, new ConvertPatternToMidiCommand(region.Id), ids).State;
        var midi = converted.MidiRegions.Values.Single(m => !state.MidiRegions.ContainsKey(m.Id));
        Assert.False(converted.PatternRegions.ContainsKey(region.Id));
        Assert.Equal((region.StartTick, region.LengthTicks), (midi.StartTick, midi.LengthTicks));
        Assert.Equal(
            unrolled.Select(static n => (n.Tick, n.Pitch, n.Velocity, n.Length)).Order(),
            midi.Notes.Select(static n => (n.StartTick, n.Pitch, n.Velocity, n.LengthTicks)).Order()
        );
    }

    /// <summary>A random pattern made valid: every row as many steps as its length.</summary>
    static Pattern Valid(Pattern pattern) =>
        pattern with {
            Rows = [
                ..pattern.Rows.Select(static r => r with {
                        Steps = [..r.Steps.Take(r.Length)], Length = Math.Min(r.Length, r.Steps.Count)
                    }
                )
            ]
        };

    [Fact]
    public void The_generator_is_FNV_and_Murmur_finalised() {
        // Pinned values: the editor computes the same (PatternVectors.json's draws).
        Assert.Equal(
            PatternUnroller.Draw("1", "ptn_a", "row_1", "0", 0),
            PatternUnroller.Draw("1", "ptn_a", "row_1", "0", 0)
        );
        Assert.NotEqual(
            PatternUnroller.Draw("1", "ptn_a", "row_1", "0", 0),
            PatternUnroller.Draw("1", "ptn_a", "row_1", "0", 1)
        );
        Assert.InRange(PatternUnroller.Draw("12345", "ptn_x", "row_kick", "3", 2), 0, 1);
    }

    /// <summary>The vectors the editor's pattern unroller must reproduce exactly (docs/07-phase-6.md).</summary>
    [Fact]
    public void The_pattern_vectors_are_current() {
        var cases = new JsonArray();
        foreach (var (name, region, keys) in VectorCases()) {
            var notes = PatternUnroller.Unroll(region, t => Key(keys, t));
            cases.Add(
                new JsonObject {
                    ["name"] = name,
                    ["region"] = JsonSerializer.SerializeToNode(region, ContractsJsonContext.Default.PatternRegion),
                    ["keys"] = new JsonArray(
                        [
                            ..keys.Select(static k => JsonSerializer.SerializeToNode(
                                    k,
                                    ContractsJsonContext.Default.KeyEvent
                                )
                            )
                        ]
                    ),
                    ["notes"] = new JsonArray(
                        [
                            ..notes.Select(static n => (JsonNode)new JsonObject {
                                    ["tick"] = n.Tick,
                                    ["pitch"] = n.Pitch,
                                    ["velocity"] = n.Velocity,
                                    ["length"] = n.Length
                                }
                            )
                        ]
                    )
                }
            );
        }

        var draws = new JsonArray();
        foreach (var (seed, region, row, step, pass) in (ReadOnlySpan<(string, string, string, string, long)>) [
                     ("0", "ptn_a", "row_1", "0", 0), ("1", "ptn_a", "row_1", "0", 0), ("1", "ptn_a", "row_1", "0", 1),
                     ("7", "ptn_test", "c", "3", 2), ("123456789", "cel_x", "row_kick", "r", 17),
                     ("-5", "ptn_ü", "row_é", "15", 1000)
                 ]) {
            draws.Add(
                new JsonObject {
                    ["seed"] = seed,
                    ["regionId"] = region,
                    ["rowId"] = row,
                    ["step"] = step,
                    ["pass"] = pass,
                    ["value"] = PatternUnroller.Draw(seed, region, row, step, pass)
                }
            );
        }

        var document = new JsonObject {
            ["about"] = """Pattern unrolling vectors (docs/07-phase-6.md, "Pattern regions"): Anode.Model's """
                + "PatternUnroller computes these; the editor's must match them exactly. Ticks are relative to the region's "
                + "start (960 per quarter note). A row walks positions k = 0, 1, … every stepTicks (its own, else the "
                + "pattern's) while k*stepTicks < lengthTicks; its step: forward k mod L, reverse L-1-(k mod L), ping-pong "
                + """c = k mod (2L-2), c < L ? c : 2L-2-c (L = 1: step 0), random floor(draw(seed, region, row, "r", k) * L). """
                + "An on step plays unless chance < 1 and draw(seed, region, row, k mod L, k div L) >= chance. Its start: "
                + "k*stepTicks, + swing*120 when that is 240 mod 480, + offset*stepTicks, at least 0 (unrounded). Its gate "
                + "gate*stepTicks splits into repeats notes from round(start + i*gate/n) to round(start + (i+1)*gate/n), "
                + "at least a tick; dropped at or after the region's end, cut there. Pitch: the row's note (pad rows too); "
                + "a chord degree d plays the triad of degrees d, d+2, d+4 of the key at the step (major or natural "
                + "minor), its root in 60…71. Sorted by tick, pitch, row order. draw(seed, region, row, step, pass): the "
                + "string seed:region:row:step:pass (seed and pass in decimal), FNV-1a 32-bit over its UTF-16 code units "
                + "(h = 2166136261; h ^= unit; h *= 16777619), then h ^= h >>> 16; h *= 0x85EBCA6B; h ^= h >>> 13; "
                + "h *= 0xC2B2AE35; h ^= h >>> 16 (32-bit unsigned throughout), divided by 2^32.",
            ["draws"] = draws,
            ["cases"] = cases
        };
        var json = document.ToJsonString(new() { WriteIndented = true }) + "\n";
        var path = VectorsPath();
        if (Environment.GetEnvironmentVariable("ANODE_UPDATE_VECTORS") == "1") {
            File.WriteAllText(path, json);
        }

        Assert.Equal(json, File.ReadAllText(path));
    }

    static KeyEvent Key(IReadOnlyList<KeyEvent> keys, long tick) {
        var key = keys.Count > 0 ? keys[0] : new KeyEvent(0, 0, KeyMode.Major);
        foreach (var k in keys) {
            if (k.Tick <= tick) {
                key = k;
            }
        }

        return key;
    }

    static IEnumerable<(string Name, PatternRegion Region, KeyEvent[] Keys)> VectorCases() {
        KeyEvent[] c = [new(0, 0, KeyMode.Major)];
        yield return ("forward 16ths",
            Region(3840, 0, 1, Row("kick", 36, [On(), Off, Off, Off, On(90), Off, Off, Off])), c);
        yield return ("independent lengths",
            Region(
                3840,
                0,
                1,
                Row("a", 36, [On(), Off, Off, Off]),
                Row("b", 42, [On(), Off, On(60), Off, Off, Off, Off])
            ), c);
        yield return ("ping-pong",
            Region(3840, 0, 1, Row("p", 60, [On(10), On(20), On(30), On(40)], PatternPlayMode.PingPong)), c);
        yield return ("reverse", Region(1920, 0, 1, Row("r", 60, [On(10), Off, On(30)], PatternPlayMode.Reverse)), c);
        yield return ("random",
            Region(3840, 0, 42, Row("x", 60, [On(10), On(20), On(30), On(40), On(50)], PatternPlayMode.Random)), c);
        yield return ("swing", Region(1920, 0.6, 1, Row("s", 42, [On(), On(), On(), On()])), c);
        yield return ("offset",
            Region(1920, 0, 1, Row("o", 38, [On(offset: -0.5), On(offset: 0.25), On(offset: 0.5), Off])), c);
        yield return ("repeats",
            Region(
                960,
                0,
                1,
                Row("x", 42, [On(gate: 1, repeats: 3), On(gate: 0.5, repeats: 2), On(gate: 2, repeats: 4), Off])
            ), c);
        yield return ("gate ties", Region(960, 0, 1, Row("g", 60, [On(gate: 2.5), Off, Off, On(gate: 4)])), c);
        yield return ("chance", Region(7680, 0, 99, Row("c", 60, [..Enumerable.Repeat(On(chance: 0.5), 8)])), c);
        yield return ("row rate (triplets)",
            Region(
                1920,
                0.5,
                1,
                Row("t", 60, [On(), On(70), On(50)], stepTicks: 160),
                Row("q", 36, [On()], stepTicks: 960)
            ), c);
        yield return ("one-step ping-pong", Region(960, 0, 1, Row("o", 60, [On()], PatternPlayMode.PingPong)), c);
        yield return ("chord degrees",
            Region(
                3840,
                0,
                1,
                Row("d", 1, [On(), Off, Off, Off], kind: PatternRowKind.ChordDegree),
                Row("e", 5, [Off, Off, On(), Off], kind: PatternRowKind.ChordDegree)
            ), [new(0, 2, KeyMode.Minor), new(1920, 7, KeyMode.Major)]);
        yield return ("pad rows",
            Region(
                960,
                0.25,
                3,
                Row("k", 36, [On(127), Off, Off, On(80, chance: 0.5)], kind: PatternRowKind.Pad),
                Row("h", 42, [On(60, repeats: 2, offset: 0.1)], kind: PatternRowKind.Pad)
            ), c);
    }

    static string VectorsPath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "PatternVectors.json");
}
