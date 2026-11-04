import type {
  AudioRegion,
  AutomationPoint,
  EditCommand,
  MidiRegion,
  PatternRegion,
  SessionPlayerRegion,
  TakeFolder,
  Track
} from '@anode/api-client';
import { deleteRange, laySpan, sliceRange } from '@anode/timeline';
import type { MarqueeSpan, RegionLookup } from './tool-edits';

/** What `pasteRegions` takes: whole regions of every kind. */
export interface RegionContent {
  readonly regions: readonly AudioRegion[];
  readonly midiRegions: readonly MidiRegion[];
  readonly takeFolders: readonly TakeFolder[];
  readonly patternRegions: readonly PatternRegion[];
  readonly sessionPlayerRegions: readonly SessionPlayerRegion[];
}

/** A region's span on its track. */
export interface ClipboardSpan extends MarqueeSpan {
  readonly trackId: string;
}

/** A time range of a track. */
export interface TrackRange {
  readonly trackId: string;
  readonly startTick: number;
  readonly endTick: number;
}

/** A stretch of an automation lane: its points in that range, with its values at the range's edges. */
export interface LaneSlice extends TrackRange {
  readonly target: string;
  readonly stepped: boolean;
  readonly points: readonly AutomationPoint[];
}

/** What a track must hold for its part of the clipboard: audio, MIDI, or the kind of a Session Player's track. */
export type TrackNeed = 'audio' | 'midi' | Track['kind'];

/** A track the clipboard came from: how many tracks below the topmost, and what it held. */
export interface ClipboardTrack {
  readonly trackId: string;
  readonly row: number;
  readonly needs: readonly TrackNeed[];
}

/**
 * What ⌘C and ⌘X keep: whole regions, because a cut deletes the originals, and the track automation under them. It is
 * the editor's and lasts until the next copy or another project (a region's files are its project's).
 */
export interface RegionClipboard {
  readonly projectId: string;
  /** Where the earliest of it starts: this lands on the playhead. */
  readonly startTick: number;
  readonly content: RegionContent;
  readonly lanes: readonly LaneSlice[];
  readonly tracks: readonly ClipboardTrack[];
}

/** The lanes of a project, as far as a clipboard needs them. */
export interface LaneSource {
  readonly trackId?: string | null;
  readonly target: string;
  readonly points: readonly AutomationPoint[];
}

export const NoContent: RegionContent = {
  regions: [],
  midiRegions: [],
  takeFolders: [],
  patternRegions: [],
  sessionPlayerRegions: []
};

/** The regions among `ids`, of every kind. */
export function contentOf(ids: readonly string[], lookup: RegionLookup): RegionContent {
  const pick = <T>(map: ReadonlyMap<string, T> | undefined): T[] =>
    ids.flatMap(id => {
      const found = map?.get(id);
      return found ? [found] : [];
    });
  return {
    regions: pick(lookup.regions),
    midiRegions: pick(lookup.midiRegions),
    takeFolders: pick(lookup.takeFolders),
    patternRegions: pick(lookup.patternRegions),
    sessionPlayerRegions: pick(lookup.sessionPlayerRegions)
  };
}

/** The part of a MIDI region over `from…to`: what starts there, as its right half would be after a split. */
function clipMidi(region: MidiRegion, from: number, to: number): MidiRegion {
  const start = from - region.startTick;
  const end = to - region.startTick;
  return {
    ...region,
    startTick: Math.round(from),
    lengthTicks: Math.round(to - from),
    loopTicks: 0,
    notes: region.notes
      .filter(n => n.startTick >= start && n.startTick < end)
      .map(n => ({ ...n, startTick: Math.round(n.startTick - start) })),
    controls: region.controls
      .filter(c => c.tick >= start && c.tick < end)
      .map(c => ({ ...c, tick: Math.round(c.tick - start) }))
  };
}

/** The part of an audio region over `from…to`, its flex markers and pitch notes cut to it as a split cuts them. */
function clipAudio(
  region: AudioRegion,
  span: MarqueeSpan,
  from: number,
  to: number,
  frames: RegionLookup['frames']
): AudioRegion | null {
  const cut = frames(span.startTick, from);
  const length = Math.min(frames(from, to), region.length - cut);
  if (length <= 0) {
    return null;
  }
  const first = region.fileOffset + cut;
  const last = first + length;
  const shift = from - span.startTick;
  const fadeIn = from > span.startTick ? 0 : Math.min(region.fadeIn, length);
  return {
    ...region,
    startTick: Math.round(from),
    fileOffset: first,
    length,
    loopTicks: 0,
    fadeIn,
    fadeOut: to < span.endTick ? 0 : Math.min(region.fadeOut, length - fadeIn),
    flexMarkers: region.flexMarkers
      .filter(m => m.fileFrame > first && m.fileFrame < last && m.tick - shift > 0)
      .map(m => ({ ...m, tick: Math.round(m.tick - shift) })),
    pitchNotes: region.pitchNotes
      .filter(n => n.endFrame > first && n.startFrame < last)
      .map(n => ({ ...n, startFrame: Math.max(n.startFrame, first), endFrame: Math.min(n.endFrame, last) }))
  };
}

