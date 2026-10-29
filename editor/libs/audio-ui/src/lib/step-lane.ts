import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  input,
  numberAttribute,
  output,
  signal
} from '@angular/core';

/** A value drawn in a lane: an event's or note's bar, its geometry in CSS pixels within the lane's body. */
export interface StepBar {
  /** The bar's left edge. */
  x: number;
  /** The bar's width (a beam to the next event, or a thin note bar). */
  width: number;
  /** The top of the bar (its value's height); the bar runs down to the lane's bottom. */
  y: number;
  /** The value, for the bar's slider. */
  value: number;
  /** The value as text ("Velocity 96 at 1 2 1 1"). */
  valueText: string;
  selected?: boolean;
}

/** The lane's tools: the pencil paints a value per grid step; the line draws a straight line from point to point. */
export type StepLaneTool = 'pencil' | 'line';

/** A finished gesture: the pointer's points in CSS pixels within the body (the line tool's: its two ends). */
export interface StepLaneStroke {
  tool: StepLaneTool;
  points: readonly { x: number; y: number }[];
}

/** Captures (or releases) a pointer where the platform can (test DOMs cannot). */
function capture(element: HTMLElement, pointerId: number, on: boolean): void {
  try {
    if (on) {
      element.setPointerCapture(pointerId);
    } else {
      element.releasePointerCapture(pointerId);
    }
  } catch {
    // No capture: the gesture still ends on this element's pointerup.
  }
}

/** A key on a focused bar: ↑/↓ change its value by `by` (⇧: 10). */
export interface StepBarKey {
  index: number;
  by: number;
}

/**
 * One lane of Logic's Step Editor: a header (projected: the lane's name and its controls) and a body of value bars
 * (beams to the next event for controllers, thin bars for notes' velocities) on the Piano Roll's time scale, over the
 * owner's grid lines. Presentational: the owner computes the geometry; the lane turns pointer gestures into strokes —
 * with the pencil, every point the pointer passes; with the line, its two ends, previewed as a line while dragging.
 * The body is a `group` named after the lane; each bar is a `slider` (one focus stop, ←/→ move between bars, ↑/↓
 * change the value, as `barKey`).
 */
@Component({
  selector: 'an-step-lane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'border-hairline flex border-b',
    '[style.height.px]': 'height()'
  },
  template: `
    <div
      class="border-hairline bg-surface flex shrink-0 flex-col gap-1 overflow-hidden border-e px-1.5 py-1"
      [style.width.px]="headerWidth()"
    >
      <ng-content />
    </div>
    <div
      #body
      class="bg-surface-inset relative min-w-0 flex-1 cursor-crosshair touch-none overflow-hidden"
      role="group"
      [attr.aria-label]="label() + ' lane'"
      [attr.data-testid]="testId() || null"
      (pointerdown)="down($event)"
      (pointermove)="moveTo($event)"
      (pointerup)="up($event)"
      (pointercancel)="cancel()"
      (keydown)="key($event)"
    >
      <svg class="pointer-events-none absolute inset-0 size-full" aria-hidden="true">
        @for (x of gridLines(); track $index) {
          <line
            class="stroke-border"
            stroke-opacity="0.45"
            [attr.x1]="x"
            [attr.x2]="x"
            y1="0"
            [attr.y2]="height()"
            stroke-width="1"
          />
        }
        @for (x of barLines(); track $index) {
          <line class="stroke-border" [attr.x1]="x" [attr.x2]="x" y1="0" [attr.y2]="height()" stroke-width="1" />
        }
        @if (zeroY() !== null) {
          <line class="stroke-border" x1="0" x2="100%" [attr.y1]="zeroY()" [attr.y2]="zeroY()" stroke-width="1" />
        }
        @if (preview(); as p) {
          <polyline
            class="stroke-primary"
            fill="none"
            stroke-width="1.5"
            stroke-linejoin="round"
            stroke-linecap="round"
            [attr.points]="p"
          />
        }
      </svg>
      @for (bar of bars(); track $index) {
        <span
          role="slider"
          class="focus-visible:outline-focus absolute bottom-0 rounded-t-[2px] focus-visible:outline-2 focus-visible:outline-offset-1"
          [class.bg-primary]="!bar.selected"
          [class.bg-lit]="bar.selected"
          [style.left.px]="bar.x"
          [style.width.px]="bar.width"
          [style.top.px]="bar.y"
          [style.opacity]="bar.selected ? 1 : 0.8"
          [attr.tabindex]="$index === focusIndex() ? 0 : -1"
          [attr.data-index]="$index"
          [attr.aria-label]="label() + ' ' + ($index + 1)"
          [attr.aria-valuemin]="min()"
          [attr.aria-valuemax]="max()"
          [attr.aria-valuenow]="bar.value"
          [attr.aria-valuetext]="bar.valueText"
          aria-orientation="vertical"
        ></span>
      }
    </div>
  `
})
export class AnStepLane {
  /** The lane's name ("Velocity", "CC 74"): the body is "<label> lane", each bar "<label> <n>". */
  readonly label = input.required<string>();
  readonly bars = input<readonly StepBar[]>([]);
  readonly tool = input<StepLaneTool>('pencil');
  readonly height = input(64, { transform: numberAttribute });
  readonly headerWidth = input(128, { transform: numberAttribute });
  /** The value range, for the bars' sliders. */
  readonly min = input(0, { transform: numberAttribute });
  readonly max = input(127, { transform: numberAttribute });
  /** Grid lines (the lane's resolution) and bar lines, as x positions in the body. */
  readonly gridLines = input<readonly number[]>([]);
  readonly barLines = input<readonly number[]>([]);
  /** A centre line's y (pitch bend's 0), or null. */
  readonly zeroY = input<number | null>(null);
  /** The body's `data-testid` (`step-lane-velocity`, `step-lane-controlChange-74`). */
  readonly testId = input('');

