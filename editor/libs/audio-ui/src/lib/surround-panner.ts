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
  PannerLevelFloorDb,
  formatAngle,
  formatLevel,
  knobToLevel,
  levelToKnob,
  puckFromPoint,
  puckPoint,
  shortSpeakerLabel,
  speakerPoint,
  spreadPucks,
  wrapAngle,
  type PannerPoint,
  type PannerSpeaker
} from './panner-math';

/** The Surround Panner's settings (`SurroundPanner`): levels in dB (−144 off), spread in degrees. */
export interface SurroundPannerValue {
  angle: number;
  diversity: number;
  elevation: number;
  centreLevelDb: number;
  lfeLevelDb: number;
  spread: number;
}

/** The view's half-width in panner units: the rim at 1, room for the labels outside it. */
export const PannerView = 1.24;

/** Where a pointer is in a panner view of `box`, in panner units (x right, y front). */
export function pointerToPanner(box: DOMRect, clientX: number, clientY: number, half = PannerView): PannerPoint {
  return {
    x: ((clientX - box.left) / box.width) * 2 * half - half,
    y: half - ((clientY - box.top) / box.height) * 2 * half
  };
}

/** A panner unit's position in the view, as a percentage from the left (x) or the top (y). */
export function viewPercent(value: number, axis: 'x' | 'y', half = PannerView): number {
  return axis === 'x' ? ((value + half) / (2 * half)) * 100 : ((half - value) / (2 * half)) * 100;
}

/**
 * Logic's Surround Panner (docs/11-phase-10.md): a puck in a circle of the layout's speakers. The puck's direction is
 * the angle (ADM: 0 ahead, positive to the left) and its distance from the centre the diversity (1 on the rim: a
 * point source; 0 in the middle: every speaker). A stereo input shows its two channels as ghost pucks `spread` apart.
 * The LFE is drawn apart, under the circle. Below, knobs for the elevation (layouts with height), the centre level, the
 * LFE level and the spread (stereo inputs). The circle is one ARIA slider: ←/→ turn the angle by 5° (⇧ 1°), ↑/↓ move
 * the diversity by 5 % (⇧ 1 %), Home puts the puck front centre. Drag the puck with a mouse, pen or finger. `changed`
 * carries only the fields that changed (the owner sends them flat, `setPanner`); `committed` ends a gesture.
 * `compact` draws only the circle (a channel strip), with an expand button for the full editor.
 */
