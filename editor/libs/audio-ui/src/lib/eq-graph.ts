import { ChangeDetectionStrategy, Component, computed, input, numberAttribute, output, signal } from '@angular/core';
import { type EqBand, analyserBands, eqResponse, logFrequencies } from './eq-response';
import { areaPath, polyline, spectrumX } from './spectrum-graph';

/** The curve's span on the graph: ±24 dB, the bands' range. */
const GainRange = 24;
/** The analyser's span on the graph, dBFS: its floor at the bottom, 0 dBFS at the top. */
const AnalyserFloor = -90;
const LowHz = 20;
const HighHz = 20_000;
const FrequencyTicks = [100, 1_000, 10_000];
const GainTicks = [-12, 0, 12];
const CurvePoints = 240;
const AxisLabels = [
  { hz: 100, text: '100' },
  { hz: 1_000, text: '1k' },
  { hz: 10_000, text: '10k' }
];

/** A frequency at a place across the graph, 0…1 (log). */
export function eqFrequency(position: number): number {
  return LowHz * (HighHz / LowHz) ** Math.min(1, Math.max(0, position));
}

/** A gain's place down the graph, 0 at the top. */
export function eqGainY(db: number): number {
  return (GainRange - Math.min(GainRange, Math.max(-GainRange, db))) / (2 * GainRange);
}

/** The gain at a place down the graph, 0…1. */
export function eqGainAt(position: number): number {
  return GainRange - Math.min(1, Math.max(0, position)) * 2 * GainRange;
}

/** A level's place down the graph on the analyser's scale, 0 at the top. */
export function analyserY(db: number): number {
  return Math.min(1, Math.max(0, db / AnalyserFloor));
}

/** "1.20 kHz", "85 Hz". */
export function eqFrequencyText(hz: number): string {
  return hz >= 1_000 ? `${(hz / 1_000).toFixed(2)} kHz` : `${Math.round(hz)} Hz`;
}

/** A band's state as the screen reader hears it: "1.00 kHz, +3.0 dB, Q 1.00". */
export function eqBandText(band: EqBand): string {
  const hz = eqFrequencyText(band.frequency);
  if (band.kind === 'highPass' || band.kind === 'lowPass') {
    return `${hz}, ${(band.slope + 1) * 12} dB per octave${band.on ? '' : ', off'}`;
  }
  const db = `${band.gainDb < 0 ? '−' : '+'}${Math.abs(band.gainDb).toFixed(1)} dB`;
  const place = band.place && band.place !== 'stereo' ? `, ${band.place}` : '';
  const moving = band.dynamicDb ?? 0;
  const dynamic = band.dynamic
    ? `, dynamic ${moving < 0 ? '−' : moving > 0 ? '+' : ''}${Math.abs(moving).toFixed(1)} dB`
    : '';
  return `${hz}, ${db}, Q ${band.q.toFixed(2)}${place}${dynamic}${band.on ? '' : ', off'}`;
}

/** Where a band's handle sits: a cut on the 0 dB line, the others at their gain. */
export function eqHandleY(band: EqBand): number {
  return band.kind === 'highPass' || band.kind === 'lowPass' ? eqGainY(0) : eqGainY(band.gainDb);
}

/** A change a band's handle asks for. */
export interface EqBandChange {
  readonly band: EqBand;
  readonly field: 'frequency' | 'gainDb' | 'q' | 'slope';
  readonly value: number;
}

/**
 * An EQ's curve over a live analyser: the response of `bands` in `stroke-knob-arc` over the output's spectrum, a filled
 * `fill-primary/30` area on its own −90…0 dBFS scale, in a `well` with frequency and gain grid lines. Each band is a
 * numbered handle: drag it (frequency across, gain up and down; a cut moves across only), turn the wheel over it for Q (a
 * cut's slope), or focus it: ←/→ frequency, ↑/↓ gain (a cut's slope), Page Up/Down Q, ⇧ finer. `bandChange` asks for
 * the values; the owner holds them. A dynamic band's handle has a ring, and the curve follows what it adds; bands placed
 * on the mid or side split the curve, the side's dashed. Each handle is an ARIA slider whose text says frequency, gain and Q; the graph
 * itself is an image with `label`.
 */
