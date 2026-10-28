import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, model, output } from '@angular/core';

/** How the Varispeed display shows the speed (Logic's): a percentage, the resulting tempo, or the detune. */
export type VarispeedDisplayUnit = 'percent' | 'tempo' | 'detune';

/** The speed range Varispeed allows (50 %…150 %). */
export const VarispeedRange = [0.5, 1.5] as const;

/** A speed as the display shows it: "−10.00 %", "108.00 bpm" (at `bpm`), "−1.82 st". */
export function varispeedText(speed: number, unit: VarispeedDisplayUnit, bpm = 120): string {
  const signed = (value: number) => {
    const fixed = Math.abs(value).toFixed(2);
    return `${value > 0 && fixed !== '0.00' ? '+' : value < 0 && fixed !== '0.00' ? '−' : ''}${fixed}`;
  };
  switch (unit) {
    case 'percent':
      return `${signed((speed - 1) * 100)} %`;
    case 'tempo':
      return `${(bpm * speed).toFixed(2)} bpm`;
    case 'detune':
      return `${signed(12 * Math.log2(speed))} st`;
  }
}

/**
 * The Varispeed display (Logic's, in the control bar): an LCD segment labelled Varispeed with the speed in percent, the
 * resulting tempo or the detune. A spin button in percent (−50…50): ↑/↓ change it by 0.1 % (⇧ 1 %), Page keys by 5 %,
 * Home/End reach the ends, a vertical drag changes it (⇧ finely); Return or a double-click asks to type a value
 * (`edit`). `committed` fires when a change is done (each key press, the end of a drag). Shown dimmed while Varispeed is
 * off.
 */
@Component({
  selector: 'an-varispeed-display',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-flex' },
  template: `
    <div
      class="lcd-glass focus-visible:outline-focus flex h-10 min-w-24 cursor-ns-resize touch-none flex-col items-center justify-center rounded-[6px] px-3 leading-none select-none focus-visible:outline-2 focus-visible:outline-offset-1"
      role="spinbutton"
      tabindex="0"
      aria-label="Varispeed"
      aria-valuemin="-50"
      aria-valuemax="50"
      data-testid="varispeed-display"
      [class.opacity-50]="!enabled()"
      [attr.aria-valuenow]="percent()"
      [attr.aria-valuetext]="text()"
      [attr.aria-disabled]="disabled() || null"
      (pointerdown)="down($event)"
      (pointermove)="drag($event)"
      (pointerup)="up($event)"
      (pointercancel)="up($event)"
      (keydown)="key($event)"
      (dblclick)="edit.emit()"
    >
      <span class="readout text-[15px] font-semibold whitespace-nowrap">{{ text() }}</span>
      <span class="text-lcd-muted mt-1 text-[9px] font-semibold tracking-[0.08em] uppercase">Varispeed</span>
    </div>
  `
})
export class AnVarispeedDisplay {
  /** The speed, 0.5…1.5 (1 plays at the project tempo). */
  readonly speed = model(1);
  readonly unit = input<VarispeedDisplayUnit>('percent');
  /** The project tempo, for the resulting-tempo unit. */
  readonly bpm = input(120);
  /** Whether Varispeed is on (off: dimmed, still adjustable). */
  readonly enabled = input(true, { transform: booleanAttribute });
  readonly disabled = input(false, { transform: booleanAttribute });
  /** A change is done: the speed to apply. */
  readonly committed = output<number>();
  /** Return or a double-click: the person wants to type a value. */
  readonly edit = output();

  private dragging: { y: number; speed: number; pointer: number } | null = null;

  protected readonly percent = computed(() => Math.round((this.speed() - 1) * 1000) / 10);
  protected readonly text = computed(() => varispeedText(this.speed(), this.unit(), this.bpm()));

  protected set(speed: number, commit: boolean): void {
    if (this.disabled()) {
      return;
    }
    const next = Math.round(Math.min(VarispeedRange[1], Math.max(VarispeedRange[0], speed)) * 10_000) / 10_000;
    if (next !== this.speed()) {
      this.speed.set(next);
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
    this.dragging = { y: event.clientY, speed: this.speed(), pointer: event.pointerId };
  }

  protected drag(event: PointerEvent): void {
    if (this.dragging?.pointer !== event.pointerId) {
      return;
    }
    // 0.2 % a pixel (⇧: 0.02 %).
    const perPixel = event.shiftKey ? 0.0002 : 0.002;
    this.set(this.dragging.speed + (this.dragging.y - event.clientY) * perPixel, false);
  }

  protected up(event: PointerEvent): void {
    if (this.dragging?.pointer !== event.pointerId) {
      return;
    }
    const started = this.dragging.speed;
    this.dragging = null;
    if (this.speed() !== started) {
      this.committed.emit(this.speed());
    }
  }

  protected key(event: KeyboardEvent): void {
    const fine = event.shiftKey ? 0.01 : 0.001;
    const step = { ArrowUp: fine, ArrowRight: fine, ArrowDown: -fine, ArrowLeft: -fine, PageUp: 0.05, PageDown: -0.05 }[
      event.key
    ];
    if (step !== undefined) {
      event.preventDefault();
      this.set(this.speed() + step, true);
    } else if (event.key === 'Home' || event.key === 'End') {
      event.preventDefault();
      this.set(event.key === 'Home' ? VarispeedRange[0] : VarispeedRange[1], true);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      this.edit.emit();
    }
  }
}
