using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     The checks a package's stored score content passes when it is read (docs/10-phase-9.md): the commands' own,
///     against the project read so far, with notes in tracks' other alternatives counted.
/// </summary>
public static partial class Edits {
    /// <summary>A stored symbol checked: as a command checks it, its notes possibly in another alternative.</summary>
    public static ScoreSymbol CheckStoredSymbol(ProjectState state, ScoreSymbol? symbol) =>
        symbol?.Id is { Length: > 0 }
            ? CheckSymbol(state, NoteTracks(state, true), symbol)
            : throw new EditException("A score symbol has an id.");

    public static ChordSymbol CheckStoredChord(ProjectState state, ChordSymbol? chord) =>
        chord?.Id is { Length: > 0 } ? CheckChord(state, chord) : throw new EditException("A chord symbol has an id.");

    /// <summary>A stored syllable: of a note of the project, verse 1…8, 1…200 characters.</summary>
    public static LyricSyllable CheckStoredLyric(ProjectState state, LyricSyllable? lyric) {
        if (lyric?.Id is not { Length: > 0 }
            || !NoteTracks(state, true).ContainsKey(lyric.NoteId)
            || lyric.Verse is < 1 or > MaxVerses
            || lyric.Text is not { Length: > 0 and <= ScoreSymbols.MaxTextLength }
            || !Enum.IsDefined(lyric.Syllabic)) {
            throw new EditException("A damaged syllable.");
        }

        return lyric;
    }

    /// <summary>A stored score set: its tracks that are still MIDI tracks of the project.</summary>
    public static ScoreSet CheckStoredSet(ProjectState state, ScoreSet? set) =>
        CheckScoreSet(
            state,
            set is null
                ? null
                : set with {
                    TrackIds = [
                        ..(set.TrackIds ?? [])
                            .Where(id => id is not null && state.FindTrack(id) is { } t && HoldsMidi(t))
                            .Distinct(StringComparer.Ordinal)
                    ]
                }
        );

    /// <summary>The stored layout: as read, without names of tracks that are gone, else the default.</summary>
    public static ScoreLayout CheckStoredLayout(ProjectState state, ScoreLayout? layout) {
        if (layout is null) {
            return ScoreLayout.Default;
        }

        try {
            return CheckLayout(
                state,
                layout with {
                    InstrumentNames = [
                        ..(layout.InstrumentNames ?? [])
                            .Where(n => n?.TrackId is not null && state.FindTrack(n.TrackId) is not null)
                            .DistinctBy(static n => n.TrackId)
                    ]
                }
            );
        } catch (EditException) {
            return ScoreLayout.Default;
        }
    }
}
