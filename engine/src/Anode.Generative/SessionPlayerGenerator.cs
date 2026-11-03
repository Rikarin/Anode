using Anode.Contracts;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Anode.Generative;

/// <summary>
///     Everything a Session Player region's notes depend on (docs/12-phase-11.md, "Generation is deterministic"): its
///     player, parameters, seed, place and length (project ticks), the chord track, the arrangement sections, the
///     signatures and the project's key track, and the rhythm source's onsets over the region (project ticks: a MIDI
///     track's note starts, an audio track's transients; null: no rhythm source). The style library's version is the
///     generator's (<see cref="StyleLibrary.Version" />). The server gathers them per region; only what lies over the
///     region's range (and the section and chord before it) changes the notes.
/// </summary>
public sealed record GenerationInput(
    SessionPlayerKind Player,
    SessionPlayerParameters Parameters,
    long Seed,
    long StartTick,
    long LengthTicks,
    ChordTrack Chords,
    IReadOnlyList<ArrangementMarker> Sections,
    IReadOnlyList<SignatureEvent> Signatures,
    IReadOnlyList<KeyEvent> Keys,
    IReadOnlyList<long>? RhythmOnsets
);

/// <summary>
///     A region's generated notes, positioned relative to its start (as a MIDI region's; ids 1, 2, … in order, channel
///     1; the Drummer's on <see cref="DrumKitNotes" />), and the hash of what made them.
/// </summary>
public sealed record GeneratedNotes(string Hash, IReadOnlyList<Note> Notes);

/// <summary>
///     The Session Player generator (docs/adr/0014-session-players-and-chord-id.md; workstream D): rules over the style
///     library's phrase banks. Complexity and intensity pick and morph phrases; the variation grammar follows the
///     section kinds; fills come at section ends and by the fill amount; swing, feel and seeded humanisation shape
///     timing and velocity; the Drummer leans towards the rhythm source; bass and keys map the phrases' chord-relative
///     degrees onto the chord track, voice-led within their range. A pure function: thread-safe, no I/O, no clock, no
///     unseeded randomness, the same notes on every machine (integer ticks; no floating-point result decides a note).
/// </summary>
/// <remarks>
///     A region whose style the library does not have, or whose style is another player's, plays nothing (its notes
///     are empty), so a library without a style a project names still compiles the project.
/// </remarks>
public sealed class SessionPlayerGenerator(StyleLibrary library) {
    const int Format = 1;

    public StyleLibrary Library { get; } = library;

    /// <summary>The notes of a region with <paramref name="input" />.</summary>
    public GeneratedNotes Generate(GenerationInput input) {
        var relevant = Relevant(input);
        var hash = HashOf(relevant);
        var parameters = relevant.Parameters;
        if (Library.Find(parameters.StyleId) is not { } style
            || style.Player != input.Player
            || input.LengthTicks <= 0) {
            return new(hash, []);
        }

        var timeline = new Timeline(relevant, parameters.FollowChords);
        var plans = Arranger.Plan(style, parameters, timeline, relevant.Seed);
        var hits = style.Player switch {
            SessionPlayerKind.Drummer =>
                DrumPlayer.Play(style, parameters, plans, relevant.RhythmOnsets, relevant.Seed),
            SessionPlayerKind.Bass => BassPlayer.Play(style, parameters, timeline, plans, relevant.Seed),
            _ => KeysPlayer.Play(style, parameters, timeline, plans, relevant.Seed)
        };
        return new(hash, Arranger.Perform(hits, style, parameters, timeline, relevant.Seed));
    }

    /// <summary>
    ///     The hash of <paramref name="input" /> and the library's version (lowercase hex SHA-256 of a canonical
    ///     encoding): the server's cache key; equal hashes, equal notes.
    /// </summary>
    public string Hash(GenerationInput input) => HashOf(Relevant(input));

