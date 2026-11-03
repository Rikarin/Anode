using Anode.Contracts;
using CsCheck;

namespace Anode.Generative.Tests;

/// <summary>
///     The Bass and Keyboard Players over random chord tracks (docs/12-phase-11.md, exit criterion step 4): the root (or
///     slash bass) on every chord change's downbeat, every note in range, chord tones only (passing notes between
///     chords, under 10 %), voices moving by at most a fifth.
/// </summary>
public sealed class PlayerPropertyTests {
    const int Bar = Inputs.Bar;
    const int Bars = 8;

    /// <summary>How far humanisation moves a note at most (the styles' humanise timing is at most this).</summary>
    const int Tolerance = 12;

    static T Pick<T>(Random random, IReadOnlyList<T> items) => items[random.Next(items.Count)];

    internal static (GenerationInput Input, ChordTrack Chords) Random(int seed, SessionPlayerKind player) {
        var random = new Random(seed);
        var style = Pick(random, [..Inputs.StylesOf(player)]);
        var parameters = Pick(
            random,
            [..style.Presets.Select(static p => p.Parameters), ..style.Sections.Select(static s => s.Parameters)]
        );
        parameters = parameters with {
            Pad = new(random.NextDouble(), random.NextDouble()),
            FillAmount = random.NextDouble(),
            Swing = random.Next(3) == 0 ? random.NextDouble() : 0,
            Feel = random.Next(3) == 0 ? random.NextDouble() * 2 - 1 : 0
        };
        if (player == SessionPlayerKind.Bass) {
            var low = random.Next(24, 61);
            parameters = parameters with {
                Bass = new(low, random.Next(low + 12, 73), random.Next(2) == 0, random.Next(2) == 0)
            };
        } else {
            // A keyboard range of two octaves or more (narrower ranges can leave no placement within a fifth).
            var low = random.Next(36, 73);
            KeysLeftHand[] hands = [KeysLeftHand.Off, KeysLeftHand.Root, KeysLeftHand.Octaves, KeysLeftHand.RootFifth];
            parameters = parameters with {
                Keys = new(
                    Pick(random, Enum.GetValues<KeysVoicing>()),
                    low,
                    random.Next(low + 24, 97),
                    Pick(random, hands),
                    random.Next(2) == 0 || style.Rhythms.Count == 0 ? null : Pick(random, style.Rhythms).Id
                )
            };
        }

        var chords = Inputs.RandomChords(random, Bars + 2);
        var start = random.Next(2) * (long)Bar;
        return (Inputs.Input(player, parameters, random.Next(), start, Bars * (long)Bar - start, chords), chords);
    }

    /// <summary>Where a chord change's downbeat lands: its tick moved by the feel (at the region's start at the earliest).</summary>
    static int Index(ChordTrack chords, Chord chord) => chords.Chords.ToList().IndexOf(chord);

    static long Downbeat(GenerationInput input, long tick) =>
        Math.Max(0, tick - input.StartTick - (long)Math.Round(input.Parameters.Feel * 120));

    /// <summary>The chord changes over the region (the one sounding at its start included).</summary>
    static IEnumerable<Chord> Changes(GenerationInput input, ChordTrack chords) =>
        chords.Chords.Where(c => c.Tick < input.StartTick + input.LengthTicks
            && (c.Tick >= input.StartTick || Inputs.ChordAt(chords, input.StartTick) == c)
        );

    [Fact]
    public void Bass_plays_the_root_on_every_change_in_range_one_note_at_a_time() =>
        Gen.Int.Sample(
            static seed => {
                var (input, chords) = Random(seed, SessionPlayerKind.Bass);
                var notes = Inputs.Generator.Generate(input).Notes;
                var bass = input.Parameters.Bass!;
                Assert.NotEmpty(notes);
                Assert.All(notes, n => Assert.InRange(n.Pitch, bass.LowNote, bass.HighNote));
                for (var k = 0; k + 1 < notes.Count; k++) {
                    Assert.True(
                        notes[k].StartTick + notes[k].LengthTicks <= notes[k + 1].StartTick,
                        $"seed {seed}: notes {k} and {k + 1} overlap"
                    );
                }

                foreach (var chord in Changes(input, chords)) {
                    var at = Downbeat(input, Math.Max(chord.Tick, input.StartTick));
                    var pc = PitchSources.PitchClass(chord.Bass ?? chord.Root);
                    var there = notes.Where(n => Math.Abs(n.StartTick - at) <= Tolerance).ToList();
                    Assert.True(
                        there.Count >= 1 && there.TrueForAll(n => n.Pitch % 12 == pc),
                        $"seed {seed}: at {at} expected pitch class {pc}, found {string.Join(", ", there.Select(static n => n.Pitch))}"
                    );
                }
            },
            iter: 300
        );