  /** A finished pencil or line gesture. */
  readonly stroke = output<StepLaneStroke>();
  readonly barKey = output<StepBarKey>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly points = signal<readonly { x: number; y: number }[]>([]);
  private pointer: number | null = null;
  private readonly focused = signal(0);

  protected readonly focusIndex = computed(() => Math.min(this.focused(), Math.max(0, this.bars().length - 1)));
  protected readonly preview = computed(() => {
    const points = this.points();
    return points.length > 1 ? points.map(p => `${p.x},${p.y}`).join(' ') : '';
  });

  private local(event: PointerEvent): { x: number; y: number } {
    const body = event.currentTarget as HTMLElement;
    const rect = body.getBoundingClientRect();
    return {
      x: Math.round((event.clientX - rect.left) * 100) / 100,
      y: Math.min(this.height(), Math.max(0, Math.round((event.clientY - rect.top) * 100) / 100))
    };
  }

  protected down(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    event.preventDefault();
    this.pointer = event.pointerId;
    capture(event.currentTarget as HTMLElement, event.pointerId, true);
    const point = this.local(event);
    this.points.set(this.tool() === 'line' ? [point, point] : [point]);
  }

  protected moveTo(event: PointerEvent): void {
    if (this.pointer !== event.pointerId) {
      return;
    }
    const point = this.local(event);
    this.points.update(points => (this.tool() === 'line' ? [points[0] ?? point, point] : [...points, point]));
  }

  protected up(event: PointerEvent): void {
    if (this.pointer !== event.pointerId) {
      return;
    }
    this.moveTo(event);
    this.pointer = null;
    capture(event.currentTarget as HTMLElement, event.pointerId, false);
    const points = this.points();
    this.points.set([]);
    if (points.length > 0) {
      this.stroke.emit({ tool: this.tool(), points });
    }
  }

  protected cancel(): void {
    this.pointer = null;
    this.points.set([]);
  }

  protected key(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    const index = Number(target.dataset['index']);
    if (target.getAttribute('role') !== 'slider' || Number.isNaN(index)) {
      return;
    }
    const bars = this.host.nativeElement.querySelectorAll<HTMLElement>('[role="slider"]');
    switch (event.key) {
      case 'ArrowLeft':
      case 'ArrowRight': {
        const next = Math.max(0, Math.min(bars.length - 1, index + (event.key === 'ArrowRight' ? 1 : -1)));
        this.focused.set(next);
        bars[next]?.focus();
        break;
      }
      case 'ArrowUp':
      case 'ArrowDown':
        this.focused.set(index);
        this.barKey.emit({ index, by: (event.key === 'ArrowUp' ? 1 : -1) * (event.shiftKey ? 10 : 1) });
        break;
      case 'Home':
      case 'End': {
        const next = event.key === 'Home' ? 0 : bars.length - 1;
        this.focused.set(next);
        bars[next]?.focus();
        break;
      }
      default:
        return;
    }
    event.preventDefault();
  }
}
