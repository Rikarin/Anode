import { booleanAttribute, ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { ChannelStrip, MeterTap, MeterType, PanLaw, SoloMode, Track } from '@anode/api-client';
import { XuiNumericInputImports } from '@xui/numeric-input';
import { XuiPopoverImports } from '@xui/popover';
import { XuiSelectImports } from '@xui/select';
import { MixerStore, type StripInputChange } from '../state/mixer.store';
import { ProjectStore } from '../state/project.store';

/** The pan laws, as Logic's Audio settings list them. */
export const PanLaws: readonly { value: PanLaw; label: string }[] = [
  { value: 'zeroDb', label: '0 dB' },
  { value: 'minus3Db', label: '−3 dB' },
  { value: 'minus3DbCompensated', label: '−3 dB compensated' },
  { value: 'minus4Point5Db', label: '−4.5 dB' },
  { value: 'minus6Db', label: '−6 dB' }
];

/** The meter types, as the meter menu lists them. */
export const MeterTypes: readonly { value: MeterType; label: string }[] = [
  { value: 'peak', label: 'Peak' },
  { value: 'rms', label: 'RMS' },
  { value: 'k20', label: 'K-20' },
  { value: 'k14', label: 'K-14' },
  { value: 'k12', label: 'K-12' },
  { value: 'lufs', label: 'LUFS' }
];

/** Where a strip's meter can read. */
export const MeterTaps: readonly { value: MeterTap; label: string }[] = [
  { value: 'input', label: 'Input' },
  { value: 'preFader', label: 'Pre Fader' },
  { value: 'postFader', label: 'Post Fader' }
];

/** What a strip's solo can do. */
export const SoloModes: readonly { value: SoloMode; label: string }[] = [
  { value: 'inPlace', label: 'Solo in Place' },
  { value: 'afl', label: 'After-Fader Listen' },
  { value: 'pfl', label: 'Pre-Fader Listen' }
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
  if (strip.soloSafe) {
    parts.push('Safe');
  }
  if (strip.meter.type !== 'peak') {
    parts.push(MeterTypes.find(t => t.value === strip.meter.type)?.label ?? strip.meter.type);
  }
  if (strip.meter.tap !== 'postFader') {
    parts.push(strip.meter.tap === 'input' ? 'In' : 'Pre');
  }
  return parts.join(' ');
}

/**
 * A strip's settings, as a slot row that opens a popover: its meter's type and tap point, then (not on the master) trim,
 * polarity, delay, solo safe, and dual pan with its two positions for a stereo strip. The row says what is set, so a
 * strip with a trim shows it without being opened.
 */
@Component({
  selector: 'an-strip-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiNumericInputImports, XuiPopoverImports, XuiSelectImports],
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
        <span class="text-foreground-muted">Meter</span>
        <xui-select
          aria-label="Meter type"
          data-testid="strip-meter-type"
          [items]="meterTypes"
          [itemText]="optionText"
          [filterable]="false"
          [disabled]="surround()"
          [value]="meterType()"
          (valueChange)="$event && setMeter({ meterType: $event.value })"
        />
        <span class="text-foreground-muted">Reads</span>
        <xui-select
          aria-label="Meter tap point"
          data-testid="strip-meter-tap"
          [items]="meterTaps"
          [itemText]="optionText"
          [filterable]="false"
          [value]="meterTap()"
          (valueChange)="$event && setMeter({ meterTap: $event.value })"
        />
        @if (track()) {
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
          <span class="text-foreground-muted">Solo Safe</span>
          <label class="text-foreground flex items-center gap-2">
            <input
              type="checkbox"
              class="accent-(--color-primary)"
              data-testid="strip-solo-safe"
              [checked]="strip().soloSafe"
              (change)="set({ soloSafe: !strip().soloSafe })"
            />
            On
          </label>
        }
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
  private readonly project = inject(ProjectStore);

  /** The track whose strip this is; null: the master, which has its meter's options only. */
  readonly track = input.required<Track | null>();
  readonly name = input.required<string>();
  /** A surround strip's meter shows peaks per speaker whatever its type (its tap point still applies). */
  readonly surround = input(false, { transform: booleanAttribute });

  protected readonly meterTypes = MeterTypes;
  protected readonly meterTaps = MeterTaps;
  protected readonly optionText = (option: { label: string }) => option.label;
  protected readonly strip = computed(() => this.track()?.strip ?? this.project.master());
  protected readonly summary = computed(() => stripSummary(this.strip()));
  protected readonly meterType = computed(() => MeterTypes.find(t => t.value === this.strip().meter.type) ?? null);
  protected readonly meterTap = computed(() => MeterTaps.find(t => t.value === this.strip().meter.tap) ?? null);
  /** A mono source has no channels to pan apart (inserts make it stereo, but its pan still reads as mono). */
  protected readonly stereo = computed(() => {
    const track = this.track();
    return track !== null && (track.kind !== 'audio' || track.input.stereo);
  });

  protected set(change: StripInputChange): void {
    const track = this.track();
    if (track) {
      void this.mixer.setStripInput(track.id, change);
    }
  }

  protected setMeter(change: { meterType?: MeterType; meterTap?: MeterTap }): void {
    void this.mixer.setMeter(this.track()?.id ?? null, change);
  }
}
