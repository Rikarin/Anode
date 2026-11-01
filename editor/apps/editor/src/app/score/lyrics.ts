import type { LyricChange, LyricSyllable, ScoreView, Syllabic } from '@anode/api-client';

/**
 * Lyrics entry as Logic does it: select a note, type its syllable; Space or Tab enters it and moves to the next note
 * (⇧Tab the previous), a hyphen ends a syllable inside a word and moves on, an underscore draws an extender line,
 * Return enters it and stops, Escape stops without it. One `setLyrics` per syllable (one undo step each).
 */

/** The notes lyrics go to, in order: one per drawn note or chord (its top note) of a part's first staff and voice. */
export function lyricNotes(view: ScoreView, trackId: string): number[] {
  const part = view.parts.find(p => p.trackId === trackId);
  const notes: number[] = [];
  for (const measure of part?.measures ?? []) {
    const voice = measure.staves[0]?.voices.find(v => v.number === 1);
    for (const event of voice?.events ?? []) {
      // The first segment of a tied note only.
      if (event.kind === 'rest' || event.id.includes('-')) {
        continue;
      }
      const top = [...event.notes].sort((a, b) => b.pitch - a.pitch)[0];
      if (top && !notes.includes(top.noteId)) {
        notes.push(top.noteId);
      }
    }
  }
  return notes;
}

/** What a key does in the lyric field. */
export interface LyricKeyResult {
  /** The syllable to enter (null: nothing to change). */
  change: LyricChange | null;
  /** Where to go next: the next note, the previous, or stay. */
  move: -1 | 0 | 1;
  /** Whether entry stops. */
  done: boolean;
}

/**
 * The effect of a key on the lyric field holding `text` for `noteId`. `continuing` says whether the previous note's
 * syllable ends with a hyphen (so this one is the middle or end of its word); `existing` is the note's syllable in this
 * verse, if any (an emptied field removes it). Null: an ordinary key that the field types.
 */
export function lyricKey(
  key: { key: string; shiftKey: boolean },
  noteId: number,
  text: string,
  continuing: boolean,
  existing: LyricSyllable | null
): LyricKeyResult | null {
  const syllable = (syllabic: Syllabic, extender = false): LyricChange | null => {
    const trimmed = text.trim();
    if (!trimmed && !existing) {
      return null;
    }
    if (existing?.text === trimmed && existing.syllabic === syllabic && existing.extender === extender) {
      return null;
    }
    return { noteId, text: trimmed, syllabic: trimmed ? syllabic : 'single', extender: trimmed ? extender : false };
  };
  const ending: Syllabic = continuing ? 'end' : 'single';
  switch (key.key) {
    case ' ':
      return { change: syllable(ending), move: 1, done: false };
    case 'Tab':
      return { change: syllable(ending), move: key.shiftKey ? -1 : 1, done: false };
    case '-':
      return { change: syllable(continuing ? 'middle' : 'begin'), move: 1, done: false };
    case '_':
      return { change: syllable(ending, true), move: 1, done: false };
    case 'Enter':
      return {
        change: syllable(existing && !text.trim() ? 'single' : (existing?.syllabic ?? ending)),
        move: 0,
        done: true
      };
    case 'Escape':
      return { change: null, move: 0, done: true };
    default:
      return null;
  }
}

/** Whether the syllable before a note (in the order) continues its word. */
export function continuesWord(
  lyrics: readonly LyricSyllable[],
  order: readonly number[],
  noteId: number,
  verse: number
): boolean {
  const index = order.indexOf(noteId);
  const previous = index > 0 ? order[index - 1] : undefined;
  const syllable = previous === undefined ? undefined : lyrics.find(l => l.noteId === previous && l.verse === verse);
  return syllable?.syllabic === 'begin' || syllable?.syllabic === 'middle';
}