    /// <summary>
    ///     A Drummer region's notes as a pattern (Convert to Pattern): one pad row per kit note played, steps of
    ///     <paramref name="stepTicks" />, over <paramref name="lengthTicks" />; notes off the grid go to the nearest step.
    /// </summary>
    /// <remarks>
    ///     A step keeps its note's timing as its offset (a fraction of the step) and its length as its gate, so the
    ///     pattern plays the notes it was made from; two notes of a pitch on one step keep the louder. Rows are as long
    ///     as the region (in steps), in note order.
    /// </remarks>
    public static Pattern ToPattern(IReadOnlyList<Note> notes, long lengthTicks, int stepTicks = 240) {
        ArgumentOutOfRangeException.ThrowIfLessThan(stepTicks, 1);
        var steps = (int)Math.Max(1, (lengthTicks + stepTicks - 1) / stepTicks);
        var rows = new SortedDictionary<int, PatternStep[]>();
        foreach (var note in notes) {
            if (!rows.TryGetValue(note.Pitch, out var row)) {
                row = rows[note.Pitch] = [..Enumerable.Repeat(new PatternStep(false, 100, 1, 1, 1, 0), steps)];
            }

            var step = (int)Math.Clamp((note.StartTick + stepTicks / 2) / stepTicks, 0, steps - 1);
            if (row[step].On && row[step].Velocity >= note.Velocity) {
                continue;
            }

            var offset = Math.Clamp((note.StartTick - (double)step * stepTicks) / stepTicks, -0.5, 0.5);
            var gate = Math.Clamp(note.LengthTicks / (double)stepTicks, 1.0 / stepTicks, 16);
            row[step] = new(true, Math.Clamp(note.Velocity, 1, 127), gate, 1, 1, offset);
        }

        return new(
            [
                ..rows.Select(r => new PatternRow(
                        $"row_{r.Key}",
                        PatternRowKind.Pad,
                        r.Key,
                        PadName(r.Key),
                        steps,
                        null,
                        PatternPlayMode.Forward,
                        false,
                        r.Value
                    )
                )
            ],
            0,
            1,
            stepTicks
        );
    }

    /// <summary>The player a Session Player track kind plays (null for other kinds).</summary>
    public static SessionPlayerKind? PlayerOf(TrackKind kind) =>
        kind switch {
            TrackKind.Drummer => SessionPlayerKind.Drummer,
            TrackKind.BassPlayer => SessionPlayerKind.Bass,
            TrackKind.KeyboardPlayer => SessionPlayerKind.Keyboard,
            _ => null
        };

    /// <summary>A kit note's name, as the Drummer's kits name their pads.</summary>
    static string PadName(int note) =>
        note switch {
            DrumKitNotes.Kick => "Kick",
            DrumKitNotes.SideStick => "Side Stick",
            DrumKitNotes.Snare => "Snare",
            DrumKitNotes.Clap => "Clap",
            DrumKitNotes.FloorTom => "Floor Tom",
            DrumKitNotes.ClosedHiHat => "Closed Hi-Hat",
            DrumKitNotes.LowTom => "Low Tom",
            DrumKitNotes.PedalHiHat => "Pedal Hi-Hat",
            DrumKitNotes.OpenHiHat => "Open Hi-Hat",
            DrumKitNotes.MidTom => "Mid Tom",
            DrumKitNotes.Crash => "Crash",
            DrumKitNotes.HighTom => "High Tom",
            DrumKitNotes.Ride => "Ride",
            DrumKitNotes.RideBell => "Ride Bell",
            DrumKitNotes.Tambourine => "Tambourine",
            DrumKitNotes.Cowbell => "Cowbell",
            DrumKitNotes.Shaker => "Shaker",
            _ => $"Note {note}"
        };

