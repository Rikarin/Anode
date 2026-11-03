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
import { AnKnob } from './knob';
import {
  formatAngle,
  objectFromSidePoint,
  objectFromTopPoint,
  objectSidePoint,
  objectTopPoint,
  shortSpeakerLabel,
  speakerPoint,
  wrapAngle,
  type PannerPoint,
  type PannerSpeaker
} from './panner-math';
import { PannerView, pointerToPanner, viewPercent } from './surround-panner';

/** The 3D Object Panner's settings (`ObjectPanner`): degrees, distance and size 0…1. */
export interface ObjectPannerValue {
  azimuth: number;
  elevation: number;
  distance: number;
  size: number;
}

/** The side view's extent in panner units: x 0…1 (the front to the right), y −1…1, with a margin. */
const SideMargin = 0.12;

/**
 * Logic's 3D Object Panner (docs/11-phase-10.md): an object's position in two views. The top view (a circle of the
 * layout's ear-level and upper speakers, ahead up) sets the azimuth (ADM: positive to the left) and the distance; the
 * side view (seen from the right, the front to the right, up up) sets the elevation (−90…90°) and the distance. The
 * Size knob spreads the object (0 a point … 1 everywhere). Each view is an ARIA slider: in the top view ←/→ turn the
 * azimuth by 5° (⇧ 1°) and ↑/↓ the distance by 5 %; in the side view ↑/↓ the elevation by 5° and ←/→ the distance.
 * `changed` carries only the fields that changed; `committed` ends a gesture.
 */
