import type { AudioRegion, EditCommand } from '@anode/api-client';
import { snapTick } from '@anode/timeline';

/** The shortest a trim leaves a region, in frames. */
export const MinRegionFrames = 64;

/** What edits of a region need to know besides the region. */
export interface EditContext {
  /** Audio frames per tick at the project's tempo and sample rate. */
  framesPerTick: number;
  /** The snap step in ticks; 0 when snapping is off. */
  snap: number;
}

/** A region where a move would put it. */
export interface MovedRegion {
  id: string;
  startTick: number;
  lane: number;
}

/**
 * Where a drag moves the selected regions: by the same distance in time and in lanes. The grabbed region's start
 * snaps to the grid and the others keep their offsets from it; no region goes before the start or off the lanes.
 */
export function moveRegions(
  regions: readonly { id: string; startTick: number; lane: number }[],
  grabbed: { startTick: number },
  deltaTick: number,
  deltaLane: number,
  laneCount: number,
  context: EditContext
): MovedRegion[] {
  if (regions.length === 0) {
    return [];
  }

  let delta = snapTick(grabbed.startTick + deltaTick, context.snap) - grabbed.startTick;
  const earliest = Math.min(...regions.map(r => r.startTick));
  delta = Math.max(delta, -earliest);
  const lowest = Math.min(...regions.map(r => r.lane));
  const highest = Math.max(...regions.map(r => r.lane));
  const lanes = Math.min(Math.max(deltaLane, -lowest), laneCount - 1 - highest);
  return regions.map(r => ({ id: r.id, startTick: Math.round(r.startTick + delta), lane: r.lane + lanes }));
}

/** The commands for a move: only the regions that moved, with the track when it changed. */
export function moveCommands(
  moved: readonly MovedRegion[],
  regions: ReadonlyMap<string, AudioRegion>,
  trackAt: (lane: number) => string | undefined
): EditCommand[] {
  const commands: EditCommand[] = [];
  for (const m of moved) {
    const region = regions.get(m.id);
    const trackId = trackAt(m.lane);
    if (!region || !trackId || (region.startTick === m.startTick && region.trackId === trackId)) {
      continue;
    }

    commands.push({
      type: 'moveRegion',
      regionId: m.id,
      startTick: m.startTick,
      ...(trackId === region.trackId ? {} : { trackId })
    });
  }

  return commands;
}

/** A region's span after a trim. */
export interface Trimmed {
  startTick: number;
  fileOffset: number;
  length: number;
}

/**
 * Moves a region's start to `tick` (snapped), revealing or hiding the start of its audio: not before the file's first
 * frame or the project start, and not past its end less {@link MinRegionFrames}.
 */
export function trimStart(region: AudioRegion, tick: number, context: EditContext): Trimmed {
  const { framesPerTick } = context;
  const end = region.startTick + region.length / framesPerTick;
  const fileStart = region.startTick - region.fileOffset / framesPerTick;
  const wanted = snapTick(tick, context.snap);
  const start = Math.min(end - MinRegionFrames / framesPerTick, Math.max(0, fileStart, wanted));
  const shift = Math.max(-region.fileOffset, Math.round((start - region.startTick) * framesPerTick));
  return {
    startTick: Math.round(region.startTick + shift / framesPerTick),
    fileOffset: region.fileOffset + shift,
    length: region.length - shift
  };
}

/** Moves a region's end to `tick` (snapped): not past the end of its file, nor closer to its start than the minimum. */
export function trimEnd(region: AudioRegion, tick: number, fileFrames: number, context: EditContext): Trimmed {
  const wanted = snapTick(tick, context.snap);
  const frames = Math.round((wanted - region.startTick) * context.framesPerTick);
  const length = Math.min(fileFrames - region.fileOffset, Math.max(MinRegionFrames, frames));
  return { startTick: region.startTick, fileOffset: region.fileOffset, length };
}

/** The fade-in that ends at `tick`, in frames: from none to the region's length less its fade-out. Not snapped. */
export function fadeInTo(region: AudioRegion, tick: number, context: EditContext): number {
  const frames = Math.round((tick - region.startTick) * context.framesPerTick);
  return Math.max(0, Math.min(region.length - region.fadeOut, frames));
}

/** The fade-out that starts at `tick`, in frames. */
export function fadeOutFrom(region: AudioRegion, tick: number, context: EditContext): number {
  const end = region.startTick + region.length / context.framesPerTick;
  const frames = Math.round((end - tick) * context.framesPerTick);
  return Math.max(0, Math.min(region.length - region.fadeIn, frames));
}

/** Where the scissors cut a region clicked at `tick` (snapped), or null when that is not strictly inside it. */
export function splitPoint(region: AudioRegion, tick: number, context: EditContext): number | null {
  const at = snapTick(tick, context.snap);
  const end = region.startTick + region.length / context.framesPerTick;
  return at > region.startTick && at < end ? Math.round(at) : null;
}

/** "Move Region" or "Move 3 Regions": undo labels that say what the step did. */
export function plural(verb: string, count: number, noun = 'Region'): string {
  return count === 1 ? `${verb} ${noun}` : `${verb} ${count} ${noun}s`;
}