    /// <summary>
    ///     The part of <paramref name="input" /> the notes depend on, in tick order: the chord, scale, key and section
    ///     sounding at the start (or ending before it) and those inside the region, the signatures before its end (they
    ///     place its bar lines), the rhythm source's onsets inside it.
    /// </summary>
    static GenerationInput Relevant(GenerationInput input) {
        var start = input.StartTick;
        var end = input.StartTick + Math.Max(0, input.LengthTicks);

        static List<T> Around<T>(IEnumerable<T> items, Func<T, long> tick, long start, long end) {
            var sorted = items.OrderBy(tick).ToList();
            var before = sorted.FindLastIndex(i => tick(i) <= start);
            return [..sorted.Where((i, k) => k == before || tick(i) > start && tick(i) < end)];
        }

        var sections = input.Sections.OrderBy(static s => s.StartTick)
            .ThenBy(static s => s.Id, StringComparer.Ordinal)
            .ToList();
        var previous = sections.FindLastIndex(s => s.StartTick + s.LengthTicks <= start);
        return input with {
            Chords = new(
                Around(input.Chords.Chords, static c => c.Tick, start, end),
                Around(input.Chords.Scales, static s => s.Tick, start, end)
            ),
            Sections = [
                ..sections.Where((s, k) => k == previous || s.StartTick < end && s.StartTick + s.LengthTicks > start)
            ],
            Signatures = [..input.Signatures.Where(s => s.Tick < end).OrderBy(static s => s.Tick)],
            Keys = Around(input.Keys, static k => k.Tick, start, end),
            RhythmOnsets = input.RhythmOnsets is null
                ? null
                : [..input.RhythmOnsets.Where(t => t >= start && t < end).Order()]
        };
    }

    /// <summary>The canonical encoding of a relevant input (one field per line, invariant numbers), hashed.</summary>
    string HashOf(GenerationInput input) {
        var b = new StringBuilder();
        IEnumerable<object?[]> lines = [
            ["anode.session-player", Format, Library.Version],
            [input.Player, input.Seed, input.StartTick, input.LengthTicks],
            ..ParameterLines(input.Parameters),
            ..SongLines(input)
        ];
        foreach (var line in lines) {
            b.AppendJoin('|', line.Select(Field)).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(b.ToString())));
    }

    /// <summary>A field of the canonical encoding: invariant, round-trip numbers, booleans as 0 and 1, ~ for null.</summary>
    static string Field(object? value) =>
        value switch {
            null => "~",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            bool f => f ? "1" : "0",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "~"
        };

    static IEnumerable<object?[]> ParameterLines(SessionPlayerParameters p) {
        yield return [
            p.StyleId, p.PresetId, p.Pad.Complexity, p.Pad.Intensity, p.FillAmount, p.FillAtSectionEnds, p.Swing,
            p.Feel, p.FollowChords
        ];
        if (p.Drums is { } drums) {
            yield return ["drums", drums.RhythmSourceTrackId];
            foreach (var piece in drums.Pieces) {
                yield return [piece.Piece, piece.On, piece.Variation];
            }
        }

        if (p.Bass is { } bass) {
            yield return ["bass", bass.LowNote, bass.HighNote, bass.Slides, bass.DeadNotes];
        }

        if (p.Keys is { } keys) {
            yield return ["keys", keys.Voicing, keys.LowNote, keys.HighNote, keys.LeftHand, keys.RhythmId];
        }
    }

    /// <summary>The song over the region: chords (pitch classes), scales, sections (kinds), signatures, keys, onsets.</summary>
    static IEnumerable<object?[]> SongLines(GenerationInput input) {
        foreach (var chord in input.Chords.Chords) {
            int? bass = chord.Bass is null ? null : PitchSources.PitchClass(chord.Bass);
            yield return ["chord", chord.Tick, PitchSources.PitchClass(chord.Root), chord.Quality, bass];
        }

        foreach (var scale in input.Chords.Scales) {
            yield return ["scale", scale.Tick, PitchSources.PitchClass(scale.Tonic), scale.Scale];
        }

        foreach (var section in input.Sections) {
            yield return ["section", section.StartTick, section.LengthTicks, SectionKinds.Of(section.Name)];
        }

        foreach (var signature in input.Signatures) {
            yield return ["signature", signature.Tick, signature.Numerator, signature.Denominator];
        }

        foreach (var key in input.Keys) {
            yield return ["key", key.Tick, key.Tonic, key.Mode];
        }

        if (input.RhythmOnsets is { } onsets) {
            yield return ["onsets", onsets.Count];
            yield return [..onsets.Cast<object?>()];
        }
    }
}
