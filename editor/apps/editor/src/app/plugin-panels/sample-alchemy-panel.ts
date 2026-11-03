import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { ParameterInfo } from '@anode/api-client';
import { PanelParameter } from './panel-parameter';
import { panelBinding } from './panel-patterns';
import { placeAt, placePercent, stepPosition } from './physical-model-panel';
import type { PluginPanelHost } from './plugin-panel';

/** The source the view assumes when it does not know the sample's length: Sample Alchemy's generated default, 4 s. */
export const DefaultSourceSeconds = 4;

/** Columns of the source overview. */
const Columns = 96;

/**
 * A stylised overview of the source (0…1 per column): the window cannot read the instrument's loaded file through the
 * panel host, so it draws a decaying tone with a seeded texture, the same every time.
 */
export function sourceOverview(columns = Columns): number[] {
  let seed = 7;
  return Array.from({ length: columns }, (_, i) => {
    seed = (seed * 16_807) % 2_147_483_647;
    const texture = 0.75 + (seed / 2_147_483_647) * 0.25;
    const envelope = Math.min(1, i / 3) * Math.exp(-i / (columns * 0.45));
    return Math.round(Math.max(0.04, envelope * texture) * 1000) / 1000;
  });
}

/** The grain window across the source, in % of the well: from the position, a grain's size long (at least 1 %). */
export function grainWindow(position: number, sizeSeconds: number, sourceSeconds = DefaultSourceSeconds) {
  const start = placePercent(position);
  const width = Math.max(1, Math.min(95 - start, (sizeSeconds / sourceSeconds) * 90));
  return { start, width };
}

/** The additive mode's partials: `count` bars across the view, each 1/k as loud, `spread` stretching them apart. */
export function partialBars(count: number, spread: number): { x: number; height: number }[] {
  const n = Math.max(1, Math.round(count));
  return Array.from({ length: n }, (_, i) => {
    const k = i + 1;
    // On a log axis of the partials; spread stretches them apart (inharmonic, as the morph's spread does).
    const x = placePercent(Math.min(1, (Math.log(k) * (1 + spread / 2)) / Math.log(n + 1)));
    return { x: Math.round(x * 10) / 10, height: Math.round((1 / k) * 1000) / 10 };
  });
}

/**
 * Sample Alchemy's panel: the source view — the sample's overview in a `well` (a stylised one: see
 * {@link sourceOverview}) with, by mode, the granular window (the position as an ARIA slider, drag or ←/→, the grain's
 * size as its width and the spray as a wider pale band), the additive mode's partials as bars, or the spectral mode's
 * smoothed envelope; the mode as a `bezel` radiogroup and the mode's controls under it.
 */
@Component({
  selector: 'an-sample-alchemy-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PanelParameter],
  host: {
    class: 'flex flex-col gap-2',
    role: 'group',
    'aria-label': 'Source',
    'data-testid': 'sample-alchemy-panel'
  },
  template: `
    @if (modeParameter(); as mode) {
      <div
        class="bezel-group text-foreground h-6.5 self-start text-[11px] font-semibold"
        role="radiogroup"
        aria-label="Mode"
      >
        @for (name of mode.valueNames ?? []; track name; let i = $index) {
          <button
            type="button"
            role="radio"
            class="px-3"
            [attr.aria-checked]="modeIndex() === i"
            [attr.tabindex]="modeIndex() === i ? 0 : -1"
            [attr.data-testid]="'alchemy-mode-' + i"
            [class.text-lit]="modeIndex() === i"
            (click)="setMode(mode, i)"
            (keydown)="modeKey($event, mode)"
          >
            {{ name }}
          </button>
        }
      </div>
    }
    <div class="well relative h-20 overflow-hidden rounded-[6px]" data-testid="alchemy-source">
      <svg class="absolute inset-0 size-full" viewBox="0 0 100 40" preserveAspectRatio="none" aria-hidden="true">
        @for (peak of overview; track $index) {
          <rect
            class="fill-foreground-muted"
            fill-opacity="0.55"
            [attr.x]="5 + ($index * 90) / overview.length"
            [attr.y]="20 - peak * 18"
            [attr.width]="90 / overview.length - 0.2"
            [attr.height]="peak * 36"
          />
        }
        @if (modeName() === 'Additive') {
          @for (bar of partials(); track $index) {
            <rect
              class="fill-primary"
              [attr.x]="bar.x - 0.3"
              [attr.y]="40 - bar.height * 0.38"
              width="0.6"
              [attr.height]="bar.height * 0.38"
            />
          }
        } @else if (modeName() === 'Spectral') {
          <polyline
            class="stroke-knob-arc fill-none"
            stroke-width="0.8"
            vector-effect="non-scaling-stroke"
            [attr.points]="envelope"
          />
        }
      </svg>
      @if (modeName() === 'Granular' && positionParameter(); as position) {
        <span
          class="bg-primary/15 pointer-events-none absolute inset-y-0"
          aria-hidden="true"
          [style.left.%]="spray().start"
          [style.width.%]="spray().width"
        ></span>
        <span
          class="bg-primary/45 pointer-events-none absolute inset-y-0"
          aria-hidden="true"
          [style.left.%]="grain().start"
          [style.width.%]="grain().width"
        ></span>
        <span
          class="bg-primary focus-visible:outline-focus absolute inset-y-0 w-1 -translate-x-1/2 cursor-ew-resize touch-none rounded-full outline-offset-2 focus-visible:outline-2"
          role="slider"
          tabindex="0"
          aria-label="Grain position"
          aria-valuemin="0"
          aria-valuemax="100"
          data-testid="alchemy-position"
          [attr.aria-valuenow]="Math.round(positionValue() * 100)"
          [attr.aria-valuetext]="Math.round(positionValue() * 100) + ' % into the sample'"
          [style.left.%]="grain().start"
          (pointerdown)="grab($event)"
          (pointermove)="drag($event, position)"
          (pointerup)="release($event)"
          (pointercancel)="release($event)"
          (keydown)="key($event, position)"
        ></span>
      }
    </div>
    <div class="flex flex-wrap items-end gap-x-1 gap-y-2">
      @for (parameter of controls(); track parameter.id) {
        <an-panel-parameter [host]="host()" [parameter]="parameter" />
      }
    </div>
  `
})
export class SampleAlchemyPanel {
  readonly host = input.required<PluginPanelHost>();

