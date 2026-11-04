import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { PluginPanelHost } from './plugin-panel';

const Notes = ['C', 'C♯', 'D', 'D♯', 'E', 'F', 'F♯', 'G', 'G♯', 'A', 'A♯', 'B'];

/** A MIDI note's name and octave ("A4", "C♯3"). */
export function noteName(midi: number): string {
  const note = Math.round(midi);
  return `${Notes[((note % 12) + 12) % 12]}${Math.floor(note / 12) - 1}`;
}

/** Within this many cents a tuner says the note is in tune. */
const InTuneCents = 3;

/**
 * The Tuner: the note in large type, a needle over ±50 cents (the insert's reading values: Hz, note, cents), and the
 * pitch. A meter, so a screen reader hears the note and how far off it is.
 */
@Component({
  selector: 'an-tuner-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'well flex flex-col items-center gap-1 rounded-[6px] p-3',
    role: 'meter',
    'aria-label': 'Tuner',
    'aria-valuemin': '-50',
    'aria-valuemax': '50',
    '[attr.aria-valuenow]': 'cents()',
    '[attr.aria-valuetext]': 'text()',
    'data-testid': 'tuner-panel'
  },
  template: `
    <span class="text-foreground text-[28px] leading-none font-semibold" data-testid="tuner-note">{{ note() }}</span>
    <div class="bg-knob-track relative h-1 w-full rounded-full" aria-hidden="true">
      <span class="bg-foreground-muted absolute top-[-4px] left-1/2 h-3 w-px"></span>
      @if (heard()) {
        <span
          class="absolute top-[-6px] h-4 w-1 -translate-x-1/2 rounded-full"
          data-testid="tuner-needle"
          [class.bg-meter-low]="inTune()"
          [class.bg-meter-mid]="!inTune()"
          [style.left.%]="needle()"
        ></span>
      }
    </div>
    <span class="readout text-foreground text-[11px]" data-testid="tuner-offset">{{ offset() }}</span>
  `
})
export class TunerPanel {
  readonly host = input.required<PluginPanelHost>();

  private readonly values = computed(() => this.host().readingValues?.() ?? []);
  protected readonly heard = computed(() => (this.values()[0] ?? 0) > 0);
  protected readonly cents = computed(() => (this.heard() ? Math.round((this.values()[2] ?? 0) * 10) / 10 : 0));
  protected readonly note = computed(() => (this.heard() ? noteName(this.values()[1] ?? 69) : '—'));
  protected readonly inTune = computed(() => Math.abs(this.cents()) <= InTuneCents);
  protected readonly needle = computed(() => 50 + this.cents());
  protected readonly offset = computed(() => {
    if (!this.heard()) {
      return 'No pitch';
    }
    const cents = this.cents();
    const sign = cents > 0 ? '+' : cents < 0 ? '−' : '';
    return `${sign}${Math.abs(cents).toFixed(1)} ct · ${(this.values()[0] ?? 0).toFixed(1)} Hz`;
  });
  protected readonly text = computed(() => (this.heard() ? `${this.note()}, ${this.offset()}` : 'No pitch'));
}

/** A trace as polyline points: `width` × `height`, 0 in the middle, ±1 at the edges. */
export function tracePoints(samples: readonly number[], width: number, height: number): string {
  const last = Math.max(1, samples.length - 1);
  return samples
    .map((sample, i) => {
      const y = (0.5 - Math.min(1, Math.max(-1, sample)) * 0.5) * height;
      return `${Math.round((i / last) * width * 10) / 10},${Math.round(y * 10) / 10}`;
    })
    .join(' ');
}

/** The Oscilloscope: the left trace, and the right over it (the insert's reading values, left then right). */
@Component({
  selector: 'an-scope-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex flex-col gap-1', 'data-testid': 'scope-panel' },
  template: `
    <svg
      class="well block h-36 w-full rounded-[6px]"
      role="img"
      aria-label="Oscilloscope trace"
      viewBox="0 0 600 240"
      preserveAspectRatio="none"
    >
      <line
        class="stroke-knob-track"
        x1="0"
        x2="600"
        y1="120"
        y2="120"
        stroke-width="1"
        vector-effect="non-scaling-stroke"
      />
      @if (left(); as trace) {
        <polyline
          class="stroke-knob-arc fill-none"
          data-testid="scope-left"
          stroke-width="1.25"
          vector-effect="non-scaling-stroke"
          [attr.points]="trace"
        />
      }
      @if (right(); as trace) {
        <polyline
          class="stroke-meter-mid fill-none"
          data-testid="scope-right"
          stroke-width="1.25"
          vector-effect="non-scaling-stroke"
          [attr.points]="trace"
        />
      }
    </svg>
  `
})
export class ScopePanel {
  readonly host = input.required<PluginPanelHost>();

  private readonly values = computed(() => this.host().readingValues?.() ?? []);
  protected readonly left = computed(() => {
    const values = this.values();
    return values.length > 1 ? tracePoints(values.slice(0, values.length / 2), 600, 240) : null;
  });
  protected readonly right = computed(() => {
    const values = this.values();
    return values.length > 1 ? tracePoints(values.slice(values.length / 2), 600, 240) : null;
  });
}

/** The goniometer's points: (side, mid) pairs after the two leading values, on a 200-unit square with mid up. */
export function goniometerPoints(values: readonly number[], size = 200): { x: number; y: number }[] {
  const points: { x: number; y: number }[] = [];
  const scale = (size / 2) * Math.SQRT1_2;
  for (let i = 2; i + 1 < values.length; i += 2) {
    points.push({
      x: Math.round((size / 2 + (values[i] ?? 0) * scale) * 10) / 10,
      y: Math.round((size / 2 - (values[i + 1] ?? 0) * scale) * 10) / 10
    });
  }
  return points;
}

/** The Phase Meter: a goniometer of the last audio and the balance (the window's reading is the correlation). */
@Component({
  selector: 'an-phase-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex flex-col items-center gap-2', 'data-testid': 'phase-panel' },
  template: `
    <svg class="well block size-40 rounded-[6px]" role="img" aria-label="Goniometer" viewBox="0 0 200 200">
      <line class="stroke-knob-track" x1="100" x2="100" y1="0" y2="200" stroke-width="1" />
      <line class="stroke-knob-track" x1="0" x2="200" y1="200" y2="0" stroke-width="1" />
      <line class="stroke-knob-track" x1="0" x2="200" y1="0" y2="200" stroke-width="1" />
      @for (point of points(); track $index) {
        <circle class="fill-knob-arc" r="1.5" data-testid="goniometer-point" [attr.cx]="point.x" [attr.cy]="point.y" />
      }
    </svg>
    <span class="readout text-foreground text-[11px]" data-testid="phase-balance">Balance {{ balance() }}</span>
  `
})
export class PhasePanel {
  readonly host = input.required<PluginPanelHost>();

  private readonly values = computed(() => this.host().readingValues?.() ?? []);
  protected readonly points = computed(() => goniometerPoints(this.values()));
  protected readonly balance = computed(() => {
    const db = this.values()[1];
    if (db === undefined || Math.abs(db) < 0.05) {
      return 'centred';
    }
    return `${Math.abs(db).toFixed(1)} dB ${db > 0 ? 'right' : 'left'}`;
  });
}
