import { ChangeDetectionStrategy, Component, computed, input, numberAttribute } from '@angular/core';

/** Where `value` sits between `min` and `max`, 0…1 (0 for no value). */
export function readingFraction(value: number | null, min: number, max: number): number {
  if (value === null || !Number.isFinite(value) || max <= min) {
    return 0;
  }
  return Math.min(1, Math.max(0, (value - min) / (max - min)));
}

/** A reading as text: "−4.5 dB", "−14.0 LUFS", "—" for none. */
export function formatReading(value: number | null, unit: string): string {
  if (value === null || !Number.isFinite(value)) {
    return '—';
  }
  const fixed = value.toFixed(1);
  const text = value < 0 && fixed !== '-0.0' ? `−${fixed.slice(1)}` : fixed === '-0.0' ? '0.0' : fixed;
  return unit ? `${text} ${unit}` : text;
}

/**
 * A plugin's reading in its editor: a compressor's gain reduction (`reduction`: a yellow bar growing from the right
 * edge, 0 dB at the right, as Logic's gain reduction meter) or a level (`level`: a green bar from the left),
 * with the value as a readout. An ARIA meter.
 */
@Component({
  selector: 'an-reading-meter',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'flex w-full min-w-28 items-center gap-2',
    role: 'meter',
    '[attr.aria-label]': 'label()',
    '[attr.aria-valuemin]': 'min()',
    '[attr.aria-valuemax]': 'max()',
    '[attr.aria-valuenow]': 'now()',
    '[attr.aria-valuetext]': 'text()'
  },
  template: `
    <span class="text-foreground-muted shrink-0 text-[10px] font-semibold tracking-wide uppercase" aria-hidden="true">{{
      label()
    }}</span>
    <span class="well relative h-2 min-w-0 flex-1 overflow-hidden rounded-[2px]" aria-hidden="true">
      @if (kind() === 'reduction') {
        <span class="bg-meter-mid absolute inset-y-0 end-0" [style.width.%]="reduction() * 100"></span>
      } @else {
        <span class="bg-meter-low absolute inset-y-0 start-0" [style.width.%]="fraction() * 100"></span>
      }
    </span>
    <span class="readout text-foreground w-16 shrink-0 text-end text-[10px] font-medium" aria-hidden="true">{{
      text()
    }}</span>
  `
})
export class AnReadingMeter {
  readonly label = input.required<string>();
  /** The reading; null when the plugin reports none. */
  readonly value = input<number | null>(null);
  readonly min = input(-30, { transform: numberAttribute });
  readonly max = input(0, { transform: numberAttribute });
  readonly unit = input('dB');
  readonly kind = input<'reduction' | 'level'>('level');

  protected readonly fraction = computed(() => readingFraction(this.value(), this.min(), this.max()));
  /** Gain reduction grows from 0 dB (the top of the range) downwards. */
  protected readonly reduction = computed(() => (this.value() === null ? 0 : 1 - this.fraction()));
  protected readonly text = computed(() => formatReading(this.value(), this.unit()));
  protected readonly now = computed(() => {
    const value = this.value();
    // A meter needs a value: with no reading it rests at its minimum (its text says there is none).
    return value === null || !Number.isFinite(value) ? this.min() : Math.round(value * 10) / 10;
  });
}