  protected readonly Math = Math;
  protected readonly overview = sourceOverview();
  private readonly binding = computed(() => panelBinding(this.host().plugin, 'sampleAlchemy'));
  private readonly roles = computed(() => this.binding()?.roles ?? {});

  protected readonly modeParameter = computed(() => this.roles()['mode']);
  protected readonly positionParameter = computed(() => this.roles()['position']);
  protected readonly modeIndex = computed(() => {
    const mode = this.modeParameter();
    return mode ? Math.round(this.host().value(mode.id) - mode.min) : 0;
  });
  /** The mode as the instrument names it: the parameter's order is the instrument's. */
  protected readonly modeName = computed(() => this.modeParameter()?.valueNames?.[this.modeIndex()] ?? 'Granular');
  protected readonly positionValue = computed(() => {
    const position = this.positionParameter();
    return position ? this.normal(position) : 0;
  });
  /** The grain size parameter is in milliseconds. */
  protected readonly grain = computed(() => grainWindow(this.positionValue(), this.valueOf('size', 80) / 1000));
  /** The spray widens where grains may start: the window grown by the spray on each side. */
  protected readonly spray = computed(() => {
    const grain = this.grain();
    const spray = (this.valueOf('spray', 0) / 1000 / DefaultSourceSeconds) * 90; // milliseconds of the source
    const start = Math.max(5, grain.start - spray);
    return { start, width: Math.min(95, grain.start + grain.width + spray) - start };
  });
  /** The partial spread parameter runs −100…100 %. */
  protected readonly partials = computed(() =>
    partialBars(this.valueOf('partials', 32), this.valueOf('spread', 0) / 100)
  );
  /** The spectral envelope: the overview smoothed. */
  protected readonly envelope = this.overview
    .map((_, i, all) => {
      const part = all.slice(Math.max(0, i - 6), i + 7);
      const mean = part.reduce((a, b) => a + b, 0) / part.length;
      return `${5 + (i * 90) / all.length},${Math.round((38 - mean * 34) * 10) / 10}`;
    })
    .join(' ');
  /** The mode's own controls. */
  protected readonly controls = computed(() => {
    const roles = this.roles();
    const byMode: Record<string, string[]> = {
      Granular: ['position', 'size', 'density', 'spray', 'pitch'],
      Additive: ['partials', 'spread'],
      Spectral: ['noise']
    };
    return (byMode[this.modeName()] ?? []).map(role => roles[role]).filter((p): p is ParameterInfo => !!p);
  });

  private pointer: number | null = null;

  private normal(parameter: ParameterInfo): number {
    const span = parameter.max - parameter.min;
    return span === 0 ? 0 : (this.host().value(parameter.id) - parameter.min) / span;
  }

  private valueOf(role: string, fallback: number): number {
    const parameter = this.roles()[role];
    return parameter ? this.host().value(parameter.id) : fallback;
  }

  protected setMode(mode: ParameterInfo, index: number): void {
    if (index !== this.modeIndex()) {
      this.host().set(mode.id, mode.min + index);
    }
  }

  protected modeKey(event: KeyboardEvent, mode: ParameterInfo): void {
    const step = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[event.key];
    const count = (mode.valueNames?.length ?? 1) || 1;
    if (step === undefined) {
      return;
    }
    event.preventDefault();
    const next = (this.modeIndex() + step + count) % count;
    this.setMode(mode, next);
    const group = (event.currentTarget as HTMLElement).parentElement;
    group?.querySelectorAll<HTMLElement>('[role="radio"]')[next]?.focus();
  }

  protected grab(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    const handle = event.currentTarget as HTMLElement;
    handle.setPointerCapture(event.pointerId);
    handle.focus();
    this.pointer = event.pointerId;
  }

  protected drag(event: PointerEvent, position: ParameterInfo): void {
    const box = (event.currentTarget as HTMLElement).parentElement?.getBoundingClientRect();
    if (this.pointer === event.pointerId && box) {
      this.place(position, placeAt(event.clientX, box));
    }
  }

  protected release(event: PointerEvent): void {
    if (this.pointer === event.pointerId) {
      this.pointer = null;
    }
  }

  protected key(event: KeyboardEvent, position: ParameterInfo): void {
    const next = stepPosition(this.normal(position), event.key, event.shiftKey);
    if (next !== null) {
      event.preventDefault();
      this.place(position, next);
    }
  }

  private place(parameter: ParameterInfo, normal: number): void {
    const value = parameter.min + normal * (parameter.max - parameter.min);
    if (Math.abs(value - this.host().value(parameter.id)) > 1e-9) {
      this.host().set(parameter.id, value);
    }
  }
}
