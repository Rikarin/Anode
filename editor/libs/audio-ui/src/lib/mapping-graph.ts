import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  booleanAttribute,
  computed,
  inject,
  input,
  model,
  numberAttribute,
  output
} from '@angular/core';

/** A point of a mapping graph: input x and output y, both 0…1. */
export interface MappingPoint {
  readonly x: number;
  readonly y: number;
}

/** The most points a mapping graph holds (the Server's limit). */
export const MappingGraphMaxPoints = 16;

const Linear: readonly MappingPoint[] = [
  { x: 0, y: 0 },
  { x: 1, y: 1 }
];
/** Handles stay this far inside the well so the ends are not clipped. */
const Inset = 6;

function clamp01(value: number): number {
  return Math.min(1, Math.max(0, value));
}

function round(value: number): number {
  return Math.round(clamp01(value) * 1000) / 1000;
}

/**
 * A graph's output at `x`: piecewise linear through its points, flat beyond its ends. (The app's `smart-math.ts`
 * holds the tested original; the library cannot import the app, so this is its minimal twin.)
 */
export function mappingGraphValue(graph: readonly MappingPoint[], x: number): number {
  const points = graph.length >= 2 ? graph : Linear;
  const t = clamp01(x);
  const first = points[0] as MappingPoint;
  if (t <= first.x) {
    return first.y;
  }
  for (let i = 1; i < points.length; i++) {
    const a = points[i - 1] as MappingPoint;
    const b = points[i] as MappingPoint;
    if (t <= b.x) {
      return b.x === a.x ? b.y : a.y + ((t - a.x) / (b.x - a.x)) * (b.y - a.y);
    }
  }
  return (points[points.length - 1] as MappingPoint).y;
}

let nextId = 0;

/**
 * Logic's Smart Control mapping graph: how a control's value (x, 0…1) becomes its parameter's (y, 0…1), drawn in a
 * sunken well. Each point is a handle and an ARIA slider ("Point 1"…) for its output: ↑/↓ move it by 5 % (⇧: 1 %),
 * Home/End to the bottom and top, ←/→ move it along x between its neighbours (the ends stay at x 0 and 1), Enter adds a
 * point halfway to the next one, Delete removes a middle point. With the pointer: drag a handle, press the well to add
 * a point on the curve (up to 16), double-click a middle handle to remove it. `invert` draws (and the sliders read)
 * the inverted output, 1 − y, as the mapping applies it; the stored points are never inverted. `graph` changes live
 * during a drag; `committed` fires when an edit is done.
 */
@Component({
  selector: 'an-mapping-graph',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-flex flex-col items-start gap-0.5 select-none' },
  template: `
    <div
      class="well relative cursor-copy touch-none rounded-[5px]"
      role="group"
      [attr.aria-labelledby]="labelId"
      [class.opacity-35]="disabled()"
      [style.width.px]="width()"
      [style.height.px]="height()"
      (pointerdown)="add($event)"
    >
      <svg class="pointer-events-none absolute inset-0 size-full overflow-visible" aria-hidden="true">
        @for (f of grid; track f) {
          <line
            [attr.x1]="px(f)"
            [attr.x2]="px(f)"
            [attr.y1]="py(0)"
            [attr.y2]="py(1)"
            class="stroke-knob-track"
            stroke-width="1"
            stroke-opacity="0.5"
          />
          <line
            [attr.x1]="px(0)"
            [attr.x2]="px(1)"
            [attr.y1]="py(f)"
            [attr.y2]="py(f)"
            class="stroke-knob-track"
            stroke-width="1"
            stroke-opacity="0.5"
          />
        }
        <path class="fill-primary/20 stroke-none" [attr.d]="area()" />
        <path class="stroke-knob-arc fill-none" stroke-width="1.75" stroke-linejoin="round" [attr.d]="line()" />
      </svg>
      @for (point of shown(); track $index; let i = $index) {
        <span
          class="knob-body focus-visible:outline-focus absolute size-2.5 -translate-x-1/2 -translate-y-1/2 cursor-grab rounded-full outline-offset-1 focus-visible:outline-2"
          role="slider"
          data-point
          [attr.tabindex]="disabled() ? -1 : 0"
          [attr.aria-label]="'Point ' + (i + 1)"
          aria-valuemin="0"
          aria-valuemax="100"
          [attr.aria-valuenow]="percent(point.y)"
          [attr.aria-valuetext]="'In ' + percent(point.x) + ' %, out ' + percent(point.y) + ' %'"
          [attr.aria-disabled]="disabled() || null"
          [style.left.px]="px(point.x)"
          [style.top.px]="py(point.y)"
          (pointerdown)="grab($event, i)"
          (pointermove)="drag($event, i)"
          (pointerup)="release($event)"
          (pointercancel)="release($event)"
          (dblclick)="remove(i)"
          (keydown)="key($event, i)"
        ></span>
      }
    </div>
    <span class="text-foreground-muted text-[10px] leading-tight" [id]="labelId">{{ label() }}</span>
  `
})
export class AnMappingGraph {
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  protected readonly labelId = `an-mapping-graph-${nextId++}`;
  protected readonly grid = [0.25, 0.5, 0.75];