/**
 * What a marquee selection holds, the regions `marqueeDeleteCommands` takes: those inside whole, audio and MIDI
 * regions across an edge cut to it. Other kinds across an edge stay out.
 */
export function marqueeContent(
  from: number,
  to: number,
  spans: readonly MarqueeSpan[],
  lookup: RegionLookup
): RegionContent {
  const touched = spans.filter(s => s.endTick > from && s.startTick < to);
  const whole = contentOf(
    touched.filter(s => s.startTick >= from && s.endTick <= to).map(s => s.id),
    lookup
  );
  const across = touched.filter(s => s.startTick < from || s.endTick > to);
  return {
    ...whole,
    regions: [
      ...whole.regions,
      ...across.flatMap(span => {
        const region = lookup.regions.get(span.id);
        const lo = Math.max(from, span.startTick);
        const clipped = region && clipAudio(region, span, lo, Math.min(to, span.endTick), lookup.frames);
        return clipped ? [clipped] : [];
      })
    ],
    midiRegions: [
      ...whole.midiRegions,
      ...across.flatMap(span => {
        const region = lookup.midiRegions.get(span.id);
        return region ? [clipMidi(region, Math.max(from, span.startTick), Math.min(to, span.endTick))] : [];
      })
    ]
  };
}

/** Whether a clipboard would paste anything. */
export function isEmpty(content: RegionContent, lanes: readonly LaneSlice[] = []): boolean {
  return regionCount(content) === 0 && lanes.length === 0;
}

export function regionCount(content: RegionContent): number {
  return (
    content.regions.length +
    content.midiRegions.length +
    content.takeFolders.length +
    content.patternRegions.length +
    content.sessionPlayerRegions.length
  );
}

/** Each track's spans merged where they touch: the ranges whose automation goes with the regions. */
export function mergedRanges(spans: readonly TrackRange[]): TrackRange[] {
  const ranges: TrackRange[] = [];
  const ordered = [...spans].sort((a, b) => a.trackId.localeCompare(b.trackId) || a.startTick - b.startTick);
  for (const span of ordered) {
    const last = ranges.at(-1);
    if (last?.trackId === span.trackId && span.startTick <= last.endTick) {
      ranges[ranges.length - 1] = { ...last, endTick: Math.max(last.endTick, span.endTick) };
    } else {
      ranges.push({ trackId: span.trackId, startTick: span.startTick, endTick: span.endTick });
    }
  }
  return ranges;
}

/**
 * The automation under `ranges`: a lane counts where it has points inside a range, so a constant value elsewhere on the
 * track does not stamp itself over the place it is pasted.
 */
export function laneSlices(
  ranges: readonly TrackRange[],
  lanes: readonly LaneSource[],
  stepped: (trackId: string, target: string) => boolean
): LaneSlice[] {
  return ranges.flatMap(range => {
    const startTick = Math.round(range.startTick);
    const endTick = Math.round(range.endTick);
    return lanes
      .filter(lane => lane.trackId === range.trackId)
      .flatMap(lane => {
        const isStepped = stepped(range.trackId, lane.target);
        const points = sliceRange(lane.points, startTick, endTick, isStepped);
        return points.length > 0
          ? [{ trackId: range.trackId, target: lane.target, stepped: isStepped, startTick, endTick, points }]
          : [];
      });
  });
}

function automationCommand(trackId: string, target: string, points: readonly AutomationPoint[]): EditCommand {
  return { type: 'setAutomation', trackId, target, points: [...points] };
}

/** The slices taken out of their lanes, keeping the values around them (as Delete does a range). */
export function cutLaneCommands(slices: readonly LaneSlice[], lanes: readonly LaneSource[]): EditCommand[] {
  const edited = new Map<string, { trackId: string; target: string; points: readonly AutomationPoint[] }>();
  for (const slice of slices) {
    const key = `${slice.trackId}|${slice.target}`;
    const points =
      edited.get(key)?.points ?? lanes.find(l => l.trackId === slice.trackId && l.target === slice.target)?.points;
    edited.set(key, {
      trackId: slice.trackId,
      target: slice.target,
      points: deleteRange(points ?? [], slice.startTick, slice.endTick, slice.stepped)
    });
  }
  return [...edited.values()].map(e => automationCommand(e.trackId, e.target, e.points));
}

