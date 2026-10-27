import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  type ElementRef,
  afterNextRender,
  booleanAttribute,
  inject,
  input,
  signal,
  viewChild,
  viewChildren
} from '@angular/core';
import { MeterFloorDb, meterFraction } from './level-meter';
import { linearToDb } from './taper';

/** A stereo level, linear (1 = 0 dBFS). */
export interface MeterLevel {
  readonly left: number;
  readonly right: number;
}

/** Where a meter reads its level each animation frame: a function, so telemetry never goes through change detection. */
export type MeterSource = () => MeterLevel;

/** How fast the bar falls, in dB per second (it rises at once). */
export const MeterFallDbPerSecond = 26;
/** How long the peak line holds before it falls. */
export const PeakHoldMs = 1_500;

/** One channel's meter ballistics: instant attack, a steady fall, a held peak. */
export class MeterBallistics {
  /** The level the bar shows, in dBFS. */
  display = -Infinity;
  /** The held peak, in dBFS. */
  peak = -Infinity;
  /** Whether the input went over 0 dBFS since the last reset. */
  clipped = false;
  private peakAt = 0;
  private last = 0;

  /** Takes the level read at `now` (ms) and moves the bar and the peak line. */
  step(levelDb: number, now: number): void {
    const elapsed = this.last ? Math.max(0, now - this.last) / 1000 : 0;
    this.last = now;
    const fallen = Number.isFinite(this.display) ? this.display - MeterFallDbPerSecond * elapsed : -Infinity;
    this.display = Math.max(levelDb, fallen < MeterFloorDb ? -Infinity : fallen);
    if (levelDb >= this.peak || now - this.peakAt > PeakHoldMs) {
      const peakFallen = Number.isFinite(this.peak) ? this.peak - MeterFallDbPerSecond * elapsed : -Infinity;
      this.peak = levelDb >= this.peak ? levelDb : Math.max(this.display, peakFallen);
      if (levelDb >= this.peak) {
        this.peakAt = now;
      }
    }
    if (levelDb > 0) {
      this.clipped = true;
    }
  }
}

/**
 * A stereo peak meter for a channel strip. It draws from `source` in its own animation frames, setting transforms on
 * its bars directly, so the ~30 Hz telemetry of a mixer full of strips costs no change detection (docs/01-implementation
 * -plan.md §6.2). The bars fill from green through amber to red at 0 dBFS; a line holds the peak; the clip light stays
 * on until clicked. For assistive technology it is a meter whose value is updated a few times a second.
 */
@Component({
  selector: 'an-peak-meter',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-flex h-48 w-4 flex-col gap-0.5' },
  template: `
    <button
      type="button"
      class="h-1.5 w-full shrink-0 rounded-[1px]"
      [class]="clipped() ? 'bg-error' : 'bg-surface-inset'"
      [attr.aria-label]="label() + ': reset clip indicator'"
      [attr.aria-pressed]="clipped()"
      (click)="resetClip()"
    ></button>
    <div
      #meter
      role="meter"
      class="flex min-h-0 flex-1 gap-px"
      [attr.aria-label]="label()"
      [attr.aria-valuemin]="floor"
      aria-valuemax="0"
      [attr.aria-valuenow]="floor"
    >
      @for (channel of channels(); track $index) {
        <div class="bg-surface-inset relative flex-1 overflow-hidden rounded-[1px]">
          <div class="from-success via-warning to-error absolute inset-0 bg-linear-to-t from-60% via-85%"></div>
          <div #cover class="bg-surface-inset absolute inset-0 origin-top" style="transform: scaleY(1)"></div>
          <div #hold class="bg-foreground absolute inset-x-0 top-0 h-px opacity-0"></div>
        </div>
      }
    </div>
  `
})
export class AnPeakMeter {
  private readonly meter = viewChild<ElementRef<HTMLElement>>('meter');
  private readonly covers = viewChildren<ElementRef<HTMLElement>>('cover');
  private readonly holds = viewChildren<ElementRef<HTMLElement>>('hold');
  private readonly ballistics = [new MeterBallistics(), new MeterBallistics()];
  private announcedAt = 0;

  /** Read every animation frame; null shows silence. */
  readonly source = input<MeterSource | null>(null);
  readonly label = input('Level');
  /** One bar instead of two. */
  readonly mono = input(false, { transform: booleanAttribute });

  protected readonly floor = MeterFloorDb;
  protected readonly clipped = signal(false);
  protected readonly channels = () => (this.mono() ? [0] : [0, 1]);

  constructor() {
    let frame = 0;
    const draw = (now: number) => {
      this.draw(now);
      frame = requestAnimationFrame(draw);
    };
    afterNextRender(() => {
      if (typeof requestAnimationFrame === 'function') {
        frame = requestAnimationFrame(draw);
      }
    });
    inject(DestroyRef).onDestroy(() => {
      if (typeof cancelAnimationFrame === 'function') {
        cancelAnimationFrame(frame);
      }
    });
  }

  /** Reads the source and moves the bars; public so tests can drive it without animation frames. */
  draw(now: number): void {
    const level = this.source()?.() ?? { left: 0, right: 0 };
    const mono = this.mono();
    const inputs = mono ? [Math.max(level.left, level.right)] : [level.left, level.right];
    const covers = this.covers();
    const holds = this.holds();
    let loudest = -Infinity;
    inputs.forEach((linear, channel) => {
      const meter = this.ballistics[channel];
      if (!meter) {
        return;
      }
      meter.step(linearToDb(linear), now);
      loudest = Math.max(loudest, meter.display);
      const cover = covers[channel]?.nativeElement;
      if (cover) {
        cover.style.transform = `scaleY(${(1 - meterFraction(meter.display)).toFixed(4)})`;
      }
      const hold = holds[channel]?.nativeElement;
      if (hold) {
        const fraction = meterFraction(meter.peak);
        hold.style.opacity = fraction > 0 ? '1' : '0';
        hold.style.top = `${((1 - fraction) * 100).toFixed(2)}%`;
      }
    });
    if (!this.clipped() && this.ballistics.some(meter => meter.clipped)) {
      this.clipped.set(true);
    }

    // The ARIA value, a few times a second rather than every frame.
    if (now - this.announcedAt > 250) {
      this.announcedAt = now;
      const value = Number.isFinite(loudest) ? Math.round(Math.max(MeterFloorDb, Math.min(0, loudest))) : MeterFloorDb;
      this.meter()?.nativeElement.setAttribute('aria-valuenow', String(value));
    }
  }

  protected resetClip(): void {
    for (const meter of this.ballistics) {
      meter.clipped = false;
    }
    this.clipped.set(false);
  }
}
