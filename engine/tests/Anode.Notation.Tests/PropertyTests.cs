using Anode.Contracts;
using Anode.Model;
using CsCheck;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Anode.Notation.Tests;

/// <summary>
///     Properties of the notation engine over random projects (meters and their changes, keys, staff styles, region
///     settings with odd grids, notes on and off the grid, overlapping, across bar lines): every note appears exactly
///     once; each voice of each measure is filled exactly by its events; a note's tied segments are contiguous, chained
///     and sum to its displayed length; written values match durations; beams, tuplets and voices are well formed; the
///     MEI has every element; the same project gives the same view.
/// </summary>
public sealed class PropertyTests {
    static readonly (int Numerator, int Denominator)[] Meters = [(4, 4), (3, 4), (2, 4), (2, 2), (6, 8), (9, 8), (12, 8), (3, 8), (5, 8), (7, 8), (5, 4), (6, 4), (3, 16)];

    static readonly JsonSerializerOptions Json = new(ContractsJsonContext.Default.Options) {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(ContractsJsonContext.Default, new DefaultJsonTypeInfoResolver())
    };

    static readonly Gen<RegionNotation> Notation = Gen.Select(
        Gen.OneOfConst(0, 0, 120, 240, 480, 960, 100, 333),
        Gen.OneOfConst(0, 160, 320, 80, 100, 640),
        Gen.Bool,
        Gen.Bool,
        Gen.Bool,
        Gen.Int[0, 3],
        (straight, triplet, interpretation, noOverlap, syncopation, dots) =>
            new RegionNotation(straight, triplet, interpretation, noOverlap, syncopation, dots)
    );

    static readonly Gen<(int Pitch, int Start, int Length)> NoteGen = Gen.Select(
        Gen.Int[21, 108],
        Gen.OneOf(Gen.Int[0, 15_360], Gen.Int[0, 64].Select(i => i * 240), Gen.Int[0, 96].Select(i => i * 160), Gen.Select(Gen.Int[0, 64], Gen.Int[-40, 40], (i, j) => Math.Max(0, i * 240 + j))),
        Gen.OneOf(Gen.Int[1, 4000], Gen.OneOfConst(240, 480, 960, 1440, 1920, 3840, 160, 320, 640))
    );

    static readonly Gen<RandomRegion> RegionGen = Gen.Select(Gen.Int[0, 7680], Gen.Int[1, 19_200], Notation, NoteGen.Array[0, 24], (start, length, notation, notes) => new RandomRegion(start, length, notation, notes));

    static readonly Gen<RandomScore> ScoreGen = Gen.Select(
        Gen.Select(Gen.Int[0, Meters.Length - 1], Gen.Int[1, 3]).Array[1, 3],
        Gen.Select(Gen.Int[0, 11], Gen.Bool, Gen.Int[0, 15_360]).Array[1, 2],
        Gen.Select(Gen.Int[0, StaffStyleIds.All.Count - 1], RegionGen.Array[1, 2]).Array[1, 2],
        Gen.Bool,
        (meters, keys, tracks, transposing) => new RandomScore(meters, keys, tracks, transposing)
    );