    [Fact]
    public void Keys_play_chord_tones_in_range_and_lead_their_voices() =>
        Gen.Int.Sample(
            static seed => {
                var (input, chords) = Random(seed, SessionPlayerKind.Keyboard);
                var notes = Inputs.Generator.Generate(input).Notes;
                var keys = input.Parameters.Keys!;
                Assert.NotEmpty(notes);
                var right = notes.Where(n => n.Pitch >= keys.LowNote).ToList();
                Assert.All(right, n => Assert.InRange(n.Pitch, keys.LowNote, keys.HighNote));
                Assert.All(notes.Except(right), n => Assert.InRange(n.Pitch, 24, keys.LowNote - 1));

                // Chord tones of the chord where the note starts (allowing for humanisation and the feel); the others
                // are passing notes, just before a change.
                var shift = (long)Math.Round(input.Parameters.Feel * 120);
                var changes = chords.Chords.Select(static c => c.Tick).ToList();
                var passing = 0;
                foreach (var note in notes) {
                    var grid = note.StartTick + input.StartTick + shift;
                    if (Inputs.Holds(Inputs.ChordAt(chords, grid), note.Pitch)
                        || Inputs.Holds(Inputs.ChordAt(chords, grid + Tolerance), note.Pitch)
                        || Inputs.Holds(Inputs.ChordAt(chords, Math.Max(0, grid - Tolerance)), note.Pitch)) {
                        continue;
                    }

                    passing++;
                    var next = changes.FirstOrDefault(t => t > grid - Tolerance, long.MaxValue / 2);
                    Assert.True(
                        next - grid <= 240 + Tolerance && grid + note.LengthTicks <= next + Tolerance,
                        $"seed {seed}: {note.Pitch} at {grid} is no chord tone and leads to no change"
                    );
                }

                Assert.True(passing < 0.1 * notes.Count, $"seed {seed}: {passing} passing notes of {notes.Count}");

                // Voicings (the right hand's notes struck together, as many as most such strikes have: whole
                // voicings, not their top or upper voices or an added octave) of neighbouring chords: no voice moves by
                // more than a fifth.
                var strikes = right.GroupBy(static n => n.StartTick).Where(static g => g.Count() >= 3).ToList();
                if (strikes.Count == 0) {
                    return;
                }

                var size = strikes.GroupBy(static g => g.Count())
                    .OrderByDescending(static g => g.Count())
                    .ThenBy(static g => g.Key)
                    .First()
                    .Key;
                var voicings = strikes.Where(g => g.Count() == size)
                    .Select(g => (Tick: g.Key,
                            Chord: Inputs.ChordAt(chords, g.Key + input.StartTick + shift + Tolerance),
                            Pitches: g.Select(static n => n.Pitch).Order().ToArray())
                    )
                    .ToList();
                for (var k = 1; k < voicings.Count; k++) {
                    var (a, b) = (voicings[k - 1], voicings[k]);
                    // Neighbouring chords only (a chord between them voiced only in arpeggios leads elsewhere).
                    if (Index(chords, b.Chord) != Index(chords, a.Chord) + 1) {
                        continue;
                    }

                    var largest = a.Pitches.Zip(b.Pitches, static (x, y) => Math.Abs(x - y)).Max();
                    Assert.True(
                        largest <= 7,
                        $"seed {seed}: {string.Join(" ", a.Pitches)} → {string.Join(" ", b.Pitches)} at {b.Tick}"
                    );
                }
            },
            iter: 300
        );

    [Fact]
    public void Bass_and_keys_off_the_chord_track_play_the_keys_tonic_chord() {
        foreach (var player in (SessionPlayerKind[])[SessionPlayerKind.Bass, SessionPlayerKind.Keyboard]) {
            var style = Inputs.StylesOf(player).First();
            var input = Inputs.Input(player, style.Presets[0].Parameters with { FollowChords = false }) with {
                Keys = [new(0, 9, KeyMode.Minor)]
            };
            var notes = Inputs.Generator.Generate(input).Notes;
            Assert.NotEmpty(notes);
            // A minor: A first, and A, C and E but for the odd passing or approach note.
            Assert.Equal(9, notes[0].Pitch % 12);
            Assert.True(notes.Count(static n => n.Pitch % 12 is 9 or 0 or 4) >= 0.9 * notes.Count);
        }
    }
}
