import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import type { AudioRegion, FlexMarker } from '@anode/api-client';
import { AnFlexMarker, type FlexNudge } from '@anode/audio-ui';
import { NAME_BAR_HEIGHT, NAME_BAR_MIN_HEIGHT } from '@anode/timeline';
import {
  beginFlexDrag,
  deleteFlexMarker,
  dragFlexMarker,
  flexHit,
  sortMarkers,
  type FlexDrag,
  type FlexZone
} from '../flex/flex-markers';
import type { RegionTiming } from '../music/region-timing';
import { FlexStore } from '../state/flex.store';
import { LanesStore } from '../state/lanes.store';
import { Ppq, ProjectStore } from '../state/project.store';
import { TracksViewStore } from '../state/tracks-view.store';

/** Pixels the pointer moves before a press on a marker becomes a drag. */
const DragThreshold = 2;
/** A key nudge: a 1/64 note, ⇧ a 1/16. */
const NudgeTicks = Ppq / 16;
const BigNudgeTicks = Ppq / 4;

const ZoneLabels: Record<FlexZone, string> = {
  marker: 'Move Flex Marker',
  transient: 'Add Flex Marker',
  neighbours: 'Add Flex Markers',
  single: 'Add Flex Marker'
};

/** One flexed region as drawn: where its waveform area is, its timing, transients and markers. */
interface FlexBox {
  region: AudioRegion;
  timing: RegionTiming;
  left: number;
  top: number;
  width: number;
  height: number;
  /** Transients' x inside the box. */
  transients: number[];
  markers: { marker: FlexMarker; x: number; index: number; min: number; max: number; text: string }[];
}

/** A drag in progress on one region. */
interface Drag {
  regionId: string;
  drag: FlexDrag;
  zone: FlexZone;
  x: number;
  moved: boolean;
  /** The markers as they are drawn now. */
  markers: FlexMarker[];
}

/**
 * Flex Time over the Tracks area (Show Flex, ⌘F): on each region of a track whose flex is on, its transients drawn
 * faintly and its flex markers as Logic draws them, over the waveform (under the name bar, so the name bar still moves
 * the region). A press in the upper half on empty audio makes a marker there and on the transients either side and
 * drags the middle one; on a transient, a marker on it; in the lower half a single marker; on a marker, drags it. A
 * double-click on a marker deletes it; ←/→ nudge a focused marker. Each gesture is one `setFlexMarkers` edit; the
 * markers preview it while dragging.
 */
@Component({
  selector: 'an-flex-overlay',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnFlexMarker],
  host: { class: 'pointer-events-none absolute inset-0 overflow-hidden' },
  template: `
    @for (box of boxes(); track box.region.id) {
      <div
        class="pointer-events-auto absolute cursor-crosshair touch-none"
        role="group"
        data-testid="flex-region"
        [attr.data-region-id]="box.region.id"
        [attr.aria-label]="'Flex markers of ' + box.region.name"
        [style.left.px]="box.left"
        [style.top.px]="box.top"
        [style.width.px]="box.width"
        [style.height.px]="box.height"
        (pointerdown)="down($event, box)"
        (pointermove)="move($event, box)"
        (pointerup)="up($event, box)"
        (pointercancel)="drag.set(null)"
        (dblclick)="doubleClick($event, box)"
      >
        @if (box.transients.length) {
          <svg class="pointer-events-none absolute inset-0 size-full" aria-hidden="true">
            <path class="stroke-(--transient)" stroke-width="1" fill="none" [attr.d]="path(box)" />
          </svg>
        }
        @for (m of box.markers; track m.index) {
          <an-flex-marker
            [style.left.px]="m.x"
            [label]="'Flex marker ' + (m.index + 1)"
            [value]="m.marker.tick"
            [min]="m.min"
            [max]="m.max"
            [valueText]="m.text"
            [active]="activeMarker(box.region.id, m.index)"
            (nudge)="nudge(box, m.index, $event)"
            (remove)="remove(box, m.index)"
          />
        }
      </div>
    }
  `
})
export class FlexOverlay {
  private readonly project = inject(ProjectStore);
  private readonly flex = inject(FlexStore);
  private readonly lanes = inject(LanesStore);
  private readonly view = inject(TracksViewStore);

  /** Pixels from the top of the timeline to the first lane (the ruler and the global tracks). */
  readonly lanesTop = input(0);
  /** The timeline's width, to leave out regions off screen. */
  readonly width = input(4000);

  protected readonly drag = signal<Drag | null>(null);