@Component({
  selector: 'an-eq-graph',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex flex-col gap-1' },
  template: `
    <div
      class="well relative w-full touch-none overflow-hidden rounded-[6px]"
      data-testid="eq-graph"
      [style.height.px]="height()"
    >
      <svg
        class="absolute inset-0 block size-full"
        role="img"
        [attr.aria-label]="label()"
        viewBox="0 0 600 240"
        preserveAspectRatio="none"
      >
        @for (tick of frequencyTicks; track tick) {
          <line
            class="stroke-knob-track"
            stroke-width="1"
            vector-effect="non-scaling-stroke"
            [attr.x1]="x(tick)"
            [attr.x2]="x(tick)"
            y1="0"
            y2="240"
          />
        }
        @for (tick of gainTicks; track tick) {
          <line
            class="stroke-knob-track"
            stroke-width="1"
            vector-effect="non-scaling-stroke"
            x1="0"
            x2="600"
            [attr.y1]="y(tick)"
            [attr.y2]="y(tick)"
          />
        }
        @if (spectrum().length > 0) {
          <path class="fill-primary/30" data-testid="eq-analyser" [attr.d]="analyser()" />
        }
        <polyline
          class="stroke-knob-arc fill-none"
          data-testid="eq-curve"
          stroke-width="1.75"
          stroke-linejoin="round"
          vector-effect="non-scaling-stroke"
          [attr.points]="curve()"
        />
        @if (sideCurve(); as side) {
          <polyline
            class="stroke-meter-mid fill-none"
            data-testid="eq-curve-side"
            stroke-width="1.25"
            stroke-dasharray="4 3"
            stroke-linejoin="round"
            vector-effect="non-scaling-stroke"
            [attr.points]="side"
          />
        }
      </svg>
      @for (band of bands(); track band.id; let i = $index) {
        <span
          class="focus-visible:outline-focus border-hairline-strong absolute flex size-4 -translate-x-1/2 -translate-y-1/2 cursor-grab touch-none items-center justify-center rounded-full border text-[9px] font-semibold outline-offset-1 focus-visible:outline-2"
          role="slider"
          tabindex="0"
          data-testid="eq-handle"
          [class.bg-primary]="band.on"
          [class.text-primary-foreground]="band.on"
          [class.bg-surface-raised]="!band.on"
          [class.text-foreground]="!band.on"
          [class.ring-2]="band.dynamic"
          [class.ring-meter-mid]="band.dynamic"
          [attr.data-band]="band.id"
          [attr.aria-label]="band.label"
          [attr.aria-valuemin]="20"
          [attr.aria-valuemax]="20000"
          [attr.aria-valuenow]="round(band.frequency)"
          [attr.aria-valuetext]="text(band)"
          [style.left.%]="position(band.frequency) * 100"
          [style.top.%]="handleY(band) * 100"
          (pointerdown)="grab($event)"
          (pointermove)="drag($event, band)"
          (pointerup)="release($event)"
          (pointercancel)="release($event)"
          (wheel)="wheel($event, band)"
          (keydown)="key($event, band)"
          (focus)="bandSelect.emit(band)"
          >{{ i + 1 }}</span
        >
      }
    </div>
    <div class="text-foreground-muted readout relative h-3 text-[9px]" aria-hidden="true">
      @for (tick of axisLabels; track tick.hz) {
        <span class="absolute -translate-x-1/2" [style.left.%]="position(tick.hz) * 100">{{ tick.text }}</span>
      }
    </div>
  `
})
export class AnEqGraph {
  readonly bands = input.required<readonly EqBand[]>();
  /** The output's spectrum, dBFS by analyser band (empty: none yet). */
  readonly spectrum = input<readonly number[]>([]);
  readonly outputDb = input(0, { transform: numberAttribute });
  /** The project's sample rate: the curve bends as the engine's does near Nyquist. */
  readonly sampleRate = input(48_000, { transform: numberAttribute });
  readonly label = input('EQ curve');
  readonly height = input(180, { transform: numberAttribute });

  readonly bandChange = output<EqBandChange>();
  /** A handle took the focus (a press does that): its band is the one the owner's controls show. */
  readonly bandSelect = output<EqBand>();

  protected readonly frequencyTicks = FrequencyTicks;
  protected readonly gainTicks = GainTicks;
  protected readonly axisLabels = AxisLabels;
  protected readonly text = eqBandText;
  protected readonly handleY = eqHandleY;

  private readonly grabbed = signal<number | null>(null);
  private readonly frequencies = logFrequencies(CurvePoints);

