using Anode.Contracts;

namespace Anode.Generative.Tests;

/// <summary>
///     The generator (docs/12-phase-11.md, workstream D): determinism and the hash, the XY pad, fills at section ends,
///     rhythm following, Convert to Pattern.
/// </summary>
public sealed class GeneratorTests {
    const int Bar = Inputs.Bar;

    static readonly LibraryTests.NotesComparer Same = new();

    public static TheoryData<string> AllStyles => [..Inputs.Library.Catalog.Styles.Select(static s => s.Id)];

    public static TheoryData<string> DrumStyles =>
        [..Inputs.StylesOf(SessionPlayerKind.Drummer).Select(static s => s.Id)];

    static GenerationInput Input(string styleId, double complexity = 0.5, double intensity = 0.5, long seed = 1) {
        var style = Inputs.Style(styleId);
        return Inputs.Input(style.Player, Inputs.Parameters(styleId, complexity, intensity), seed);
    }

    static IReadOnlyList<Note> Notes(GenerationInput input) => Inputs.Generator.Generate(input).Notes;

    [Fact]
    public void Each_Session_Player_track_kind_plays_its_player() {
        Assert.Equal(SessionPlayerKind.Drummer, SessionPlayerGenerator.PlayerOf(TrackKind.Drummer));
        Assert.Equal(SessionPlayerKind.Bass, SessionPlayerGenerator.PlayerOf(TrackKind.BassPlayer));
        Assert.Equal(SessionPlayerKind.Keyboard, SessionPlayerGenerator.PlayerOf(TrackKind.KeyboardPlayer));
        Assert.Null(SessionPlayerGenerator.PlayerOf(TrackKind.Instrument));
    }

    [Theory]
    [MemberData(nameof(AllStyles))]
    public void The_same_input_gives_the_same_notes(string styleId) {
        var input = Input(styleId, 0.7, 0.6, 42);
        var first = Inputs.Generator.Generate(input);
        Assert.NotEmpty(first.Notes);
        Assert.Equal(first, Inputs.Generator.Generate(input), Same);
        Assert.Equal(first, new SessionPlayerGenerator(StyleLibrary.Load(Inputs.StylesFolder())).Generate(input), Same);
        // On many threads at once, too.
        var parallel = Enumerable.Range(0, 8).AsParallel().Select(_ => Inputs.Generator.Generate(input)).ToList();
        Assert.All(parallel, p => Assert.Equal(first, p, Same));
        // Ids 1, 2, … in order, channel 1, inside the region.
        Assert.Equal(
            Enumerable.Range(1, first.Notes.Count).Select(static i => (long)i),
            first.Notes.Select(static n => n.Id)
        );
        Assert.All(
            first.Notes,
            static n => {
                Assert.Equal(1, n.Channel);
                Assert.InRange(n.StartTick, 0, 8 * Bar - 1);
                Assert.InRange(n.StartTick + n.LengthTicks, n.StartTick + 1, 8 * Bar);
                Assert.InRange(n.Velocity, 1, 127);
            }
        );
    }

