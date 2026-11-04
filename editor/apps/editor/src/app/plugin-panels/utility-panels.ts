import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { formatParameter } from '@anode/audio-ui';
import { spectrumX } from '@anode/audio-ui/lazy';
import { tracePoints } from './meter-panels';
import type { PluginPanelHost } from './plugin-panel';

/** A downward expander's gain at a level (`GainComputer.Expansion` in the engine), dB, limited by the range. */
export function expanderGain(level: number, threshold: number, ratio: number, knee: number, range: number): number {
  const over = level - threshold;
  const slope = Math.max(ratio, 1) - 1;
  let gain: number;
  if (2 * over > knee) {
    gain = 0;
  } else if (knee > 0 && 2 * Math.abs(over) <= knee) {
    const x = over - knee / 2;
    gain = (-slope * x * x) / (2 * knee);
  } else {
    gain = slope * over;
  }
  return Math.max(gain, range);
}

const CurveFloor = -96;

/** The Expander: its transfer curve, input across and output up, with the input at 1:1 behind it. */
@Component({
  selector: 'an-expander-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex flex-col gap-1', 'data-testid': 'expander-panel' },
  template: `
    <svg
      class="well block h-32 w-full rounded-[6px]"
      role="img"
      [attr.aria-label]="label()"
      viewBox="0 0 240 240"
      preserveAspectRatio="none"
    >
      <line
        class="stroke-knob-track"
        x1="0"
        y1="240"
        x2="240"
        y2="0"
        stroke-width="1"
        vector-effect="non-scaling-stroke"
      />
      <polyline
        class="stroke-knob-arc fill-none"
        data-testid="expander-curve"
        stroke-width="1.75"
        vector-effect="non-scaling-stroke"
        [attr.points]="curve()"
      />
    </svg>
  `
})
export class ExpanderPanel {
  readonly host = input.required<PluginPanelHost>();

  protected readonly label = computed(() => {
    const host = this.host();
    const threshold = formatParameter(host.value('threshold'), 'dB');
    return `Expander curve: ${threshold} threshold at ${host.value('ratio').toFixed(1)}:1`;
  });
  protected readonly curve = computed(() => {
    const host = this.host();
    const [threshold, ratio, knee, range] = [
      host.value('threshold'),
      host.value('ratio'),
      host.value('knee'),
      host.value('range')
    ];
    const points: string[] = [];
    for (let db = CurveFloor; db <= 0; db += 1) {
      const out = db + expanderGain(db, threshold, ratio, knee, range);
      const x = ((db - CurveFloor) / -CurveFloor) * 240;
      const y = Math.min(240, (-out / -CurveFloor) * 240);
      points.push(`${Math.round(x * 10) / 10},${Math.round(y * 10) / 10}`);
    }
    return points.join(' ');
  });
}

/** One cycle of a waveform, for the Test Oscillator's picture; noise is drawn from a fixed scribble. */
export function waveCycle(wave: number): number[] {
  return Array.from({ length: 96 }, (_, i) => {
    const t = i / 96;
    switch (wave) {
      case 0:
        return Math.sin(2 * Math.PI * t);
      case 1:
        return t < 0.25 ? 4 * t : t < 0.75 ? 2 - 4 * t : 4 * t - 4;
      case 2:
        return t < 0.5 ? 1 : -1;
      case 3:
        return 2 * ((t + 0.5) % 1) - 1;
      default:
        return Math.sin(i * 12.9898) * (wave === 4 ? 1 : 0.5 + 0.5 * Math.sin(i * 0.2));
    }
  });
}

const Presets = [100, 440, 1_000, 10_000];

/** The Test Oscillator: its waveform, its frequency or sweep, and a few reference frequencies at a press. */
@Component({
  selector: 'an-oscillator-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex flex-col gap-2', 'data-testid': 'oscillator-panel' },
  template: `
    <svg
      class="well block h-16 w-full rounded-[6px]"
      role="img"
      [attr.aria-label]="waveName()"
      viewBox="0 0 600 120"
      preserveAspectRatio="none"
    >
      <polyline
        class="stroke-knob-arc fill-none"
        data-testid="oscillator-wave"
        stroke-width="1.5"
        vector-effect="non-scaling-stroke"
        [attr.points]="wave()"
      />
    </svg>
    <div class="bezel-group text-foreground flex h-6 text-[11px] font-semibold" role="group" aria-label="Frequency">
      @for (preset of presets; track preset) {
        <button
          type="button"
          class="min-w-0 flex-1 px-1"
          [attr.aria-pressed]="host().value('frequency') === preset"
          (click)="host().set('frequency', preset)"
        >
          {{ hz(preset) }}
        </button>
      }
    </div>
  `
})
export class OscillatorPanel {
  readonly host = input.required<PluginPanelHost>();

  protected readonly presets = Presets;
  protected readonly waveform = computed(() => this.host().plugin.parameters.find(p => p.id === 'waveform'));
  protected readonly wave = computed(() => tracePoints(waveCycle(this.host().value('waveform')), 600, 120));
  protected readonly waveName = computed(() => {
    const names = this.waveform()?.valueNames ?? [];
    return `Waveform: ${names[Math.round(this.host().value('waveform'))] ?? ''}`;
  });

  protected hz(value: number): string {
    return formatParameter(value, 'Hz');
  }
}

/** The bands a Frequency Spread makes, left or right in turn, as places across a log axis (0…100). */
export function spreadBands(bands: number, low: number, high: number): { from: number; to: number; side: 'L' | 'R' }[] {
  const ratio = (high / low) ** (1 / Math.max(1, bands));
  return Array.from({ length: bands }, (_, b) => ({
    from: spectrumX(low * ratio ** b) * 100,
    to: spectrumX(low * ratio ** (b + 1)) * 100,
    side: b % 2 === 0 ? 'L' : 'R'
  }));
}

/** The Frequency Spread: which side each band goes to, on a 20 Hz – 20 kHz axis; its height follows Width. */
@Component({
  selector: 'an-spread-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex flex-col gap-1', 'data-testid': 'spread-panel' },
  template: `
    <div class="well relative h-16 overflow-hidden rounded-[6px]" role="img" [attr.aria-label]="label()">
      <span class="border-hairline absolute inset-x-0 top-1/2 border-t" aria-hidden="true"></span>
      @for (band of bands(); track $index) {
        <span
          class="bg-primary/40 absolute"
          data-testid="spread-band"
          aria-hidden="true"
          [attr.data-side]="band.side"
          [style.left.%]="band.from"
          [style.width.%]="band.to - band.from"
          [style.height.%]="height()"
          [style.top.%]="band.side === 'L' ? 50 - height() : 50"
        ></span>
      }
    </div>
  `
})
export class SpreadPanel {
  readonly host = input.required<PluginPanelHost>();

  protected readonly bands = computed(() => {
    const host = this.host();
    return spreadBands(Math.round(host.value('bands')), host.value('low'), host.value('high'));
  });
  protected readonly height = computed(() => this.host().value('width') * 0.5);
  protected readonly label = computed(() => {
    const host = this.host();
    return `${this.bands().length} bands from ${formatParameter(host.value('low'), 'Hz')} to ${formatParameter(host.value('high'), 'Hz')}, left and right in turn`;
  });
}