@Component({
  selector: 'an-object-panner',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnKnob],
  host: { class: 'inline-flex flex-col items-center gap-1 select-none' },
  template: `
    <div class="flex items-end gap-2">
      <div class="flex flex-col items-center gap-0.5">
        <div
          role="slider"
          aria-roledescription="object panner top view"
          class="focus-visible:outline-focus relative cursor-crosshair touch-none rounded-full outline-offset-2 focus-visible:outline-2"
          [style.width.px]="size()"
          [style.height.px]="size()"
          [class.opacity-35]="disabled()"
          [attr.tabindex]="disabled() ? -1 : 0"
          [attr.aria-label]="label() + ' top view'"
          aria-valuemin="-180"
          aria-valuemax="180"
          [attr.aria-valuenow]="round(value().azimuth)"
          [attr.aria-valuetext]="topText()"
          [attr.aria-disabled]="disabled() || null"
          (pointerdown)="down($event, 'top')"
          (pointermove)="move($event)"
          (pointerup)="up($event)"
          (pointercancel)="up($event)"
          (keydown)="topKey($event)"
        >
          <svg [attr.viewBox]="topBox" class="absolute inset-0 size-full overflow-visible" aria-hidden="true">
            <circle cx="0" cy="0" r="1" class="fill-surface-inset stroke-hairline-strong" stroke-width="0.02" />
            <circle cx="0" cy="0" r="0.5" class="stroke-knob-track fill-none" stroke-width="0.012" />
            <line x1="0" y1="-1" x2="0" y2="1" class="stroke-knob-track" stroke-width="0.01" />
            <line x1="-1" y1="0" x2="1" y2="0" class="stroke-knob-track" stroke-width="0.01" />
            <circle
              [attr.cx]="top().x"
              [attr.cy]="-top().y"
              [attr.r]="0.08 + value().size * 0.9"
              class="fill-primary/15 stroke-primary/40"
              stroke-width="0.012"
            />
            @for (s of drawn(); track s.label) {
              <circle
                [attr.cx]="s.point.x"
                [attr.cy]="-s.point.y"
                [attr.r]="s.upper ? 0.045 : 0.06"
                [class]="s.upper ? 'fill-foreground-muted' : 'fill-foreground'"
              />
              @if (!s.upper) {
                <text
                  [attr.x]="s.point.x * 1.14"
                  [attr.y]="-s.point.y * 1.14"
                  class="fill-foreground"
                  font-size="0.13"
                  font-weight="600"
                  text-anchor="middle"
                  dominant-baseline="central"
                >
                  {{ s.short }}
                </text>
              }
            }
          </svg>
          <span
            class="knob-body ring-primary absolute size-3.5 -translate-x-1/2 -translate-y-1/2 rounded-full ring-1"
            [style.left.%]="percent(top().x, 'x')"
            [style.top.%]="percent(top().y, 'y')"
            aria-hidden="true"
          ></span>
        </div>
        <span class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase" aria-hidden="true"
          >Top</span
        >
      </div>
      <div class="flex flex-col items-center gap-0.5">
        <div
          role="slider"
          aria-roledescription="object panner side view"
          class="well focus-visible:outline-focus relative cursor-crosshair touch-none rounded-[6px] outline-offset-2 focus-visible:outline-2"
          [style.width.px]="sideWidth()"
          [style.height.px]="size()"
          [class.opacity-35]="disabled()"
          [attr.tabindex]="disabled() ? -1 : 0"
          [attr.aria-label]="label() + ' side view'"
          aria-valuemin="-90"
          aria-valuemax="90"
          [attr.aria-valuenow]="round(value().elevation)"
          [attr.aria-valuetext]="sideText()"
          [attr.aria-disabled]="disabled() || null"
          (pointerdown)="down($event, 'side')"
          (pointermove)="move($event)"
          (pointerup)="up($event)"
          (pointercancel)="up($event)"
          (keydown)="sideKey($event)"
        >
          <svg [attr.viewBox]="sideBox" class="absolute inset-0 size-full" aria-hidden="true">
            <path d="M 0 -1 A 1 1 0 0 1 0 1" class="stroke-knob-track fill-none" stroke-width="0.015" />
            <line x1="0" y1="0" x2="1" y2="0" class="stroke-knob-track" stroke-width="0.01" />
            <circle cx="0" cy="0" r="0.06" class="fill-foreground" />
          </svg>
          <span
            class="knob-body ring-primary absolute size-3.5 -translate-x-1/2 -translate-y-1/2 rounded-full ring-1"
            [style.left.%]="sideLeft()"
            [style.top.%]="sideTop()"
            aria-hidden="true"
          ></span>
        </div>
        <span class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase" aria-hidden="true"
          >Side</span
        >
      </div>
    </div>
    <span class="readout text-foreground text-[11px] font-medium" aria-hidden="true"
      >{{ topText() }}, {{ sideText() }}</span
    >
    <an-knob
      label="Size"
      [min]="0"
      [max]="1"
      [defaultValue]="0"
      [value]="value().size"
      [disabled]="disabled()"
      [format]="percentText"
      (valueChange)="patch({ size: round($event) })"
      (committed)="committed.emit(value())"
    />
  `
})
export class AnObjectPanner {
  protected readonly topBox = `${-PannerView} ${-PannerView} ${2 * PannerView} ${2 * PannerView}`;
  protected readonly sideBox = `${-SideMargin} ${-1 - SideMargin} ${1 + 2 * SideMargin} ${2 + 2 * SideMargin}`;
  protected readonly percentText = (v: number) => `${Math.round(v * 100)} %`;

  readonly value = model<ObjectPannerValue>({ azimuth: 0, elevation: 0, distance: 1, size: 0 });
  /** The layout's speakers (drawn in the top view). */
  readonly speakers = input<readonly PannerSpeaker[]>([]);
  readonly label = input('3D Object Panner');
  /** Each view's height in CSS pixels (the top view is square, the side view half as wide). */
  readonly size = input(140, { transform: numberAttribute });
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly changed = output<Partial<ObjectPannerValue>>();
  readonly committed = output<ObjectPannerValue>();

  private drag: { pointer: number; view: 'top' | 'side' } | null = null;

  protected readonly top = computed(() => objectTopPoint(this.value()));
  protected readonly side = computed(() => objectSidePoint(this.value()));
  protected readonly sideWidth = computed(() =>
    Math.round((this.size() * (1 + 2 * SideMargin)) / (2 + 2 * SideMargin))
  );
  protected readonly sideLeft = computed(() => ((this.side().x + SideMargin) / (1 + 2 * SideMargin)) * 100);
  protected readonly sideTop = computed(() => ((1 + SideMargin - this.side().y) / (2 + 2 * SideMargin)) * 100);
  protected readonly drawn = computed(() =>
    this.speakers().flatMap(s => {
      const point = speakerPoint(s);
      return point ? [{ label: s.label, short: shortSpeakerLabel(s.label), point, upper: s.elevation > 0 }] : [];
    })
  );
  protected readonly topText = computed(
    () => `${formatAngle(this.value().azimuth)}, distance ${Math.round(this.value().distance * 100)} %`
  );
  protected readonly sideText = computed(() => `elevation ${Math.round(this.value().elevation)}°`);

