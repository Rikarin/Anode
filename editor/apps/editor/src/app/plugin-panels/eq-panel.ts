import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { snapToStep } from '@anode/audio-ui';
import { AnEqGraph, type EqBandChange } from '@anode/audio-ui/lazy';
import { EqBands, EqOutput, eqBandsOf } from './eq-bands';
import type { PluginPanelHost } from './plugin-panel';

/** How fast the analyser falls between frames: it rises at once, and sinks by this share of the gap. */
const AnalyserFall = 0.35;

/** A spectrum frame over the last one shown: rises at once, falls gently, so the display reads as a level. */
export function settleSpectrum(previous: readonly number[], next: readonly number[]): number[] {
  return next.map((db, i) => {
    const was = previous[i] ?? db;
    return db >= was ? db : was + (db - was) * AnalyserFall;
  });
}

/**
 * The Channel EQ's and the Linear Phase EQ's curve (`an-eq-graph`): a handle per band over the live spectrum of the
 * output (the insert's reading values), and a row of band switches. The window's knobs stay below for exact values.
 */
@Component({
  selector: 'an-eq-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnEqGraph],
  host: { class: 'flex flex-col gap-2', 'data-testid': 'eq-panel' },
  template: `
    <an-eq-graph
      [bands]="bands()"
      [spectrum]="spectrum()"
      [outputDb]="outputDb()"
      [sampleRate]="host().sampleRate ?? 48000"
      [label]="host().plugin.name + ' curve'"
      (bandChange)="change($event)"
    />
    <div class="bezel-group flex" role="group" aria-label="Bands on">
      @for (band of bands(); track band.id; let i = $index) {
        <button
          type="button"
          class="text-foreground h-6 min-w-0 flex-1 px-0 text-[10px] font-semibold"
          data-testid="eq-band-switch"
          [class.text-lit]="band.on"
          [attr.aria-pressed]="band.on"
          [attr.aria-label]="band.label"
          (click)="toggle(i)"
        >
          {{ i + 1 }}
        </button>
      }
    </div>
  `
})
export class EqPanel {
  readonly host = input.required<PluginPanelHost>();

  private shown: readonly number[] = [];

  protected readonly bands = computed(() => {
    const host = this.host();
    return eqBandsOf(host.plugin.id, id => host.value(id));
  });
  protected readonly outputDb = computed(() => this.host().value(EqOutput));
  protected readonly spectrum = computed(() => {
    this.shown = settleSpectrum(this.shown, this.host().readingValues?.() ?? []);
    return this.shown;
  });

  protected toggle(index: number): void {
    const parameters = EqBands[index];
    if (parameters) {
      this.host().set(parameters.on, this.host().value(parameters.on) >= 0.5 ? 0 : 1);
    }
  }

  protected change({ band, field, value }: EqBandChange): void {
    const parameters = EqBands.find(b => b.id === band.id);
    const id = !parameters
      ? undefined
      : field === 'frequency'
        ? parameters.frequency
        : field === 'gainDb'
          ? parameters.gain
          : field === 'q'
            ? parameters.q
            : parameters.slope;
    const parameter = this.host().plugin.parameters.find(p => p.id === id);
    if (!parameter) {
      return;
    }
    const next = snapToStep(value, parameter);
    if (next !== this.host().value(parameter.id)) {
      this.host().set(parameter.id, next);
    }
  }
}
