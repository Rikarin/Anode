import { Injectable, computed, signal } from '@angular/core';
import type { DragMode } from '../tracks/drag-modes';
import type { SnapMode } from '../tracks/snap';
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

/** The global tracks Logic shows above the tracks, in its order. */
export type GlobalLane = 'marker' | 'arrangement' | 'tempo' | 'signature' | 'key';
export const GlobalLanes: readonly GlobalLane[] = ['arrangement', 'marker', 'tempo', 'signature', 'key'];

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
 * open. Per window, not per project, and not undoable.
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
  readonly globalLanes = signal<ReadonlySet<GlobalLane>>(new Set(['arrangement', 'marker', 'tempo', 'signature']));
  /** Take folders shown open, their takes in rows under them. */
  readonly expandedFolders = signal<ReadonlySet<string>>(new Set());
  /** The marquee tool's selection. */
  readonly marquee = signal<MarqueeRange | null>(null);

  readonly pixelsPerTick = computed(() => this.pixelsPerQuarter() / Ppq);

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
