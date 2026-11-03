using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.Time;

namespace Anode.Model.Compile;

/// <summary>
///     Pitch Source (docs/12-phase-11.md, "Rules the contracts imply"): how a region plays following the chord track.
///     Each chord holds from its tick to the next; a region transposes by the shortest interval from its key's tonic
///     (its own key, else the project's at its start) to the chord's root, −6…+5 semitones. A MIDI region in Root mode
///     moves each note by its chord's interval; in Chords mode the moved note then goes to the nearest tone of that
///     chord (a tie goes down). Before the first chord a region plays as it is. An audio region plays one transposition
///     per chord segment, its pieces crossfaded over <see cref="CrossfadeSeconds" /> at the changes.
/// </summary>
public static class PitchFollow {
    /// <summary>The crossfade between an audio region's chord segments.</summary>
    public const double CrossfadeSeconds = 0.010;

    /// <summary>Whether <paramref name="mode" /> follows anything over <paramref name="track" />'s chords.</summary>
    public static bool Follows(PitchSourceMode mode, ChordTrack track) => mode != PitchSourceMode.Off && track.Chords.Count > 0;

    /// <summary>The interval a region of key <paramref name="keyRoot" /> (a pitch class) plays over <paramref name="chord" />.</summary>
    public static int Interval(int keyRoot, Chord chord) =>
        PitchSources.RootInterval(keyRoot, PitchSources.PitchClass(chord.Root));

    /// <summary>The MIDI region as it plays following the chord track: itself when it follows nothing.</summary>
    public static MidiRegion Followed(ProjectState state, MidiRegion region) {
        var chords = state.ChordTrack;
        if (!Follows(region.PitchSource, chords) || region.Notes.Count == 0) {
            return region;
        }

        var keyRoot = Edits.KeyRootOf(state, region.Key, region.StartTick);
        var notes = new Note[region.Notes.Count];
        for (var i = 0; i < notes.Length; i++) {
            var note = region.Notes[i];
            notes[i] = Edits.ChordAt(chords, region.StartTick + note.StartTick) is { } chord
                ? note with { Pitch = Pitch(note.Pitch, keyRoot, chord, region.PitchSource) }
                : note;
        }

        return region with { Notes = notes };
    }

    /// <summary>A note's pitch over <paramref name="chord" />: moved by its interval, then (Chords) to its nearest tone.</summary>
    public static int Pitch(int pitch, int keyRoot, Chord chord, PitchSourceMode mode) {
        var moved = pitch + Interval(keyRoot, chord);
        if (mode == PitchSourceMode.Chords) {
            moved = NearestTone(moved, chord);
        }

        return Math.Clamp(moved, 0, 127);
    }

    /// <summary>The nearest pitch to <paramref name="pitch" /> that is a tone of <paramref name="chord" /> (a tie goes down).</summary>
    public static int NearestTone(int pitch, Chord chord) {
        var root = PitchSources.PitchClass(chord.Root);
        var tones = ChordQualities.Intervals(chord.Quality).Select(i => (root + i) % 12).ToHashSet();
        if (chord.Bass is { } bass) {
            tones.Add(PitchSources.PitchClass(bass));
        }

        for (var distance = 0; distance <= 6; distance++) {
            if (tones.Contains(((pitch - distance) % 12 + 12) % 12)) {
                return pitch - distance;
            }

            if (tones.Contains((pitch + distance) % 12)) {
                return pitch + distance;
            }
        }

        return pitch;
    }

    /// <summary>
    ///     An audio region's chord segments over <paramref name="startTick" />…<paramref name="endTick" />: where each
    ///     starts (the first at <paramref name="startTick" />) and its interval (0 before the first chord); neighbours
    ///     of equal interval merged.
    /// </summary>
    public static IReadOnlyList<(long Tick, int Interval)> Segments(
        ProjectState state,
        RegionKey? key,
        long regionStart,
        long startTick,
        long endTick
    ) {
        var chords = state.ChordTrack;
        var keyRoot = Edits.KeyRootOf(state, key, regionStart);
        var segments = new List<(long Tick, int Interval)> {
            (startTick, Edits.ChordAt(chords, startTick) is { } first ? Interval(keyRoot, first) : 0)
        };
        foreach (var chord in chords.Chords) {
            if (chord.Tick <= startTick || chord.Tick >= endTick) {
                continue;
            }

            var interval = Interval(keyRoot, chord);
            if (interval != segments[^1].Interval) {
                segments.Add((chord.Tick, interval));
            }
        }

        return segments;
    }

