import type { AudioRegion, EditCommand, MidiRegion } from '@anode/api-client';
import { snapTick } from '@anode/timeline';

/** The shortest a trim leaves a region, in frames. */
export const MinRegionFrames = 64;

/** What edits of a region need to know besides the region. */
export interface EditContext {
  /** Audio frames per tick at the project's tempo and sample rate. */
  framesPerTick: number;
  /** The snap step in ticks; 0 when snapping is off. */
  snap: number;
  /** Snaps a position (a snap mode other than a fixed step: bars of the signature track, frames…); else `snap`. */
  snapTo?: (tick: number) => number;
}

/** A position snapped as the context says. */
export function snapIn(context: Pick<EditContext, 'snap' | 'snapTo'>, tick: number): number {
  return context.snapTo ? context.snapTo(tick) : snapTick(tick, context.snap);
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

  let delta = snapIn(context, grabbed.startTick + deltaTick) - grabbed.startTick;
  const earliest = Math.min(...regions.map(r => r.startTick));
  delta = Math.max(delta, -earliest);
  const lowest = Math.min(...regions.map(r => r.lane));
  const highest = Math.max(...regions.map(r => r.lane));
  const lanes = Math.min(Math.max(deltaLane, -lowest), laneCount - 1 - highest);
  return regions.map(r => ({ id: r.id, startTick: Math.round(r.startTick + delta), lane: r.lane + lanes }));
}

/**
 * The commands for a move: only the regions that moved, with the track when it changed. A region dropped on a track
 * that cannot hold it (audio on a MIDI track, MIDI on an audio track: `fits` says) moves in time on its own track.
 */
export function moveCommands(
  moved: readonly MovedRegion[],
  regions: ReadonlyMap<string, Pick<AudioRegion, 'startTick' | 'trackId'>>,
  trackAt: (lane: number) => string | undefined,
  fits: (regionId: string, trackId: string) => boolean = () => true
): EditCommand[] {
  const commands: EditCommand[] = [];
  for (const m of moved) {
    const region = regions.get(m.id);
    const landed = trackAt(m.lane);
    const trackId = landed && fits(m.id, landed) ? landed : region?.trackId;
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
  const wanted = snapIn(context, tick);
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
  const wanted = snapIn(context, tick);
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
  const at = snapIn(context, tick);
  const end = region.startTick + region.length / context.framesPerTick;
  return at > region.startTick && at < end ? Math.round(at) : null;
}

/** The shortest a trim leaves a MIDI region, in ticks, when snapping is off. */
export const MinMidiRegionTicks = 60;

/** A MIDI region's span after a trim; its notes stay where they are in time (`resizeMidiRegion`). */
export interface TrimmedMidi {
  startTick: number;
  lengthTicks: number;
}

/** Moves a MIDI region's start to `tick` (snapped): not before the project start, nor past its end less a step. */
export function trimMidiStart(
  region: Pick<MidiRegion, 'startTick' | 'lengthTicks'>,
  tick: number,
  snap: number
): TrimmedMidi {
  const end = region.startTick + region.lengthTicks;
  const minimum = snap > 0 ? snap : MinMidiRegionTicks;
  const start = Math.max(0, Math.min(end - minimum, snapTick(tick, snap)));
  return { startTick: start, lengthTicks: end - start };
}

/** Moves a MIDI region's end to `tick` (snapped), at least a step after its start. Nothing limits it on the right. */
export function trimMidiEnd(
  region: Pick<MidiRegion, 'startTick' | 'lengthTicks'>,
  tick: number,
  snap: number
): TrimmedMidi {
  const minimum = snap > 0 ? snap : MinMidiRegionTicks;
  return { startTick: region.startTick, lengthTicks: Math.max(minimum, snapTick(tick, snap) - region.startTick) };
}

/** Where the scissors cut a span clicked at `tick` (snapped), or null when that is not strictly inside it. */
export function splitSpan(start: number, end: number, tick: number, snap: number): number | null {
  const at = snapTick(tick, snap);
  return at > start && at < end ? Math.round(at) : null;
}

/**
 * The empty MIDI region the pencil (or a double-click on a MIDI track's empty lane) creates at `tick`: one bar from the
 * bar line at or before it.
 */
export function newMidiRegion(tick: number, barTicks: number): TrimmedMidi {
  return { startTick: Math.max(0, Math.floor(tick / barTicks) * barTicks), lengthTicks: barTicks };
}

/** "Move Region" or "Move 3 Regions": undo labels that say what the step did. */
export function plural(verb: string, count: number, noun = 'Region'): string {
  return count === 1 ? `${verb} ${noun}` : `${verb} ${count} ${noun}s`;
}
