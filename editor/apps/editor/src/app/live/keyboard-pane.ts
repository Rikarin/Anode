import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { AnPianoKeyboard } from '@anode/audio-ui';
import { XuiButtonImports } from '@xui/button';
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
  imports: [AnPianoKeyboard, XuiButtonImports, XuiTooltipImports],
  host: { class: 'flex h-full min-h-0 flex-col gap-2 p-2', role: 'region', 'aria-label': 'Keyboard' },
  template: `
    <div class="flex items-center gap-2 text-xs" role="toolbar" aria-label="Musical typing">
      <span class="text-foreground-muted" data-testid="keyboard-target">
        {{ live.target() ? 'Plays ' + live.target()?.name : 'Select an instrument or MIDI track to play it' }}
      </span>
      <button
        xuiButton
        type="button"
        size="sm"
        class="ms-auto px-2 text-xs"
        [variant]="live.typing() ? 'default' : 'ghost'"
        [active]="live.typing()"
        xuiTooltip="Play with the computer keyboard: A W S E D F T G Y H U J K; Z and X change the octave"
        data-testid="musical-typing"
        (click)="live.typing.set(!live.typing())"
      >
        Musical typing
      </button>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="ghost"
        aria-label="Octave down (Z)"
        (click)="live.shiftOctave(-1)"
      >
        Z
      </button>
      <span class="w-10 text-center tabular-nums" aria-live="polite">{{ octaveName() }}</span>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="ghost"
        aria-label="Octave up (X)"
        (click)="live.shiftOctave(1)"
      >
        X
      </button>
      <label class="text-foreground-muted flex items-center gap-1">
        Velocity
        <input
          type="number"
          class="border-border bg-surface-inset text-foreground h-6 w-12 rounded border px-1 text-xs tabular-nums"
          min="1"
          max="127"
          [value]="live.velocity()"
          (change)="setVelocity($event)"
        />
      </label>
    </div>
    <div class="min-h-0 flex-1 overflow-x-auto">
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