    /// <summary>
    ///     A following audio region's playlist entries: one per chord segment, each through the pitch shift at its
    ///     transpose plus the segment's interval, crossfaded with its neighbours; the region's own entry when it follows
    ///     nothing or every segment's interval is 0.
    /// </summary>
    public static IReadOnlyList<PlacedEntry> Entries(
        ProjectState state,
        AudioRegion region,
        Track track,
        TempoMap map,
        int fileId,
        FileAnalysis? analysis
    ) {
        var timing = RegionTiming.Of(state, region);
        if (!Follows(region.PitchSource, state.ChordTrack)) {
            return [Warps.Entry(timing, track, map, state.SampleRate, fileId, analysis)];
        }

        var segments = Segments(state, region.Key, region.StartTick, region.StartTick, (long)Math.Ceiling(timing.EndTick));
        if (segments is [{ Interval: 0 }]) {
            return [Warps.Entry(timing, track, map, state.SampleRate, fileId, analysis)];
        }

        var file = state.Files.GetValueOrDefault(region.FileId);
        var rate = state.SampleRate;
        var fade = Math.Max(1, (long)Math.Round(CrossfadeSeconds * rate));
        PlacedEntry Through(int interval) =>
            Warps.Entry(
                RegionTiming.Of(
                    region with { Transpose = region.Transpose + interval },
                    file?.Tempo,
                    true,
                    state.TempoMap,
                    state.SignatureMap,
                    rate
                ),
                track,
                map,
                rate,
                fileId,
                analysis
            );

        // Where each segment starts: a change that would leave a segment shorter than four crossfades is dropped (the
        // chord before it plays on; at the region's start, the chord after it plays from there).
        var span = Through(0);
        var shortest = 4 * fade;
        var cuts = new List<(long Sample, int Interval)>();
        foreach (var (tick, interval) in segments) {
            var at = cuts.Count == 0 ? span.Start : map.SampleAt(tick, rate);
            if (cuts.Count > 0 && span.End - at < shortest) {
                break;
            }

            if (cuts.Count > 0 && at - cuts[^1].Sample < shortest) {
                if (cuts.Count == 1) {
                    cuts[0] = (cuts[0].Sample, interval);
                }

                continue;
            }

            if (cuts.Count == 0 || cuts[^1].Interval != interval) {
                cuts.Add((at, interval));
            }
        }

        if (cuts is [{ Interval: 0 }]) {
            return [Warps.Entry(timing, track, map, state.SampleRate, fileId, analysis)];
        }

        var entries = new List<PlacedEntry>(cuts.Count);
        for (var i = 0; i < cuts.Count; i++) {
            // Each segment through the pitch shift (a warped region) at its own transposition, running a crossfade
            // past the change into the next.
            var opening = i == 0;
            var closing = i == cuts.Count - 1;
            var end = closing ? span.End : Math.Min(cuts[i + 1].Sample + fade, span.End);
            var piece = Slice(Through(cuts[i].Interval), cuts[i].Sample, end);
            var entry = piece.Entry;
            // The region's own fades (on its first and last pieces) in what the crossfades leave, with a crossfade's
            // slack (Varispeed rounds each of them).
            var fadeIn = opening ? entry.FadeIn : fade;
            var fadeOut = closing ? entry.FadeOut : fade;
            if (opening && !closing) {
                fadeIn = Math.Min(fadeIn, Math.Max(0, entry.Length - 2 * fade));
            } else if (closing && !opening) {
                fadeOut = Math.Min(fadeOut, Math.Max(0, entry.Length - 2 * fade));
            }

            entries.Add(piece with { Entry = entry with { FadeIn = (int)fadeIn, FadeOut = (int)fadeOut } });
        }

        return entries;
    }

    /// <summary>The part of an entry from <paramref name="start" /> to <paramref name="end" />, its warp map sliced.</summary>
    static PlacedEntry Slice(PlacedEntry placed, long start, long end) {
        var e = placed.Entry;
        var length = end - start;
        var entry = e with {
            Start = start,
            Length = length,
            FadeIn = start == e.Start ? (int)Math.Min(e.FadeIn, length) : 0,
            FadeOut = end == e.Start + e.Length ? (int)Math.Min(e.FadeOut, length) : 0
        };
        return placed.Warp is { } warp
            ? placed with { Entry = entry, Warp = Warps.Slice(warp, start - e.Start, length) }
            : placed with { Entry = entry };
    }
}
