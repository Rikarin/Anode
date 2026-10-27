import type { AudioRegion, Track } from '@anode/api-client';
import { createItems, createRegionDetails, ItemFlag, parseHexColor, type TimelineScene } from '@anode/timeline';
import { Ppq } from '../state/project.store';

/** A region's colour when its track's is not a `#rrggbb` the renderer can read. */
const Fallback = parseHexColor('#0090ff');

/** What the timeline draws, and how its item indices map back to region ids. */
export interface TracksScene {
  readonly scene: TimelineScene;
  /** Region id per item index. */
  readonly regionIds: readonly string[];
  /** Item index per region id. */
  readonly indexOf: ReadonlyMap<string, number>;
}

export interface SceneInput {
  tracks: readonly Track[];
  regions: readonly AudioRegion[];
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
 * The Tracks area's scene: one item per region on its track's lane, in its track's colour, with its file mapping,
 * fades, gain, name, waveform key and selection. Regions of tracks not in the list are left out.
 */
export function buildScene(input: SceneInput): TracksScene {
  const lanes = new Map(input.tracks.map((track, lane) => [track.id, lane]));
  const colors = new Map(input.tracks.map(track => [track.id, trackColor(track.color)]));
  const muted = new Set(input.tracks.filter(track => track.strip.mute).map(track => track.id));
  const regions = input.regions.filter(region => lanes.has(region.trackId));
  const items = createItems(regions.length);
  const details = createRegionDetails(
    regions.length,
    input.framesPerTick,
    regions.map(region => region.name)
  );
  const regionIds: string[] = [];
  const indexOf = new Map<string, number>();
  regions.forEach((region, i) => {
    items.start[i] = region.startTick;
    items.length[i] = region.length / input.framesPerTick;
    items.lane[i] = lanes.get(region.trackId) ?? 0;
    items.color[i] = colors.get(region.trackId) ?? Fallback;
    details.waveform[i] = input.waveformKey(region.fileId);
    details.fileOffset[i] = region.fileOffset;
    details.fadeIn[i] = region.fadeIn;
    details.fadeOut[i] = region.fadeOut;
    details.gain[i] = 10 ** (region.gainDb / 20);
    details.flags[i] =
      (input.selected.has(region.id) ? ItemFlag.Selected : 0) | (muted.has(region.trackId) ? ItemFlag.Muted : 0);
    regionIds.push(region.id);
    indexOf.set(region.id, i);
  });

  return {
    scene: {
      kind: 'regions',
      ppq: Ppq,
      beatsPerBar: input.beatsPerBar,
      laneCount: Math.max(1, input.tracks.length),
      items,
      regions: details
    },
    regionIds,
    indexOf
  };
}
