using Anode.Contracts;
using Anode.Interchange.MusicXml;
using Anode.Model;
using Anode.Model.Editing;
using Anode.Notation;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Anode.Interchange.Tests.MusicXml;

/// <summary>
///     MusicXML from the real notation engine (docs/10-phase-9.md, integration): the shared vectors
///     (<c>Anode.Notation.Tests/ScoreVectors.json</c>) and A's golden views are written, validated against the 4.0 XSD
///     and read back. A vector's notes come back where the score draws them (display quantise is what the file holds),
///     with its staff style, and render again to the same events.
/// </summary>
public sealed class MusicXmlEngineTests {
    static readonly JsonSerializerOptions Json = new(ContractsJsonContext.Default.Options) {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(ContractsJsonContext.Default, new DefaultJsonTypeInfoResolver())
    };

    static readonly string Shared = Path.Combine(AppContext.BaseDirectory, "MusicXml", "Notation");

    public static TheoryData<string> Vectors() {
        var data = new TheoryData<string>();
        foreach (var c in Document()["cases"]!.AsArray()) {
            data.Add((string)c!["name"]!);
        }

        return data;
    }

    public static TheoryData<string> Goldens() {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(Shared, "Golden"), "*.json")
                     .Order(StringComparer.Ordinal)) {
            data.Add(Path.GetFileNameWithoutExtension(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void A_vector_validates_and_reads_back_as_the_notes_the_score_draws(string name) {
        var c = Document()["cases"]!.AsArray().Single(v => (string?)v!["name"] == name)!;
        var (state, track) = Project(c);
        var part = (bool)c["part"]!;
        var view = NotationEngine.Render(state, state.Score, part ? new(PartTrackId: track) : new());
        var file = MusicXmlWriter.Write(view, false);
        MusicXmlSchema.AssertValid(file);

        var content = MusicXmlReader.Read(file);
        var imported = Assert.Single(content.Tracks);
        Assert.Equal((string)c["staffStyleId"]!, imported.StaffStyleId);
        Assert.Equal(Drawn(view), Notes(imported));

        // Imported into an empty project with the vector's settings, the notes render as the same events.
        var again = Imported(state, content, c);
        var reimported = NotationEngine.Render(
            again,
            again.Score,
            part ? new(PartTrackId: again.Tracks[0].Id) : new()
        );
        Assert.Equal(Events(view), Events(reimported));
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    public void A_golden_view_validates_and_reads_back_with_its_notes_and_score_content(string name) {
        var view = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(Shared, "Golden", $"{name}.json")),
            ContractsJsonContext.Default.ScoreView
        )!;
        foreach (var compressed in new[] { false, true }) {
            var file = MusicXmlWriter.Write(view, compressed);
            MusicXmlSchema.AssertValid(file);
            var content = MusicXmlReader.Read(file);
            Assert.Equal(view.Parts.Count, content.Tracks.Count);
            Assert.Equal(view.Parts.Select(static p => p.Name), content.Tracks.Select(static t => t.Name));
            Assert.Equal(
                view.Parts.Select(static p => p.StaffStyleId),
                content.Tracks.Select(static t => t.StaffStyleId)
            );
            for (var p = 0; p < view.Parts.Count; p++) {
                Assert.Equal(Drawn(view, p), Notes(content.Tracks[p]));
            }

            var score = content.Score;
            Assert.Equal(view.Lyrics.Count, score?.Lyrics.Count ?? 0);
            Assert.Equal(view.Chords.Count, score?.Chords.Count ?? 0);
            // Rehearsal symbols come back as markers (docs/interchange/musicxml.md, "What is lost").
            Assert.Equal(
                view.Symbols.Select(static s => s.Kind).Where(static k => k != ScoreSymbolKind.Rehearsal).Order(),
                (score?.Symbols ?? []).Select(static s => s.Kind).Order()
            );
            foreach (var rehearsal in view.Symbols.Where(static s => s.Kind == ScoreSymbolKind.Rehearsal)) {
                Assert.Contains(content.Markers ?? [], m => m.Name == rehearsal.Text);
            }
        }
    }

    [Fact]
    public void Every_factory_style_comes_back_as_itself() {
        foreach (var style in FactoryStaffStyles.All) {
            var state = ProjectDefaults.NewProject("prj_test", "Test", 48_000);
            var ids = new SequentialIds();
            state = Edits.Apply(state, new AddTrackCommand(style.Name, Kind: TrackKind.Midi), ids).State;
            var track = state.Tracks[^1].Id;
            state = Edits.Apply(state, new AssignStaffStyleCommand([track], style.Id), ids).State;
            state = Edits.Apply(state, new AddMidiRegionCommand(track, 0, 3840), ids).State;
            var region = state.MidiRegions.Values.Single().Id;
            var pitch = style.DrumMap is { } map ? map[0].Pitch : 60;
            state = Edits.Apply(state, new AddNotesCommand(region, [new(pitch, 90, 1, 0, 960)]), ids).State;
            foreach (var request in new ScoreRequest[] { new(), new(PartTrackId: track) }) {
                var file = MusicXmlWriter.Write(NotationEngine.Render(state, state.Score, request), false);
                MusicXmlSchema.AssertValid(file);
                var imported = Assert.Single(MusicXmlReader.Read(file).Tracks);
                Assert.Equal(style.Id, imported.StaffStyleId);
                Assert.Equal(pitch, Assert.Single(imported.MidiRegions.Single().Notes).Pitch);
            }
        }
    }

    static JsonNode Document() => JsonNode.Parse(File.ReadAllText(Path.Combine(Shared, "ScoreVectors.json")))!;

    /// <summary>A vector's project: its signatures and keys, one MIDI track of its staff style, its region of notes.</summary>
    static (ProjectState State, string Track) Project(JsonNode c) {
        var state = ProjectDefaults.NewProject("prj_test", "Test", 48_000);
        var ids = new SequentialIds();
        var signatures = c["signatures"].Deserialize<SignatureEvent[]>(Json)!;
        var keys = c["keys"].Deserialize<KeyEvent[]>(Json)!;
        state = Edits.Apply(state, new SetSignaturesCommand(signatures), ids).State;
        state = Edits.Apply(state, new SetKeysCommand(keys), ids).State;
        state = Edits.Apply(state, new AddTrackCommand("Part", Kind: TrackKind.Midi), ids).State;
        var track = state.Tracks[^1].Id;
        state = Edits.Apply(state, new AssignStaffStyleCommand([track], (string)c["staffStyleId"]!), ids).State;
        state = Edits.Apply(state, new AddMidiRegionCommand(track, 0, (long)c["regionLengthTicks"]!), ids).State;
        var region = state.MidiRegions.Values.Single();
        var notes = c["notes"]!.AsArray()
            .Select(static n => new Note(
                    (long)n!["id"]!,
                    (int)n["pitch"]!,
                    80,
                    1,
                    (long)n["startTick"]!,
                    (long)n["lengthTicks"]!,
                    0
                )
            );
        state = state with {
            MidiRegions = state.MidiRegions.SetItem(
                region.Id,
                region with { Notes = Edits.Sorted([..notes]), Notation = Notation(c) }
            )
        };
        return (state, track);
    }

    static RegionNotation Notation(JsonNode c) => c["notation"].Deserialize<RegionNotation>(Json)!;

    /// <summary>The content imported into a new project with the vector's meter and key, its region at the vector's settings.</summary>
    static ProjectState Imported(ProjectState original, ImportedContent content, JsonNode c) {
        var ids = new SequentialIds();
        var state = ProjectDefaults.NewProject("prj_again", "Again", 48_000);
        state = Edits.Apply(state, new SetSignaturesCommand(original.Settings.Signatures), ids).State;
        state = Edits.Apply(state, new SetKeysCommand(original.Settings.Keys), ids).State;
        state = Edits.Apply(
                state,
                new ImportTracksCommand(content with { TempoEvents = null, Signatures = null, Keys = null }),
                ids
            )
            .State;
        var notation = Notation(c);
        return Edits.Apply(
                state,
                new SetRegionNotationCommand(
                    [..state.MidiRegions.Keys],
                    notation.DisplayQuantizeTicks,
                    notation.TripletQuantizeTicks,
                    notation.Interpretation,
                    notation.NoOverlap,
                    notation.Syncopation,
                    notation.MaxDots
                ),
                ids
            )
            .State;
    }

    /// <summary>Every note of a part as drawn: concert pitch, the start of its first segment, to the end of its last.</summary>
    static List<(long Start, long Length, int Pitch)> Drawn(ScoreView view, int part = 0) {
        var spans = new Dictionary<long, (long Start, long End, int Pitch)>();
        foreach (var measure in view.Parts[part].Measures) {
            foreach (var e in measure.Staves.SelectMany(static s => s.Voices).SelectMany(static v => v.Events)) {
                foreach (var n in e.Notes) {
                    var end = e.Tick + e.DurationTicks;
                    spans[n.NoteId] = spans.TryGetValue(n.NoteId, out var s)
                        ? (Math.Min(s.Start, e.Tick), Math.Max(s.End, end), n.Pitch)
                        : (e.Tick, end, n.Pitch);
                }
            }
        }

        return [..spans.Values.Select(static s => (s.Start, s.End - s.Start, s.Pitch)).Order()];
    }

    static List<(long Start, long Length, int Pitch)> Notes(ImportedTrack track) => [
        ..track.MidiRegions
            .SelectMany(static r => r.Notes.Select(n => (r.StartTick + n.StartTick, n.LengthTicks, n.Pitch)))
            .Order()
    ];

    /// <summary>Every event as the score draws it, without ids: measure, staff, voice, kind, timing, value, pitches, ties.</summary>
    static List<string> Events(ScoreView view) {
        var events = new List<string>();
        var part = view.Parts[0];
        for (var m = 0; m < part.Measures.Count; m++) {
            for (var s = 0; s < part.Measures[m].Staves.Count; s++) {
                foreach (var voice in part.Measures[m].Staves[s].Voices) {
                    foreach (var e in voice.Events) {
                        var notes = string.Join(
                            '+',
                            e.Notes.Select(static n =>
                                $"{n.Pitch}{n.Written.Step}{n.Written.Alter}{n.Written.Octave}{(n.TieStart ? "~" : string.Empty)}{(n.TieStop ? "^" : string.Empty)}"
                            )
                        );
                        events.Add(
                            $"m{m + 1} s{s} v{voice.Number} {e.Kind} {e.Tick}+{e.DurationTicks} {e.Duration.Value}{new string('.', e.Duration.Dots)}{(e.Duration.Tuplet is null ? string.Empty : "t")} {notes}"
                        );
                    }
                }
            }
        }

        return events;
    }
}
