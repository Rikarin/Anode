import { ChangeDetectionStrategy, Component, computed, input, numberAttribute } from '@angular/core';

/** The graph's frequency axis: 20 Hz to 20 kHz, logarithmic. */
const LowHz = 20;
const HighHz = 20_000;

/** A frequency's place across the graph, 0…1 (log). */
export function spectrumX(hz: number): number {
  const clamped = Math.min(HighHz, Math.max(LowHz, hz));
  return Math.log(clamped / LowHz) / Math.log(HighHz / LowHz);
}

/** The dB span the graph shows: both series with 3 dB to spare, rounded out to 6 dB, at least 12 dB tall. */
export function spectrumRange(...series: readonly (readonly number[])[]): { min: number; max: number } {
  const values = series.flat().filter(Number.isFinite);
  if (values.length === 0) {
    return { min: -12, max: 12 };
  }
  let min = Math.floor((Math.min(...values) - 3) / 6) * 6;
  let max = Math.ceil((Math.max(...values) + 3) / 6) * 6;
  if (max - min < 12) {
    const middle = (min + max) / 2;
    min = middle - 6;
    max = middle + 6;
  }
  return { min, max };
}

/** A series as SVG points over `width` × `height` (one per band; bands and values pair by index). */
export function spectrumPoints(
  bands: readonly number[],
  values: readonly number[],
  range: { min: number; max: number },
  width: number,
  height: number
): { x: number; y: number }[] {
  const span = range.max - range.min || 1;
  return bands.slice(0, values.length).map((hz, i) => ({
    x: Math.round(spectrumX(hz) * width * 10) / 10,
    y: Math.round((1 - ((values[i] ?? 0) - range.min) / span) * height * 10) / 10
  }));
}

/** A polyline's `points`. */
export function polyline(points: readonly { x: number; y: number }[]): string {
  return points.map(p => `${p.x},${p.y}`).join(' ');
}

/** The area under a series down to the graph's bottom, as a path. */
export function areaPath(points: readonly { x: number; y: number }[], height: number): string {
  const first = points[0];
  const last = points.at(-1);
  if (!first || !last) {
    return '';
  }
  return `M${first.x},${height} ${points.map(p => `L${p.x},${p.y}`).join(' ')} L${last.x},${height} Z`;
}

const Ticks = [100, 1_000, 10_000];

/**
 * A spectrum against a reference curve (the Mastering Assistant's analysis): a `well` with frequency grid lines at 100
 * Hz, 1 kHz and 10 kHz and a 0 dB line in `stroke-knob-track`, the measured spectrum in `stroke-knob-arc` over a
 * `fill-primary/20` area and the reference as a dashed muted line. Bands are frequencies (Hz), the two series dB by
 * band. An image: its name says what it compares, the readings beside it carry the numbers.
 */
@Component({
  selector: 'an-spectrum-graph',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex flex-col gap-1' },
  template: `
    <svg
      class="well block w-full rounded-[6px]"
      role="img"
      [attr.aria-label]="label()"
      [attr.viewBox]="'0 0 ' + width + ' ' + height()"
      [style.height.px]="height()"
      preserveAspectRatio="none"
    >
      @for (tick of ticks; track tick) {
        <line
          class="stroke-knob-track"
          stroke-width="1"
          vector-effect="non-scaling-stroke"
          [attr.x1]="x(tick)"
          [attr.x2]="x(tick)"
          y1="0"
          [attr.y2]="height()"
        />
      }
      @if (zero() !== null) {
        <line
          class="stroke-knob-track"
          stroke-width="1"
          vector-effect="non-scaling-stroke"
          x1="0"
          [attr.x2]="width"
          [attr.y1]="zero()"
          [attr.y2]="zero()"
        />
      }
      @if (spectrum().length > 0) {
        <path class="fill-primary/20" [attr.d]="area()" />
        <polyline
          class="stroke-knob-arc fill-none"
          stroke-width="1.5"
          stroke-linejoin="round"
          vector-effect="non-scaling-stroke"
          [attr.points]="spectrumLine()"
        />
      }
      @if (curve().length > 0) {
        <polyline
          class="stroke-foreground-muted fill-none"
          stroke-width="1.25"
          stroke-dasharray="4 3"
          vector-effect="non-scaling-stroke"
          [attr.points]="curveLine()"
        />
      }
    </svg>
    <div class="text-foreground-muted readout flex justify-between text-[9px]" aria-hidden="true">
      <span>20</span><span>100</span><span>1k</span><span>10k</span><span>20k</span>
    </div>
  `
})
export class AnSpectrumGraph {
  /** Band centres (Hz). */
  readonly bands = input.required<readonly number[]>();
  /** The measured spectrum, dB by band (empty: none yet). */
  readonly spectrum = input<readonly number[]>([]);
  /** The reference curve, dB by band (empty: none). */
  readonly curve = input<readonly number[]>([]);
  readonly label = input('Spectrum');
  readonly height = input(120, { transform: numberAttribute });

  /** The drawing's width in its own units (the SVG stretches to the element). */
  protected readonly width = 600;
  protected readonly ticks = Ticks;

  private readonly range = computed(() => spectrumRange(this.spectrum(), this.curve()));
  private readonly spectrumPoints = computed(() =>
    spectrumPoints(this.bands(), this.spectrum(), this.range(), this.width, this.height())
  );
  protected readonly spectrumLine = computed(() => polyline(this.spectrumPoints()));
  protected readonly area = computed(() => areaPath(this.spectrumPoints(), this.height()));
  protected readonly curveLine = computed(() =>
    polyline(spectrumPoints(this.bands(), this.curve(), this.range(), this.width, this.height()))
  );
  /** The 0 dB line, when 0 dB is in the range. */
  protected readonly zero = computed(() => {
    const { min, max } = this.range();
    return min < 0 && max > 0 ? (max / (max - min)) * this.height() : null;
  });

  protected x(hz: number): number {
    return spectrumX(hz) * this.width;
  }
}
