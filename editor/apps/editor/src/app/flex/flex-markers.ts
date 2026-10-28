import type { FlexMarker } from '@anode/api-client';
import type { RegionTiming } from '../music/region-timing';

/** How close (CSS pixels) the pointer must be to a marker or a transient to grab it. */
export const MarkerGrabPx = 5;

/**
 * What a press in a region's flex view does (Logic's Flex Time pointer):
 * - `marker`: on a flex marker: drag it.
 * - `transient` (upper half, on a transient marker): make a flex marker on the transient and drag it.
 * - `neighbours` (upper half, elsewhere): make a flex marker under the pointer and on the transients either side of it
 *   (so only the audio between them stretches), and drag the middle one.
 * - `single` (lower half): make one flex marker under the pointer and drag it.
 */
export type FlexZone = 'marker' | 'transient' | 'neighbours' | 'single';

export interface FlexHit {
  zone: FlexZone;
  /** The flex marker under the pointer (`marker`). */
  index?: number;
  /** The file frame the new marker goes on (`transient`, `neighbours`, `single`). */
  frame?: number;
}

/** A flex drag in progress: the markers with the new ones in, and which one moves. */
export interface FlexDrag {
  markers: FlexMarker[];
  index: number;
}

/** Markers in file-frame order. */
export function sortMarkers(markers: readonly FlexMarker[]): FlexMarker[] {
  return [...markers].sort((a, b) => a.fileFrame - b.fileFrame);
}

/**
 * What a press at `tick` (absolute) in a region does: on a flex marker (within {@link MarkerGrabPx}), on a transient in
 * the upper half, or elsewhere in the upper or lower half.
 */
export function flexHit(
  timing: RegionTiming,
  transients: readonly number[],
  tick: number,
  upper: boolean,
  ticksPerPixel: number
): FlexHit {
  const grab = MarkerGrabPx * ticksPerPixel;
  const markers = sortMarkers(timing.region.flexMarkers);
  let best = -1;
  let distance = grab;
  markers.forEach((m, i) => {
    const d = Math.abs(timing.startTick + m.tick - tick);
    if (d <= distance) {
      best = i;
      distance = d;
    }
  });
  if (best >= 0) {
    return { zone: 'marker', index: best };
  }
  if (upper) {
    const transient = nearestTransient(timing, transients, tick, grab);
    if (transient !== null) {
      return { zone: 'transient', frame: transient };
    }
    return { zone: 'neighbours', frame: Math.round(timing.frameAt(tick)) };
  }
  return { zone: 'single', frame: Math.round(timing.frameAt(tick)) };
}

/** The transient (file frame, inside the region) drawn within `grab` ticks of `tick`, or null. */
export function nearestTransient(
  timing: RegionTiming,
  transients: readonly number[],
  tick: number,
  grab: number
): number | null {
  let found: number | null = null;
  let distance = grab;
  for (const frame of transients) {
    if (!inside(timing, frame)) {
      continue;
    }
    const d = Math.abs(timing.tickAt(frame) - tick);
    if (d <= distance) {
      found = frame;
      distance = d;
    }
  }
  return found;
}

function inside(timing: RegionTiming, frame: number): boolean {
  const { fileOffset, length } = timing.region;
  return frame > fileOffset && frame < fileOffset + length;
}

/** A marker that keeps `frame` where it plays now (adding it changes nothing until it is dragged). */
function markerAt(timing: RegionTiming, frame: number): FlexMarker {
  return { fileFrame: Math.round(frame), tick: Math.round(timing.tickAt(frame) - timing.startTick) };
}

/** Adds a marker at `frame` unless one is there; returns the markers and its index. */
function withMarker(timing: RegionTiming, markers: readonly FlexMarker[], frame: number): FlexDrag {
  const existing = markers.findIndex(m => m.fileFrame === Math.round(frame));
  if (existing >= 0) {
    return { markers: [...markers], index: existing };
  }
  const added = markerAt(timing, frame);
  const next = sortMarkers([...markers, added]);
  return { markers: next, index: next.indexOf(added) };
}

/** Starts a drag for a hit: the markers (new ones added where they play now) and the one that moves. */
export function beginFlexDrag(timing: RegionTiming, transients: readonly number[], hit: FlexHit): FlexDrag | null {
  const markers = sortMarkers(timing.region.flexMarkers);
  if (hit.zone === 'marker') {
    return hit.index === undefined ? null : { markers, index: hit.index };
  }
  const frame = hit.frame;
  if (frame === undefined || !inside(timing, frame)) {
    return null;
  }
  if (hit.zone !== 'neighbours') {
    return withMarker(timing, markers, frame);
  }
  const before = [...transients].filter(f => inside(timing, f) && f < frame).pop();
  const after = transients.find(f => inside(timing, f) && f > frame);
  let next = markers;
  for (const f of [before, after]) {
    if (f !== undefined) {
      next = withMarker(timing, next, f).markers;
    }
  }
  return withMarker(timing, next, frame);
}

/**
 * Moves marker `index` to `tick` (relative to the region's start): it stays at least `gap` ticks after the marker
 * before it (or the region's start) and before the one after it (or the region's end).
 */
export function dragFlexMarker(drag: FlexDrag, tick: number, lengthTicks: number, gap = 1): FlexMarker[] {
  const markers = drag.markers.map(m => ({ ...m }));
  const marker = markers[drag.index];
  if (!marker) {
    return markers;
  }
  const lo = (markers[drag.index - 1]?.tick ?? 0) + gap;
  const hi = (markers[drag.index + 1]?.tick ?? Math.floor(lengthTicks)) - gap;
  marker.tick = Math.round(Math.min(hi, Math.max(lo, tick)));
  return markers;
}

/** The markers without marker `index` (double-click). */
export function deleteFlexMarker(markers: readonly FlexMarker[], index: number): FlexMarker[] {
  return sortMarkers(markers).filter((_, i) => i !== index);
}
