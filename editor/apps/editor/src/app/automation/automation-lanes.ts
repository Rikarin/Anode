import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  computed,
  inject,
  output,
  signal
} from '@angular/core';
import type { AutomationPoint, Track } from '@anode/api-client';
import { AnAutomationLane, type AutomationHandle, type AutomationPointKey } from '@anode/audio-ui';
import {
  type AutomationProjection,
  automationPaths,
  hitPoint,
  hitSegment,
  insertPoint,
  movePoints,
  pointsInRange,
  removePoints,
  replaceSpan,
  setCurve,
  simplify
} from '@anode/timeline';
import { LanesStore } from '../state/lanes.store';
import { ProjectStore } from '../state/project.store';
import { TracksViewStore } from '../state/tracks-view.store';
import { snapBy, snapUnit } from '../tracks/snap';
import { AutomationStore } from './automation.store';
import type { AutomationTarget } from './targets';

/** Space above and below a lane's curve, so points at the ends stay grabbable. */
export const LanePadding = 5;
/** How far (CSS pixels) a press moves before it is a drag. */
const DragThreshold = 3;
/** Pixels of vertical drag for a full bend of a segment. */
const CurvePixels = 80;

/** A lane on screen: its row, where it is, and how it maps values to its height. */
interface VisibleLane {
  id: string;
  track: Track;
  target: string;
  info: AutomationTarget;
  top: number;
  points: readonly AutomationPoint[];
  label: string;
}

type Gesture =
  | { kind: 'move'; laneId: string; indices: number[]; x: number; y: number; tick: number; moved: boolean }
  | { kind: 'curve'; laneId: string; index: number; y: number; curve: number; sign: number }
  | { kind: 'range'; laneId: string; x: number; tick: number; moved: boolean; y: number }
  | { kind: 'draw'; laneId: string; stroke: { x: number; y: number }[] };

/** The y of a normalised value in a lane `height` tall. */
export function laneY(normal: number, height: number): number {
  return LanePadding + (1 - Math.min(1, Math.max(0, normal))) * Math.max(1, height - 2 * LanePadding);
}

/** The normalised value at y in a lane `height` tall. */
export function laneNormal(y: number, height: number): number {
  return Math.min(1, Math.max(0, 1 - (y - LanePadding) / Math.max(1, height - 2 * LanePadding)));
}

/**
 * The automation sub-lanes over the timeline (docs/05-phase-4.md): each visible automation row's curve, points and
 * selection, drawn by `an-automation-lane` at its lane's place, and edited with the Tracks area's tools. Pointer: a
 * click adds a point (snapped), a drag on a point moves it (and the other selected ones), ⌃⇧-drag bends a segment, a
 * drag on empty space selects a time range, a double-click deletes a point. Pencil draws, Eraser deletes. A drag
 * previews here and commits one `setAutomation` when it ends.
 */
@Component({
  selector: 'an-automation-lanes',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnAutomationLane],
  host: { class: 'pointer-events-none block overflow-hidden' },
  template: `
    @for (lane of visible(); track lane.id) {
      <an-automation-lane
        class="bg-surface-sunken/70 border-hairline pointer-events-auto absolute inset-x-0 touch-none border-b"
        [attr.data-lane]="lane.id"
        [style.top.px]="lane.top"
        [style.cursor]="cursor()"
        [label]="lane.label"
        [height]="height()"
        [color]="lane.track.color"
        [mode]="lane.track.automationMode"
        [path]="paths(lane).line"
        [area]="paths(lane).area"
        [points]="handles(lane)"
        [range]="range(lane)"
        (pointerdown)="down(lane, $event)"
        (pointermove)="move(lane, $event)"
        (pointerup)="up(lane, $event)"
        (pointercancel)="cancel()"
        (dblclick)="doubleClick(lane, $event)"
        (wheel)="scrollWheel.emit($event)"
        (pointKey)="key(lane, $event)"
      />
    }
  `
})
export class AutomationLanes {
  private readonly lanes = inject(LanesStore);
  private readonly view = inject(TracksViewStore);
  private readonly project = inject(ProjectStore);
  protected readonly automation = inject(AutomationStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** The overlay's size in CSS pixels (the timeline's lanes), kept by a ResizeObserver. */
  private readonly size = signal({ width: 2000, height: 2000 });
  private readonly width = computed(() => this.size().width);
  /** A wheel over a lane: the timeline scrolls or zooms with it. */
  readonly scrollWheel = output<WheelEvent>();