/** What a track must hold for each source track of `content`. */
export function trackNeeds(content: RegionContent, kindOf: (trackId: string) => Track['kind'] | undefined) {
  const needs = new Map<string, Set<TrackNeed>>();
  const add = (trackId: string, need: TrackNeed) => needs.set(trackId, (needs.get(trackId) ?? new Set()).add(need));
  [...content.regions, ...content.takeFolders].forEach(r => add(r.trackId, 'audio'));
  [...content.midiRegions, ...content.patternRegions].forEach(r => add(r.trackId, 'midi'));
  content.sessionPlayerRegions.forEach(r => add(r.trackId, kindOf(r.trackId) ?? 'drummer'));
  return needs;
}

/** Whether a track can hold what `need` asks (the Server's `HoldsMidi`, an audio track, or the same player's). */
export function holds(track: Pick<Track, 'kind' | 'drumMachine'>, need: TrackNeed): boolean {
  if (need === 'midi') {
    return (
      ['instrument', 'midi', 'bassPlayer', 'keyboardPlayer'].includes(track.kind) ||
      (track.drumMachine ?? null) !== null
    );
  }
  return track.kind === need;
}

/**
 * Where each source track lands: the topmost on `anchor`, the others as many tracks below it as they were below the
 * topmost, moving on down past tracks that cannot hold them. Null when the anchor cannot, or the tracks run out.
 */
export function placeTracks(
  sources: readonly ClipboardTrack[],
  anchor: string,
  tracks: readonly Pick<Track, 'id' | 'kind' | 'drumMachine'>[]
): Map<string, string> | null {
  const first = tracks.findIndex(t => t.id === anchor);
  const top = sources[0];
  if (first < 0 || !top) {
    return null;
  }
  const fits = (index: number, source: ClipboardTrack) => {
    const track = tracks[index];
    return !!track && source.needs.every(need => holds(track, need));
  };
  const places = new Map<string, string>();
  let slip = 0;
  let previous = first - 1;
  for (const source of sources) {
    let index = Math.max(first + source.row - top.row + slip, previous + 1);
    while (index < tracks.length && !fits(index, source) && source !== top) {
      index++;
    }
    const track = tracks[index];
    if (!track || !fits(index, source)) {
      return null;
    }
    slip = index - (first + source.row - top.row);
    previous = index;
    places.set(source.trackId, track.id);
  }
  return places;
}

/** A take folder `by` ticks later: its takes and comp with it. */
function shiftedFolder(folder: TakeFolder, by: number): TakeFolder {
  return {
    ...folder,
    startTick: folder.startTick + by,
    takes: folder.takes.map(t => ({ ...t, startTick: t.startTick + by })),
    comp: folder.comp.map(s => ({ ...s, startTick: s.startTick + by, endTick: s.endTick + by }))
  };
}

/** `pasteRegions` of `content` `offset` ticks later, on the tracks `places` gives. */
export function pasteCommand(content: RegionContent, offset: number, places: ReadonlyMap<string, string>): EditCommand {
  const placed = <T extends { startTick: number; trackId: string }>(region: T): T => ({
    ...region,
    startTick: region.startTick + offset,
    trackId: places.get(region.trackId) ?? region.trackId
  });
  return {
    type: 'pasteRegions',
    regions: content.regions.map(placed),
    midiRegions: content.midiRegions.map(placed),
    takeFolders: content.takeFolders.map(f => ({
      ...shiftedFolder(f, offset),
      trackId: places.get(f.trackId) ?? f.trackId
    })),
    patternRegions: content.patternRegions.map(placed),
    sessionPlayerRegions: content.sessionPlayerRegions.map(placed)
  };
}

/**
 * The slices laid over the lanes of the tracks they land on, replacing what those lanes had there. A slice whose target
 * the track does not have (`accepts`: a plugin of another track) stays behind, and `skipped` counts it.
 */
export function pasteLaneCommands(
  slices: readonly LaneSlice[],
  offset: number,
  places: ReadonlyMap<string, string>,
  lanes: readonly LaneSource[],
  accepts: (trackId: string, slice: LaneSlice) => boolean
): { commands: EditCommand[]; skipped: number } {
  const edited = new Map<string, { trackId: string; target: string; points: readonly AutomationPoint[] }>();
  let skipped = 0;
  for (const slice of slices) {
    const trackId = places.get(slice.trackId);
    if (!trackId || !accepts(trackId, slice)) {
      skipped++;
      continue;
    }
    const key = `${trackId}|${slice.target}`;
    const points =
      edited.get(key)?.points ?? lanes.find(l => l.trackId === trackId && l.target === slice.target)?.points;
    const stroke = slice.points.map(p => ({ ...p, tick: p.tick + offset }));
    edited.set(key, { trackId, target: slice.target, points: laySpan(points ?? [], stroke, slice.stepped) });
  }
  return { commands: [...edited.values()].map(e => automationCommand(e.trackId, e.target, e.points)), skipped };
}