    [Fact]
    public void Every_note_appears_exactly_once_and_its_tied_segments_sum_to_its_displayed_length() =>
        ScoreGen.Sample(score => {
            var state = score.Build();
            var view = Render(state, score);
            var notes = view.Parts.SelectMany(p => p.Measures)
                .SelectMany(m => m.Staves)
                .SelectMany(s => s.Voices)
                .SelectMany(v => v.Events.SelectMany(e => e.Notes.Select(n => (Event: e, Note: n))))
                .GroupBy(x => x.Note.NoteId)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Event.Tick).ToList());
            var expected = Playable(state).ToList();
            Assert.Equal(expected.Order(), notes.Keys.Order());
            foreach (var (id, segments) in notes) {
                Assert.Single(segments, s => !s.Note.TieStop);
                for (var k = 0; k < segments.Count; k++) {
                    var (e, n) = segments[k];
                    Assert.Equal(ScoreIds.Note(id, k + 1), n.Id);
                    Assert.Equal(k > 0, n.TieStop);
                    Assert.Equal(k < segments.Count - 1, n.TieStart);
                    if (k > 0) {
                        Assert.Equal(segments[k - 1].Event.Tick + segments[k - 1].Event.DurationTicks, e.Tick);
                    }
                }

                var displayed = segments[^1].Event.Tick + segments[^1].Event.DurationTicks - segments[0].Event.Tick;
                Assert.Equal(displayed, segments.Sum(s => s.Event.DurationTicks));
                Assert.True(displayed > 0);
            }
        });

    [Fact]
    public void Each_voice_of_each_measure_is_filled_by_its_events_with_matching_values() =>
        ScoreGen.Sample(score => {
            var view = Render(score.Build(), score);
            foreach (var part in view.Parts) {
                Assert.Equal(view.Measures.Count, part.Measures.Count);
                for (var m = 0; m < view.Measures.Count; m++) {
                    var measure = view.Measures[m];
                    for (var s = 0; s < part.Staves.Count; s++) {
                        var voices = part.Measures[m].Staves[s].Voices;
                        Assert.Equal(1, voices[0].Number);
                        Assert.True(voices.Count <= Math.Max(1, part.Staves[s].Voices));
                        Assert.Equal(voices.Select(v => v.Number).Order(), voices.Select(v => v.Number));
                        foreach (var voice in voices) {
                            var at = measure.StartTick;
                            foreach (var e in voice.Events) {
                                Assert.Equal(at, e.Tick);
                                Assert.True(e.DurationTicks > 0);
                                at += e.DurationTicks;
                                if (!(voice.Events.Count == 1 && e.Kind == ScoreEventKind.Rest)) {
                                    Assert.Equal(Ticks(e.Duration), e.DurationTicks);
                                }

                                Assert.Equal(e.Kind == ScoreEventKind.Rest, e.Notes.Count == 0);
                                Assert.Equal(e.Kind == ScoreEventKind.Chord, e.Notes.Count > 1);
                                Assert.Equal(e.Notes.OrderBy(n => n.StaffPosition), e.Notes);
                            }

                            Assert.Equal(measure.StartTick + measure.LengthTicks, at);
                            Beams(voice.Events);
                            Tuplets(voice.Events);
                        }
                    }
                }
            }
        });

    [Fact]
    public void The_MEI_has_every_element_and_the_view_is_deterministic() =>
        ScoreGen.Sample(score => {
            var state = score.Build();
            var view = Render(state, score);
            MeiTests.Structure(view);
            Assert.Equal(JsonSerializer.Serialize(view, Json), JsonSerializer.Serialize(Render(state, score), Json));
        }, iter: 60);

    [Fact]
    public void Random_scores_are_valid_MEI_5() =>
        ScoreGen.Sample(score => MeiTests.Valid(Render(score.Build(), score)), iter: 12);

    static ScoreView Render(ProjectState state, RandomScore score) =>
        NotationEngine.Render(
            state,
            ProjectScore.Empty with { Layout = ScoreLayout.Default with { TransposingScore = score.Transposing } },
            new()
        );

    /// <summary>The notes that sound: inside their region.</summary>
    static IEnumerable<long> Playable(ProjectState state) =>
        state.MidiRegions.Values.SelectMany(r => r.Notes.Where(n => n.StartTick >= 0 && n.StartTick < r.LengthTicks && n.LengthTicks > 0).Select(n => n.Id));

    /// <summary>What a written duration lasts: the value with its dots, two thirds of it in a triplet.</summary>
    static long Ticks(ScoreDuration duration) {
        var value = 7680L >> (int)duration.Value;
        var total = value * ((1L << (duration.Dots + 1)) - 1) / (1L << duration.Dots);
        return duration.Tuplet is { } tuplet ? total * tuplet.Normal / tuplet.Actual : total;
    }

    static void Beams(IReadOnlyList<ScoreEvent> events) {
        foreach (var group in events.Where(e => e.Beam is not null).GroupBy(e => e.Beam)) {
            var members = group.ToList();
            Assert.True(members.Count >= 2);
            var first = events.ToList().IndexOf(members[0]);
            Assert.Equal(members, events.Skip(first).Take(members.Count));
            Assert.All(members, e => Assert.True(e.Notes.Count > 0 && e.Duration.Value >= NoteValue.Eighth));
            Assert.Single(members.Select(e => e.Duration.Tuplet?.Group).Distinct());
        }
    }

    static void Tuplets(IReadOnlyList<ScoreEvent> events) {
        foreach (var group in events.Where(e => e.Duration.Tuplet is not null).GroupBy(e => e.Duration.Tuplet!.Group)) {
            var members = group.ToList();
            var first = events.ToList().IndexOf(members[0]);
            Assert.Equal(members, events.Skip(first).Take(members.Count));
            Assert.True(members.Count >= 2);
            var span = members.Sum(e => e.DurationTicks);
            Assert.True(span % 3 == 0 && Meter.IsPlainValue(span / 3 * 3 / 2), $"A triplet group of {span} ticks.");
        }
    }

    sealed record RandomRegion(int Start, int Length, RegionNotation Notation, (int Pitch, int Start, int Length)[] Notes);

    sealed record RandomScore((int Meter, int Bars)[] Meters, (int Tonic, bool Minor, int Tick)[] Keys, (int Style, RandomRegion[] Regions)[] Tracks, bool Transposing) {
        public override string ToString() =>
            $"meters {string.Join(' ', Meters.Select(m => $"{PropertyTests.Meters[m.Meter]}x{m.Bars}"))}; keys {string.Join(' ', Keys)}; transposing {Transposing}; "
            + string.Join(
                "; ",
                Tracks.Select(t => $"{StaffStyleIds.All[t.Style]}: " + string.Join(" | ", t.Regions.Select(r => $"region {r.Start}+{r.Length} {r.Notation} notes {string.Join(' ', r.Notes.Select(n => $"{n.Pitch}@{n.Start}+{n.Length}"))}")))
            );

        public ProjectState Build() {
            var signatures = new List<SignatureEvent>();
            var tick = 0L;
            foreach (var (meter, bars) in Meters) {
                var (numerator, denominator) = PropertyTests.Meters[meter];
                signatures.Add(new(tick, numerator, denominator));
                tick += bars * (3840L / denominator * numerator);
            }

            var project = new TestProject().Signatures([..signatures]);
            project.Keys([..Keys.Select((k, i) => new KeyEvent(i == 0 ? 0 : k.Tick, k.Tonic, k.Minor ? KeyMode.Minor : KeyMode.Major)).OrderBy(k => k.Tick).DistinctBy(k => k.Tick)]);
            var id = 0L;
            for (var t = 0; t < Tracks.Length; t++) {
                var track = project.Track($"T{t}", StaffStyleIds.All[Tracks[t].Style]);
                foreach (var region in Tracks[t].Regions) {
                    project.Region(
                        track,
                        region.Start,
                        region.Length,
                        region.Notation,
                        [..region.Notes.Select(n => TestProject.N(++id, n.Pitch, n.Start, n.Length))]
                    );
                }
            }

            return project.State;
        }
    }
}
