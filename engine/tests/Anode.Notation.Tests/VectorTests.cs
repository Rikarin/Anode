using Anode.Contracts;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using static Anode.Notation.Tests.TestProject;

namespace Anode.Notation.Tests;

/// <summary>
///     The shared vectors (<c>ScoreVectors.json</c>, docs/notation/README.md, "Vectors"): MIDI notes and settings in,
///     the expected measures and events out, for workstreams C and D. The engine must give every case's events exactly;
///     <c>ANODE_UPDATE_VECTORS=1</c> rewrites the file from the cases below (review the diff).
/// </summary>
public sealed class VectorTests {
    const int Q = 960;
    const int Bar = 3840;

    static readonly JsonSerializerOptions Json = new(ContractsJsonContext.Default.Options) {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(ContractsJsonContext.Default, new DefaultJsonTypeInfoResolver())
    };

    static readonly JsonSerializerOptions Indented = new(Json) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static readonly SignatureEvent[] FourFour = [new(0, 4, 4)];
    static readonly KeyEvent[] CMajor = [new(0, 0, KeyMode.Major)];

    sealed record Case(
        string Name,
        string Description,
        string StaffStyleId,
        bool Part,
        SignatureEvent[] Signatures,
        KeyEvent[] Keys,
        RegionNotation Notation,
        Note[] Notes
    );

    static Case[] Cases() => [
        new("quarters", "Four quarter notes in 4/4.", StaffStyleIds.Treble, false, FourFour, CMajor, RegionNotation.Default, [N(1, 60, 0, Q), N(2, 62, Q, Q), N(3, 64, 2 * Q, Q), N(4, 65, 3 * Q, Q)]),
        new("tie-across-bar", "A half note from beat 4 tied over the bar line; rests by the meter.", StaffStyleIds.Treble, false, FourFour, CMajor, RegionNotation.Default, [N(1, 72, 3 * Q, 2 * Q)]),
        new("dotted", "Dotted quarter, eighth, half.", StaffStyleIds.Treble, false, FourFour, CMajor, RegionNotation.Default, [N(1, 60, 0, 1440), N(2, 62, 1440, 480), N(3, 64, 2 * Q, 2 * Q)]),
        new("no-dots", "The dotted quarter with max dots 0: quarter tied to an eighth.", StaffStyleIds.Treble, false, FourFour, CMajor, RegionNotation.Default with { MaxDots = 0 }, [N(1, 60, 0, 1440), N(2, 62, 1440, 480), N(3, 64, 2 * Q, 2 * Q)]),
        new("syncopation-off", "Eighth, quarter, eighth: the quarter tied at the beat; the half bar beamed.", StaffStyleIds.Treble, false, FourFour, CMajor, RegionNotation.Default, [N(1, 60, 0, 480), N(2, 62, 480, Q), N(3, 64, 1440, 480), N(4, 65, 2 * Q, 2 * Q)]),
        new("syncopation-on", "The same with syncopation shown: the quarter undivided.", StaffStyleIds.Treble, false, FourFour, CMajor, RegionNotation.Default with { Syncopation = true }, [N(1, 60, 0, 480), N(2, 62, 480, Q), N(3, 64, 1440, 480), N(4, 65, 2 * Q, 2 * Q)]),
        new("interpretation-on", "Short notes on the beats read as quarters.", StaffStyleIds.Treble, false, FourFour, CMajor, RegionNotation.Default, [N(1, 60, 0, 200), N(2, 62, Q, 200), N(3, 64, 2 * Q, 200), N(4, 65, 3 * Q, 200)]),
        new("interpretation-off", "The same without interpretation: sixteenths and rests.", StaffStyleIds.Treble, false, FourFour, CMajor, RegionNotation.Default with { Interpretation = false }, [N(1, 60, 0, 200), N(2, 62, Q, 200), N(3, 64, 2 * Q, 200), N(4, 65, 3 * Q, 200)]),
        new("off-grid", "Eighths and a half note slightly off the 1/16 grid.", StaffStyleIds.Treble, false, FourFour, CMajor, RegionNotation.Default, [N(1, 60, 15, 450), N(2, 62, 470, 500), N(3, 64, 975, 440), N(4, 65, 1430, 470), N(5, 67, 1910, 1900)]),
        new("triplets", "Eighth triplets (one group) and sixteenth triplets (two groups).", StaffStyleIds.Treble, false, FourFour, CMajor, RegionNotation.Default, [N(1, 60, 0, 320), N(2, 62, 320, 320), N(3, 64, 640, 320), N(4, 65, 960, 160), N(5, 67, 1120, 160), N(6, 69, 1280, 160), N(7, 71, 1440, 160), N(8, 72, 1600, 160), N(9, 74, 1760, 160)]),
        new("triplet-quarter-eighth", "A triplet quarter and eighth under one bracket.", StaffStyleIds.Treble, false, FourFour, CMajor, RegionNotation.Default, [N(1, 60, 0, 640), N(2, 62, 640, 320)]),
        new("two-voices", "A held half note over two quarters: voice 1 up, voice 2 down.", StaffStyleIds.Treble, false, FourFour, CMajor, RegionNotation.Default, [N(1, 76, 0, 2 * Q), N(2, 72, 0, Q), N(3, 74, Q, Q)]),
        new("piano-split", "Chords on both staves of the piano (split at C3 = 60).", StaffStyleIds.Piano, false, FourFour, CMajor, RegionNotation.Default, [N(1, 60, 0, 2 * Q), N(2, 64, 0, 2 * Q), N(3, 59, 0, 2 * Q), N(4, 55, 0, 2 * Q), N(5, 48, 2 * Q, 2 * Q)]),
        new("clarinet-part", "A B♭ clarinet part: written a major second up, in D major.", StaffStyleIds.ClarinetBb, true, FourFour, CMajor, RegionNotation.Default, [N(1, 60, 0, Q), N(2, 66, Q, Q), N(3, 70, 2 * Q, 2 * Q)]),
        new("key-change", "G major then F major: accidentals carried in the bar and cancelled.", StaffStyleIds.Treble, false, FourFour, [new(0, 7, KeyMode.Major), new(Bar, 5, KeyMode.Major)], RegionNotation.Default, [N(1, 66, 0, Q), N(2, 65, Q, Q), N(3, 65, 2 * Q, Q), N(4, 66, 3 * Q, Q), N(5, 70, Bar, Q), N(6, 71, Bar + Q, Q), N(7, 70, Bar + 2 * Q, Q), N(8, 61, Bar + 3 * Q, Q)]),
        new("compound", "6/8: dotted quarter, three beamed eighths; quarter, eighth, dotted quarter; dotted half.", StaffStyleIds.Treble, false, [new(0, 6, 8)], CMajor, RegionNotation.Default, [N(1, 60, 0, 1440), N(2, 62, 1440, 480), N(3, 64, 1920, 480), N(4, 65, 2400, 480), N(5, 67, 2880, 960), N(6, 69, 3840, 480), N(7, 71, 4320, 1440), N(8, 72, 5760, 2880)]),
        new("meter-changes", "4/4, 6/8, 3/4, 5/8 and 2/2 with ties over their bar lines.", StaffStyleIds.Treble, false, [new(0, 4, 4), new(Bar, 6, 8), new(Bar + 2880, 3, 4), new(Bar + 2 * 2880, 5, 8), new(Bar + 2 * 2880 + 2400, 2, 2)], CMajor, RegionNotation.Default, [N(1, 60, 2 * Q, 3 * Q), N(2, 62, Bar + 1440, 480), N(3, 64, Bar + 1920, 1920), N(4, 65, Bar + 2880 + Q, 2880), N(5, 67, Bar + 2 * 2880 + 2400, Bar)]),
        new("drums", "Kick, snare and hi-hats on the drum map: positions, noteheads, stems up.", StaffStyleIds.Drums, false, FourFour, CMajor, RegionNotation.Default, [N(1, 36, 0, 120), N(2, 42, 0, 120), N(3, 42, 480, 120), N(4, 38, 960, 120), N(5, 42, 960, 120), N(6, 46, 1440, 120)]),
        new("exit-criterion", "The exit criterion's step 1 region (docs/10-phase-9.md) at 1/16 with the 1/16-triplet grid.", StaffStyleIds.Piano, false, FourFour, CMajor, Grid(240, 160), [..Tests.Cases.ExitCriterion().State.MidiRegions.Values.Single().Notes]),
        new("exit-criterion-eighths", "The same at 1/8 with the 1/8-triplet grid (Logic's \"8,12\").", StaffStyleIds.Piano, false, FourFour, CMajor, Grid(480, 320), [..Tests.Cases.ExitCriterion().State.MidiRegions.Values.Single().Notes])
    ];

