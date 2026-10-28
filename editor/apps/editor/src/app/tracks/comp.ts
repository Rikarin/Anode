import type { CompSegment, TakeFolder } from '@anode/api-client';

/** The shortest comp segment Quick Swipe keeps, in ticks (shorter leftovers are dropped). */
export const MinSegmentTicks = 10;

/** Segments in time order, with neighbouring segments of the same take merged and empty ones dropped. */
export function normalizeComp(segments: readonly CompSegment[]): CompSegment[] {
  const sorted = segments
    .filter(s => s.endTick - s.startTick >= MinSegmentTicks)
    .map(s => ({ ...s }))
    .sort((a, b) => a.startTick - b.startTick);
  const merged: CompSegment[] = [];
  for (const s of sorted) {
    const last = merged.at(-1);
    if (last?.takeId === s.takeId && s.startTick <= last.endTick) {
      last.endTick = Math.max(last.endTick, s.endTick);
    } else {
      merged.push(s);
    }
  }
  return merged;
}

/**
 * Quick Swipe Comping: a swipe across a take from `from` to `to` makes that range of the comp come from that take.
 * What other segments covered there is cut away (a segment the swipe falls inside splits in two); the folder's span
 * bounds the swipe.
 */
export function swipeComp(
  comp: readonly CompSegment[],
  takeId: string,
  from: number,
  to: number,
  folder: { startTick: number; lengthTicks: number }
): CompSegment[] {
  const start = Math.max(folder.startTick, Math.round(Math.min(from, to)));
  const end = Math.min(folder.startTick + folder.lengthTicks, Math.round(Math.max(from, to)));
  if (end - start < MinSegmentTicks) {
    return normalizeComp(comp);
  }

  const kept: CompSegment[] = [];
  for (const s of comp) {
    if (s.endTick <= start || s.startTick >= end) {
      kept.push({ ...s });
      continue;
    }
    if (s.startTick < start) {
      kept.push({ ...s, endTick: start });
    }
    if (s.endTick > end) {
      kept.push({ ...s, startTick: end });
    }
  }
  kept.push({ startTick: start, endTick: end, takeId });
  return normalizeComp(kept);
}

/** Removes a range from the comp (an ⌥-swipe in Logic: that range plays nothing). */
export function clearComp(comp: readonly CompSegment[], from: number, to: number): CompSegment[] {
  const start = Math.min(from, to);
  const end = Math.max(from, to);
  return normalizeComp(
    comp.flatMap(s => {
      if (s.endTick <= start || s.startTick >= end) {
        return [s];
      }
      return [
        ...(s.startTick < start ? [{ ...s, endTick: start }] : []),
        ...(s.endTick > end ? [{ ...s, startTick: end }] : [])
      ];
    })
  );
}

/** The take the comp plays at `tick`, or null. */
export function takeAt(comp: readonly CompSegment[], tick: number): string | null {
  return comp.find(s => s.startTick <= tick && tick < s.endTick)?.takeId ?? null;
}

/** The comp segments of one take (what a take row highlights). */
export function segmentsOf(comp: readonly CompSegment[], takeId: string): CompSegment[] {
  return comp.filter(s => s.takeId === takeId);
}

/** Whether the comp is one whole take (Logic shows the take's name then, not "Comp"). */
export function wholeTake(folder: Pick<TakeFolder, 'comp' | 'startTick' | 'lengthTicks'>): string | null {
  const [only] = folder.comp;
  return folder.comp.length === 1 &&
    only &&
    only.startTick <= folder.startTick &&
    only.endTick >= folder.startTick + folder.lengthTicks
    ? only.takeId
    : null;
}

/**
 * Whether these regions can be packed into one take folder: two or more, all on one track, each overlapping or
 * touching the span of those before it.
 */
export function canPack(regions: readonly { trackId: string; startTick: number; endTick: number }[]): boolean {
  if (regions.length < 2 || new Set(regions.map(r => r.trackId)).size !== 1) {
    return false;
  }
  const sorted = [...regions].sort((a, b) => a.startTick - b.startTick);
  let end = sorted[0]?.endTick ?? 0;
  for (const r of sorted.slice(1)) {
    if (r.startTick > end + 1) {
      return false;
    }
    end = Math.max(end, r.endTick);
  }
  return true;
}
