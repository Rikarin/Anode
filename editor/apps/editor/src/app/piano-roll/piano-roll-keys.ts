import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RULER_HEIGHT } from '@anode/timeline';
import { isBlackKey, noteName } from '../midi/midi-math';
import { LiveMidiStore } from '../state/live-midi.store';
import { PianoRollStore } from '../state/piano-roll.store';
import { pitchOfLane } from './notes-scene';

const Pitches = Array.from({ length: 128 }, (_, i) => 127 - i);

/**
 * The Piano Roll's keyboard column: a key per pitch lane, scrolled with the grid, each C named (C3 is middle C).
 * Pressing a key plays it on the track (when it is the track live MIDI plays); keys light while they sound. The
 * on-screen keyboard is the accessible way to play, so this column is hidden from assistive technology.
 */
@Component({
  selector: 'an-piano-roll-keys',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'border-hairline bg-surface relative block overflow-hidden border-e select-none',
    'aria-hidden': 'true',
    '(pointerdown)': 'down($event)',
    '(pointerup)': 'up()',
    '(pointercancel)': 'up()',
    '(lostpointercapture)': 'up()',
    '(wheel)': 'wheel($event)'
  },
  template: `
    <div class="glass-bar absolute inset-x-0 top-0 z-10" [style.height.px]="ruler"></div>
    <div class="absolute inset-x-0" [style.top.px]="ruler" [style.transform]="'translateY(' + offset() + 'px)'">
      @for (pitch of pitches; track pitch) {
        <div
          class="relative flex items-center justify-end pe-1 text-[9px] leading-none font-medium"
          [class.roll-key-white]="!lit().has(pitch)"
          [class.key-lit]="lit().has(pitch)"
          [style.height.px]="store.laneHeight()"
        >
          @if (black(pitch) && !lit().has(pitch)) {
            <span class="roll-key-black absolute inset-y-0 left-0 w-[62%] rounded-e-[2px]"></span>
          }
          @if (pitch % 12 === 0 && store.laneHeight() >= 8) {
            {{ name(pitch) }}
          }
        </div>
      }
    </div>
  `
})
export class PianoRollKeys {
  protected readonly store = inject(PianoRollStore);
  private readonly live = inject(LiveMidiStore);
  /** Lanes that fit beside the grid, for keeping the scroll inside the keyboard. */
  readonly visibleLanes = input(40);

  protected readonly ruler = RULER_HEIGHT;
  protected readonly pitches = Pitches;
  protected readonly offset = computed(() => -this.store.firstLane() * this.store.laneHeight());
  /** Keys sounding on the Piano Roll's track. */
  protected readonly lit = computed<ReadonlySet<number>>(() =>
    this.store.track()?.id === this.live.target()?.id ? this.live.sounding() : new Set()
  );
  private pressed: number | null = null;

  protected black(pitch: number): boolean {
    return isBlackKey(pitch);
  }

  protected name(pitch: number): string {
    return noteName(pitch);
  }

  protected down(event: PointerEvent): void {
    if (event.button !== 0 || this.store.track()?.id !== this.live.target()?.id) {
      return;
    }

    const element = event.currentTarget as HTMLElement;
    const rect = element.getBoundingClientRect();
    const y = event.clientY - rect.top - RULER_HEIGHT;
    if (y < 0) {
      return;
    }

    element.setPointerCapture(event.pointerId);
    const pitch = pitchOfLane(this.store.firstLane() + y / this.store.laneHeight());
    if (pitch >= 0 && pitch <= 127) {
      this.pressed = pitch;
      // Further right on the key is louder, as on Logic's.
      const velocity = Math.round(50 + Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width)) * 77);
      this.live.noteOn(pitch, velocity);
    }
  }

  protected up(): void {
    if (this.pressed !== null) {
      this.live.noteOff(this.pressed);
      this.pressed = null;
    }
  }

  protected wheel(event: WheelEvent): void {
    event.preventDefault();
    this.store.scrollBy(0, event.deltaY, this.visibleLanes());
  }
}
