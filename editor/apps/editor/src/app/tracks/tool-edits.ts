import type { AudioRegion, EditCommand, MidiRegion, TakeFolder } from '@anode/api-client';
import type { DragAdjustment } from './drag-modes';
import { crossfadeAt } from './drag-modes';

/** The regions of a project the tools edit, by id, with how to measure audio in ticks. */
export interface RegionLookup {
  regions: ReadonlyMap<string, AudioRegion>;
  midiRegions: ReadonlyMap<string, MidiRegion>;
  takeFolders: ReadonlyMap<string, TakeFolder>;
  /** Where an audio region ends, in ticks. */
  endOf: (region: Pick<AudioRegion, 'startTick' | 'length'>) => number;
  /** Frames between two ticks. */
  frames: (from: number, to: number) => number;
  /** A file's length in frames. */
  fileFrames: (fileId: string) => number;
}

/** The Mute tool (and ⌃M): mutes the regions, or unmutes them when the first is muted. */
export function muteCommands(ids: readonly string[], lookup: RegionLookup): EditCommand[] {
  const muted = (id: string) =>
    lookup.regions.get(id)?.muted ?? lookup.midiRegions.get(id)?.muted ?? lookup.takeFolders.get(id)?.muted ?? false;
  const first = ids[0];
  if (first === undefined) {
    return [];
  }
  const to = !muted(first);
  return ids.flatMap((id): EditCommand[] => {
    if (lookup.regions.has(id)) {
      return [{ type: 'setRegion', regionId: id, muted: to }];
    }
    if (lookup.midiRegions.has(id)) {
      return [{ type: 'setMidiRegion', regionId: id, muted: to }];
    }
    if (lookup.takeFolders.has(id)) {
      return [{ type: 'setTakeFolder', folderId: id, muted: to }];
    }
    return [];
  });
}

/** A rename by the Text tool, for any kind of region. */
export function renameCommand(id: string, name: string, lookup: RegionLookup): EditCommand | null {
  if (lookup.regions.has(id)) {
    return { type: 'setRegion', regionId: id, name };
  }
  if (lookup.midiRegions.has(id)) {
    return { type: 'setMidiRegion', regionId: id, name };
  }
  if (lookup.takeFolders.has(id)) {
    return { type: 'setTakeFolder', folderId: id, name };
  }
  return null;
}

/** An audio region's end moved to `endTick` (No Overlap), as a resize. */
function trimAudioEnd(region: AudioRegion, endTick: number, lookup: RegionLookup): EditCommand {
  return {
    type: 'resizeRegion',
    regionId: region.id,
    startTick: region.startTick,
    fileOffset: region.fileOffset,
    length: Math.max(1, lookup.frames(region.startTick, endTick))
  };
}

/** An audio region's start moved to `startTick`, keeping its end. */
function trimAudioStart(region: AudioRegion, startTick: number, lookup: RegionLookup): EditCommand {
  const cut = lookup.frames(region.startTick, startTick);
  return {
    type: 'resizeRegion',
    regionId: region.id,
    startTick: Math.round(startTick),
    fileOffset: region.fileOffset + cut,
    length: Math.max(1, region.length - cut)
  };
}

/**
 * The drag mode's adjustments as commands, merged into the move: a shuffled region's `moveRegion` takes its new start;
 * trims, deletions and crossfade fades are added.
 */
export function adjustmentCommands(
  moves: EditCommand[],
  adjustments: readonly DragAdjustment[],
  lookup: RegionLookup
): EditCommand[] {
  const commands = [...moves];
  for (const a of adjustments) {
    switch (a.kind) {
      case 'move': {
        const index = commands.findIndex(c => c.type === 'moveRegion' && c.regionId === a.id);
        const existing = commands[index];
        if (existing?.type === 'moveRegion') {
          commands[index] = { ...existing, startTick: a.startTick };
        } else {
          commands.push({ type: 'moveRegion', regionId: a.id, startTick: a.startTick });
        }
        break;
      }
      case 'trimEnd': {
        const audio = lookup.regions.get(a.id);
        const midi = lookup.midiRegions.get(a.id);
        const moved = commands.find(c => c.type === 'moveRegion' && c.regionId === a.id);
        const start = moved?.type === 'moveRegion' ? moved.startTick : (audio?.startTick ?? midi?.startTick ?? 0);
        if (audio) {
          commands.push(trimAudioEnd({ ...audio, startTick: start }, a.endTick, lookup));
        } else if (midi) {
          commands.push({ type: 'resizeMidiRegion', regionId: a.id, startTick: start, lengthTicks: a.endTick - start });
        }
        break;
      }
      case 'delete':
        commands.push({ type: 'deleteRegions', regionIds: [a.id] });
        break;
      case 'fade': {
        const audio = lookup.regions.get(a.id);
        if (!audio) {
          break;
        }
        const moved = commands.find(c => c.type === 'moveRegion' && c.regionId === a.id);
        const start = moved?.type === 'moveRegion' ? moved.startTick : audio.startTick;
        const end = lookup.endOf({ startTick: start, length: audio.length });
        commands.push({
          type: 'setRegion',
          regionId: a.id,
          ...(a.fadeInTicks !== undefined ? { fadeIn: lookup.frames(start, start + a.fadeInTicks) } : {}),
          ...(a.fadeOutTicks !== undefined ? { fadeOut: lookup.frames(end - a.fadeOutTicks, end) } : {})
        });
        break;
      }
    }
  }
  return commands;
}

