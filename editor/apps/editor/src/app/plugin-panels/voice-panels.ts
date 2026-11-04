import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AnXyPad, type XyValue } from '@anode/audio-ui';
import type { PluginPanelHost } from './plugin-panel';

/** The band meters' floor, dBFS. */
const BandFloor = -60;

/** A band's level as a bar's height, 0…100. */
export function bandHeight(db: number): number {
  return Math.min(100, Math.max(0, (1 - db / BandFloor) * 100));
}

/** What a source needs on the sidechain while none is connected; null when it needs nothing more. */
export function sidechainHint(source: number, sidechain: string | null | undefined): string | null {
  if (sidechain) {
    return null;
  }
  return source === 1
    ? 'Choose the carrier as this insert’s Side Chain in the Mixer.'
    : source === 2
      ? 'Choose the voice as this insert’s Side Chain in the Mixer.'
      : null;
}

/**
 * The Vocoder: what it hears in each band (the insert's reading values, lowest first), as a bar per band, and what its
 * source needs. An image: the numbers live in the reading beside it.
 */
@Component({
  selector: 'an-vocoder-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex flex-col gap-1', 'data-testid': 'vocoder-panel' },
  template: `
    <div class="well flex h-20 items-end gap-px rounded-[6px] p-1" role="img" aria-label="Vocoder bands">
      @for (band of bands(); track $index) {
        <span
          class="bg-meter-low min-w-0 flex-1 rounded-[1px]"
          data-testid="vocoder-band"
          aria-hidden="true"
          [style.height.%]="band"
        ></span>
      }
    </div>
    @if (hint(); as text) {
      <p class="text-foreground-muted text-[11px]" data-testid="vocoder-hint">{{ text }}</p>
    }
  `
})
export class VocoderPanel {
  readonly host = input.required<PluginPanelHost>();

  protected readonly bands = computed(() => (this.host().readingValues?.() ?? []).map(bandHeight));
  protected readonly hint = computed(() =>
    sidechainHint(Math.round(this.host().value('source')), this.host().sidechain)
  );
}

/** Pitch and formant, in semitones, on a pad: the middle is the voice as it is. */
export function shapeOnPad(pitch: number, formant: number): XyValue {
  return { x: (pitch + 24) / 48, y: (formant + 12) / 24 };
}

/** The pad's point as pitch and formant, to a tenth of a semitone. */
export function padToShape(point: XyValue): { pitch: number; formant: number } {
  return {
    pitch: Math.round((point.x * 48 - 24) * 10) / 10,
    formant: Math.round((point.y * 24 - 12) * 10) / 10
  };
}

/** The Voice Shaper: pitch across and formant up on one pad, with what it hears (the insert's reading values). */
@Component({
  selector: 'an-voice-shaper-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnXyPad],
  host: { class: 'flex items-center gap-3', 'data-testid': 'voice-shaper-panel' },
  template: `
    <an-xy-pad
      label="Pitch and formant"
      xLabel="Pitch"
      yLabel="Formant"
      [size]="132"
      [value]="pad()"
      (valueChange)="move($event)"
    />
    <dl class="readout text-foreground grid grid-cols-[auto_auto] gap-x-2 gap-y-0.5 text-[11px]">
      <dt class="text-foreground-muted">Heard</dt>
      <dd data-testid="voice-heard">{{ heard() }}</dd>
      <dt class="text-foreground-muted">Shift</dt>
      <dd data-testid="voice-shift">{{ shift() }}</dd>
      @if (reference(); as hz) {
        <dt class="text-foreground-muted">Side Chain</dt>
        <dd data-testid="voice-reference">{{ hz }}</dd>
      }
    </dl>
  `
})
export class VoiceShaperPanel {
  readonly host = input.required<PluginPanelHost>();

  private readonly values = computed(() => this.host().readingValues?.() ?? []);
  protected readonly pad = computed(() => shapeOnPad(this.host().value('pitch'), this.host().value('formant')));
  protected readonly heard = computed(() => hz(this.values()[0]) ?? 'No pitch');
  protected readonly reference = computed(() => hz(this.values()[2]));
  protected readonly shift = computed(() => {
    const cents = Math.round((this.values()[1] ?? 0) / 10) / 10;
    return `${cents < 0 ? '−' : cents > 0 ? '+' : ''}${Math.abs(cents).toFixed(1)} st`;
  });

  protected move(point: XyValue): void {
    const { pitch, formant } = padToShape(point);
    const host = this.host();
    if (pitch !== host.value('pitch')) {
      host.set('pitch', pitch);
    }
    if (formant !== host.value('formant')) {
      host.set('formant', formant);
    }
  }
}

function hz(value: number | undefined): string | null {
  return value ? `${value.toFixed(1)} Hz` : null;
}
