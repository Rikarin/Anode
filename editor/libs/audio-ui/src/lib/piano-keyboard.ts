import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  input,
  numberAttribute,
  output,
  signal
} from '@angular/core';

const Names = ['C', 'C♯', 'D', 'D♯', 'E', 'F', 'F♯', 'G', 'G♯', 'A', 'A♯', 'B'];
const Black = new Set([1, 3, 6, 8, 10]);
/** Where each black key sits, in white-key widths from its octave's C. */
const BlackOffset: Readonly<Record<number, number>> = { 1: 0.62, 3: 1.72, 6: 3.6, 8: 4.66, 10: 5.72 };

/** A pitch's name as Logic numbers octaves: middle C (60) is C3. */
export function pitchName(pitch: number): string {
  return `${Names[((pitch % 12) + 12) % 12] ?? '?'}${Math.floor(pitch / 12) - 2}`;
}

/** A key as drawn: its pitch, colour and box in white-key widths. */
export interface KeyboardKey {
  pitch: number;
  black: boolean;
  /** Left edge and width in white-key widths from the keyboard's first white key. */
  left: number;
  width: number;
  name: string;
}

/** The keys of a keyboard from `from` (moved down to a white key) over `octaves`, plus the top C. */
export function keyboardKeys(from: number, octaves: number): KeyboardKey[] {
  let start = Math.max(0, Math.min(127, from));
  while (Black.has(start % 12)) {
    start--;
  }
  const end = Math.min(127, start + 12 * octaves);
  const keys: KeyboardKey[] = [];
  let whites = 0;
  for (let pitch = start; pitch <= end; pitch++) {
    const degree = pitch % 12;
    if (Black.has(degree)) {
      const octaveStart = whites - whiteIndex(degree);
      keys.push({
        pitch,
        black: true,
        left: octaveStart + (BlackOffset[degree] ?? 0),
        width: 0.62,
        name: pitchName(pitch)
      });
    } else {
      keys.push({ pitch, black: false, left: whites, width: 1, name: pitchName(pitch) });
      whites++;
    }
  }
  return keys;
}

/** How many white keys of its octave come before a black key's degree. */
function whiteIndex(degree: number): number {
  return [0, 1, 1, 2, 2, 3, 4, 4, 5, 5, 6, 6][degree] ?? 0;
}

/**
 * An on-screen keyboard (Logic's Keyboard window). Press a key to play it: lower on the key is louder, as on Logic's;
 * drag across keys for a glissando. `sounding` lights keys (what the engine reports playing, held keys too). Every key
 * is a button named by its note (C3 is middle C), pressed with Space or Enter; the arrow keys move between keys.
 */
@Component({
  selector: 'an-piano-keyboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'block select-none',
    role: 'group',
    '[attr.aria-label]': 'label()'
  },
  template: `
    <div
      class="relative h-full touch-none"
      [style.width.px]="whiteCount() * keyWidth()"
      (pointerup)="release($event)"
      (pointercancel)="release($event)"
      (lostpointercapture)="lost($event)"
    >
      @for (key of keys(); track key.pitch) {
        <button
          type="button"
          class="focus-visible:outline-focus absolute top-0 rounded-b-[3px] border focus-visible:z-20 focus-visible:outline-2 focus-visible:-outline-offset-2"
          [class]="key.black ? blackClass(key.pitch) : whiteClass(key.pitch)"
          [style.left.px]="key.left * keyWidth()"
          [style.width.px]="key.width * keyWidth()"
          [style.height]="key.black ? '62%' : '100%'"
          [attr.aria-label]="key.name"
          [attr.aria-pressed]="lit(key.pitch)"
          [attr.tabindex]="key.pitch === focusPitch() ? 0 : -1"
          [attr.data-pitch]="key.pitch"
          [disabled]="disabled()"
          (pointerdown)="press($event, key.pitch)"
          (pointerenter)="slide($event, key.pitch)"
          (keydown)="keyDown($event, key.pitch)"
          (keyup)="keyUp($event, key.pitch)"
          (blur)="keyUp(null, key.pitch)"
        >
          @if (!key.black && key.pitch % 12 === 0 && !lit(key.pitch)) {
            <span aria-hidden="true" class="pointer-events-none absolute inset-x-0 bottom-1 text-[9px] font-medium">
              {{ key.name }}
            </span>
          }
        </button>
      }
    </div>
  `
})
export class AnPianoKeyboard {
  /** The lowest key (moved down to a white key). */
  readonly from = input(48, { transform: numberAttribute });
  readonly octaves = input(2, { transform: numberAttribute });
  readonly keyWidth = input(22, { transform: numberAttribute });
  /** Pitches to light. */
  readonly sounding = input<ReadonlySet<number>>(new Set());
  readonly label = input('Keyboard');
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly noteOn = output<{ pitch: number; velocity: number }>();
  readonly noteOff = output<number>();