  protected readonly height = computed(() => this.view.laneHeight());
  /** A lane's points while a gesture changes them, before they are sent. */
  private readonly draft = signal<{ laneId: string; points: readonly AutomationPoint[] } | null>(null);
  private readonly rangeDraft = signal<{ laneId: string; x0: number; x1: number } | null>(null);
  private gesture: Gesture | null = null;
  private readonly pixelsPerTick = computed(() => this.view.pixelsPerTick());

  protected readonly cursor = computed(() => {
    switch (this.view.tool()) {
      case 'pencil':
        return 'copy';
      case 'eraser':
        return 'not-allowed';
      default:
        return 'default';
    }
  });

  protected readonly visible = computed<VisibleLane[]>(() => {
    const height = this.height();
    const first = this.view.firstLane();
    const draft = this.draft();
    const count = Math.ceil(this.size().height / height) + 2;
    return this.lanes.rows().flatMap((row, lane): VisibleLane[] => {
      if (row.kind !== 'automation' || lane < Math.floor(first) - 1 || lane > first + count) {
        return [];
      }
      const info = this.automation.target(row.track.id, row.target);
      return [
        {
          id: row.id,
          track: row.track,
          target: row.target,
          info,
          top: (lane - first) * height,
          points: draft?.laneId === row.id ? draft.points : this.automation.points(row.track.id, row.target),
          label: `${row.track.name} ${info.name} automation`
        }
      ];
    });
  });

  constructor() {
    const element = this.host.nativeElement;
    const measure = () => this.size.set({ width: element.clientWidth || 2000, height: element.clientHeight || 2000 });
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    observer?.observe(element);
    afterNextRender(measure);
    inject(DestroyRef).onDestroy(() => {
      observer?.disconnect();
      this.gesture = null;
    });
  }

  private projection(lane: VisibleLane): AutomationProjection {
    const origin = this.view.originTick();
    const ppt = this.pixelsPerTick();
    const height = this.height();
    return {
      tickToX: tick => (tick - origin) * ppt,
      valueToY: value => laneY(lane.info.toNormal(value), height)
    };
  }

  /** The curve; an empty lane draws a flat line at the control's current value. */
  protected paths(lane: VisibleLane): { line: string; area: string } {
    const origin = this.view.originTick();
    return automationPaths(
      lane.points.length ? lane.points : [{ tick: 0, value: lane.info.default, curve: 0 }],
      this.projection(lane),
      origin,
      origin + this.width() / this.pixelsPerTick(),
      this.height(),
      { stepped: lane.info.stepped }
    );
  }

  protected handles(lane: VisibleLane): AutomationHandle[] {
    const projection = this.projection(lane);
    const selection = this.automation.selection();
    const selected = selection?.trackId === lane.track.id && selection.target === lane.target ? selection.points : null;
    return lane.points.map((point, i) => ({
      x: projection.tickToX(point.tick),
      y: projection.valueToY(point.value),
      selected: selected?.has(i) ?? false,
      valueText: lane.info.format(point.value)
    }));
  }

  protected range(lane: VisibleLane): { x0: number; x1: number } | null {
    const draft = this.rangeDraft();
    if (draft?.laneId === lane.id) {
      return { x0: draft.x0, x1: draft.x1 };
    }
    const selection = this.automation.selection();
    if (selection?.range && selection.trackId === lane.track.id && selection.target === lane.target) {
      const projection = this.projection(lane);
      return { x0: projection.tickToX(selection.range.startTick), x1: projection.tickToX(selection.range.endTick) };
    }
    return null;
  }

  private local(event: PointerEvent | MouseEvent): { x: number; y: number } {
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }

  private tickAt(x: number): number {
    return this.view.originTick() + x / this.pixelsPerTick();
  }

  private snap(tick: number): number {
    const mode = this.view.snap() ? this.view.snapMode() : 'ticks';
    return snapBy(tick, mode, this.snapContext());
  }

