import { ChangeDetectionStrategy, Component, booleanAttribute, input, output } from '@angular/core';
import { AnFader } from './fader';
import { AnPanKnob } from './pan-knob';
import { AnPeakMeter, type MeterSource } from './peak-meter';

/**
 * One mixer channel strip, top to bottom as in Logic: the owner's slot rows (inserts, sends, output, group, automation:
 * projected content), pan, then the fader beside its meter, mute and solo, and the channel's name on a bar of its
 * colour. Values come in as inputs and go out as events; the owner decides what a change does (the editor sends it,
 * coalesced). The master has no solo; a VCA strip has no pan and no meter; `flow` lights the strips the selected
 * strip's signal passes through.
 */
@Component({
  selector: 'an-channel-strip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnFader, AnPanKnob, AnPeakMeter],
  host: {
    class: 'strip-body flex h-full min-h-max w-21 shrink-0 flex-col items-center gap-2 pt-2.5',
    role: 'group',
    '[attr.aria-label]': 'name() + " channel strip"',
    '[attr.data-vca]': 'vca() || null',
    '[class.strip-selected]': 'selected()',
    '[class.strip-flow]': 'flow()'
  },
  template: `
    <div class="flex w-full flex-col gap-0.5 px-1 empty:hidden"><ng-content /></div>
    @if (vca()) {
      <div class="h-12 shrink-0" aria-hidden="true"></div>
    } @else {
      <an-pan-knob
        [label]="name() + ' pan'"
        [value]="pan()"
        (valueChange)="panChange.emit($event)"
        (committed)="panCommit.emit($event)"
      />
    }
    <div class="flex min-h-40 flex-1 items-stretch gap-1">
      <an-fader
        class="h-auto!"
        [label]="name() + ' volume'"
        [value]="volumeDb()"
        (valueChange)="volumeChange.emit($event)"
        (committed)="volumeCommit.emit($event)"
      />
      @if (!vca()) {
        <an-peak-meter class="h-auto! pt-6 pb-2" [label]="name() + ' level'" [source]="meter()" [mono]="mono()" />
      }
    </div>
    <div class="flex gap-1">
      <button
        type="button"
        class="state-toggle toggle-mute h-5 w-7"
        [attr.aria-pressed]="mute()"
        [attr.aria-label]="'Mute ' + name()"
        (click)="muteToggle.emit()"
      >
        M
      </button>
      @if (!master()) {
        <button
          type="button"
          class="state-toggle toggle-solo h-5 w-7"
          [attr.aria-pressed]="solo()"
          [attr.aria-label]="'Solo ' + name()"
          (click)="soloToggle.emit()"
        >
          S
        </button>
      }
    </div>
    <div class="w-full px-1 pb-1">
      <div
        class="text-foreground bg-surface-inset w-full truncate rounded-[4px] px-1 py-1 text-center text-[11px] font-semibold shadow-[inset_0_3px_0_var(--strip-color),inset_0_0_0_1px_var(--hairline)]"
        [style.--strip-color]="color()"
        [attr.title]="name()"
      >
        {{ name() }}
      </div>
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
  /** A VCA strip: a fader that offsets its members, with no pan and no meter of its own. */
  readonly vca = input(false, { transform: booleanAttribute });
  /** On the selected strip's signal flow (Logic's signal-flow highlight). */
  readonly flow = input(false, { transform: booleanAttribute });
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