  protected readonly boxes = computed<FlexBox[]>(() => {
    if (!this.flex.showFlex()) {
      return [];
    }
    const flexed = this.flex.flexedTracks();
    const ppt = this.view.pixelsPerTick();
    const origin = this.view.originTick();
    const firstLane = this.view.firstLane();
    const laneHeight = this.view.laneHeight();
    const top = this.lanesTop();
    const width = this.width();
    const drag = this.drag();
    const meter = this.project.meter();
    const inset = laneHeight >= NAME_BAR_MIN_HEIGHT + 2 ? NAME_BAR_HEIGHT + 1 : 1;
    const x = (tick: number) => (tick - origin) * ppt;
    const boxes: FlexBox[] = [];
    for (const region of this.project.regionList()) {
      if (!flexed.has(region.trackId)) {
        continue;
      }
      const lane = this.lanes.laneOf(region.trackId);
      if (lane < 0 || lane + 1 <= firstLane) {
        continue;
      }
      const markers = drag?.regionId === region.id ? drag.markers : sortMarkers(region.flexMarkers);
      const timing = this.project.timing({ ...region, flexMarkers: markers });
      const left = x(timing.startTick);
      const right = x(timing.endTick);
      if (right < 0 || left > width) {
        continue;
      }
      const analysis = this.flex.analysis(region.fileId);
      const transients = (analysis?.transients ?? [])
        .filter(f => f > region.fileOffset && f < region.fileOffset + region.length)
        .map(f => x(timing.tickAt(f)) - left)
        .filter(px => px >= -left - 1 && px <= width - left + 1);
      const length = timing.lengthTicks;
      boxes.push({
        region,
        timing,
        left,
        top: top + (lane - firstLane) * laneHeight + inset,
        width: Math.max(1, right - left),
        height: Math.max(4, laneHeight - inset - 2),
        transients,
        markers: markers.map((marker, index) => {
          const p = meter.position(timing.startTick + marker.tick);
          return {
            marker,
            index,
            x: marker.tick * ppt,
            min: (markers[index - 1]?.tick ?? 0) + 1,
            max: (markers[index + 1]?.tick ?? Math.floor(length)) - 1,
            text: `${p.bar} ${p.beat} ${p.division} ${p.tick}`
          };
        })
      });
    }
    return boxes;
  });

  protected path(box: FlexBox): string {
    return box.transients.map(px => `M${Math.round(px) + 0.5} 0V${box.height}`).join('');
  }

  protected activeMarker(regionId: string, index: number): boolean {
    const drag = this.drag();
    return drag?.regionId === regionId && drag.drag.index === index;
  }

  /** The tick under a pointer, absolute. */
  private tickAt(event: PointerEvent | MouseEvent, box: FlexBox): number {
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    return box.timing.startTick + (event.clientX - rect.left) / this.view.pixelsPerTick();
  }

  protected down(event: PointerEvent, box: FlexBox): void {
    if (event.button !== 0) {
      return;
    }
    event.stopPropagation();
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const upper = event.clientY - rect.top < rect.height / 2;
    const transients = this.transientFrames(box.region);
    const hit = flexHit(box.timing, transients, this.tickAt(event, box), upper, 1 / this.view.pixelsPerTick());
    const drag = beginFlexDrag(box.timing, transients, hit);
    if (!drag) {
      return;
    }
    try {
      (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    } catch {
      // no capture (tests): moves still reach the box
    }
    this.drag.set({
      regionId: box.region.id,
      drag,
      zone: hit.zone,
      x: event.clientX,
      moved: false,
      markers: drag.markers
    });
  }

  protected move(event: PointerEvent, box: FlexBox): void {
    const drag = this.drag();
    if (drag?.regionId !== box.region.id) {
      return;
    }
    const moved = drag.moved || Math.abs(event.clientX - drag.x) >= DragThreshold;
    if (!moved) {
      return;
    }
    const tick = this.tickAt(event, box) - box.timing.startTick;
    const markers = dragFlexMarker(drag.drag, tick, box.timing.lengthTicks);
    this.drag.set({ ...drag, moved: true, markers });
  }

  protected up(event: PointerEvent, box: FlexBox): void {
    const drag = this.drag();
    if (drag?.regionId !== box.region.id) {
      return;
    }
    event.stopPropagation();
    this.drag.set(null);
    const before = sortMarkers(box.region.flexMarkers);
    if (!sameMarkers(before, drag.markers)) {
      void this.flex.setMarkers(box.region.id, drag.markers, drag.moved ? 'Move Flex Marker' : ZoneLabels[drag.zone]);
    }
  }

  protected doubleClick(event: MouseEvent, box: FlexBox): void {
    event.stopPropagation();
    const ticksPerPixel = 1 / this.view.pixelsPerTick();
    const tick =
      box.timing.startTick +
      (event.clientX - (event.currentTarget as HTMLElement).getBoundingClientRect().left) * ticksPerPixel;
    const hit = flexHit(box.timing, [], tick, false, ticksPerPixel);
    if (hit.zone === 'marker' && hit.index !== undefined) {
      this.remove(box, hit.index);
    }
  }

  protected nudge(box: FlexBox, index: number, nudge: FlexNudge): void {
    const markers = sortMarkers(box.region.flexMarkers);
    const marker = markers[index];
    if (!marker) {
      return;
    }
    const by = nudge.direction * (nudge.big ? BigNudgeTicks : NudgeTicks);
    const next = dragFlexMarker({ markers, index }, marker.tick + by, box.timing.lengthTicks);
    if (!sameMarkers(markers, next)) {
      void this.flex.setMarkers(box.region.id, next, 'Move Flex Marker');
    }
  }

  protected remove(box: FlexBox, index: number): void {
    void this.flex.setMarkers(box.region.id, deleteFlexMarker(box.region.flexMarkers, index), 'Delete Flex Marker');
  }

  private transientFrames(region: AudioRegion): number[] {
    return this.flex.analyses().get(region.fileId)?.transients ?? [];
  }
}

function sameMarkers(a: readonly FlexMarker[], b: readonly FlexMarker[]): boolean {
  return a.length === b.length && a.every((m, i) => m.fileFrame === b.at(i)?.fileFrame && m.tick === b.at(i)?.tick);
}
