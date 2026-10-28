import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  input,
  model,
  numberAttribute,
  output
} from '@angular/core';
import { type ParameterTaper, formatParameter, fromNormal, snapToStep, toNormal } from './parameter-values';

/** Pixels of vertical drag for the whole travel; ⇧ makes it ten times finer. */
const DragPixels = 200;
const Sweep = 270;

/** A point on the dial's circle (radius `r` around 12,12) at `normal` of the sweep from 7:30. */
function point(normal: number, r: number): { x: number; y: number } {
  const angle = ((135 + normal * Sweep) * Math.PI) / 180;
  return { x: 12 + r * Math.cos(angle), y: 12 + r * Math.sin(angle) };
}

/** An SVG arc along the dial's track from `from` to `to` (fractions of the sweep). */
function arcPath(from: number, to: number): string {
  const r = 10;
  const a = point(from, r);
  const b = point(to, r);
  const large = (to - from) * Sweep > 180 ? 1 : 0;
  return `M ${a.x.toFixed(2)} ${a.y.toFixed(2)} A ${r} ${r} 0 ${large} 1 ${b.x.toFixed(2)} ${b.y.toFixed(2)}`;
}

let nextId = 0;

/**
 * A rotary control for a plugin parameter (Cathode's knob, made generic): any range with a unit, a linear or
 * logarithmic taper (automatic: logarithmic for frequencies and times spanning two decades), and discrete steps. An
 * ARIA slider: arrows step 1 % of the travel or one step (⇧: finer), Page keys 10 %, Home and End go to the ends; drag
 * vertically (⇧ for fine); double-click or ⌥-click resets it to its default. `committed` fires when an interaction
 * ends: a good moment for an undo point.
 */
@Component({
  selector: 'an-knob',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-flex w-16 flex-col items-center gap-0.5 select-none' },
  template: `
    <div
      role="slider"
      class="focus-visible:outline-focus size-9 cursor-ns-resize touch-none rounded-full outline-offset-2 focus-visible:outline-2"
      [class.opacity-50]="disabled()"
      [attr.tabindex]="disabled() ? -1 : 0"
      [attr.aria-labelledby]="labelId"
      [attr.aria-valuemin]="min()"
      [attr.aria-valuemax]="max()"
      [attr.aria-valuenow]="rounded()"
      [attr.aria-valuetext]="display()"
      [attr.aria-disabled]="disabled() || null"
      [attr.title]="label() + ': ' + display() + ' (double-click for the default)'"
      (pointerdown)="grab($event)"
      (pointermove)="drag($event)"
      (pointerup)="release($event)"
      (pointercancel)="release($event)"
      (dblclick)="reset()"
      (keydown)="key($event)"
    >
      <svg viewBox="0 0 24 24" class="size-full" aria-hidden="true">
        <path [attr.d]="track" class="stroke-border fill-none" stroke-width="2.5" stroke-linecap="round" />
        @if (arc(); as d) {
          <path [attr.d]="d" class="stroke-primary fill-none" stroke-width="2.5" stroke-linecap="round" />
        }
        <circle cx="12" cy="12" r="6.5" class="fill-surface-raised stroke-border-strong" stroke-width="1" />
        <line
          x1="12"
          y1="12"
          [attr.x2]="pointer().x"
          [attr.y2]="pointer().y"
          class="stroke-foreground"
          stroke-width="1.5"
          stroke-linecap="round"
        />
      </svg>
    </div>
    <span class="text-foreground text-[10px] leading-tight tabular-nums" aria-hidden="true">{{ display() }}</span>
    <span class="text-foreground-muted line-clamp-2 max-w-full text-center text-[10px] leading-tight" [id]="labelId">
      {{ label() }}
    </span>
  `
})
export class AnKnob {
  protected readonly labelId = `an-knob-${nextId++}`;
  private dragStart: { y: number; normal: number } | null = null;

  readonly value = model(0);
  readonly min = input(0, { transform: numberAttribute });
  readonly max = input(1, { transform: numberAttribute });
  /** The value a double-click returns to; the minimum when unset. */
  readonly defaultValue = input<number | null>(null);
  /** Discrete values: `steps` + 1 from min to max; 0 for continuous. */
  readonly steps = input(0, { transform: numberAttribute });
  /** The travel's shape; automatic when unset. */
  readonly taper = input<ParameterTaper | null>(null);
  readonly unit = input('');
  readonly label = input('');
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly committed = output<number>();

  private readonly range = computed(() => {
    const taper = this.taper();
    return { min: this.min(), max: this.max(), steps: this.steps(), ...(taper ? { taper } : {}) };
  });
  protected readonly track = arcPath(0, 1);
  protected readonly normal = computed(() => toNormal(this.value(), this.range()));
  /** The value's stretch of the track: from the centre for a range around zero, else from the start. */
  protected readonly arc = computed(() => {
    const n = this.normal();
    const origin = this.min() < 0 && this.max() > 0 ? toNormal(0, this.range()) : 0;
    const from = Math.min(origin, n);
    const to = Math.max(origin, n);
    return to - from < 0.002 ? null : arcPath(from, to);
  });
  protected readonly pointer = computed(() => point(this.normal(), 6));
  protected readonly display = computed(() => formatParameter(this.value(), this.unit(), this.steps()));
  protected readonly rounded = computed(() => Math.round(this.value() * 1000) / 1000);

  protected grab(event: PointerEvent): void {
    if (this.disabled() || event.button !== 0) {
      return;
    }

    if (event.altKey) {
      this.reset();
      return;
    }

    const element = event.currentTarget as HTMLElement;
    element.setPointerCapture(event.pointerId);
    element.focus();
    this.dragStart = { y: event.clientY, normal: this.normal() };
  }

  protected drag(event: PointerEvent): void {
    if (!this.dragStart) {
      return;
    }

    const scale = event.shiftKey ? DragPixels * 10 : DragPixels;
    // Re-anchor on every move so pressing or releasing ⇧ mid-drag does not jump.
    const normal = Math.min(1, Math.max(0, this.dragStart.normal + (this.dragStart.y - event.clientY) / scale));
    this.dragStart = { y: event.clientY, normal };
    this.set(fromNormal(normal, this.range()));
  }

  protected release(event: PointerEvent): void {
    if (this.dragStart) {
      this.dragStart = null;
      (event.currentTarget as HTMLElement).releasePointerCapture(event.pointerId);
      this.committed.emit(this.value());
    }
  }

  protected key(event: KeyboardEvent): void {
    if (this.disabled()) {
      return;
    }

    const steps = this.steps();
    const fine = event.shiftKey ? 0.001 : 0.01;
    const step = steps > 0 ? 1 / steps : fine;
    const moves: Record<string, number> = {
      ArrowUp: step,
      ArrowRight: step,
      ArrowDown: -step,
      ArrowLeft: -step,
      PageUp: Math.max(step, 0.1),
      PageDown: -Math.max(step, 0.1)
    };
    const move = moves[event.key];
    if (move !== undefined) {
      this.set(fromNormal(this.normal() + move, this.range()));
    } else if (event.key === 'Home' || event.key === 'End') {
      this.set(event.key === 'Home' ? this.min() : this.max());
    } else {
      return;
    }

    event.preventDefault();
    this.committed.emit(this.value());
  }

  protected reset(): void {
    if (!this.disabled()) {
      const value = snapToStep(this.defaultValue() ?? this.min(), this.range());
      this.value.set(value);
      this.committed.emit(value);
    }
  }

  private set(value: number): void {
    if (value !== this.value()) {
      this.value.set(value);
    }
  }
}
