import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  input,
  model,
  output,
  signal
} from '@angular/core';

/** A point on an XY pad, each axis 0…1 (y up). */
export interface XyValue {
  x: number;
  y: number;
}

let nextId = 0;

function clamp01(value: number): number {
  return Math.min(1, Math.max(0, value));
}

/**
 * An XY pad (Logic's Remix FX pads and the Smart Controls pad): a sunken square with a lit puck, both axes 0…1 with y
 * up. Drag anywhere on it; the pad is engaged (`engaged` true) while held, as Remix FX's effects play only while
 * touched. From the keyboard it is one ARIA slider: ←/→ move x, ↑/↓ move y by 5 % (⇧: 1 %), Home/End the corners, and
 * Space or Enter holds it engaged while down. `committed` fires when a gesture ends.
 */
@Component({
  selector: 'an-xy-pad',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-flex flex-col items-center gap-1 select-none' },
  template: `
    <div
      class="well focus-visible:outline-focus relative cursor-crosshair touch-none overflow-hidden rounded-[6px] outline-offset-2 focus-visible:outline-2"
      role="slider"
      aria-roledescription="XY pad"
      [style.width.px]="size()"
      [style.height.px]="size()"
      [class.opacity-35]="disabled()"
      [attr.tabindex]="disabled() ? -1 : 0"
      [attr.aria-labelledby]="labelId"
      aria-valuemin="0"
      aria-valuemax="100"
      [attr.aria-valuenow]="percent(value().x)"
      [attr.aria-valuetext]="text()"
      [attr.aria-disabled]="disabled() || null"
      [attr.data-engaged]="held()"
      (pointerdown)="down($event)"
      (pointermove)="move($event)"
      (pointerup)="up($event)"
      (pointercancel)="up($event)"
      (keydown)="key($event)"
      (keyup)="keyUp($event)"
    >
      <svg class="absolute inset-0 size-full" aria-hidden="true">
        <line x1="50%" y1="0" x2="50%" y2="100%" class="stroke-knob-track" stroke-width="1" />
        <line x1="0" y1="50%" x2="100%" y2="50%" class="stroke-knob-track" stroke-width="1" />
        <line
          [attr.x1]="percent(value().x) + '%'"
          y1="0"
          [attr.x2]="percent(value().x) + '%'"
          y2="100%"
          class="stroke-knob-arc"
          stroke-width="1"
          [attr.stroke-opacity]="held() ? 0.8 : 0.35"
        />
        <line
          x1="0"
          [attr.y1]="100 - percent(value().y) + '%'"
          x2="100%"
          [attr.y2]="100 - percent(value().y) + '%'"
          class="stroke-knob-arc"
          stroke-width="1"
          [attr.stroke-opacity]="held() ? 0.8 : 0.35"
        />
      </svg>
      <span
        class="knob-body absolute size-4 -translate-x-1/2 translate-y-1/2 rounded-full transition-transform duration-100"
        [class.scale-125]="held()"
        [class.ring-2]="held()"
        [class.ring-primary]="held()"
        [style.left.%]="percent(value().x)"
        [style.bottom.%]="percent(value().y)"
        aria-hidden="true"
      ></span>
    </div>
    <span class="text-foreground text-[10px] leading-tight font-semibold" [id]="labelId">{{ label() }}</span>
    @if (xLabel() || yLabel()) {
      <span class="text-foreground-muted readout text-[10px] leading-tight">{{ text() }}</span>
    }
  `
})
export class AnXyPad {
  protected readonly labelId = `an-xy-pad-${nextId++}`;

  readonly value = model<XyValue>({ x: 0.5, y: 0.5 });
  readonly label = input('XY Pad');
  /** What each axis controls ("Cutoff", "Resonance"); shown under the pad and read out. */
  readonly xLabel = input('');
  readonly yLabel = input('');
  /** The pad's side in CSS pixels. */
  readonly size = input(120);
  readonly disabled = input(false, { transform: booleanAttribute });
  /** The pad is held (pointer down, or Space/Enter down). */
  readonly engaged = output<boolean>();
  /** A gesture ended (pointer up, a key press): a good moment for an undo point. */
  readonly committed = output<XyValue>();

  protected readonly held = signal(false);
  private pointer: number | null = null;

  protected readonly text = computed(() => {
    const { x, y } = this.value();
    return `${this.xLabel() || 'X'} ${this.percent(x)} %, ${this.yLabel() || 'Y'} ${this.percent(y)} %`;
  });

  protected percent(value: number): number {
    return Math.round(clamp01(value) * 100);
  }

  protected down(event: PointerEvent): void {
    if (this.disabled() || event.button !== 0) {
      return;
    }
    const pad = event.currentTarget as HTMLElement;
    pad.setPointerCapture(event.pointerId);
    pad.focus();
    this.pointer = event.pointerId;
    this.engage(true);
    this.place(pad, event);
  }

  protected move(event: PointerEvent): void {
    if (this.pointer === event.pointerId) {
      this.place(event.currentTarget as HTMLElement, event);
    }
  }

  protected up(event: PointerEvent): void {
    if (this.pointer !== event.pointerId) {
      return;
    }
    this.pointer = null;
    this.engage(false);
    this.committed.emit(this.value());
  }

  protected key(event: KeyboardEvent): void {
    if (this.disabled()) {
      return;
    }
    const step = event.shiftKey ? 0.01 : 0.05;
    const { x, y } = this.value();
    const moves: Record<string, XyValue> = {
      ArrowLeft: { x: x - step, y },
      ArrowRight: { x: x + step, y },
      ArrowDown: { x, y: y - step },
      ArrowUp: { x, y: y + step },
      Home: { x: 0, y: 0 },
      End: { x: 1, y: 1 }
    };
    const next = moves[event.key];
    if (next) {
      event.preventDefault();
      this.set(next);
      this.committed.emit(this.value());
    } else if ((event.key === ' ' || event.key === 'Enter') && !event.repeat) {
      event.preventDefault();
      this.engage(true);
    }
  }

  protected keyUp(event: KeyboardEvent): void {
    if ((event.key === ' ' || event.key === 'Enter') && this.held()) {
      event.preventDefault();
      this.engage(false);
      this.committed.emit(this.value());
    }
  }

  private place(pad: HTMLElement, event: PointerEvent): void {
    const box = pad.getBoundingClientRect();
    if (box.width <= 0 || box.height <= 0) {
      return;
    }
    this.set({ x: (event.clientX - box.left) / box.width, y: 1 - (event.clientY - box.top) / box.height });
  }

  private set(next: XyValue): void {
    const value = { x: Math.round(clamp01(next.x) * 1000) / 1000, y: Math.round(clamp01(next.y) * 1000) / 1000 };
    const current = this.value();
    if (value.x !== current.x || value.y !== current.y) {
      this.value.set(value);
    }
  }

  private engage(on: boolean): void {
    if (this.held() !== on) {
      this.held.set(on);
      this.engaged.emit(on);
    }
  }
}