@Component({
  selector: 'an-surround-panner',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnKnob],
  host: { class: 'inline-flex flex-col items-center gap-1 select-none' },
  template: `
    <div
      role="slider"
      aria-roledescription="surround panner"
      class="focus-visible:outline-focus relative cursor-crosshair touch-none rounded-full outline-offset-2 focus-visible:outline-2"
      [style.width.px]="size()"
      [style.height.px]="size()"
      [class.opacity-35]="disabled()"
      [attr.tabindex]="disabled() ? -1 : 0"
      [attr.aria-label]="label()"
      aria-valuemin="-180"
      aria-valuemax="180"
      [attr.aria-valuenow]="round(value().angle)"
      [attr.aria-valuetext]="text()"
      [attr.aria-disabled]="disabled() || null"
      [attr.data-testid]="testId()"
      (pointerdown)="down($event)"
      (pointermove)="move($event)"
      (pointerup)="up($event)"
      (pointercancel)="up($event)"
      (dblclick)="reset()"
      (keydown)="key($event)"
    >
      <svg [attr.viewBox]="viewBox" class="absolute inset-0 size-full overflow-visible" aria-hidden="true">
        <circle cx="0" cy="0" r="1" class="fill-surface-inset stroke-hairline-strong" stroke-width="0.02" />
        <circle cx="0" cy="0" r="0.62" class="stroke-knob-track fill-none" stroke-width="0.012" />
        <line x1="0" y1="-1" x2="0" y2="1" class="stroke-knob-track" stroke-width="0.01" />
        <line x1="-1" y1="0" x2="1" y2="0" class="stroke-knob-track" stroke-width="0.01" />
        <!-- The spread of the sound: wider as diversity falls. -->
        <circle
          [attr.cx]="puck().x"
          [attr.cy]="-puck().y"
          [attr.r]="0.12 + (1 - value().diversity) * 0.75"
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
          @if (!compact()) {
            <text
              [attr.x]="s.label_.x"
              [attr.y]="-s.label_.y"
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
        @if (hasLfe()) {
          <rect x="0.86" y="0.98" width="0.2" height="0.14" rx="0.03" class="fill-foreground-muted" />
          @if (!compact()) {
            <text
              x="0.96"
              y="1.19"
              class="fill-foreground"
              font-size="0.11"
              font-weight="600"
              text-anchor="middle"
              dominant-baseline="central"
            >
              LFE
            </text>
          }
        }
        @if (stereoInput()) {
          @for (ghost of ghosts(); track $index) {
            <circle
              [attr.cx]="ghost.x"
              [attr.cy]="-ghost.y"
              r="0.07"
              class="fill-primary/60 stroke-primary"
              stroke-width="0.012"
            />
          }
        }
      </svg>
      <span
        class="knob-body ring-primary absolute size-3.5 -translate-x-1/2 -translate-y-1/2 rounded-full ring-1"
        [style.left.%]="percent(puck().x, 'x')"
        [style.top.%]="percent(puck().y, 'y')"
        aria-hidden="true"
      ></span>
    </div>
    @if (compact()) {
      <button
        type="button"
        class="bezel text-foreground h-5 rounded-[5px] px-2 text-[10px] font-semibold"
        [attr.aria-label]="'Open ' + label() + ', ' + shortText()"
        [disabled]="disabled()"
        (click)="expand.emit()"
      >
        {{ shortText() }}
      </button>
    } @else {
      <span class="readout text-foreground text-[11px] font-medium" aria-hidden="true">{{ text() }}</span>
      <div class="flex flex-wrap justify-center gap-1" role="group" [attr.aria-label]="label() + ' levels'">
        @if (hasHeight()) {
          <an-knob
            label="Elevation"
            unit="°"
            [min]="0"
            [max]="90"
            [defaultValue]="0"
            [value]="value().elevation"
            [disabled]="disabled()"
            [format]="degrees"
            (valueChange)="patch({ elevation: round($event) })"
            (committed)="committed.emit(value())"
          />
        }
        @if (hasCentre()) {
          <an-knob
            label="Centre Level"
            [min]="floor"
            [max]="0"
            [defaultValue]="0"
            [value]="knob(value().centreLevelDb)"
            [disabled]="disabled()"
            [format]="level"
            (valueChange)="patch({ centreLevelDb: toLevel($event) })"
            (committed)="committed.emit(value())"
          />
        }
        @if (hasLfe()) {
          <an-knob
            label="LFE Level"
            [min]="floor"
            [max]="0"
            [defaultValue]="floor"
            [value]="knob(value().lfeLevelDb)"
            [disabled]="disabled()"
            [format]="level"
            (valueChange)="patch({ lfeLevelDb: toLevel($event) })"
            (committed)="committed.emit(value())"
          />
        }
        @if (stereoInput()) {
          <an-knob
            label="Spread"
            unit="°"
            [min]="0"
            [max]="180"
            [defaultValue]="60"
            [value]="value().spread"
            [disabled]="disabled()"
            [format]="degrees"
            (valueChange)="patch({ spread: round($event) })"
            (committed)="committed.emit(value())"
          />
        }
      </div>
    }
  `
})
export class AnSurroundPanner {
  protected readonly viewBox = `${-PannerView} ${-PannerView} ${2 * PannerView} ${2 * PannerView}`;
  protected readonly floor = PannerLevelFloorDb;
  protected readonly level = formatLevel;
  protected readonly degrees = (v: number) => `${Math.round(v)}°`;
  protected readonly knob = levelToKnob;
  protected readonly toLevel = knobToLevel;

  readonly value = model<SurroundPannerValue>({
    angle: 0,
    diversity: 1,
    elevation: 0,
    centreLevelDb: 0,
    lfeLevelDb: -144,
    spread: 60
  });
  /** The output layout's speakers, in channel order. */
  readonly speakers = input<readonly PannerSpeaker[]>([]);
  readonly label = input('Surround Panner');
  /** The circle's side in CSS pixels. */
  readonly size = input(160, { transform: numberAttribute });
  /** A stereo input: its channels `spread` apart, and the Spread knob. */
  readonly stereoInput = input(false, { transform: booleanAttribute });
  /** Only the circle and an expand button (a channel strip). */
  readonly compact = input(false, { transform: booleanAttribute });
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly testId = input<string | null>(null);
  /** What changed, field by field (every step of a drag). */
  readonly changed = output<Partial<SurroundPannerValue>>();
  /** A gesture ended. */
  readonly committed = output<SurroundPannerValue>();
  /** The compact panner's expand button. */
  readonly expand = output();

