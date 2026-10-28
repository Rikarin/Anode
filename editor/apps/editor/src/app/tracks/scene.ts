import type { AudioRegion, MidiRegion, Track } from '@anode/api-client';
import {
  createItems,
  createNoteMiniatures,
  createRegionDetails,
  ItemFlag,
  parseHexColor,
  type TimelineScene
} from '@anode/timeline';
import { Ppq } from '../state/project.store';

/** A region's colour when its track's is not a `#rrggbb` the renderer can read. */
const Fallback = parseHexColor('#0090ff');

/** What the timeline draws, and how its item indices map back to region ids. */
export interface TracksScene {
  readonly scene: TimelineScene;
  /** Region id per item index (audio regions first, then MIDI regions). */
  readonly regionIds: readonly string[];
  /** Item index per region id. */
  readonly indexOf: ReadonlyMap<string, number>;
}

export interface SceneInput {
  tracks: readonly Track[];
  regions: readonly AudioRegion[];
  midiRegions?: readonly MidiRegion[];
  framesPerTick: number;
  /** Quarter notes per bar (a 6/8 bar is 3). */
  beatsPerBar: number;
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

/**
 * The Tracks area's scene: one item per region on its track's lane, in its track's colour, with its selection. Audio
 * regions carry their file mapping, fades, gain, name and waveform key; MIDI regions (flagged {@link ItemFlag.Midi})
 * their name and note miniatures. Regions of tracks not in the list are left out.
 */
export function buildScene(input: SceneInput): TracksScene {
  const lanes = new Map(input.tracks.map((track, lane) => [track.id, lane]));
  const colors = new Map(input.tracks.map(track => [track.id, trackColor(track.color)]));
  const muted = new Set(input.tracks.filter(track => track.strip.mute).map(track => track.id));
  const regions = input.regions.filter(region => lanes.has(region.trackId));
  const midi = (input.midiRegions ?? []).filter(region => lanes.has(region.trackId));
  const count = regions.length + midi.length;
  const items = createItems(count);
  const details = createRegionDetails(count, input.framesPerTick, [
    ...regions.map(region => region.name),
    ...midi.map(region => region.name)
  ]);
  const regionIds: string[] = [];
  const indexOf = new Map<string, number>();
  const common = (i: number, id: string, trackId: string, start: number, length: number) => {
    items.start[i] = start;
    items.length[i] = length;
    items.lane[i] = lanes.get(trackId) ?? 0;
    items.color[i] = colors.get(trackId) ?? Fallback;
    details.flags[i] = (input.selected.has(id) ? ItemFlag.Selected : 0) | (muted.has(trackId) ? ItemFlag.Muted : 0);
    regionIds.push(id);
    indexOf.set(id, i);
  };
  regions.forEach((region, i) => {
    common(i, region.id, region.trackId, region.startTick, region.length / input.framesPerTick);
    details.waveform[i] = input.waveformKey(region.fileId);
    details.fileOffset[i] = region.fileOffset;
    details.fadeIn[i] = region.fadeIn;
    details.fadeOut[i] = region.fadeOut;
    details.gain[i] = 10 ** (region.gainDb / 20);
  });
  midi.forEach((region, k) => {
    const i = regions.length + k;
    common(i, region.id, region.trackId, region.startTick, region.lengthTicks);
    details.flags[i] = (details.flags[i] ?? 0) | ItemFlag.Midi;
  });
  const notes = createNoteMiniatures([
    ...regions.map(() => []),
    ...midi.map(region =>
      region.notes.map(note => ({
        start: note.startTick,
        length: note.lengthTicks,
        pitch: Math.min(127, Math.max(0, note.pitch + region.playback.transpose))
      }))
    )
  ]);

  return {
    scene: {
      kind: 'regions',
      ppq: Ppq,
      beatsPerBar: input.beatsPerBar,
      laneCount: Math.max(1, input.tracks.length),
      items,
      regions: { ...details, notes }
    },
    regionIds,
    indexOf
  };
}
