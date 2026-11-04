import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { ChannelStrip, PanLaw, Track } from '@anode/api-client';
import { XuiNumericInputImports } from '@xui/numeric-input';
import { XuiPopoverImports } from '@xui/popover';
import { MixerStore, type StripInputChange } from '../state/mixer.store';

/** The pan laws, as Logic's Audio settings list them. */
export const PanLaws: readonly { value: PanLaw; label: string }[] = [
  { value: 'zeroDb', label: '0 dB' },
  { value: 'minus3Db', label: '−3 dB' },
  { value: 'minus3DbCompensated', label: '−3 dB compensated' },
  { value: 'minus4Point5Db', label: '−4.5 dB' },
  { value: 'minus6Db', label: '−6 dB' }
];

/** What the strip's settings button says: what differs from a plain strip, or that nothing does. */
export function stripSummary(strip: ChannelStrip): string {
  const parts = [];
  if (strip.trimDb !== 0) {
    parts.push(`${strip.trimDb > 0 ? '+' : ''}${strip.trimDb.toFixed(1)} dB`);
  }
  if (strip.polarityInverted) {
    parts.push('ø');
  }
  if (strip.delayMs !== 0) {
    parts.push(`${strip.delayMs > 0 ? '+' : ''}${strip.delayMs} ms`);
  }
  if (strip.dualPan) {
    parts.push('Dual');
  }
  return parts.join(' ');
}

/**
 * A strip's input stage, as a slot row that opens a popover: trim, polarity, delay, and dual pan with its two positions
 * for a stereo strip. The row says what is set, so a strip with a trim shows it without being opened.
 */
@Component({
  selector: 'an-strip-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiNumericInputImports, XuiPopoverImports],
  host: { class: 'block' },
  template: `
    <button
      type="button"
      class="strip-slot text-foreground w-full justify-center"
      data-testid="strip-settings"
      placement="right-start"
      [attr.aria-label]="'Strip settings of ' + name() + (summary() ? ': ' + summary() : '')"
      [attr.title]="summary() || 'Strip settings'"
      [xuiPopover]="panel"
    >
      @if (summary(); as text) {
        <span class="readout truncate font-semibold">{{ text }}</span>
      } @else {
        <span class="text-foreground-muted">Input</span>
      }
    </button>
    <ng-template #panel>
      <div class="grid w-52 grid-cols-[1fr_6rem] items-center gap-x-3 gap-y-2 p-2 text-xs" data-testid="strip-input">
        <label class="text-foreground-muted" for="an-strip-trim">Trim (dB)</label>
        <xui-numeric-input
          size="sm"
          id="an-strip-trim"
          aria-label="Trim in dB"
          data-testid="strip-trim"
          [min]="-24"
          [max]="24"
          [stepSize]="0.5"
          [majorStepSize]="3"
          [value]="strip().trimDb"
          (valueChange)="set({ trimDb: $event ?? 0 })"
        />
        <span class="text-foreground-muted">Polarity</span>
        <label class="text-foreground flex items-center gap-2">
          <input
            type="checkbox"
            class="accent-(--color-primary)"
            data-testid="strip-polarity"
            [checked]="strip().polarityInverted"
            (change)="set({ polarityInverted: !strip().polarityInverted })"
          />
          Invert
        </label>
        <label class="text-foreground-muted" for="an-strip-delay">Delay (ms)</label>
        <xui-numeric-input
          size="sm"
          id="an-strip-delay"
          aria-label="Delay in milliseconds"
          data-testid="strip-delay"
          [min]="-500"
          [max]="500"
          [stepSize]="1"
          [majorStepSize]="10"
          [value]="strip().delayMs"
          (valueChange)="set({ delayMs: $event ?? 0 })"
        />
        @if (stereo()) {
          <span class="text-foreground-muted">Dual Pan</span>
          <label class="text-foreground flex items-center gap-2">
            <input
              type="checkbox"
              class="accent-(--color-primary)"
              data-testid="strip-dual-pan"
              [checked]="strip().dualPan"
              (change)="set({ dualPan: !strip().dualPan })"
            />
            On
          </label>
          @if (strip().dualPan) {
            <label class="text-foreground-muted" for="an-strip-pan-left">Left to</label>
            <xui-numeric-input
              size="sm"
              id="an-strip-pan-left"
              aria-label="Left channel pan"
              data-testid="strip-pan-left"
              [min]="-1"
              [max]="1"
              [stepSize]="0.05"
              [majorStepSize]="0.25"
              [value]="strip().panLeft"
              (valueChange)="set({ panLeft: $event ?? -1 })"
            />
            <label class="text-foreground-muted" for="an-strip-pan-right">Right to</label>
            <xui-numeric-input
              size="sm"
              id="an-strip-pan-right"
              aria-label="Right channel pan"
              data-testid="strip-pan-right"
              [min]="-1"
              [max]="1"
              [stepSize]="0.05"
              [majorStepSize]="0.25"
              [value]="strip().panRight"
              (valueChange)="set({ panRight: $event ?? 1 })"
            />
          }
        }
      </div>
    </ng-template>
  `
})
export class StripSettings {
  private readonly mixer = inject(MixerStore);

  readonly track = input.required<Track>();
  readonly name = input.required<string>();

  protected readonly strip = computed(() => this.track().strip);
  protected readonly summary = computed(() => stripSummary(this.strip()));
  /** A mono source has no channels to pan apart (inserts make it stereo, but its pan still reads as mono). */
  protected readonly stereo = computed(() => this.track().kind !== 'audio' || this.track().input.stereo);

  protected set(change: StripInputChange): void {
    void this.mixer.setStripInput(this.track().id, change);
  }
}