  protected round(value: number): number {
    return Math.round(value * 100) / 100;
  }

  protected percent(value: number, axis: 'x' | 'y'): number {
    return viewPercent(value, axis);
  }

  protected patch(change: Partial<ObjectPannerValue>): void {
    const current = this.value();
    const changed: Partial<ObjectPannerValue> = {};
    for (const [field, next] of Object.entries(change) as [keyof ObjectPannerValue, number][]) {
      if (current[field] !== next) {
        changed[field] = next;
      }
    }
    if (Object.keys(changed).length) {
      this.value.set({ ...current, ...changed });
      this.changed.emit(changed);
    }
  }

  protected down(event: PointerEvent, view: 'top' | 'side'): void {
    if (this.disabled() || event.button !== 0) {
      return;
    }
    const element = event.currentTarget as HTMLElement;
    element.setPointerCapture(event.pointerId);
    element.focus();
    this.drag = { pointer: event.pointerId, view };
    this.place(element, event);
  }

  protected move(event: PointerEvent): void {
    if (this.drag?.pointer === event.pointerId) {
      this.place(event.currentTarget as HTMLElement, event);
    }
  }

  protected up(event: PointerEvent): void {
    if (this.drag?.pointer === event.pointerId) {
      this.drag = null;
      this.committed.emit(this.value());
    }
  }

  protected topKey(event: KeyboardEvent): void {
    const v = this.value();
    const turn = event.shiftKey ? 1 : 5;
    const step = event.shiftKey ? 0.01 : 0.05;
    this.keyed(event, {
      ArrowLeft: { azimuth: wrapAngle(v.azimuth + turn) },
      ArrowRight: { azimuth: wrapAngle(v.azimuth - turn) },
      ArrowUp: { distance: this.round(Math.min(1, v.distance + step)) },
      ArrowDown: { distance: this.round(Math.max(0, v.distance - step)) },
      Home: { azimuth: 0, distance: 1 }
    });
  }

  protected sideKey(event: KeyboardEvent): void {
    const v = this.value();
    const turn = event.shiftKey ? 1 : 5;
    const step = event.shiftKey ? 0.01 : 0.05;
    this.keyed(event, {
      ArrowUp: { elevation: Math.min(90, v.elevation + turn) },
      ArrowDown: { elevation: Math.max(-90, v.elevation - turn) },
      ArrowRight: { distance: this.round(Math.min(1, v.distance + step)) },
      ArrowLeft: { distance: this.round(Math.max(0, v.distance - step)) },
      Home: { elevation: 0 }
    });
  }

  private keyed(event: KeyboardEvent, moves: Record<string, Partial<ObjectPannerValue>>): void {
    const change = moves[event.key];
    if (this.disabled() || !change) {
      return;
    }
    event.preventDefault();
    this.patch(change);
    this.committed.emit(this.value());
  }

  private place(element: HTMLElement, event: PointerEvent): void {
    const box = element.getBoundingClientRect();
    if (box.width <= 0 || box.height <= 0 || !this.drag) {
      return;
    }
    const current = this.value();
    if (this.drag.view === 'top') {
      const next = objectFromTopPoint(pointerToPanner(box, event.clientX, event.clientY), current);
      this.patch({ azimuth: next.azimuth, distance: next.distance });
    } else {
      const point: PannerPoint = {
        x: ((event.clientX - box.left) / box.width) * (1 + 2 * SideMargin) - SideMargin,
        y: 1 + SideMargin - ((event.clientY - box.top) / box.height) * (2 + 2 * SideMargin)
      };
      const next = objectFromSidePoint(point, current);
      this.patch({ elevation: next.elevation, distance: next.distance });
    }
  }
}
