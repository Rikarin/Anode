/**
 * The element ids of the rendered score: the MEI `xml:id`s the Server's notation engine writes, which Verovio keeps in
 * its SVG (engine/src/Anode.Contracts/Score.cs, `ScoreIds`), so a click maps to the model without asking the Server.
 * Prefixes: `n` note, `c` chord, `r` rest, `s` symbol, `l` lyric, `h` chord symbol (harmony), `m` measure. A note drawn
 * as tied segments has `n{noteId}` on the first and `n{noteId}-{k}` on the k-th (k ≥ 2).
 */
export const ScoreIds = {
  note: (noteId: number, segment = 1): string => (segment <= 1 ? `n${noteId}` : `n${noteId}-${segment}`),
  chord: (lowestNoteId: number, segment = 1): string =>
    segment <= 1 ? `c${lowestNoteId}` : `c${lowestNoteId}-${segment}`,
  /** A rest: its track, measure number, staff (0…), voice (1…) and index among the voice's rests in the measure (1…). */
  rest: (trackId: string, measure: number, staff: number, voice: number, index: number): string =>
    `r${trackId}-${measure}-${staff}-${voice}-${index}`,
  symbol: (symbolId: string): string => `s${symbolId}`,
  lyric: (lyricId: string): string => `l${lyricId}`,
  chordSymbol: (chordId: string): string => `h${chordId}`,
  measure: (measure: number): string => `m${measure}`
} as const;

const NoteElement = /^n([1-9][0-9]*)(?:-[0-9]+)?$/;

/** The note id of a note element (`n…`, any segment), else null. */
export function noteIdOf(elementId: string): number | null {
  const match = NoteElement.exec(elementId);
  return match?.[1] ? Number(match[1]) : null;
}

/** The symbol id of a symbol element (`s…`), else null. */
export function symbolIdOf(elementId: string): string | null {
  return prefixed(elementId, 's');
}

/** The lyric syllable id of a lyric element (`l…`), else null. */
export function lyricIdOf(elementId: string): string | null {
  return prefixed(elementId, 'l');
}

/** The chord symbol id of a harmony element (`h…`), else null. */
export function chordSymbolIdOf(elementId: string): string | null {
  return prefixed(elementId, 'h');
}

function prefixed(elementId: string, prefix: string): string | null {
  return elementId.length > 1 && elementId.startsWith(prefix) ? elementId.slice(1) : null;
}
