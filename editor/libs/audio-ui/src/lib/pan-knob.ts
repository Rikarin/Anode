import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, model, output } from '@angular/core';
import { formatPan } from './taper';

/** Pixels of vertical drag for the whole range. */
const DragPixels = 150;
const Sweep = 270;

/** A point on the knob's circle (radius `r` around 12,12) at `normal` of the sweep from 7:30. */
function point(normal: number, r: number): { x: number; y: number } {
  const angle = ((135 + normal * Sweep) * Math.PI) / 180;
  return { x: 12 + r * Math.cos(angle), y: 12 + r * Math.sin(angle) };
}

/**
 * A channel's pan: a small knob from −1 (hard left) to 1 (hard right), shown as Logic does (L64 … C … R63). An ARIA
 * slider: arrows step 1/64 (⇧: finer), Home and End go hard left and right; drag vertically; double-click or
 * ⌥-click centres it.
 */
@Component({
  selector: 'an-pan-knob',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-flex flex-col items-center gap-0.5 select-none' },
  template: `
    <div
      role="slider"
      class="focus-visible:outline-focus relative size-8 cursor-ns-resize touch-none rounded-full outline-offset-2 focus-visible:outline-2"
      [class.opacity-50]="disabled()"
      [attr.tabindex]="disabled() ? -1 : 0"
      [attr.aria-label]="label()"
      aria-valuemin="-1"
      aria-valuemax="1"
      [attr.aria-valuenow]="rounded()"
      [attr.aria-valuetext]="display()"
      [attr.aria-disabled]="disabled() || null"
      [attr.title]="label() + ': ' + display() + ' (double-click to centre)'"
      (pointerdown)="grab($event)"
      (pointermove)="drag($event)"
      (pointerup)="release($event)"
      (pointercancel)="release($event)"
      (dblclick)="reset()"
      (keydown)="key($event)"
    >
      <span class="knob-body absolute inset-[23%]" aria-hidden="true"></span>
      <svg viewBox="0 0 24 24" class="absolute inset-0 size-full" aria-hidden="true">
        <path [attr.d]="track" class="stroke-knob-track fill-none" stroke-width="2.25" stroke-linecap="round" />
        <path [attr.d]="arc()" class="stroke-knob-arc fill-none" stroke-width="2.25" stroke-linecap="round" />
        <line
          [attr.x1]="pointerFrom().x"
          [attr.y1]="pointerFrom().y"
          [attr.x2]="pointer().x"
          [attr.y2]="pointer().y"
          class="stroke-knob-pointer"
          stroke-width="1.5"
          stroke-linecap="round"
        />
      </svg>
    </div>
    <span
      class="readout text-foreground bg-surface-inset border-hairline min-w-9 rounded-[4px] border px-1 py-px text-center text-[10px] font-medium shadow-[inset_0_1px_1px_rgb(0_0_0/0.2)]"
      aria-hidden="true"
      >{{ display() }}</span
    >
  `
})
export class AnPanKnob {
  private dragStart: { y: number; value: number } | null = null;

  readonly value = model(0);
  readonly label = input('Pan');
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly committed = output<number>();

  protected readonly track = arcPath(0, 1);
  protected readonly normal = computed(() => (Math.min(1, Math.max(-1, this.value())) + 1) / 2);
  protected readonly arc = computed(() => arcPath(Math.min(0.5, this.normal()), Math.max(0.5, this.normal())));
  protected readonly pointer = computed(() => point(this.normal(), 5.25));
  protected readonly pointerFrom = computed(() => point(this.normal(), 2));
  protected readonly display = computed(() => formatPan(this.value()));
  protected readonly rounded = computed(() => Math.round(this.value() * 100) / 100);

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
    this.dragStart = { y: event.clientY, value: this.value() };
  }

  protected drag(event: PointerEvent): void {
    if (!this.dragStart) {
      return;
    }

    const scale = event.shiftKey ? DragPixels * 10 : DragPixels;
    const value = Math.min(1, Math.max(-1, this.dragStart.value + (2 * (this.dragStart.y - event.clientY)) / scale));
    this.dragStart = { y: event.clientY, value };
    this.value.set(Math.round(value * 64) / 64);
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

    const step = event.shiftKey ? 1 / 256 : 1 / 64;
    const steps: Record<string, number> = { ArrowUp: step, ArrowRight: step, ArrowDown: -step, ArrowLeft: -step };
    const delta = steps[event.key];
    if (delta !== undefined) {
      this.value.set(Math.min(1, Math.max(-1, this.value() + delta)));
    } else if (event.key === 'Home' || event.key === 'End') {
      this.value.set(event.key === 'Home' ? -1 : 1);
    } else {
      return;
    }

    event.preventDefault();
    this.committed.emit(this.value());
  }

  protected reset(): void {
    if (!this.disabled()) {
      this.value.set(0);
      this.committed.emit(0);
    }
  }
}

/** An SVG arc along the knob's track from `from` to `to` (fractions of the sweep). */
function arcPath(from: number, to: number): string {
  const r = 10;
  const a = point(from, r);
  const b = point(to, r);
  const large = (to - from) * Sweep > 180 ? 1 : 0;
  return `M ${a.x.toFixed(2)} ${a.y.toFixed(2)} A ${r} ${r} 0 ${large} 1 ${b.x.toFixed(2)} ${b.y.toFixed(2)}`;
}
