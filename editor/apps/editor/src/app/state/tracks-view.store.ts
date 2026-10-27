import { Injectable, computed, signal } from '@angular/core';
import { Ppq } from './project.store';

/** The Tracks area's tools (Logic's Left-click tool menu; more in later phases). */
export type TracksTool = 'pointer' | 'pencil' | 'scissors';

export const MinPixelsPerQuarter = 2;
export const MaxPixelsPerQuarter = 960;
export const MinLaneHeight = 36;
export const MaxLaneHeight = 160;

/**
 * Where the Tracks area looks and how it edits: horizontal zoom and scroll (the tick at the left edge), vertical scroll
 * (the lane at the top, fractional) and lane height, the tool, snap to grid, and whether the view follows the playhead
 * (Logic's Catch). Per window, not per project, and not undoable.
 */
@Injectable({ providedIn: 'root' })
export class TracksViewStore {
  readonly pixelsPerQuarter = signal(48);
  readonly originTick = signal(0);
  readonly firstLane = signal(0);
  readonly laneHeight = signal(72);
  readonly tool = signal<TracksTool>('pointer');
  readonly snap = signal(true);
  readonly follow = signal(true);

  readonly pixelsPerTick = computed(() => this.pixelsPerQuarter() / Ppq);

  /** Zooms by `factor`, keeping the tick under `anchorX` (CSS pixels from the timeline's left edge) in place. */
  zoom(factor: number, anchorX = 0): void {
    const before = this.pixelsPerTick();
    const anchorTick = this.originTick() + anchorX / before;
    const zoom = Math.min(MaxPixelsPerQuarter, Math.max(MinPixelsPerQuarter, this.pixelsPerQuarter() * factor));
    this.pixelsPerQuarter.set(zoom);
    this.originTick.set(Math.max(0, anchorTick - anchorX / (zoom / Ppq)));
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
}
