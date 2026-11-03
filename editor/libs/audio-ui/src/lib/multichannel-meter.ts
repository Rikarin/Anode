import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  type ElementRef,
  afterNextRender,
  booleanAttribute,
  computed,
  inject,
  input,
  signal,
  viewChild,
  viewChildren
} from '@angular/core';
import { MeterFloorDb, meterFraction } from './level-meter';
import { shortSpeakerLabel, speakerName } from './panner-math';
import { MeterBallistics } from './peak-meter';
import { linearToDb } from './taper';

/** Where a multichannel meter reads its levels each animation frame: linear peaks, one per channel in layout order. */
export type MultiMeterSource = () => readonly number[];

/**
 * A meter of one bar per speaker (docs/11-phase-10.md, "multichannel metering"), in the layout's channel order, each
 * labelled with its short name (L R C LFE Ls Rs Lss Rss Lrs Rrs Ltf …, from the ADM labels): the stereo peak meter's
 * LED look and ballistics (segments green to −12 dB, yellow to −6, red near 0; a held peak segment), drawn from `source`
 * in animation frames without change detection, and one clip light for all channels. `labels` off draws the bars only
 * (a channel strip), each still named for assistive technology.
 */
@Component({
  selector: 'an-multichannel-meter',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-flex h-48 flex-col gap-0.5' },
  template: `
    <button
      type="button"
      class="well h-1.5 w-full shrink-0 rounded-[2px] p-px"
      [attr.aria-label]="label() + ': reset clip indicator'"
      [attr.aria-pressed]="clipped()"
      (click)="resetClip()"
    >
      <span
        class="block size-full rounded-[1px] transition-colors duration-100"
        [class]="clipped() ? 'bg-meter-high shadow-[0_0_4px_var(--meter-high)]' : 'bg-meter-unlit'"
      ></span>
    </button>
    <div
      #meter
      role="meter"
      class="well flex min-h-0 flex-1 gap-px rounded-[2px] p-px"
      [attr.aria-label]="label()"
      [attr.aria-valuemin]="floor"
      aria-valuemax="0"
      [attr.aria-valuenow]="floor"
      [attr.aria-description]="description()"
    >
      @for (channel of channels(); track $index) {
        <div
          class="meter-segments relative overflow-hidden"
          [style.width.px]="barWidth()"
          [attr.title]="channel.name"
          [attr.data-channel]="channel.short"
        >
          <div class="meter-gradient absolute inset-0"></div>
          <div #cover class="bg-meter-unlit absolute inset-0 origin-top" style="transform: scaleY(1)"></div>
          <div #hold class="bg-meter-peak absolute inset-x-0 top-0 h-0.5 opacity-0"></div>
        </div>
      }
    </div>
    @if (labels()) {
      <div class="flex gap-px px-px" aria-hidden="true">
        @for (channel of channels(); track $index) {
          <span
            class="text-foreground overflow-hidden text-center text-[8px] leading-none font-semibold"
            [style.width.px]="barWidth()"
            >{{ channel.short }}</span
          >
        }
      </div>
    }
  `
})
export class AnMultichannelMeter {
  private readonly meter = viewChild<ElementRef<HTMLElement>>('meter');
  private readonly covers = viewChildren<ElementRef<HTMLElement>>('cover');
  private readonly holds = viewChildren<ElementRef<HTMLElement>>('hold');
  private ballistics: MeterBallistics[] = [];
  private announcedAt = 0;

  /** The layout's ADM speaker labels, in channel order (`M+030`, `M-030`, `M+000`, `LFE1` …). */
  readonly speakers = input<readonly string[]>(['M+030', 'M-030']);
  /** Read every animation frame; null shows silence. */
  readonly source = input<MultiMeterSource | null>(null);
  readonly label = input('Level');
  /** The short speaker names under the bars. */
  readonly labels = input(true, { transform: booleanAttribute });
  /** Each bar's width in CSS pixels. */
  readonly barWidth = input(6);

  protected readonly floor = MeterFloorDb;
  protected readonly clipped = signal(false);
  protected readonly channels = computed(() =>
    this.speakers().map(label => ({ short: shortSpeakerLabel(label), name: speakerName(label) }))
  );
  protected readonly description = computed(
    () =>
      `Channels: ${this.channels()
        .map(c => c.short)
        .join(', ')}`
  );

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
    const count = this.speakers().length;
    if (this.ballistics.length !== count) {
      this.ballistics = Array.from({ length: count }, () => new MeterBallistics());
    }
    const levels = this.source()?.() ?? [];
    const covers = this.covers();
    const holds = this.holds();
    let loudest = -Infinity;
    this.ballistics.forEach((meter, channel) => {
      meter.step(linearToDb(levels[channel] ?? 0), now);
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