  private snapContext() {
    return {
      meter: this.project.meter(),
      tempo: this.project.tempoMap(),
      sampleRate: this.project.info()?.sampleRate ?? 48_000,
      pixelsPerTick: this.pixelsPerTick()
    };
  }

  protected down(lane: VisibleLane, event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    const target = event.currentTarget as Partial<HTMLElement>;
    target.setPointerCapture?.(event.pointerId);
    (event.target as Partial<HTMLElement> | null)?.focus?.();
    const { x, y } = this.local(event);
    const projection = this.projection(lane);
    const tool = this.view.toolFor(event);
    const hit = hitPoint(lane.points, x, y, projection);
    const id = lane.track.id;

    if (tool === 'eraser') {
      if (hit >= 0) {
        void this.automation.commit(id, lane.target, removePoints(lane.points, [hit]));
      }
      return;
    }
    if (tool === 'pencil') {
      this.gesture = { kind: 'draw', laneId: lane.id, stroke: [{ x, y }] };
      return;
    }
    if (event.ctrlKey && event.shiftKey) {
      const segment = hitSegment(lane.points, x, y, projection);
      const a = lane.points[segment];
      const b = lane.points[segment + 1];
      if (a && b) {
        const sign = lane.info.toNormal(b.value) >= lane.info.toNormal(a.value) ? 1 : -1;
        this.gesture = { kind: 'curve', laneId: lane.id, index: segment, y, curve: a.curve, sign };
        return;
      }
    }
    if (hit >= 0) {
      const selection = this.automation.selection();
      const same = selection?.trackId === id && selection.target === lane.target;
      let selected = same ? new Set(selection.points) : new Set<number>();
      if (event.shiftKey) {
        if (!selected.delete(hit)) {
          selected.add(hit);
        }
      } else if (!selected.has(hit)) {
        selected = new Set([hit]);
      }
      this.automation.selectPoints(id, lane.target, selected);
      const point = lane.points[hit] as AutomationPoint;
      this.gesture = {
        kind: 'move',
        laneId: lane.id,
        indices: selected.has(hit) ? [...selected] : [],
        x,
        y,
        tick: point.tick,
        moved: false
      };
      return;
    }
    this.gesture = { kind: 'range', laneId: lane.id, x, y, tick: this.tickAt(x), moved: false };
  }

  protected move(lane: VisibleLane, event: PointerEvent): void {
    const gesture = this.gesture;
    if (gesture?.laneId !== lane.id) {
      return;
    }
    const { x, y } = this.local(event);
    const original = this.automation.points(lane.track.id, lane.target);
    switch (gesture.kind) {
      case 'draw':
        gesture.stroke.push({ x, y });
        this.draft.set({ laneId: lane.id, points: replaceSpan(original, this.stroke(lane, gesture.stroke)) });
        return;
      case 'curve': {
        const curve = gesture.curve + ((gesture.y - y) / CurvePixels) * gesture.sign;
        this.draft.set({ laneId: lane.id, points: setCurve(original, gesture.index, curve) });
        return;
      }
      case 'move': {
        if (!gesture.moved && Math.hypot(x - gesture.x, y - gesture.y) < DragThreshold) {
          return;
        }
        gesture.moved = true;
        const dTick = this.snap(gesture.tick + (x - gesture.x) / this.pixelsPerTick()) - gesture.tick;
        const dNormal = (gesture.y - y) / Math.max(1, this.height() - 2 * LanePadding);
        const info = lane.info;
        const shift = (value: number) => info.fromNormal(info.toNormal(value) + dNormal);
        this.draft.set({ laneId: lane.id, points: movePoints(original, gesture.indices, dTick, shift) });
        return;
      }
      case 'range': {
        if (!gesture.moved && Math.abs(x - gesture.x) < DragThreshold) {
          return;
        }
        gesture.moved = true;
        const projection = this.projection(lane);
        const a = this.snap(gesture.tick);
        const b = this.snap(this.tickAt(x));
        this.rangeDraft.set({ laneId: lane.id, x0: projection.tickToX(a), x1: projection.tickToX(b) });
        return;
      }
    }
  }

