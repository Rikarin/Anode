import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { AnPianoKeyboard } from '@anode/audio-ui';
import { XuiTooltipImports } from '@xui/tooltip';
import { noteName } from '../midi/midi-math';
import { LiveMidiStore } from '../state/live-midi.store';

/**
 * The Keyboard pane (Logic's Keyboard and Musical Typing windows in one): an on-screen keyboard that plays the selected
 * instrument or MIDI track through `/midi/live` and lights what the engine reports sounding on it, and the musical
 * typing switch with its octave (Z and X move it) and velocity.
 */
@Component({
  selector: 'an-keyboard-pane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnPianoKeyboard, XuiTooltipImports],
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Keyboard' },
  template: `
    <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2 text-xs" role="toolbar" aria-label="Musical typing">
      <span class="text-foreground-muted" data-testid="keyboard-target">
        {{ live.target() ? 'Plays ' + live.target()?.name : 'Select an instrument or MIDI track to play it' }}
      </span>
      <button
        type="button"
        class="bezel text-foreground ms-auto h-6.5 px-2.5 text-[11px] font-semibold"
        [class.text-lit]="live.typing()"
        [attr.aria-pressed]="live.typing() ? 'true' : 'false'"
        xuiTooltip="Play with the computer keyboard: A W S E D F T G Y H U J K; Z and X change the octave"
        data-testid="musical-typing"
        (click)="live.typing.set(!live.typing())"
      >
        Musical typing
      </button>
      <div class="bezel-group text-foreground h-6.5 text-[11px] font-semibold">
        <button type="button" class="w-7" aria-label="Octave down (Z)" (click)="live.shiftOctave(-1)">Z</button>
        <span class="readout grid w-10 place-items-center" aria-live="polite">{{ octaveName() }}</span>
        <button type="button" class="w-7" aria-label="Octave up (X)" (click)="live.shiftOctave(1)">X</button>
      </div>
      <label class="text-foreground-muted flex items-center gap-1.5 text-[11px]">
        Velocity
        <input
          type="number"
          class="border-hairline bg-surface-inset text-foreground readout h-6.5 w-14 rounded-[5px] border px-1.5 text-xs shadow-[inset_0_1px_1px_rgb(0_0_0/0.2)]"
          min="1"
          max="127"
          [value]="live.velocity()"
          (change)="setVelocity($event)"
        />
      </label>
    </div>
    <div class="bg-surface-sunken min-h-0 flex-1 overflow-x-auto p-2">
      <an-piano-keyboard
        class="h-full min-h-16"
        label="On-screen keyboard"
        data-testid="on-screen-keyboard"
        [from]="36"
        [octaves]="5"
        [sounding]="live.sounding()"
        [disabled]="!live.target()"
        (noteOn)="live.noteOn($event.pitch, $event.velocity)"
        (noteOff)="live.noteOff($event)"
      />
    </div>
  `
})
export class KeyboardPane {
  protected readonly live = inject(LiveMidiStore);
  protected readonly octaveName = computed(() => noteName(this.live.octave() * 12));

  protected setVelocity(event: Event): void {
    const velocity = Math.round(Number((event.target as HTMLInputElement).value));
    if (velocity >= 1 && velocity <= 127) {
      this.live.velocity.set(velocity);
    }
  }
}
