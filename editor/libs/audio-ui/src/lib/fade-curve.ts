import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, model, output } from '@angular/core';

let nextId = 0;

/**
 * A fade's gain at position `x` (0…1) for curve `c` (−1…1), as Anode's engine plays it (docs/04-phase-3.md): a fade-in
 * is x^(2^(−2c)); a fade-out at x is a fade-in at 1 − x. 0 is linear; −1 starts slowly (exponential-looking), 1 starts
 * fast (logarithmic-looking).
 */
export function fadeCurveGain(x: number, c: number, out = false): number {
  const position = Math.min(1, Math.max(0, out ? 1 - x : x));
  return position ** (2 ** (-2 * Math.min(1, Math.max(-1, c))));
}

/**
 * A fade's curve (Logic's fade curve in the Region inspector): the gain shape drawn in a sunken well, adjustable by
 * dragging up or down (⇧ for fine), arrow and Page keys, Home/End for the extremes, and a double-click for linear. A
 * slider from −1 to 1; `committed` fires when a change is done (the end of a drag, each key press).
 */
@Component({
  selector: 'an-fade-curve',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-flex flex-col items-center gap-0.5' },
  template: `
    <div
      class="well focus-visible:outline-focus relative h-9 w-14 cursor-ns-resize touch-none rounded-[5px] focus-visible:outline-2 focus-visible:outline-offset-1"
      role="slider"
      tabindex="0"
      [class.opacity-35]="disabled()"
      [attr.aria-labelledby]="labelId"
      aria-valuemin="-1"
      aria-valuemax="1"
      [attr.aria-valuenow]="value()"
      [attr.aria-valuetext]="text()"
      [attr.aria-disabled]="disabled() || null"
      (pointerdown)="down($event)"
      (pointermove)="drag($event)"
      (pointerup)="up($event)"
      (pointercancel)="up($event)"
      (keydown)="key($event)"
      (dblclick)="set(0, true)"
    >
      <svg class="absolute inset-1 size-[calc(100%-0.5rem)] overflow-visible" viewBox="0 0 48 28" aria-hidden="true">
        <path class="fill-primary/20 stroke-none" [attr.d]="area()" />
        <path class="stroke-knob-arc fill-none" stroke-width="1.75" stroke-linecap="round" [attr.d]="line()" />
      </svg>
    </div>
    <span class="text-foreground-muted text-[10px] leading-tight" [id]="labelId">{{ label() }}</span>
    <span class="text-foreground readout text-[10px] leading-tight">{{ text() }}</span>
  `
})
export class AnFadeCurve {
  protected readonly labelId = `an-fade-curve-${nextId++}`;

  readonly value = model(0);
  /** A fade-out's curve is drawn falling. */
  readonly out = input(false, { transform: booleanAttribute });
  readonly label = input('Curve');
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly committed = output<number>();

  private dragging: { y: number; value: number; pointer: number } | null = null;

  protected readonly text = computed(() => {
    const v = this.value();
    return v === 0 ? 'Linear' : `${v > 0 ? '+' : ''}${v.toFixed(2)}`;
  });

  private readonly points = computed(() => {
    const c = this.value();
    const out = this.out();
    const steps = 24;
    return Array.from({ length: steps + 1 }, (_, i) => {
      const x = i / steps;
      return [x * 48, 28 - fadeCurveGain(x, c, out) * 28] as const;
    });
  });

  protected readonly line = computed(() =>
    this.points()
      .map(([x, y], i) => `${i === 0 ? 'M' : 'L'}${x.toFixed(2)} ${y.toFixed(2)}`)
      .join(' ')
  );

  protected readonly area = computed(() => `${this.line()} L48 28 L0 28 Z`);

  protected set(value: number, commit: boolean): void {
    if (this.disabled()) {
      return;
    }
    const next = Math.round(Math.min(1, Math.max(-1, value)) * 100) / 100;
    if (next !== this.value()) {
      this.value.set(next);
      if (commit) {
        this.committed.emit(next);
      }
    }
  }

  protected down(event: PointerEvent): void {
    if (this.disabled() || event.button !== 0) {
      return;
    }
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    this.dragging = { y: event.clientY, value: this.value(), pointer: event.pointerId };
  }

  protected drag(event: PointerEvent): void {
    if (this.dragging?.pointer !== event.pointerId) {
      return;
    }
    const scale = event.shiftKey ? 400 : 80;
    this.set(this.dragging.value + (this.dragging.y - event.clientY) / scale, false);
  }

  protected up(event: PointerEvent): void {
    if (this.dragging?.pointer !== event.pointerId) {
      return;
    }
    const started = this.dragging.value;
    this.dragging = null;
    if (this.value() !== started) {
      this.committed.emit(this.value());
    }
  }

  protected key(event: KeyboardEvent): void {
    const step = { ArrowUp: 0.05, ArrowRight: 0.05, ArrowDown: -0.05, ArrowLeft: -0.05, PageUp: 0.25, PageDown: -0.25 }[
      event.key
    ];
    if (step !== undefined) {
      event.preventDefault();
      this.set(this.value() + step, true);
    } else if (event.key === 'Home' || event.key === 'End') {
      event.preventDefault();
      this.set(event.key === 'Home' ? -1 : 1, true);
    }
  }
}