  protected up(lane: VisibleLane, event: PointerEvent): void {
    const gesture = this.gesture;
    this.gesture = null;
    if (gesture?.laneId !== lane.id) {
      return;
    }
    const id = lane.track.id;
    const draft = this.draft();
    const { x, y } = this.local(event);
    switch (gesture.kind) {
      case 'draw':
      case 'curve':
        if (draft?.laneId === lane.id) {
          void this.commit(id, lane.target, draft.points);
        } else {
          this.draft.set(null);
        }
        return;
      case 'move':
        if (gesture.moved && draft?.laneId === lane.id) {
          void this.commit(id, lane.target, draft.points);
        }
        this.draft.set(null);
        return;
      case 'range': {
        this.rangeDraft.set(null);
        if (gesture.moved) {
          const a = this.snap(gesture.tick);
          const b = this.snap(this.tickAt(x));
          this.automation.selectRange(id, lane.target, a, b, pointsInRange(lane.points, a, b));
          return;
        }
        // A click on empty space: a point there.
        const tick = this.snap(this.tickAt(x));
        const value = lane.info.fromNormal(laneNormal(y, this.height()));
        const { points, index } = insertPoint(lane.points, { tick, value, curve: 0 });
        this.automation.selectPoints(id, lane.target, [index]);
        void this.automation.commit(id, lane.target, points);
        return;
      }
    }
  }

  /** Sends a gesture's points and keeps showing them until the lane's delta arrives. */
  private async commit(trackId: string, target: string, points: readonly AutomationPoint[]): Promise<void> {
    await this.automation.commit(trackId, target, points);
    this.draft.set(null);
  }

  protected cancel(): void {
    this.gesture = null;
    this.draft.set(null);
    this.rangeDraft.set(null);
  }

  protected doubleClick(lane: VisibleLane, event: MouseEvent): void {
    const { x, y } = this.local(event);
    const hit = hitPoint(lane.points, x, y, this.projection(lane));
    if (hit >= 0) {
      this.automation.clearSelection();
      void this.automation.commit(lane.track.id, lane.target, removePoints(lane.points, [hit]));
    }
  }

  /** A pencil stroke as points: thinned in pixels, in time order. */
  private stroke(lane: VisibleLane, stroke: readonly { x: number; y: number }[]): AutomationPoint[] {
    const kept = simplify(
      stroke,
      p => p.x,
      p => p.y,
      2
    );
    return kept
      .map(p => ({
        tick: Math.max(0, Math.round(this.tickAt(p.x))),
        value: lane.info.fromNormal(laneNormal(p.y, this.height())),
        curve: 0
      }))
      .sort((a, b) => a.tick - b.tick);
  }

  /** Keys on a focused point: ↑↓ nudge its value (⇧ finer), ←→ move it a grid step (⇧ a tick), Delete removes it. */
  protected key(lane: VisibleLane, event: AutomationPointKey): void {
    const { index } = event;
    const point = lane.points[index];
    if (!point) {
      return;
    }
    const id = lane.track.id;
    const info = lane.info;
    switch (event.key) {
      case 'delete':
        this.automation.clearSelection();
        void this.automation.commit(id, lane.target, removePoints(lane.points, [index]));
        return;
      case 'up':
      case 'down': {
        const step = (event.fine ? 0.001 : 0.01) * (event.key === 'up' ? 1 : -1);
        const steps = info.steps > 0 ? (event.key === 'up' ? 1 : -1) * ((info.max - info.min) / info.steps) : 0;
        const shift = (value: number) =>
          steps ? Math.min(info.max, Math.max(info.min, value + steps)) : info.fromNormal(info.toNormal(value) + step);
        void this.automation.commit(id, lane.target, movePoints(lane.points, [index], 0, shift));
        return;
      }
      case 'left':
      case 'right': {
        const unit = event.fine ? 1 : snapUnit(point.tick, this.view.snapMode(), this.snapContext()) || 1;
        const dTick = event.key === 'right' ? unit : -unit;
        void this.automation.commit(id, lane.target, movePoints(lane.points, [index], dTick));
        return;
      }
    }
  }
}
