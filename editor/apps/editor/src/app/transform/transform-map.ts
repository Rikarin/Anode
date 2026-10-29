import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { identityMap } from '../midi-edit/transform';

/** The map's size: one output per input value 0…127. */
export const MapSteps = 128;

function clampValue(value: number): number {
  return Math.min(127, Math.max(0, Math.round(value)));
}

/** A map of 128 values 0…127 (a missing or short map reads as the identity). */
export function normalizeMap(map: readonly number[] | null | undefined): number[] {
  const identity = identityMap();
  return identity.map(i => clampValue(map?.[i] ?? i));
}

/**
 * The map with a stroke drawn from input `x0` at output `y0` to `x1` at `y1`: every step between them takes the
 * line's value there (a pointer moving fast still leaves no gaps).
 */
export function drawMapLine(map: readonly number[], x0: number, y0: number, x1: number, y1: number): number[] {
  const next = normalizeMap(map);
  const from = Math.min(127, Math.max(0, Math.round(x0)));
  const to = Math.min(127, Math.max(0, Math.round(x1)));
  if (from === to) {
    next[to] = clampValue(y1);
    return next;
  }
  const step = from < to ? 1 : -1;
  for (let x = from; x !== to + step; x += step) {
    next[x] = clampValue(y0 + ((y1 - y0) * (x - from)) / (to - from));
  }
  return next;
}

/** The map with step `index` moved by `delta` (kept 0…127). */
export function nudgeMap(map: readonly number[], index: number, delta: number): number[] {
  const next = normalizeMap(map);
  const i = Math.min(127, Math.max(0, index));
  next[i] = clampValue((next[i] ?? i) + delta);
  return next;
}

/** A pointer's place in the map editor: the input step under x and the output at y (top 127). */
export function mapPoint(x: number, y: number, width: number, height: number): { index: number; value: number } {
  const index = Math.min(127, Math.max(0, Math.floor((x / Math.max(1, width)) * MapSteps)));
  const value = clampValue(127 * (1 - y / Math.max(1, height)));
  return { index, value };
}

/**
 * The MIDI Transform window's 128-step map editor (`useMap`): one bar per input value 0…127, as high as its output.
 * Draw with the pointer (a stroke sets every step it crosses). From the keyboard the map is one slider for the focused
 * step: ←/→ move to the previous or next step (⇧: 8), ↑/↓ change its output (⇧: 8), Home/End to 0 and 127, Page Up
 * and Page Down by 16. Reset returns the identity. `changed` fires with the whole map after each edit.
 */
@Component({
  selector: 'an-transform-map',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex flex-col gap-1' },
  template: `
    <div class="flex items-center gap-2">
      <span class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">Map</span>
      <span class="text-foreground readout text-[11px]" aria-hidden="true"
        >In {{ focusIndex() }} → Out {{ values()[focusIndex()] }}</span
      >
      <button
        type="button"
        class="bezel text-foreground ms-auto h-6 px-2 text-[11px] font-semibold"
        data-testid="transform-map-reset"
        (click)="changed.emit(identity())"
      >
        Reset Map
      </button>
    </div>
    <div
      class="well focus-visible:outline-focus relative h-32 cursor-crosshair touch-none rounded-[5px] focus-visible:outline-2"
      role="slider"
      tabindex="0"
      data-testid="transform-map"
      aria-valuemin="0"
      aria-valuemax="127"
      [attr.aria-label]="'Map: output of input ' + focusIndex()"
      [attr.aria-valuenow]="values()[focusIndex()]"
      [attr.aria-valuetext]="'Input ' + focusIndex() + ', output ' + values()[focusIndex()]"
      (keydown)="key($event)"
      (pointerdown)="down($event)"
      (pointermove)="move($event)"
      (pointerup)="up($event)"
      (pointercancel)="up($event)"
    >
      <svg class="absolute inset-0 size-full" viewBox="0 0 128 128" preserveAspectRatio="none" aria-hidden="true">
        <line x1="0" y1="128" x2="128" y2="0" class="stroke-knob-track" stroke-width="0.5" />
        @for (value of values(); track $index) {
          <rect
            [attr.x]="$index"
            [attr.y]="127 - value"
            width="1"
            [attr.height]="value + 1"
            [class]="$index === focusIndex() ? 'fill-primary' : 'fill-primary/45'"
          />
        }
      </svg>
    </div>
  `
})
export class TransformMap {
  /** The map (null: the identity). */
  readonly map = input<readonly number[] | null>(null);
  readonly changed = output<number[]>();

  protected readonly values = computed(() => normalizeMap(this.map()));
  protected readonly focusIndex = signal(60);
  protected readonly identity = identityMap;
  private stroke: { pointer: number; index: number; value: number } | null = null;
  /** The map while a stroke draws (shown before it is committed). */
  private drawing: number[] | null = null;

  protected key(event: KeyboardEvent): void {
    const big = event.shiftKey ? 8 : 1;
    const index = this.focusIndex();
    switch (event.key) {
      case 'ArrowLeft':
        this.focusIndex.set(Math.max(0, index - big));
        break;
      case 'ArrowRight':
        this.focusIndex.set(Math.min(127, index + big));
        break;
      case 'ArrowUp':
        this.changed.emit(nudgeMap(this.values(), index, big));
        break;
      case 'ArrowDown':
        this.changed.emit(nudgeMap(this.values(), index, -big));
        break;
      case 'PageUp':
        this.changed.emit(nudgeMap(this.values(), index, 16));
        break;
      case 'PageDown':
        this.changed.emit(nudgeMap(this.values(), index, -16));
        break;
      case 'Home':
        this.changed.emit(nudgeMap(this.values(), index, -127));
        break;
      case 'End':
        this.changed.emit(nudgeMap(this.values(), index, 127));
        break;
      default:
        return;
    }
    event.preventDefault();
  }

  private point(event: PointerEvent): { index: number; value: number } {
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    return mapPoint(event.clientX - rect.left, event.clientY - rect.top, rect.width, rect.height);
  }

  protected down(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    const p = this.point(event);
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    this.stroke = { pointer: event.pointerId, ...p };
    this.focusIndex.set(p.index);
    this.drawing = drawMapLine(this.values(), p.index, p.value, p.index, p.value);
    this.changed.emit(this.drawing);
  }

  protected move(event: PointerEvent): void {
    if (this.stroke?.pointer !== event.pointerId || !this.drawing) {
      return;
    }
    const p = this.point(event);
    this.drawing = drawMapLine(this.drawing, this.stroke.index, this.stroke.value, p.index, p.value);
    this.stroke = { pointer: event.pointerId, ...p };
    this.focusIndex.set(p.index);
    this.changed.emit(this.drawing);
  }

  protected up(event: PointerEvent): void {
    if (this.stroke?.pointer === event.pointerId) {
      this.stroke = null;
      this.drawing = null;
    }
  }
}
