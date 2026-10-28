import type { AudioRegion, MidiRegion, TakeFolder, Track } from '@anode/api-client';
import {
  createItems,
  createNoteMiniatures,
  createRegionDetails,
  ItemFlag,
  type MeterEvent,
  parseHexColor,
  type TimelineScene
} from '@anode/timeline';
import type { LaneRow } from '../state/lanes';
import { Ppq } from '../state/project.store';
import { wholeTake } from './comp';

/** A region's colour when its track's is not a `#rrggbb` the renderer can read. */
const Fallback = parseHexColor('#0090ff');

/** What the timeline draws, and how its item indices map back to region ids. */
export interface TracksScene {
  readonly scene: TimelineScene;
  /**
   * Region id per item index: audio regions, MIDI regions, take folders (`tkf_…`), and takes in open folders as
   * `folderId/takeId`. A part drawn inside an item (a comp segment) maps to that item's id.
   */
  readonly regionIds: readonly string[];
  /** Item index per region id (whole items only). */
  readonly indexOf: ReadonlyMap<string, number>;
}

export interface SceneInput {
  tracks: readonly Track[];
  /** The Tracks area's rows; the tracks one to one when left out. */
  rows?: readonly LaneRow[];
  regions: readonly AudioRegion[];
  midiRegions?: readonly MidiRegion[];
  takeFolders?: readonly TakeFolder[];
  framesPerTick: number;
  /** Frames per tick over a span of audio, where the tempo changes (its average); `framesPerTick` when left out. */
  rate?: (span: { startTick: number; length: number }) => number;
  /** Quarter notes per bar (a 6/8 bar is 3). */
  beatsPerBar: number;
  /** The signature track. */
  meter?: readonly MeterEvent[];
  selected: ReadonlySet<string>;
  /** The waveform key the renderer knows a file's peaks by. */
  waveformKey: (fileId: string) => number;
}

/** A track colour for the renderer: `#rgb` and `#rrggbb` (the Server's colours); anything else falls back. */
export function trackColor(css: string): number {
  const short = /^#([0-9a-f])([0-9a-f])([0-9a-f])$/i.exec(css);
  const hex = short ? `#${short[1]}${short[1]}${short[2]}${short[2]}${short[3]}${short[3]}` : css;
  try {
    return parseHexColor(hex);
  } catch {
    return Fallback;
  }
}

/** One item before it is packed into the scene's columns. */
interface Draft {
  id: string;
  whole: boolean;
  lane: number;
  start: number;
  length: number;
  color: number;
  flags: number;
  name: string;
  waveform?: number;
  fileOffset?: number;
  fadeIn?: number;
  fadeOut?: number;
  fadeInCurve?: number;
  fadeOutCurve?: number;
  rate?: number;
  gain?: number;
  notes?: { start: number; length: number; pitch: number }[];
}

/**
 * The Tracks area's scene: one item per region on its track's row, in its track's colour, with its selection. Audio
 * regions carry their file mapping, fades and curves, gain, name and waveform; MIDI regions their note miniatures; take
 * folders show their comp (each segment's take waveform, drawn inside the folder); an open folder's takes are rows of
 * their own, greyed but for what the comp plays from them. Muted regions, folders and tracks are drawn muted.
 */
