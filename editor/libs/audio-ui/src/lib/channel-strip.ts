import { ChangeDetectionStrategy, Component, booleanAttribute, input, output } from '@angular/core';
import { XuiButtonImports } from '@xui/button';
import { AnFader } from './fader';
import { AnPanKnob } from './pan-knob';
import { AnPeakMeter, type MeterSource } from './peak-meter';

/**
 * One mixer channel strip, top to bottom as in Logic: pan, then the fader beside its meter, mute and solo, and the
 * channel's name on a bar of its colour. Values come in as inputs and go out as events; the owner decides what a
 * change does (the editor sends it, coalesced). The master has no solo.
 */
@Component({
  selector: 'an-channel-strip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnFader, AnPanKnob, AnPeakMeter, XuiButtonImports],
  host: {
    class: 'bg-surface border-border flex w-20 shrink-0 flex-col items-center gap-2 rounded-md border px-1 pt-2 pb-1',
    role: 'group',
    '[attr.aria-label]': 'name() + " channel strip"',
    '[class.ring-1]': 'selected()',
    '[class.ring-primary]': 'selected()'
  },
  template: `
    <an-pan-knob
      [label]="name() + ' pan'"
      [value]="pan()"
      (valueChange)="panChange.emit($event)"
      (committed)="panCommit.emit($event)"
    />
    <div class="flex items-stretch gap-1">
      <an-fader
        [label]="name() + ' volume'"
        [value]="volumeDb()"
        (valueChange)="volumeChange.emit($event)"
        (committed)="volumeCommit.emit($event)"
      />
      <an-peak-meter class="mb-4" [label]="name() + ' level'" [source]="meter()" [mono]="mono()" />
    </div>
    <div class="flex gap-1">
      <button
        xuiButton
        type="button"
        size="sm"
        class="h-5 w-6 px-0 text-[10px] font-semibold"
        [variant]="mute() ? 'default' : 'outline'"
        [color]="mute() ? 'info' : 'secondary'"
        [active]="mute()"
        [attr.aria-label]="'Mute ' + name()"
        (click)="muteToggle.emit()"
      >
        M
      </button>
      @if (!master()) {
        <button
          xuiButton
          type="button"
          size="sm"
          class="h-5 w-6 px-0 text-[10px] font-semibold"
          [variant]="solo() ? 'default' : 'outline'"
          [color]="solo() ? 'warning' : 'secondary'"
          [active]="solo()"
          [attr.aria-label]="'Solo ' + name()"
          (click)="soloToggle.emit()"
        >
          S
        </button>
      }
    </div>
    <div
      class="text-foreground w-full truncate rounded-sm border-t-4 px-1 pt-0.5 text-center text-[11px]"
      [style.border-top-color]="color()"
      [attr.title]="name()"
    >
      {{ name() }}
    </div>
  `
})
export class AnChannelStrip {
  readonly name = input.required<string>();
  /** The channel's colour (any CSS colour), shown on its name bar. */
  readonly color = input('var(--color-border-strong)');
  readonly volumeDb = input(0);
  readonly pan = input(0);
  readonly mute = input(false, { transform: booleanAttribute });
  readonly solo = input(false, { transform: booleanAttribute });
  readonly master = input(false, { transform: booleanAttribute });
  readonly selected = input(false, { transform: booleanAttribute });
  readonly mono = input(false, { transform: booleanAttribute });
  /** Where the meter reads its level each animation frame. */
  readonly meter = input<MeterSource | null>(null);

  /** The fader moved (every step of a drag). */
  readonly volumeChange = output<number>();
  /** A fader gesture ended. */
  readonly volumeCommit = output<number>();
  readonly panChange = output<number>();
  readonly panCommit = output<number>();
  readonly muteToggle = output();
  readonly soloToggle = output();
}
