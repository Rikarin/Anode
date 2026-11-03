using Anode.Contracts;

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
public sealed class SessionPlayerGenerator(StyleLibrary library) {
    public StyleLibrary Library { get; } = library;

    /// <summary>The notes of a region with <paramref name="input" />.</summary>
    public GeneratedNotes Generate(GenerationInput input) => throw Pending.WorkstreamD();

    /// <summary>
    ///     The hash of <paramref name="input" /> and the library's version (lowercase hex SHA-256 of a canonical
    ///     encoding): the server's cache key; equal hashes, equal notes.
    /// </summary>
    public string Hash(GenerationInput input) => throw Pending.WorkstreamD();

    /// <summary>
    ///     A Drummer region's notes as a pattern (Convert to Pattern): one pad row per kit note played, steps of
    ///     <paramref name="stepTicks" />, over <paramref name="lengthTicks" />; notes off the grid go to the nearest step.
    /// </summary>
    public static Pattern ToPattern(IReadOnlyList<Note> notes, long lengthTicks, int stepTicks = 240) =>
        throw Pending.WorkstreamD();

    /// <summary>The player a Session Player track kind plays (null for other kinds).</summary>
    public static SessionPlayerKind? PlayerOf(TrackKind kind) =>
        kind switch {
            TrackKind.Drummer => SessionPlayerKind.Drummer,
            TrackKind.BassPlayer => SessionPlayerKind.Bass,
            TrackKind.KeyboardPlayer => SessionPlayerKind.Keyboard,
            _ => null
        };
}