    /// <summary>
    ///     Bit-identical across runs and machines: the notes of fixed inputs fingerprinted. A change to the generator
    ///     or the library that moves them changes these (and the library's version must be bumped).
    /// </summary>
    [Theory]
    [InlineData("drums-rock")]
    [InlineData("bass-funk")]
    [InlineData("keys-pop")]
    public void The_notes_of_fixed_inputs_are_bit_identical(string styleId) {
        var notes = Notes(Input(styleId, 0.6, 0.6, 7));
        var text = string.Join(
            '\n',
            notes.Select(static n => $"{n.Id} {n.Pitch} {n.Velocity} {n.StartTick} {n.LengthTicks}")
        );
        var fingerprint = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))
        );
        Assert.Equal(Fingerprints[styleId], fingerprint);
    }

    static readonly Dictionary<string, string> Fingerprints = new() {
        ["drums-rock"] = "e86782a89671baf78bdb19a87bcd5f28d170d6b2301a3f44d3785dfe8883a83b",
        ["bass-funk"] = "1de09e4ac05f7f30fed1d5cb23d4590bc7cc1766a8da074c064d9d90feab1b29",
        ["keys-pop"] = "364e79bfb501d520e35878b566d042c24de4fb1f54df784d525786089aaace0a"
    };

    [Fact]
    public void The_hash_follows_the_inputs_over_the_region_only() {
        var input = Inputs.Input(
            SessionPlayerKind.Bass,
            Inputs.Parameters("bass-rock"),
            start: 8 * Bar,
            length: 4 * Bar,
            chords: Inputs.RandomChords(new(3), 32),
            sections: [
                new("a", "Verse", 0, 8 * Bar, "#888"), new("b", "Chorus", 8 * Bar, 8 * Bar, "#888"),
                new("c", "Bridge", 24 * Bar, 4 * Bar, "#888")
            ]
        );
        var hash = Inputs.Generator.Hash(input);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, Inputs.Generator.Generate(input).Hash);
        // Chords, sections and onsets after the region, and chords long before it, change nothing.
        var later = input with {
            Chords = new(
                [
                    ..input.Chords.Chords.Where(static c => c.Tick < 12 * Bar || c.Tick >= 20 * Bar)
                        .Select(c => c.Tick >= 20 * Bar ? c with { Quality = "dim" } : c)
                ],
                []
            ),
            Sections = [..input.Sections.Where(static s => s.StartTick < 24 * Bar)],
            RhythmOnsets = null
        };
        Assert.Equal(hash, Inputs.Generator.Hash(later));
        Assert.Equal(Inputs.Generator.Generate(input), Inputs.Generator.Generate(later), Same);
        // A chord inside it, the seed, a parameter or the region's place do.
        Assert.NotEqual(hash, Inputs.Generator.Hash(input with { Seed = 2 }));
        Assert.NotEqual(hash, Inputs.Generator.Hash(input with { StartTick = 9 * Bar }));
        Assert.NotEqual(hash, Inputs.Generator.Hash(input with { Parameters = input.Parameters with { Swing = 0.5 } }));
        var chord = input.Chords.Chords.First(static c => c.Tick > 8 * Bar);
        Assert.NotEqual(
            hash,
            Inputs.Generator.Hash(
                input with {
                    Chords = new(
                        [
                            ..input.Chords.Chords.Select(c => c == chord
                                    ? c with { Quality = c.Quality == "m" ? string.Empty : "m" }
                                    : c
                            )
                        ],
                        []
                    )
                }
            )
        );
    }

    [Theory]
    [MemberData(nameof(AllStyles))]
    public void Regenerate_gives_other_notes(string styleId) =>
        Assert.False(Notes(Input(styleId, seed: 1)).SequenceEqual(Notes(Input(styleId, seed: 2))));

    [Fact]
    public void A_style_the_library_lacks_or_of_another_player_plays_nothing() {
        Assert.Empty(
            Notes(
                Inputs.Input(SessionPlayerKind.Drummer, Inputs.Parameters("drums-rock") with { StyleId = "drums-gone" })
            )
        );
        Assert.Empty(Notes(Inputs.Input(SessionPlayerKind.Bass, Inputs.Parameters("drums-rock"))));
    }

    /// <summary>Hits per bar rise with complexity (three settings) and the mean velocity with intensity (three settings).</summary>
    [Theory]
    [MemberData(nameof(AllStyles))]
    public void Density_and_velocity_rise_with_the_pad(string styleId) {
        var density = (double[])[0, 0.5, 1];
        var hits = density.Select(c => (double)Notes(Input(styleId, c)).Count).ToArray();
        var velocity = density.Select(i => Notes(Input(styleId, 0.5, i)).Average(static n => n.Velocity)).ToArray();
        Assert.True(hits[0] < hits[1] && hits[1] < hits[2], $"hits {string.Join(", ", hits)}");
        Assert.True(velocity[0] < velocity[1] && velocity[1] < velocity[2], $"velocity {string.Join(", ", velocity)}");
    }

    /// <summary>
    ///     The last bar of each section holds a fill: more hits than the section's groove bars, with toms or a snare
    ///     roll (three or more toms and snares).
    /// </summary>
    [Theory]
    [MemberData(nameof(DrumStyles))]
    public void Fills_fall_at_section_ends(string styleId) {
        int[] fillNotes = [
            DrumKitNotes.Snare, DrumKitNotes.HighTom, DrumKitNotes.MidTom, DrumKitNotes.LowTom, DrumKitNotes.FloorTom,
            DrumKitNotes.Clap
        ];
        foreach (var (c, i) in (ReadOnlySpan<(double, double)>)[(0, 0.5), (0.5, 0.5), (1, 0.5), (0.5, 0), (0.5, 1)]) {
            for (var seed = 1; seed <= 5; seed++) {
                var notes = Notes(Input(styleId, c, i, seed));

                // A note belongs to the bar its grid position is in (humanisation moves it by a few ticks).
                int Hits(int bar) => notes.Count(n => (n.StartTick + Bar / 32) / Bar == bar);
                var groove = Enumerable.Range(0, 3).Max(Hits);
                Assert.True(Hits(3) > groove, $"{styleId} ({c}, {i}) seed {seed}: fill {Hits(3)}, groove {groove}");
                var roll = notes.Count(n =>
                    n.StartTick >= 3 * Bar + Bar / 2 && n.StartTick < 4 * Bar && fillNotes.Contains(n.Pitch)
                );
                Assert.True(
                    roll >= 3,
                    $"{styleId} ({c}, {i}) seed {seed}: {string.Join(", ", notes.Where(static n => n.StartTick >= 3 * Bar && n.StartTick < 4 * Bar).Select(static n => $"{n.Pitch}@{n.StartTick - 3 * Bar}"))}"
                );
            }
        }
    }

    [Fact]
    public void Without_fills_at_section_ends_the_last_bar_grooves() {
        var parameters = Inputs.Parameters("drums-rock") with { FillAtSectionEnds = false, FillAmount = 0 };
        var notes = Notes(Inputs.Input(SessionPlayerKind.Drummer, parameters));
        Assert.DoesNotContain(
            notes,
            static n => n.Pitch is DrumKitNotes.HighTom
                or DrumKitNotes.MidTom
                or DrumKitNotes.LowTom
                or DrumKitNotes.FloorTom
        );
    }

    /// <summary>Following a rhythm source (a bass line's note starts): at least 75 % of kicks within a 16th of an onset.</summary>
    [Theory]
    [MemberData(nameof(DrumStyles))]
    public void Drums_follow_the_rhythm_source(string styleId) {
        var bass = Notes(Inputs.Input(SessionPlayerKind.Bass, Inputs.Parameters("bass-funk", 0.8, 0.6), 5));
        var onsets = bass.Select(static n => n.StartTick).ToList();
        foreach (var c in (double[])[0.2, 0.5, 0.9]) {
            var parameters = Inputs.Parameters(styleId, c);
            var notes = Notes(
                Inputs.Input(
                    SessionPlayerKind.Drummer,
                    parameters with { Drums = parameters.Drums! with { RhythmSourceTrackId = "bass" } },
                    onsets: onsets
                )
            );
            var kicks = notes.Where(static n => n.Pitch == DrumKitNotes.Kick).ToList();
            Assert.NotEmpty(kicks);
            var near = kicks.Count(k => onsets.Exists(o => Math.Abs(o - k.StartTick) <= Bar / 16));
            Assert.True(near >= 0.75 * kicks.Count, $"{styleId} at {c}: {near} of {kicks.Count} kicks on onsets");
        }
    }

    /// <summary>The same on a bass of plain quarter notes, fills and all (a kick roll stays only on the onsets).</summary>
    [Theory]
    [MemberData(nameof(DrumStyles))]
    public void Fill_kicks_follow_a_quarter_note_rhythm_source_too(string styleId) {
        var onsets = Enumerable.Range(0, 32).Select(static beat => beat * 960L).ToList();
        foreach (var c in (double[])[0.5, 0.9]) {
            var parameters = Inputs.Parameters(styleId, c, 0.9);
            var notes = Notes(
                Inputs.Input(
                    SessionPlayerKind.Drummer,
                    parameters with { Drums = parameters.Drums! with { RhythmSourceTrackId = "bass" } },
                    onsets: onsets
                )
            );
            var kicks = notes.Where(static n => n.Pitch == DrumKitNotes.Kick).ToList();
            Assert.NotEmpty(kicks);
            var near = kicks.Count(k => onsets.Exists(o => Math.Abs(o - k.StartTick) <= Bar / 16));
            Assert.True(near >= 0.75 * kicks.Count, $"{styleId} at {c}: {near} of {kicks.Count} kicks on onsets");
        }
    }

    [Fact]
    public void Switched_off_pieces_are_silent_and_the_ride_keeps_time() {
        var parameters = Inputs.Parameters("drums-rock");
        var pieces = parameters.Drums!.Pieces
            .Select(static p => p.Piece is DrumPiece.HiHat or DrumPiece.Toms ? p with { On = false } : p)
            .ToList();
        var notes = Notes(
            Inputs.Input(
                SessionPlayerKind.Drummer,
                parameters with { Drums = parameters.Drums with { Pieces = pieces } }
            )
        );
        var off = DrumKitNotes.Of(DrumPiece.HiHat).Concat(DrumKitNotes.Of(DrumPiece.Toms)).ToHashSet();
        Assert.DoesNotContain(notes, n => off.Contains(n.Pitch));
        Assert.Contains(notes, static n => n.Pitch == DrumKitNotes.Ride);
    }

    [Fact]
    public void A_piece_variation_plays_the_styles_variation_lane() {
        var parameters = Inputs.Parameters("drums-rock", 0.3, 0.4);
        var pieces = parameters.Drums!.Pieces
            .Select(static p => p.Piece == DrumPiece.HiHat ? p with { Variation = 1 } : p)
            .ToList();
        var plain = Notes(Inputs.Input(SessionPlayerKind.Drummer, parameters))
            .Count(static n => n.Pitch == DrumKitNotes.ClosedHiHat);
        var sixteenths = Notes(
                Inputs.Input(
                    SessionPlayerKind.Drummer,
                    parameters with { Drums = parameters.Drums with { Pieces = pieces } }
                )
            )
            .Count(static n => n.Pitch == DrumKitNotes.ClosedHiHat);
        Assert.True(sixteenths > 1.5 * plain, $"{sixteenths} sixteenths, {plain} eighths");
    }

    [Fact]
    public void Swing_delays_the_offbeat_sixteenths_and_feel_moves_everything() {
        var parameters = Inputs.Parameters("drums-electronic") with { Swing = 0 };
        var straight = Notes(Inputs.Input(SessionPlayerKind.Drummer, parameters));
        Assert.All(straight, static n => Assert.Equal(0, n.StartTick % 240));
        var swung = Notes(Inputs.Input(SessionPlayerKind.Drummer, parameters with { Swing = 1 }));
        Assert.Contains(swung, static n => (ulong)n.StartTick % 480 == 320);
        var pushed = Notes(Inputs.Input(SessionPlayerKind.Drummer, parameters with { Feel = 1 }));
        Assert.Contains(pushed, static n => (ulong)n.StartTick % 240 == 120);
    }

    [Fact]
    public void Bars_follow_the_signatures() {
        var notes = Notes(
            Inputs.Input(
                SessionPlayerKind.Drummer,
                Inputs.Parameters("drums-rock"),
                signatures: [new(0, 3, 4)],
                sections: []
            )
        );
        // Every 3/4 bar starts with a kick.
        for (long bar = 0; bar < 8 * Bar; bar += 3 * Inputs.Beat) {
            Assert.Contains(notes, n => n.Pitch == DrumKitNotes.Kick && Math.Abs(n.StartTick - bar) <= 20);
        }
    }

    [Theory]
    [MemberData(nameof(DrumStyles))]
    public void Convert_to_pattern_holds_the_generated_notes(string styleId) {
        var notes = Notes(Input(styleId, 0.7, 0.7));
        var pattern = SessionPlayerGenerator.ToPattern(notes, 8 * Bar);
        Assert.Equal(240, pattern.StepTicks);
        Assert.Equal(notes.Select(static n => n.Pitch).Distinct().Order(), pattern.Rows.Select(static r => r.Note));
        var played = new List<(long Tick, int Pitch, int Velocity)>();
        foreach (var row in pattern.Rows) {
            Assert.Equal(PatternRowKind.Pad, row.Kind);
            Assert.Equal(128, row.Length);
            Assert.Equal(row.Length, row.Steps.Count);
            for (var s = 0; s < row.Steps.Count; s++) {
                var step = row.Steps[s];
                if (step.On) {
                    Assert.InRange(step.Offset, -0.5, 0.5);
                    Assert.InRange(step.Gate, double.Epsilon, 16);
                    played.Add(((long)Math.Round((s + step.Offset) * 240), row.Note, step.Velocity));
                }
            }
        }

        Assert.Equal(notes.Select(static n => (n.StartTick, n.Pitch, n.Velocity)).Order(), played.Order());
    }
}