  protected readonly keys = computed(() => {
    const keys = keyboardKeys(this.from(), this.octaves());
    // White keys first, so the black ones sit on top.
    return [...keys.filter(k => !k.black), ...keys.filter(k => k.black)];
  });
  protected readonly whiteCount = computed(() => this.keys().filter(k => !k.black).length);
  private readonly held = signal<ReadonlySet<number>>(new Set());
  protected readonly focusPitch = signal(60);
  /** The key each pointer holds: two fingers (or two pointers) hold a chord. */
  private readonly pointers = new Map<number, number>();

  protected lit(pitch: number): boolean {
    return this.sounding().has(pitch) || this.held().has(pitch);
  }

  protected whiteClass(pitch: number): string {
    return this.lit(pitch) ? 'key-lit z-0' : 'key-white z-0';
  }

  protected blackClass(pitch: number): string {
    return this.lit(pitch) ? 'key-lit z-10' : 'key-black z-10';
  }

  protected press(event: PointerEvent, pitch: number): void {
    if (this.disabled() || event.button !== 0) {
      return;
    }

    event.preventDefault();
    const target = event.currentTarget as HTMLElement;
    try {
      target.parentElement?.setPointerCapture(event.pointerId);
    } catch {
      // A pointer the browser no longer tracks: the key still plays, without capture.
    }
    this.focusPitch.set(pitch);
    this.lift(event.pointerId);
    this.start(pitch, velocityAt(event, target));
    this.pointers.set(event.pointerId, pitch);
  }

  /** A glissando: moving onto another key while pressed plays it instead. */
  protected slide(event: PointerEvent, pitch: number): void {
    const held = this.pointers.get(event.pointerId);
    if (held === undefined || held === pitch || event.buttons === 0) {
      return;
    }

    this.lift(event.pointerId);
    this.start(pitch, velocityAt(event, event.currentTarget as HTMLElement));
    this.pointers.set(event.pointerId, pitch);
  }

  protected release(event: PointerEvent): void {
    try {
      (event.currentTarget as HTMLElement).releasePointerCapture(event.pointerId);
    } catch {
      // Not captured (see press).
    }
    this.lift(event.pointerId);
  }

  protected lost(event: PointerEvent): void {
    this.lift(event.pointerId);
  }

  /** Ends the key a pointer holds, unless another pointer holds it too. */
  private lift(pointerId: number): void {
    const pitch = this.pointers.get(pointerId);
    if (pitch === undefined) {
      return;
    }
    this.pointers.delete(pointerId);
    if (![...this.pointers.values()].includes(pitch)) {
      this.stop(pitch);
    }
  }

  protected keyDown(event: KeyboardEvent, pitch: number): void {
    if (event.key === ' ' || event.key === 'Enter') {
      event.preventDefault();
      event.stopPropagation();
      if (!event.repeat) {
        this.start(pitch, 100);
      }
    } else if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
      event.preventDefault();
      event.stopPropagation();
      const pitches = this.keys()
        .map(k => k.pitch)
        .sort((a, b) => a - b);
      const at = pitches.indexOf(pitch) + (event.key === 'ArrowLeft' ? -1 : 1);
      const next = pitches[Math.min(pitches.length - 1, Math.max(0, at))] ?? pitch;
      this.focusPitch.set(next);
      const host = (event.currentTarget as HTMLElement).parentElement;
      host?.querySelector<HTMLElement>(`[data-pitch="${next}"]`)?.focus();
    }
  }

  protected keyUp(event: KeyboardEvent | null, pitch: number): void {
    if (event && event.key !== ' ' && event.key !== 'Enter') {
      return;
    }
    event?.preventDefault();
    this.stop(pitch);
  }

  private start(pitch: number, velocity: number): void {
    if (this.held().has(pitch)) {
      return;
    }
    this.held.update(held => new Set(held).add(pitch));
    this.noteOn.emit({ pitch, velocity });
  }

  private stop(pitch: number): void {
    if (!this.held().has(pitch)) {
      return;
    }
    this.held.update(held => {
      const next = new Set(held);
      next.delete(pitch);
      return next;
    });
    this.noteOff.emit(pitch);
  }
}

/** Velocity from where a key was struck: its top plays softly (40), its bottom fully (127). */
function velocityAt(event: PointerEvent, key: HTMLElement): number {
  const rect = key.getBoundingClientRect();
  const fraction = rect.height > 0 ? (event.clientY - rect.top) / rect.height : 0.75;
  return Math.round(40 + Math.min(1, Math.max(0, fraction)) * 87);
}
