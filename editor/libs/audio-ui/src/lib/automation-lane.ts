import { ChangeDetectionStrategy, Component, computed, input, numberAttribute, output } from '@angular/core';

/** A point of an automation lane as drawn: its position in CSS pixels within the lane. */
export interface AutomationHandle {
  x: number;
  y: number;
  selected: boolean;
  /** The point's value as text ("−6.0 dB"), for assistive technology. */
  valueText: string;
}

/** A key pressed on a point's handle. */
export interface AutomationPointKey {
  index: number;
  key: 'up' | 'down' | 'left' | 'right' | 'delete';
  /** ⇧ was held: a fine step. */
  fine: boolean;
}

const Keys: Readonly<Record<string, AutomationPointKey['key']>> = {
  ArrowUp: 'up',
  ArrowDown: 'down',
  ArrowLeft: 'left',
  ArrowRight: 'right',
  Delete: 'delete',
  Backspace: 'delete'
};

/** The {@link AutomationPointKey} a key press on a handle means, or null for another key. */
export function automationPointKey(
  index: number,
  event: { key: string; shiftKey: boolean }
): AutomationPointKey | null {
  const key = Keys[event.key];
  return key ? { index, key, fine: event.shiftKey } : null;
}

/**
 * One automation lane as Logic draws it: the curve over a faint fill, the points as small handles (filled when
 * selected), and a selected time range as a translucent band. Presentational: the owner computes the geometry (the
 * curve's path and the points' pixels) and handles the pointer on the host (handles carry `data-index`); a key on a
 * focused handle comes out as `pointKey`. Mode `off` dims the curve, as automation that does not play.
 */
@Component({
  selector: 'an-automation-lane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'relative block overflow-hidden',
    role: 'group',
    '[attr.aria-label]': 'label()',
    '[style.height.px]': 'height()',
    '[style.--lane-color]': 'stroke()'
  },
  template: `
    <svg class="pointer-events-none absolute inset-0 size-full overflow-visible" aria-hidden="true">
      @if (range(); as r) {
        <rect
          class="fill-primary/15 stroke-primary"
          stroke-width="1"
          [attr.x]="min(r.x0, r.x1)"
          y="0"
          [attr.width]="abs(r.x1 - r.x0)"
          [attr.height]="height()"
        />
      }
      @if (area()) {
        <path [attr.d]="area()" fill="var(--lane-color)" fill-opacity="0.14" stroke="none" />
      }
      @if (path()) {
        <path
          [attr.d]="path()"
          fill="none"
          stroke="var(--lane-color)"
          stroke-width="1.5"
          stroke-linejoin="round"
          stroke-linecap="round"
        />
      }
    </svg>
    @for (point of points(); track $index) {
      <span
        role="slider"
        tabindex="0"
        class="focus-visible:outline-focus absolute size-[9px] -translate-1/2 cursor-grab rounded-full border-[1.5px] focus-visible:outline-2 focus-visible:outline-offset-1"
        [style.left.px]="point.x"
        [style.top.px]="point.y"
        [style.border-color]="stroke()"
        [style.background-color]="point.selected ? stroke() : 'var(--color-surface)'"
        [attr.data-index]="$index"
        [attr.aria-label]="label() + ' point ' + ($index + 1)"
        aria-valuemin="0"
        aria-valuemax="1"
        [attr.aria-valuenow]="level(point.y)"
        [attr.aria-valuetext]="point.valueText"
        (keydown)="key($index, $event)"
      ></span>
    }
  `
})
export class AnAutomationLane {
  /** What the lane automates ("Vocals Volume automation"). */
  readonly label = input.required<string>();
  /** The curve's SVG path in CSS pixels; '' for none. */
  readonly path = input('');
  /** The area under the curve's SVG path; '' for none. */
  readonly area = input('');
  readonly points = input<readonly AutomationHandle[]>([]);
  readonly height = input(72, { transform: numberAttribute });
  readonly color = input('var(--color-primary)');
  /** A selected time range, in pixels. */
  readonly range = input<{ x0: number; x1: number } | null>(null);
  /** The track's automation mode: `off` dims the lane. */
  readonly mode = input('read');

  readonly pointKey = output<AutomationPointKey>();

  protected readonly stroke = computed(() => (this.mode() === 'off' ? 'var(--color-foreground-muted)' : this.color()));
  protected readonly min = Math.min;
  /** A handle's height in the lane, 0 (bottom) … 1 (top), as its slider value; its text says the real value. */
  protected level(y: number): number {
    const height = this.height();
    return height > 0 ? Math.round(Math.min(1, Math.max(0, 1 - y / height)) * 1000) / 1000 : 0;
  }

  protected readonly abs = Math.abs;

  protected key(index: number, event: KeyboardEvent): void {
    const key = automationPointKey(index, event);
    if (key) {
      event.preventDefault();
      this.pointKey.emit(key);
    }
  }
}
