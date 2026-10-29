import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { ParameterInfo } from '@anode/api-client';
import { formatParameter, fromNormal, toNormal } from '@anode/audio-ui';
import { PanelParameter } from './panel-parameter';
import { bandCount, panelBinding } from './panel-patterns';
import type { PluginPanelHost } from './plugin-panel';

/** The frequency axis: 20 Hz to 20 kHz, logarithmic. */
const Axis = { min: 20, max: 20_000, steps: 0, taper: 'log' as const };

/** A frequency's place on the axis, 0…1. */
export function axisPosition(hz: number): number {
  return toNormal(Math.min(Axis.max, Math.max(Axis.min, hz)), Axis);
}

/** The frequency at a place on the axis. */
export function axisFrequency(position: number): number {
  return fromNormal(Math.min(1, Math.max(0, position)), Axis);
}

/** Whether a band's bypass parameter is on (the upper half of its range). */
export function bypassed(parameter: ParameterInfo, value: number): boolean {
  return value >= (parameter.min + parameter.max) / 2;
}

const Ticks = [100, 1_000, 10_000];

/**
 * The Multipressor's panel: the crossovers as handles on a 20 Hz–20 kHz axis (ARIA sliders, ←/→ move one along the
 * axis between its neighbours, ⇧ finer, or drag), and a column per band with its threshold, ratio and gain knobs and
 * a bypass toggle. (Per-band gain reduction would sit under each column once the engine reports it per band; the
 * window's reading shows the overall one.)
 */
@Component({
  selector: 'an-multipressor-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PanelParameter],
  host: { class: 'flex flex-col gap-2', role: 'group', 'aria-label': 'Bands', 'data-testid': 'multipressor-panel' },
  template: `
    <div class="well relative h-9 rounded-[5px]" role="group" aria-label="Crossovers" data-testid="crossover-axis">
      @for (tick of ticks; track tick) {
        <span
          class="border-hairline absolute inset-y-0 border-s"
          [style.left.%]="position(tick) * 100"
          aria-hidden="true"
        >
          <span class="text-foreground-muted readout absolute bottom-0 ms-0.5 text-[9px]">{{ hz(tick) }}</span>
        </span>
      }
      @for (band of bands(); track band.number) {
        <span
          class="text-foreground absolute top-0.5 -translate-x-1/2 text-[10px] font-semibold"
          [style.left.%]="bandCentre(band.number) * 100"
          aria-hidden="true"
          >{{ band.number }}</span
        >
      }
      @for (crossover of crossovers(); track crossover.id; let i = $index) {
        <span
          class="bg-primary focus-visible:outline-focus absolute inset-y-0 w-1 -translate-x-1/2 cursor-ew-resize touch-none rounded-full outline-offset-2 focus-visible:outline-2"
          role="slider"
          tabindex="0"
          data-testid="crossover"
          [attr.aria-label]="'Crossover ' + (i + 1)"
          [attr.aria-valuemin]="crossover.min"
          [attr.aria-valuemax]="crossover.max"
          [attr.aria-valuenow]="round(value(crossover))"
          [attr.aria-valuetext]="hz(value(crossover))"
          [style.left.%]="position(value(crossover)) * 100"
          (pointerdown)="grab($event)"
          (pointermove)="drag($event, i)"
          (pointerup)="release($event)"
          (pointercancel)="release($event)"
          (keydown)="key($event, i)"
        ></span>
      }
    </div>
    <div class="grid gap-1" [style.grid-template-columns]="'repeat(' + bands().length + ', minmax(0, 1fr))'">
      @for (band of bands(); track band.number) {
        <div
          class="border-hairline bg-surface-raised flex flex-col items-center gap-1 rounded-[6px] border px-1 py-1.5"
          role="group"
          data-testid="multipressor-band"
          [attr.aria-label]="'Band ' + band.number"
          [class.opacity-60]="band.bypass && isBypassed(band.bypass)"
        >
          <div class="flex items-center gap-1">
            <span class="text-foreground text-[11px] font-semibold">Band {{ band.number }}</span>
            @if (band.bypass; as bypass) {
              <button
                type="button"
                class="bezel text-foreground h-5 px-1.5 text-[10px] font-semibold"
                [attr.aria-label]="'Bypass band ' + band.number"
                [attr.aria-pressed]="isBypassed(bypass)"
                [class.text-lit]="isBypassed(bypass)"
                (click)="toggleBypass(bypass)"
              >
                Bypass
              </button>
            }
          </div>
          <an-panel-parameter [host]="host()" [parameter]="band.threshold" label="Threshold" />
          <an-panel-parameter [host]="host()" [parameter]="band.ratio" label="Ratio" />
          <an-panel-parameter [host]="host()" [parameter]="band.gain" label="Gain" />
        </div>
      }
    </div>
  `
})
export class MultipressorPanel {
  readonly host = input.required<PluginPanelHost>();