export function buildScene(input: SceneInput): TracksScene {
  const rows: readonly LaneRow[] =
    input.rows ?? input.tracks.map(track => ({ kind: 'track', id: track.id, track, depth: 0 }) as const);
  const lanes = new Map<string, number>();
  rows.forEach((row, lane) => {
    if (row.kind === 'track') {
      lanes.set(row.track.id, lane);
    } else {
      lanes.set(row.id, lane);
    }
  });
  const colors = new Map(input.tracks.map(track => [track.id, trackColor(track.color)]));
  const mutedTracks = new Set(input.tracks.filter(track => track.strip.mute).map(track => track.id));
  const rate = (span: { startTick: number; length: number }) => input.rate?.(span) ?? input.framesPerTick;
  const drafts: Draft[] = [];
  const flagsOf = (id: string, trackId: string, muted: boolean) =>
    (input.selected.has(id) ? ItemFlag.Selected : 0) | (muted || mutedTracks.has(trackId) ? ItemFlag.Muted : 0);

  for (const region of input.regions) {
    const lane = lanes.get(region.trackId);
    if (lane === undefined) {
      continue;
    }
    const r = rate(region);
    drafts.push({
      id: region.id,
      whole: true,
      lane,
      start: region.startTick,
      length: region.length / r,
      color: colors.get(region.trackId) ?? Fallback,
      flags: flagsOf(region.id, region.trackId, region.muted),
      name: region.name,
      waveform: input.waveformKey(region.fileId),
      fileOffset: region.fileOffset,
      fadeIn: region.fadeIn,
      fadeOut: region.fadeOut,
      fadeInCurve: region.fadeInCurve,
      fadeOutCurve: region.fadeOutCurve,
      rate: r,
      gain: 10 ** (region.gainDb / 20)
    });
  }
  for (const region of input.midiRegions ?? []) {
    const lane = lanes.get(region.trackId);
    if (lane === undefined) {
      continue;
    }
    drafts.push({
      id: region.id,
      whole: true,
      lane,
      start: region.startTick,
      length: region.lengthTicks,
      color: colors.get(region.trackId) ?? Fallback,
      flags: flagsOf(region.id, region.trackId, region.muted) | ItemFlag.Midi,
      name: region.name,
      notes: region.notes.map(note => ({
        start: note.startTick,
        length: note.lengthTicks,
        pitch: Math.min(127, Math.max(0, note.pitch + region.playback.transpose))
      }))
    });
  }
  for (const folder of input.takeFolders ?? []) {
    const lane = lanes.get(folder.trackId);
    if (lane === undefined) {
      continue;
    }
    const color = colors.get(folder.trackId) ?? Fallback;
    const only = wholeTake(folder);
    const label = only ? folder.takes.find(t => t.id === only)?.name : 'Comp';
    drafts.push({
      id: folder.id,
      whole: true,
      lane,
      start: folder.startTick,
      length: folder.lengthTicks,
      color,
      flags: flagsOf(folder.id, folder.trackId, folder.muted) | ItemFlag.Folder,
      name: `${folder.name}: ${label ?? 'Comp'}`
    });
    const takes = new Map(folder.takes.map(t => [t.id, t]));
    // The comp, inside the folder.
    for (const segment of folder.comp) {
      const take = takes.get(segment.takeId);
      if (take) {
        drafts.push(part(folder.id, lane, take, segment.startTick, segment.endTick, color, rate, input, folder.muted));
      }
    }
    // Open: each take in its row, greyed, with the comped parts in colour.
    folder.takes.forEach(take => {
      const takeLane = lanes.get(`${folder.id}/${take.id}`);
      if (takeLane === undefined) {
        return;
      }
      const r = rate(take);
      const key = `${folder.id}/${take.id}`;
      drafts.push({
        id: key,
        whole: true,
        lane: takeLane,
        start: take.startTick,
        length: take.length / r,
        color,
        flags: ItemFlag.Muted | ItemFlag.Folder | (input.selected.has(key) ? ItemFlag.Selected : 0),
        name: take.name,
        waveform: input.waveformKey(take.fileId),
        fileOffset: take.fileOffset,
        rate: r
      });
      for (const segment of folder.comp.filter(s => s.takeId === take.id)) {
        drafts.push(part(key, takeLane, take, segment.startTick, segment.endTick, color, rate, input, false));
      }
    });
  }

  const count = drafts.length;
  const items = createItems(count);
  const details = createRegionDetails(
    count,
    input.framesPerTick,
    drafts.map(d => d.name)
  );
  const regionIds: string[] = [];
  const indexOf = new Map<string, number>();
  drafts.forEach((d, i) => {
    items.start[i] = d.start;
    items.length[i] = d.length;
    items.lane[i] = d.lane;
    items.color[i] = d.color;
    details.flags[i] = d.flags;
    details.waveform[i] = d.waveform ?? -1;
    details.fileOffset[i] = d.fileOffset ?? 0;
    details.fadeIn[i] = d.fadeIn ?? 0;
    details.fadeOut[i] = d.fadeOut ?? 0;
    details.fadeInCurve[i] = d.fadeInCurve ?? 0;
    details.fadeOutCurve[i] = d.fadeOutCurve ?? 0;
    details.rate[i] = d.rate ?? 0;
    details.gain[i] = d.gain ?? 1;
    regionIds.push(d.id);
    if (d.whole) {
      indexOf.set(d.id, i);
    }
  });
  const notes = createNoteMiniatures(drafts.map(d => d.notes ?? []));

  return {
    scene: {
      kind: 'regions',
      ppq: Ppq,
      beatsPerBar: input.beatsPerBar,
      laneCount: Math.max(1, rows.length),
      items,
      regions: { ...details, notes },
      ...(input.meter ? { meter: input.meter } : {})
    },
    regionIds,
    indexOf
  };
}

/** A comped part of a take, from `from` to `to`, drawn inside the item `owner`. */
function part(
  owner: string,
  lane: number,
  take: TakeFolder['takes'][number],
  from: number,
  to: number,
  color: number,
  rate: (span: { startTick: number; length: number }) => number,
  input: SceneInput,
  muted: boolean
): Draft {
  const r = rate(take);
  return {
    id: owner,
    whole: false,
    lane,
    start: from,
    length: to - from,
    color,
    flags: ItemFlag.Inner | (muted ? ItemFlag.Muted : 0),
    name: '',
    waveform: input.waveformKey(take.fileId),
    fileOffset: take.fileOffset + (from - take.startTick) * r,
    rate: r
  };
}
