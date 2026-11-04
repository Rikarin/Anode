import { Injectable, computed, signal } from '@angular/core';
import { DragModes, type DragMode } from '../tracks/drag-modes';
import { SnapModes, type SnapMode } from '../tracks/snap';
import { Ppq } from './project.store';

/** The Tracks area's tools (Logic's Left-click and ⌘-click tool menus). */
export type TracksTool =
  'pointer' | 'pencil' | 'eraser' | 'text' | 'scissors' | 'glue' | 'solo' | 'mute' | 'zoom' | 'fade' | 'marquee';

/** The tools in Logic's order, each with the key that picks it in the tool menu (T, then the key). */
export const Tools: readonly { value: TracksTool; label: string; key: string; icon: string; cursor: string }[] = [
  { value: 'pointer', label: 'Pointer', key: 'T', icon: 'matNearMeRound', cursor: 'default' },
  { value: 'pencil', label: 'Pencil', key: 'P', icon: 'matEditRound', cursor: 'copy' },
  { value: 'eraser', label: 'Eraser', key: 'E', icon: 'matAutoFixNormalRound', cursor: 'not-allowed' },
  { value: 'text', label: 'Text', key: 'X', icon: 'matTextFieldsRound', cursor: 'text' },
  { value: 'scissors', label: 'Scissors', key: 'I', icon: 'matContentCutRound', cursor: 'crosshair' },
  { value: 'glue', label: 'Glue', key: 'G', icon: 'matJoinFullRound', cursor: 'cell' },
  { value: 'solo', label: 'Solo', key: 'S', icon: 'matHeadphonesRound', cursor: 'help' },
  { value: 'mute', label: 'Mute', key: 'M', icon: 'matVolumeOffRound', cursor: 'pointer' },
  { value: 'zoom', label: 'Zoom', key: 'Z', icon: 'matZoomInRound', cursor: 'zoom-in' },
  { value: 'fade', label: 'Fade', key: 'A', icon: 'matTrendingUpRound', cursor: 'nwse-resize' },
  { value: 'marquee', label: 'Marquee', key: 'R', icon: 'matHighlightAltRound', cursor: 'crosshair' }
];

export const MinPixelsPerQuarter = 2;
export const MaxPixelsPerQuarter = 960;
export const MinLaneHeight = 36;
export const MaxLaneHeight = 160;

/** The global tracks Logic shows above the tracks, in its order (Phase 10 adds the movie track). */
export type GlobalLane = 'marker' | 'arrangement' | 'chord' | 'movie' | 'tempo' | 'beatMapping' | 'signature' | 'key';
export const GlobalLanes: readonly GlobalLane[] = [
  'arrangement',
  'marker',
  'chord',
  'movie',
  'tempo',
  'beatMapping',
  'signature',
  'key'
];

const DefaultGlobalLanes: readonly GlobalLane[] = ['arrangement', 'marker', 'movie', 'tempo', 'signature'];

/** The global tracks that show, in order: those chosen, the movie track only when the project has a movie. */
export function visibleGlobalLanes(chosen: ReadonlySet<GlobalLane>, hasMovie: boolean): GlobalLane[] {
  return GlobalLanes.filter(lane => chosen.has(lane) && (lane !== 'movie' || hasMovie));
}

function inRange(value: unknown, fallback: number, min: number, max: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? Math.min(max, Math.max(min, value)) : fallback;
}

function oneOf<T>(value: unknown, values: readonly T[], fallback: T): T {
  return values.includes(value as T) ? (value as T) : fallback;
}

/** A time range picked with the marquee tool, across lanes `laneFrom…laneTo` (inclusive). */
export interface MarqueeRange {
  startTick: number;
  endTick: number;
  laneFrom: number;
  laneTo: number;
}

/**
 * Where the Tracks area looks and how it edits: horizontal zoom and scroll (the tick at the left edge), vertical scroll
 * (the lane at the top, fractional) and lane height, the left-click and ⌘-click tools, snap and its mode, the drag
 * mode, whether the view follows the playhead (Logic's Catch), which global tracks show and which take folders are
 * open. Not undoable; each project remembers it (`ProjectViewStore`, except the open folders and the marquee).
 */
@Injectable({ providedIn: 'root' })
export class TracksViewStore {
  readonly pixelsPerQuarter = signal(48);
  readonly originTick = signal(0);
  readonly firstLane = signal(0);
  readonly laneHeight = signal(72);
  /** The left-click tool. */
  readonly tool = signal<TracksTool>('pointer');
  /** The ⌘-click tool. */
  readonly secondaryTool = signal<TracksTool>('marquee');
  readonly snap = signal(true);
  readonly snapMode = signal<SnapMode>('smart');
  readonly dragMode = signal<DragMode>('overlap');
  readonly follow = signal(true);
  /** Whether the global tracks show, and which. */
  readonly globalTracks = signal(false);
  readonly globalLanes = signal<ReadonlySet<GlobalLane>>(new Set(DefaultGlobalLanes));
  /** Take folders shown open, their takes in rows under them. */
  readonly expandedFolders = signal<ReadonlySet<string>>(new Set());
  /** The marquee tool's selection. */
  readonly marquee = signal<MarqueeRange | null>(null);

  readonly pixelsPerTick = computed(() => this.pixelsPerQuarter() / Ppq);