    [Fact]
    public void The_engine_gives_the_vectors_events() {
        var actual = Document();
        var path = Path();
        if (Environment.GetEnvironmentVariable("ANODE_UPDATE_VECTORS") == "1") {
            File.WriteAllText(path, actual.ToJsonString(Indented) + "\n");
            return;
        }

        var expected = JsonNode.Parse(File.ReadAllText(path))!;
        var expectedCases = expected["cases"]!.AsArray();
        var actualCases = actual["cases"]!.AsArray();
        Assert.Equal(expectedCases.Select(c => (string?)c!["name"]), actualCases.Select(c => (string?)c!["name"]));
        for (var i = 0; i < actualCases.Count; i++) {
            Assert.True(
                JsonNode.DeepEquals(expectedCases[i], actualCases[i]),
                $"Vector {actualCases[i]!["name"]} differs:\n{actualCases[i]!["expected"]!.ToJsonString(Indented)}"
            );
        }
    }

    [Fact]
    public void Every_vector_note_is_drawn_and_every_measure_filled() {
        foreach (var c in Document()["cases"]!.AsArray()) {
            var notes = c!["notes"]!.AsArray().Select(n => (long)n!["id"]!).ToHashSet();
            var events = c["expected"]!["events"]!.AsArray();
            Assert.Equal(notes, events.SelectMany(e => e!["notes"]!.AsArray()).Where(n => !(bool)n!["tieStop"]!).Select(n => (long)n!["noteId"]!).ToHashSet());
            foreach (var measure in c["expected"]!["measures"]!.AsArray()) {
                foreach (var voice in events.Where(e => (int)e!["measure"]! == (int)measure!["number"]!).GroupBy(e => ((int)e!["staff"]!, (int)e["voice"]!))) {
                    Assert.Equal((long)measure!["lengthTicks"]!, voice.Sum(e => (long)e!["durationTicks"]!));
                }
            }
        }
    }