  /** The points, sorted by x, from x 0 to x 1 (2–16 of them). */
  readonly graph = model<readonly MappingPoint[]>(Linear);
  /** Draw the inverted output (1 − y). */
  readonly invert = input(false, { transform: booleanAttribute });
  readonly label = input('Mapping graph');
  readonly width = input(160, { transform: numberAttribute });
  readonly height = input(96, { transform: numberAttribute });
  readonly disabled = input(false, { transform: booleanAttribute });
  /** An edit is done (a drag ended, a point added or removed, a key press). */
  readonly committed = output<MappingPoint[]>();

  private dragging: { pointer: number; index: number; moved: boolean } | null = null;

  /** The points as drawn: inverted when `invert`. */
  protected readonly shown = computed(() => {
    const invert = this.invert();
    const graph = this.graph().length >= 2 ? this.graph() : Linear;
    return graph.map(p => ({ x: p.x, y: invert ? 1 - p.y : p.y }));
  });

  protected readonly line = computed(() =>
    this.shown()
      .map((p, i) => `${i === 0 ? 'M' : 'L'}${this.px(p.x).toFixed(1)} ${this.py(p.y).toFixed(1)}`)
      .join(' ')
  );

  protected readonly area = computed(
    () => `${this.line()} L${this.px(1).toFixed(1)} ${this.py(0)} L${this.px(0).toFixed(1)} ${this.py(0)} Z`
  );

  protected px(x: number): number {
    return Inset + x * (this.width() - 2 * Inset);
  }

  protected py(y: number): number {
    return Inset + (1 - y) * (this.height() - 2 * Inset);
  }

  protected percent(value: number): number {
    return Math.round(clamp01(value) * 100);
  }

  protected add(event: PointerEvent): void {
    if (this.disabled() || event.button !== 0 || (event.target as HTMLElement).hasAttribute('data-point')) {
      return;
    }
    const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const x = round((event.clientX - box.left - Inset) / Math.max(1, box.width - 2 * Inset));
    this.insert(x);
  }

  protected grab(event: PointerEvent, index: number): void {
    if (this.disabled() || event.button !== 0) {
      return;
    }
    event.stopPropagation();
    const handle = event.currentTarget as HTMLElement;
    handle.setPointerCapture(event.pointerId);
    handle.focus();
    this.dragging = { pointer: event.pointerId, index, moved: false };
  }