  private pointer: number | null = null;

  protected readonly puck = computed(() => puckPoint(this.value()));
  protected readonly ghosts = computed(() => spreadPucks(this.value(), this.value().spread));
  protected readonly hasLfe = computed(() => this.speakers().some(s => s.lfe));
  protected readonly hasHeight = computed(() => this.speakers().some(s => s.elevation > 0 && !s.lfe));
  protected readonly hasCentre = computed(() => this.speakers().some(s => s.label === 'M+000'));
  protected readonly drawn = computed(() =>
    this.speakers().flatMap(s => {
      const point = speakerPoint(s);
      if (!point) {
        return [];
      }
      const upper = s.elevation > 0;
      // Labels outside the rim for the ear-level ring, inside the upper ring for the height layer.
      const r = Math.hypot(point.x, point.y) || 1;
      const at = upper ? 0.7 : 1.14 / r;
      return [
        {
          label: s.label,
          short: shortSpeakerLabel(s.label),
          point,
          upper,
          label_: { x: point.x * at, y: point.y * at }
        }
      ];
    })
  );
  protected readonly text = computed(() => {
    const v = this.value();
    return `${formatAngle(v.angle)}, diversity ${Math.round(v.diversity * 100)} %`;
  });
  protected readonly shortText = computed(() => formatAngle(this.value().angle));

  protected round(value: number): number {
    return Math.round(value * 100) / 100;
  }

  protected percent(value: number, axis: 'x' | 'y'): number {
    return viewPercent(value, axis);
  }

  /** Changes some fields, emitting only those that changed. */
  protected patch(change: Partial<SurroundPannerValue>): void {
    const current = this.value();
    const changed: Partial<SurroundPannerValue> = {};
    for (const [field, next] of Object.entries(change) as [keyof SurroundPannerValue, number][]) {
      if (current[field] !== next) {
        changed[field] = next;
      }
    }
    if (Object.keys(changed).length) {
      this.value.set({ ...current, ...changed });
      this.changed.emit(changed);
    }
  }

  protected down(event: PointerEvent): void {
    if (this.disabled() || event.button !== 0) {
      return;
    }
    const element = event.currentTarget as HTMLElement;
    element.setPointerCapture(event.pointerId);
    element.focus();
    this.pointer = event.pointerId;
    this.place(element, event);
  }

  protected move(event: PointerEvent): void {
    if (this.pointer === event.pointerId) {
      this.place(event.currentTarget as HTMLElement, event);
    }
  }

  protected up(event: PointerEvent): void {
    if (this.pointer === event.pointerId) {
      this.pointer = null;
      this.committed.emit(this.value());
    }
  }

  protected key(event: KeyboardEvent): void {
    if (this.disabled()) {
      return;
    }
    const v = this.value();
    const turn = event.shiftKey ? 1 : 5;
    const step = event.shiftKey ? 0.01 : 0.05;
    const moves: Record<string, Partial<SurroundPannerValue>> = {
      ArrowLeft: { angle: wrapAngle(v.angle + turn) },
      ArrowRight: { angle: wrapAngle(v.angle - turn) },
      ArrowUp: { diversity: this.round(Math.min(1, v.diversity + step)) },
      ArrowDown: { diversity: this.round(Math.max(0, v.diversity - step)) },
      Home: { angle: 0, diversity: 1 }
    };
    const change = moves[event.key];
    if (!change) {
      return;
    }
    event.preventDefault();
    this.patch(change);
    this.committed.emit(this.value());
  }

  /** Double-click: front centre, a point. */
  protected reset(): void {
    if (!this.disabled()) {
      this.patch({ angle: 0, diversity: 1 });
      this.committed.emit(this.value());
    }
  }

  private place(element: HTMLElement, event: PointerEvent): void {
    const box = element.getBoundingClientRect();
    if (box.width <= 0 || box.height <= 0) {
      return;
    }
    const point = pointerToPanner(box, event.clientX, event.clientY);
    const { angle, diversity } = puckFromPoint(point, this.value().angle);
    this.patch({ angle, diversity });
  }
}