    static JsonObject Document() {
        var cases = new JsonArray();
        foreach (var c in Cases()) {
            cases.Add(Vector(c));
        }

        return new() {
            ["description"] =
                "Anode notation vectors (docs/notation/README.md, \"Vectors\"): MIDI notes of one region at tick 0 on one track with a staff style and region notation settings in; the measures and every voice's events out, as the notation engine renders them. Ticks are 960 PPQ project ticks; written pitches use scientific octaves (C4 is MIDI 60); staff positions are diatonic steps above the bottom line (MEI loc); voices count from 1 within a staff, staves from 0. A case with part true is rendered as the track's extracted part (written at transposed pitch); otherwise as the full score at concert pitch.",
            ["ppq"] = 960,
            ["cases"] = cases
        };
    }

    static JsonObject Vector(Case c) {
        var project = new TestProject();
        project.Signatures(c.Signatures).Keys(c.Keys);
        var track = project.Track("T", c.StaffStyleId);
        var length = Bar * 8L;
        project.Region(track, 0, length, c.Notation, c.Notes);
        var view = NotationEngine.Render(project.State, ProjectScore.Empty, c.Part ? new(PartTrackId: track) : new());
        var events = new JsonArray();
        var part = view.Parts[0];
        for (var m = 0; m < view.Measures.Count; m++) {
            for (var s = 0; s < part.Staves.Count; s++) {
                foreach (var voice in part.Measures[m].Staves[s].Voices) {
                    foreach (var e in voice.Events) {
                        events.Add(Event(view.Measures[m].Number, s, voice.Number, e));
                    }
                }
            }
        }

        return new() {
            ["name"] = c.Name,
            ["description"] = c.Description,
            ["staffStyleId"] = c.StaffStyleId,
            ["part"] = c.Part,
            ["signatures"] = JsonSerializer.SerializeToNode(c.Signatures, Json),
            ["keys"] = JsonSerializer.SerializeToNode(c.Keys, Json),
            ["notation"] = JsonSerializer.SerializeToNode(c.Notation, Json),
            ["regionLengthTicks"] = length,
            ["notes"] = new JsonArray([..c.Notes.OrderBy(n => n.Id).Select(n => (JsonNode)new JsonObject { ["id"] = n.Id, ["pitch"] = n.Pitch, ["startTick"] = n.StartTick, ["lengthTicks"] = n.LengthTicks })]),
            ["expected"] = new JsonObject {
                ["transposing"] = view.Transposing,
                ["transpose"] = part.Transpose,
                ["measures"] = new JsonArray([
                    ..view.Measures.Select(m => (JsonNode)new JsonObject {
                        ["number"] = m.Number,
                        ["startTick"] = m.StartTick,
                        ["lengthTicks"] = m.LengthTicks,
                        ["time"] = JsonSerializer.SerializeToNode(m.Time, Json),
                        ["key"] = JsonSerializer.SerializeToNode(m.Key, Json),
                        ["barline"] = JsonSerializer.SerializeToNode(m.Barline, Json)
                    })
                ]),
                ["events"] = events
            }
        };
    }

    static JsonObject Event(int measure, int staff, int voice, ScoreEvent e) =>
        new() {
            ["measure"] = measure,
            ["staff"] = staff,
            ["voice"] = voice,
            ["id"] = e.Id,
            ["kind"] = JsonSerializer.SerializeToNode(e.Kind, Json),
            ["tick"] = e.Tick,
            ["durationTicks"] = e.DurationTicks,
            ["value"] = JsonSerializer.SerializeToNode(e.Duration.Value, Json),
            ["dots"] = e.Duration.Dots,
            ["tuplet"] = e.Duration.Tuplet is { } t ? JsonSerializer.SerializeToNode(t, Json) : null,
            ["beam"] = e.Beam,
            ["stem"] = JsonSerializer.SerializeToNode(e.Stem, Json),
            ["notes"] = new JsonArray([
                ..e.Notes.Select(n => (JsonNode)new JsonObject {
                    ["id"] = n.Id,
                    ["noteId"] = n.NoteId,
                    ["pitch"] = n.Pitch,
                    ["written"] = JsonSerializer.SerializeToNode(n.Written, Json),
                    ["staffPosition"] = n.StaffPosition,
                    ["accidental"] = n.Accidental is { } a ? JsonSerializer.SerializeToNode(a, Json) : null,
                    ["tieStart"] = n.TieStart,
                    ["tieStop"] = n.TieStop,
                    ["notehead"] = JsonSerializer.SerializeToNode(n.Notehead, Json)
                })
            ])
        };

    static string Path([CallerFilePath] string source = "") => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, "ScoreVectors.json");
}