/**
 * The Fade tool across the boundary of two audio regions on one track: a crossfade of `ticks` centred on it. Touching
 * regions extend over each other (as far as their files allow); overlapping ones fade over the overlap as it is.
 */
export function crossfadeCommands(
  earlier: AudioRegion,
  later: AudioRegion,
  ticks: number,
  lookup: RegionLookup
): EditCommand[] {
  const earlierEnd = lookup.endOf(earlier);
  if (later.startTick < earlierEnd - 1) {
    const overlap = lookup.frames(later.startTick, earlierEnd);
    return [
      { type: 'setRegion', regionId: earlier.id, fadeOut: Math.min(earlier.length, overlap) },
      { type: 'setRegion', regionId: later.id, fadeIn: Math.min(later.length, overlap) }
    ];
  }
  const spareAfterFrames = lookup.fileFrames(earlier.fileId) - earlier.fileOffset - earlier.length;
  const ticksPerFrame = (earlierEnd - earlier.startTick) / Math.max(1, earlier.length);
  const plan = crossfadeAt(
    { endTick: earlierEnd, spareAfterTicks: spareAfterFrames * ticksPerFrame },
    { startTick: later.startTick, spareBeforeTicks: later.fileOffset * ticksPerFrame },
    ticks
  );
  if (!plan) {
    return [];
  }
  const newEnd = earlierEnd + plan.extendEarlier;
  const newStart = later.startTick - plan.extendLater;
  const earlierLength = lookup.frames(earlier.startTick, newEnd);
  const laterCut = lookup.frames(newStart, later.startTick);
  const overlap = lookup.frames(newStart, newEnd);
  return [
    {
      type: 'resizeRegion',
      regionId: earlier.id,
      startTick: earlier.startTick,
      fileOffset: earlier.fileOffset,
      length: earlierLength
    },
    {
      type: 'resizeRegion',
      regionId: later.id,
      startTick: Math.round(newStart),
      fileOffset: Math.max(0, later.fileOffset - laterCut),
      length: later.length + laterCut
    },
    { type: 'setRegion', regionId: earlier.id, fadeOut: overlap },
    { type: 'setRegion', regionId: later.id, fadeIn: overlap }
  ];
}

/** A region's span as the marquee sees it. */
export interface MarqueeSpan {
  id: string;
  startTick: number;
  endTick: number;
}

/**
 * Deleting a marquee selection (Logic's marquee tool, then Delete): regions inside go, regions across an edge are
 * trimmed to it, a region across both edges is split at the end and trimmed at the start. Take folders inside go too.
 */
export function marqueeDeleteCommands(
  from: number,
  to: number,
  spans: readonly MarqueeSpan[],
  lookup: RegionLookup
): EditCommand[] {
  const commands: EditCommand[] = [];
  const deleted: string[] = [];
  for (const span of spans) {
    if (span.endTick <= from || span.startTick >= to) {
      continue;
    }
    const audio = lookup.regions.get(span.id);
    const midi = lookup.midiRegions.get(span.id);
    if (span.startTick >= from && span.endTick <= to) {
      deleted.push(span.id);
      continue;
    }
    if (!audio && !midi) {
      continue; // a take folder across an edge stays
    }
    if (span.startTick < from && span.endTick > to) {
      commands.push({ type: 'splitRegion', regionId: span.id, atTick: Math.round(to) });
    }
    if (span.startTick < from) {
      commands.push(
        audio
          ? trimAudioEnd(audio, from, lookup)
          : {
              type: 'resizeMidiRegion',
              regionId: span.id,
              startTick: span.startTick,
              lengthTicks: from - span.startTick
            }
      );
    } else if (span.endTick > to) {
      commands.push(
        audio
          ? trimAudioStart(audio, to, lookup)
          : { type: 'resizeMidiRegion', regionId: span.id, startTick: Math.round(to), lengthTicks: span.endTick - to }
      );
    }
  }
  if (deleted.length > 0) {
    commands.push({ type: 'deleteRegions', regionIds: deleted });
  }
  return commands;
}