  /** What a project remembers of this view. */
  readonly remembered = computed(() => ({
    pixelsPerQuarter: this.pixelsPerQuarter(),
    originTick: this.originTick(),
    firstLane: this.firstLane(),
    laneHeight: this.laneHeight(),
    tool: this.tool(),
    secondaryTool: this.secondaryTool(),
    snap: this.snap(),
    snapMode: this.snapMode(),
    dragMode: this.dragMode(),
    follow: this.follow(),
    globalTracks: this.globalTracks(),
    globalLanes: [...this.globalLanes()]
  }));

  /** The view a project left (what it does not hold, or holds wrongly, at its default). */
  restore(saved: unknown): void {
    const view = (typeof saved === 'object' && saved !== null ? saved : {}) as Record<string, unknown>;
    const tools = Tools.map(t => t.value);
    this.pixelsPerQuarter.set(inRange(view['pixelsPerQuarter'], 48, MinPixelsPerQuarter, MaxPixelsPerQuarter));
    this.originTick.set(inRange(view['originTick'], 0, 0, Number.MAX_SAFE_INTEGER));
    this.firstLane.set(inRange(view['firstLane'], 0, 0, Number.MAX_SAFE_INTEGER));
    this.laneHeight.set(inRange(view['laneHeight'], 72, MinLaneHeight, MaxLaneHeight));
    this.tool.set(oneOf(view['tool'], tools, 'pointer'));
    this.secondaryTool.set(oneOf(view['secondaryTool'], tools, 'marquee'));
    this.snap.set(view['snap'] !== false);
    this.snapMode.set(
      oneOf(
        view['snapMode'],
        SnapModes.map(m => m.value),
        'smart'
      )
    );
    this.dragMode.set(
      oneOf(
        view['dragMode'],
        DragModes.map(m => m.value),
        'overlap'
      )
    );
    this.follow.set(view['follow'] !== false);
    this.globalTracks.set(view['globalTracks'] === true);
    const lanes = view['globalLanes'];
    this.globalLanes.set(
      new Set(Array.isArray(lanes) ? GlobalLanes.filter(l => lanes.includes(l)) : DefaultGlobalLanes)
    );
    this.expandedFolders.set(new Set());
    this.marquee.set(null);
  }

  /** Zooms by `factor`, keeping the tick under `anchorX` (CSS pixels from the timeline's left edge) in place. */
  zoom(factor: number, anchorX = 0): void {
    const before = this.pixelsPerTick();
    const anchorTick = this.originTick() + anchorX / before;
    const zoom = Math.min(MaxPixelsPerQuarter, Math.max(MinPixelsPerQuarter, this.pixelsPerQuarter() * factor));
    this.pixelsPerQuarter.set(zoom);
    this.originTick.set(Math.max(0, anchorTick - anchorX / (zoom / Ppq)));
  }

  /** Zooms so that `[startTick, endTick]` fills `widthPx` (the zoom tool's drag). */
  zoomTo(startTick: number, endTick: number, widthPx: number): void {
    const ticks = Math.max(1, endTick - startTick);
    const zoom = Math.min(MaxPixelsPerQuarter, Math.max(MinPixelsPerQuarter, (widthPx / ticks) * Ppq));
    this.pixelsPerQuarter.set(zoom);
    this.originTick.set(Math.max(0, startTick));
  }

  /** Scrolls by CSS pixels: `dx` along time, `dy` across lanes, never before the start or above the first lane. */
  scrollBy(dx: number, dy: number, laneCount: number): void {
    if (dx) {
      this.originTick.update(origin => Math.max(0, origin + dx / this.pixelsPerTick()));
    }

    if (dy) {
      const maxLane = Math.max(0, laneCount - 1);
      this.firstLane.update(lane => Math.min(maxLane, Math.max(0, lane + dy / this.laneHeight())));
    }
  }

  /** Taller or shorter lanes (Logic's vertical zoom). */
  zoomLanes(factor: number): void {
    this.laneHeight.update(height => Math.round(Math.min(MaxLaneHeight, Math.max(MinLaneHeight, height * factor))));
  }

  /** Scrolls so that `tick` is in view, a page at a time (Catch while playing). */
  reveal(tick: number, widthPx: number): void {
    const visibleTicks = widthPx / this.pixelsPerTick();
    const origin = this.originTick();
    if (tick < origin || tick > origin + visibleTicks * 0.95) {
      this.originTick.set(Math.max(0, tick - visibleTicks * 0.05));
    }
  }

  /** Opens or closes a take folder. */
  toggleFolder(id: string, open = !this.expandedFolders().has(id)): void {
    this.expandedFolders.update(set => {
      const next = new Set(set);
      if (open) {
        next.add(id);
      } else {
        next.delete(id);
      }
      return next;
    });
  }

  toggleGlobalLane(lane: GlobalLane): void {
    this.globalLanes.update(set => {
      const next = new Set(set);
      if (!next.delete(lane)) {
        next.add(lane);
      }
      return next;
    });
  }

  /** The tool a press uses: the ⌘-click tool with ⌘ (or Ctrl off a Mac) held. */
  toolFor(event: { metaKey: boolean; ctrlKey?: boolean }): TracksTool {
    return event.metaKey ? this.secondaryTool() : this.tool();
  }
}
