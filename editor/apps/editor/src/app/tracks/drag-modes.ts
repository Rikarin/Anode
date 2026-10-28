/**
 * Logic's drag modes for the Tracks area: Overlap (regions may cover each other; the later plays), No Overlap (an
 * earlier region is shortened to where the next starts), X-Fade (overlaps become crossfades), Shuffle R and Shuffle L
 * (a moved region butts up against its neighbour on the right or the left).
 */
export type DragMode = 'overlap' | 'noOverlap' | 'crossfade' | 'shuffleRight' | 'shuffleLeft';

export const DragModes: readonly { value: DragMode; label: string }[] = [
  { value: 'overlap', label: 'Overlap' },
  { value: 'noOverlap', label: 'No Overlap' },
  { value: 'crossfade', label: 'X-Fade' },
  { value: 'shuffleRight', label: 'Shuffle R' },
  { value: 'shuffleLeft', label: 'Shuffle L' }
];

/** A region's span on its track, in ticks. */
export interface Span {
  id: string;
  trackId: string;
  startTick: number;
  endTick: number;
  /** Audio regions can crossfade; MIDI regions and take folders cannot. */
  audio?: boolean;
}

/** What a drag mode does to a region besides the move itself. */
export type DragAdjustment =
  | { kind: 'move'; id: string; startTick: number }
  | { kind: 'trimEnd'; id: string; endTick: number }
  | { kind: 'delete'; id: string }
  /** Fade lengths in ticks (the caller turns them into frames). */
  | { kind: 'fade'; id: string; fadeInTicks?: number; fadeOutTicks?: number };

/**
 * What `mode` does after `moved` (where the drag put them) land among `others` (the regions that stayed). Shuffle
 * modes move the moved regions further; No Overlap trims; X-Fade adds fades over each overlap.
 */
export function applyDragMode(mode: DragMode, moved: readonly Span[], others: readonly Span[]): DragAdjustment[] {
  switch (mode) {
    case 'overlap':
      return [];
    case 'shuffleLeft':
    case 'shuffleRight':
      return shuffle(mode, moved, others);
    case 'noOverlap':
      return noOverlap([...moved, ...others]);
    case 'crossfade':
      return crossfades([...moved, ...others]);
  }
}

/** Shuffle L: a moved region starts where the region before it on its track ends (the project start when none). */
function shuffle(mode: 'shuffleLeft' | 'shuffleRight', moved: readonly Span[], others: readonly Span[]): DragAdjustment[] {
  const result: DragAdjustment[] = [];
  const placed: Span[] = [...others];
  const order = [...moved].sort((a, b) => (mode === 'shuffleLeft' ? a.startTick - b.startTick : b.startTick - a.startTick));
  for (const m of order) {
    const length = m.endTick - m.startTick;
    const lane = placed.filter(o => o.trackId === m.trackId);
    let start: number;
    if (mode === 'shuffleLeft') {
      const before = lane.filter(o => o.startTick <= m.startTick);
      start = before.length ? Math.max(...before.map(o => o.endTick)) : 0;
    } else {
      const after = lane.filter(o => o.startTick >= m.startTick);
      start = after.length ? Math.min(...after.map(o => o.startTick)) - length : m.startTick;
    }
    start = Math.max(0, Math.round(start));
    placed.push({ ...m, startTick: start, endTick: start + length });
    if (start !== m.startTick) {
      result.push({ kind: 'move', id: m.id, startTick: start });
    }
  }
  return result;
}

/** Pairs of regions on one track that overlap: the earlier (by start) and the later. */
function overlaps(spans: readonly Span[]): [Span, Span][] {
  const pairs: [Span, Span][] = [];
  const byTrack = new Map<string, Span[]>();
  for (const s of spans) {
    byTrack.set(s.trackId, [...(byTrack.get(s.trackId) ?? []), s]);
  }
  for (const lane of byTrack.values()) {
    lane.sort((a, b) => a.startTick - b.startTick || a.id.localeCompare(b.id));
    for (let i = 0; i < lane.length; i++) {
      for (let k = i + 1; k < lane.length; k++) {
        const a = lane[i] as Span;
        const b = lane[k] as Span;
        if (b.startTick >= a.endTick) {
          break;
        }
        pairs.push([a, b]);
      }
    }
  }
  return pairs;
}

/** No Overlap: each region that runs into a later one ends where it starts; one that would vanish is deleted. */
function noOverlap(spans: readonly Span[]): DragAdjustment[] {
  const ends = new Map<string, number>();
  for (const [a, b] of overlaps(spans)) {
    ends.set(a.id, Math.min(ends.get(a.id) ?? a.endTick, b.startTick));
  }
  const byId = new Map(spans.map(s => [s.id, s]));
  return [...ends].map(([id, endTick]) =>
    endTick <= (byId.get(id)?.startTick ?? 0) ? { kind: 'delete', id } : { kind: 'trimEnd', id, endTick }
  );
}

/** X-Fade: over each overlap of two audio regions, the earlier fades out and the later fades in. */
function crossfades(spans: readonly Span[]): DragAdjustment[] {
  const fades = new Map<string, { fadeInTicks?: number; fadeOutTicks?: number }>();
  for (const [a, b] of overlaps(spans)) {
    if (a.audio === false || b.audio === false) {
      continue;
    }
    const overlap = Math.min(a.endTick, b.endTick) - b.startTick;
    if (overlap <= 0 || b.endTick <= a.endTick) {
      continue; // one inside the other is not a crossfade
    }
    fades.set(a.id, { ...fades.get(a.id), fadeOutTicks: overlap });
    fades.set(b.id, { ...fades.get(b.id), fadeInTicks: overlap });
  }
  return [...fades].map(([id, f]) => ({ kind: 'fade', id, ...f }));
}

/**
 * A crossfade of `ticks` centred on the boundary where `earlier` ends and `later` starts (the Fade tool across two
 * touching regions): how far each must extend over the other (limited by the audio each has beyond its edge) and the
 * fades over the overlap. Null when they do not touch or have nothing to extend.
 */
export function crossfadeAt(
  earlier: { endTick: number; spareAfterTicks: number },
  later: { startTick: number; spareBeforeTicks: number },
  ticks: number
): { extendEarlier: number; extendLater: number; overlap: number } | null {
  if (Math.abs(later.startTick - earlier.endTick) > 1 || ticks <= 0) {
    return null;
  }
  const half = ticks / 2;
  const extendEarlier = Math.max(0, Math.min(half, earlier.spareAfterTicks));
  const extendLater = Math.max(0, Math.min(half, later.spareBeforeTicks));
  const overlap = extendEarlier + extendLater;
  return overlap > 0 ? { extendEarlier, extendLater, overlap } : null;
}