  protected readonly ticks = Ticks;
  private readonly binding = computed(() => panelBinding(this.host().plugin, 'multipressorBands'));
  protected readonly bands = computed(() => {
    const binding = this.binding();
    if (!binding) {
      return [];
    }
    const s = binding.series;
    return Array.from({ length: bandCount(binding) }, (_, i) => ({
      number: i + 1,
      threshold: s['threshold']?.[i] as ParameterInfo,
      ratio: s['ratio']?.[i] as ParameterInfo,
      gain: s['gain']?.[i] as ParameterInfo,
      bypass: s['bypass']?.[i] ?? s['enabled']?.[i]
    }));
  });
  protected readonly crossovers = computed(() =>
    (this.binding()?.series['crossover'] ?? []).slice(0, Math.max(0, this.bands().length - 1))
  );

  private pointer: number | null = null;

  protected value(parameter: ParameterInfo): number {
    return this.host().value(parameter.id);
  }

  protected position(hz: number): number {
    return axisPosition(hz);
  }

  protected hz(value: number): string {
    return formatParameter(value, 'Hz');
  }

  protected round(value: number): number {
    return Math.round(value);
  }

  /** The middle of band `n` on the axis (between its crossovers, or the axis' ends). */
  protected bandCentre(n: number): number {
    const xs = this.crossovers().map(c => axisPosition(this.value(c)));
    const lo = n === 1 ? 0 : (xs[n - 2] ?? 0);
    const hi = xs[n - 1] ?? 1;
    return (lo + hi) / 2;
  }

  protected isBypassed(parameter: ParameterInfo): boolean {
    const on = bypassed(parameter, this.value(parameter));
    // A band's "On" parameter is the inverse of a bypass.
    return this.binding()?.series['enabled']?.includes(parameter) ? !on : on;
  }

  protected toggleBypass(parameter: ParameterInfo): void {
    const high = this.value(parameter) >= (parameter.min + parameter.max) / 2;
    this.host().set(parameter.id, high ? parameter.min : parameter.max);
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

  protected drag(event: PointerEvent, index: number): void {
    if (this.pointer !== event.pointerId) {
      return;
    }
    const box = (event.currentTarget as HTMLElement).parentElement?.getBoundingClientRect();
    if (box && box.width > 0) {
      this.move(index, axisFrequency((event.clientX - box.left) / box.width));
    }
  }

  protected release(event: PointerEvent): void {
    if (this.pointer === event.pointerId) {
      this.pointer = null;
    }
  }

  protected key(event: KeyboardEvent, index: number): void {
    const crossover = this.crossovers()[index];
    if (!crossover) {
      return;
    }
    const step = { ArrowRight: 1, ArrowUp: 1, ArrowLeft: -1, ArrowDown: -1, PageUp: 10, PageDown: -10 }[event.key];
    if (step === undefined) {
      return;
    }
    event.preventDefault();
    const size = event.shiftKey ? 0.002 : 0.01;
    this.move(index, axisFrequency(axisPosition(this.value(crossover)) + step * size));
  }

  /** Moves crossover `index` within its own range and between its neighbours (at least a tenth of an octave apart). */
  private move(index: number, hz: number): void {
    const list = this.crossovers();
    const crossover = list[index];
    if (!crossover) {
      return;
    }
    const gap = 2 ** 0.1;
    const lo = Math.max(
      crossover.min,
      list[index - 1] ? this.value(list[index - 1] as ParameterInfo) * gap : -Infinity
    );
    const hi = Math.min(crossover.max, list[index + 1] ? this.value(list[index + 1] as ParameterInfo) / gap : Infinity);
    const next = Math.round(Math.min(hi, Math.max(lo, hz)));
    if (next !== Math.round(this.value(crossover))) {
      this.host().set(crossover.id, next);
    }
  }
}