  /** The curve of what a mid signal goes through (left and right alike, when no band is placed). */
  protected readonly curve = computed(() => this.line(this.bands().filter(band => band.place !== 'side')));
  /** The side's own curve, dashed, once a band is placed so that the two differ. */
  protected readonly sideCurve = computed(() => {
    const bands = this.bands();
    return bands.some(band => band.on && band.place && band.place !== 'stereo')
      ? this.line(bands.filter(band => band.place !== 'mid'))
      : null;
  });

  protected readonly analyser = computed(() => {
    const levels = this.spectrum();
    const points = analyserBands(levels.length).map((hz, i) => ({
      x: Math.round(this.x(hz) * 10) / 10,
      y: Math.round(analyserY(levels[i] ?? AnalyserFloor) * 240 * 10) / 10
    }));
    return areaPath(points, 240);
  });

  private line(bands: readonly EqBand[]): string {
    const response = eqResponse(bands, this.frequencies, this.sampleRate(), this.outputDb());
    return polyline(
      this.frequencies.map((hz, i) => ({
        x: Math.round(this.x(hz) * 10) / 10,
        y: Math.round(this.y(response[i] ?? 0) * 10) / 10
      }))
    );
  }

  protected x(hz: number): number {
    return spectrumX(hz) * 600;
  }

  protected y(db: number): number {
    return eqGainY(db) * 240;
  }

  protected position(hz: number): number {
    return spectrumX(hz);
  }

  protected round(value: number): number {
    return Math.round(value);
  }

  protected grab(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    const handle = event.currentTarget as HTMLElement;
    handle.setPointerCapture(event.pointerId);
    handle.focus();
    this.grabbed.set(event.pointerId);
  }

  protected drag(event: PointerEvent, band: EqBand): void {
    if (this.grabbed() !== event.pointerId) {
      return;
    }
    const box = (event.currentTarget as HTMLElement).parentElement?.getBoundingClientRect();
    if (!box || box.width <= 0 || box.height <= 0) {
      return;
    }
    this.bandChange.emit({
      band,
      field: 'frequency',
      value: Math.round(eqFrequency((event.clientX - box.left) / box.width))
    });
    if (band.kind !== 'highPass' && band.kind !== 'lowPass') {
      const gain = eqGainAt((event.clientY - box.top) / box.height);
      this.bandChange.emit({ band, field: 'gainDb', value: Math.round(gain * 10) / 10 });
    }
  }

  protected release(event: PointerEvent): void {
    if (this.grabbed() === event.pointerId) {
      this.grabbed.set(null);
    }
  }

  protected wheel(event: WheelEvent, band: EqBand): void {
    event.preventDefault();
    this.turn(band, event.deltaY < 0 ? 1 : -1);
  }

  protected key(event: KeyboardEvent, band: EqBand): void {
    const fine = event.shiftKey ? 0.2 : 1;
    const cut = band.kind === 'highPass' || band.kind === 'lowPass';
    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowLeft': {
        const step = (event.key === 'ArrowRight' ? 0.01 : -0.01) * fine;
        const value = Math.round(eqFrequency(spectrumX(band.frequency) + step));
        this.bandChange.emit({ band, field: 'frequency', value });
        break;
      }
      case 'ArrowUp':
      case 'ArrowDown':
        if (cut) {
          this.turn(band, event.key === 'ArrowUp' ? 1 : -1);
        } else {
          const step = (event.key === 'ArrowUp' ? 0.5 : -0.5) * fine;
          this.bandChange.emit({ band, field: 'gainDb', value: Math.round((band.gainDb + step) * 10) / 10 });
        }
        break;
      case 'PageUp':
      case 'PageDown':
        this.turn(band, event.key === 'PageUp' ? fine : -fine);
        break;
      default:
        return;
    }
    event.preventDefault();
  }

  /** A notch of the wheel: a cut's slope, or a band's Q by an eighth of an octave's ratio. */
  private turn(band: EqBand, notches: number): void {
    if (band.kind === 'highPass' || band.kind === 'lowPass') {
      const slope = Math.min(3, Math.max(0, band.slope + Math.sign(notches)));
      if (slope !== band.slope) {
        this.bandChange.emit({ band, field: 'slope', value: slope });
      }
      return;
    }
    this.bandChange.emit({ band, field: 'q', value: Math.round(band.q * 2 ** (notches / 4) * 100) / 100 });
  }
}
