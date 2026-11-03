import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { AnDrumPads, AnTouchKeyboard, RootNames, type ScaleId, ScaleIds, Scales } from '@anode/audio-ui/lazy';
import { LiveMidiStore } from '../state/live-midi.store';
import { drumPadsOf } from './remote-model';
import { Touch } from './remote-transport';

/** What the play view shows: the keyboard, or the drum pads. */
export type PlayMode = 'keys' | 'drums';

/**
 * The remote's instruments (docs/11-phase-10.md): the touch keyboard (a scale from a root, octave down/up; each finger a
 * note, velocity by where it lands) and the drum pads (the selected Drum Machine Designer's pads, else the 16 GM
 * notes). Both play the selected track through live MIDI (`/midi/live`, the live-MIDI store the on-screen keyboard
 * uses), so recording, step input and MIDI FX behave as with a keyboard. A Drum Machine Designer track opens on its
 * pads.
 */
@Component({
  selector: 'an-remote-play',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnDrumPads, AnTouchKeyboard],
  host: { class: 'flex min-h-0 flex-col gap-2 p-2' },
  template: `
    <div class="flex flex-wrap items-center gap-2">
      <div class="bezel-group text-foreground h-11 text-[12px] font-semibold" role="radiogroup" aria-label="Play with">
        @for (option of modes; track option.value) {
          <button
            type="button"
            role="radio"
            class="min-w-20 px-3"
            [attr.data-testid]="'play-' + option.value"
            [attr.aria-checked]="mode() === option.value"
            [class.text-lit]="mode() === option.value"
            (click)="chosen.set(option.value)"
          >
            {{ option.label }}
          </button>
        }
      </div>
      @if (mode() === 'keys') {
        <label class="text-foreground flex items-center gap-1 text-[12px]">
          <span class="text-foreground-muted">Scale</span>
          <select
            class="bg-surface-inset border-hairline text-foreground h-11 rounded-md border px-2"
            data-testid="play-scale"
            (change)="scale.set(asScale($event))"
          >
            @for (id of scaleIds; track id) {
              <option [value]="id" [selected]="scale() === id">{{ scales[id].name }}</option>
            }
          </select>
        </label>
        <label class="text-foreground flex items-center gap-1 text-[12px]">
          <span class="text-foreground-muted">Root</span>
          <select
            class="bg-surface-inset border-hairline text-foreground h-11 rounded-md border px-2"
            data-testid="play-root"
            (change)="root.set(asNumber($event))"
          >
            @for (name of roots; track $index) {
              <option [value]="$index" [selected]="root() === $index">{{ name }}</option>
            }
          </select>
        </label>
        <div class="flex items-center gap-1" role="group" aria-label="Octave">
          <button type="button" [class]="touch" aria-label="Octave down" (click)="shift(-1)">−</button>
          <span class="readout text-foreground w-12 text-center text-[12px]" data-testid="play-octave"
            >C{{ base() / 12 - 2 }}</span
          >
          <button type="button" [class]="touch" aria-label="Octave up" (click)="shift(1)">+</button>
        </div>
      }
      <span class="text-foreground-muted ms-auto text-[12px]" data-testid="play-target">
        {{ target() ? 'Plays ' + target()?.name : 'Select an instrument or MIDI track to play' }}
      </span>
    </div>
    @if (mode() === 'keys') {
      <an-touch-keyboard
        class="min-h-40 flex-1"
        label="Touch keyboard"
        [scale]="scale()"
        [root]="root()"
        [base]="base()"
        [octaves]="2"
        [sounding]="live.sounding()"
        [disabled]="!target()"
        (noteOn)="live.noteOn($event.pitch, $event.velocity)"
        (noteOff)="live.noteOff($event)"
      />
    } @else {
      <an-drum-pads
        class="mx-auto aspect-square max-h-full min-h-0 w-full max-w-[32rem] flex-1"
        [label]="target()?.drumMachine ? target()?.name + ' pads' : 'Drum pads (General MIDI)'"
        [pads]="pads()"
        [sounding]="live.sounding()"
        [disabled]="!target()"
        (noteOn)="live.noteOn($event.pitch, $event.velocity)"
        (noteOff)="live.noteOff($event)"
      />
    }
  `
})
export class RemotePlay {
  protected readonly live = inject(LiveMidiStore);
  protected readonly touch = Touch;
  protected readonly modes: readonly { value: PlayMode; label: string }[] = [
    { value: 'keys', label: 'Keyboard' },
    { value: 'drums', label: 'Drums' }
  ];
  protected readonly scales = Scales;
  protected readonly scaleIds = ScaleIds;
  protected readonly roots = RootNames;

  protected readonly target = this.live.target;
  /** The mode chosen here; null: by the track (a Drum Machine Designer opens on its pads). */
  readonly chosen = signal<PlayMode | null>(null);
  readonly mode = computed<PlayMode>(() => this.chosen() ?? (this.target()?.drumMachine ? 'drums' : 'keys'));
  readonly scale = signal<ScaleId>('major');
  readonly root = signal(0);
  /** The keyboard's first C (48: C2). */
  readonly base = signal(48);
  protected readonly pads = computed(() => drumPadsOf(this.target()));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.live.releaseAll());
  }

  protected shift(by: number): void {
    this.base.update(base => Math.min(96, Math.max(0, base + by * 12)));
  }

  protected asScale(event: Event): ScaleId {
    const value = (event.target as HTMLSelectElement).value;
    return (ScaleIds as readonly string[]).includes(value) ? (value as ScaleId) : 'major';
  }

  protected asNumber(event: Event): number {
    return Number((event.target as HTMLSelectElement).value) || 0;
  }
}