  protected drag(event: PointerEvent, index: number): void {
    if (this.dragging?.pointer !== event.pointerId || this.dragging.index !== index) {
      return;
    }
    const well = (event.currentTarget as HTMLElement).parentElement;
    const box = well?.getBoundingClientRect();
    if (!box || box.width <= 2 * Inset || box.height <= 2 * Inset) {
      return;
    }
    const x = (event.clientX - box.left - Inset) / (box.width - 2 * Inset);
    const y = 1 - (event.clientY - box.top - Inset) / (box.height - 2 * Inset);
    this.dragging.moved = true;
    this.move(index, x, y);
  }

  protected release(event: PointerEvent): void {
    if (this.dragging?.pointer !== event.pointerId) {
      return;
    }
    const moved = this.dragging.moved;
    this.dragging = null;
    if (moved) {
      this.committed.emit([...this.graph()]);
    }
  }

  protected key(event: KeyboardEvent, index: number): void {
    if (this.disabled()) {
      return;
    }
    const point = this.shown()[index];
    if (!point) {
      return;
    }
    const step = event.shiftKey ? 0.01 : 0.05;
    const moves: Record<string, MappingPoint> = {
      ArrowUp: { x: point.x, y: point.y + step },
      ArrowDown: { x: point.x, y: point.y - step },
      ArrowRight: { x: point.x + step, y: point.y },
      ArrowLeft: { x: point.x - step, y: point.y },
      Home: { x: point.x, y: 0 },
      End: { x: point.x, y: 1 },
      PageUp: { x: point.x, y: point.y + 0.25 },
      PageDown: { x: point.x, y: point.y - 0.25 }
    };
    const next = moves[event.key];
    if (next) {
      event.preventDefault();
      if (this.move(index, next.x, next.y)) {
        this.committed.emit([...this.graph()]);
      }
    } else if (event.key === 'Delete' || event.key === 'Backspace') {
      event.preventDefault();
      this.remove(index);
    } else if (event.key === 'Enter' || event.key === 'Insert') {
      event.preventDefault();
      const graph = this.graph();
      const a = graph[index];
      const b = graph[index + 1] ?? graph[index - 1];
      if (a && b) {
        this.insert(round((a.x + b.x) / 2));
      }
    }
  }

  protected remove(index: number): void {
    const graph = this.graph();
    if (this.disabled() || index <= 0 || index >= graph.length - 1 || graph.length <= 2) {
      return;
    }
    const next = graph.filter((_, i) => i !== index);
    this.graph.set(next);
    this.committed.emit([...next]);
    this.focus(Math.max(0, index - 1));
  }

  /** Moves point `index` to the drawn (x, y); the ends keep their x. False when nothing changed. */
  private move(index: number, x: number, shownY: number): boolean {
    const graph = this.graph();
    const point = graph[index];
    if (!point) {
      return false;
    }
    const y = round(this.invert() ? 1 - clamp01(shownY) : shownY);
    const last = index === 0 || index === graph.length - 1;
    const lo = graph[index - 1]?.x ?? 0;
    const hi = graph[index + 1]?.x ?? 1;
    const nx = last ? point.x : Math.min(hi, Math.max(lo, round(x)));
    if (nx === point.x && y === point.y) {
      return false;
    }
    this.graph.set(graph.map((p, i) => (i === index ? { x: nx, y } : p)));
    return true;
  }

  /** Adds a point on the curve at x (strictly inside 0…1, while there is room) and focuses it. */
  private insert(x: number): void {
    const graph = this.graph().length >= 2 ? this.graph() : Linear;
    if (graph.length >= MappingGraphMaxPoints || x <= 0 || x >= 1) {
      return;
    }
    const point = { x, y: round(mappingGraphValue(graph, x)) };
    const next = [...graph, point].sort((a, b) => a.x - b.x);
    this.graph.set(next);
    this.committed.emit([...next]);
    this.focus(next.indexOf(point));
  }

  private focus(index: number): void {
    afterNextRender(() => this.element.nativeElement.querySelectorAll<HTMLElement>('[data-point]')[index]?.focus(), {
      injector: this.injector
    });
  }
}
