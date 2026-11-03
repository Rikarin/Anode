import {
  ChangeDetectionStrategy,
  Component,
  type ElementRef,
  booleanAttribute,
  computed,
  input,
  numberAttribute,
  output,
  signal,
  viewChild
} from '@angular/core';
import { pitchName } from './piano-keyboard';
import { type ScaleId, scaleNotes, touchVelocity } from './scales';

/**
 * The remote's touch keyboard (Logic Remote's, docs/11-phase-10.md): a row of equal keys for the notes of a scale from
 * a root, so every key is in key. Touch to play: each finger plays its own note (chords), sliding onto another key
 * plays it instead, and how low on the key it lands sets the velocity (top soft, bottom full). The root's keys are
 * marked. Every key is a button named by its note; Space or Return plays the focused one, arrows move between them.
 */
@Component({
  selector: 'an-touch-keyboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block select-none' },
  template: `
    <div
      #keys
      class="well flex h-full touch-none gap-px overflow-hidden rounded-md p-px"
      role="group"
      [attr.aria-label]="label()"
      [class.opacity-50]="disabled()"
      (pointerdown)="press($event)"
      (pointermove)="slide($event)"
      (pointerup)="release($event)"
      (pointercancel)="release($event)"
      (lostpointercapture)="release($event)"
    >
      @for (note of notes(); track note; let i = $index) {
        <button
          type="button"
          class="flex min-w-0 flex-1 flex-col items-center justify-end rounded-[4px] pb-2 text-[11px] font-semibold outline-offset-[-2px] focus-visible:outline-2"
          [class]="lit(note) ? 'key-lit' : 'key-white'"
          [attr.data-pitch]="note"
          [attr.aria-pressed]="lit(note)"
          [attr.tabindex]="note === focusPitch() ? 0 : -1"
          [disabled]="disabled()"
          (keydown)="keyDown($event, note, i)"
          (keyup)="keyUp($event, note)"
          (blur)="stop(note)"
        >
          <span [class.underline]="isRoot(note)" class="underline-offset-2">{{ name(note) }}</span>
        </button>
      }
    </div>
  `
})
export class AnTouchKeyboard {
  private readonly keysElement = viewChild.required<ElementRef<HTMLElement>>('keys');

  readonly scale = input<ScaleId>('major');
  /** The root's pitch class (0 C … 11 B). */
  readonly root = input(0, { transform: numberAttribute });
  /** The C the first octave starts from (48: C2). */
  readonly base = input(48, { transform: numberAttribute });
  readonly octaves = input(2, { transform: numberAttribute });
  /** Pitches to light (sounding on the track). */
  readonly sounding = input<ReadonlySet<number>>(new Set());
  readonly label = input('Keyboard');
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly noteOn = output<{ pitch: number; velocity: number }>();
  readonly noteOff = output<number>();

  protected readonly notes = computed(() => scaleNotes(this.scale(), this.root(), this.base(), this.octaves()));
  private readonly held = signal<ReadonlySet<number>>(new Set());
  protected readonly focusPitch = signal(60);
  /** The note each pointer (finger) holds. */
  private readonly pointers = new Map<number, number>();

  protected readonly name = pitchName;

  protected lit(pitch: number): boolean {
    return this.held().has(pitch) || this.sounding().has(pitch);
  }

  protected isRoot(pitch: number): boolean {
    return (((pitch - this.root()) % 12) + 12) % 12 === 0;
  }

  /** The note under a pointer and its velocity, from where it is on the keys. */
  private hit(event: PointerEvent): { pitch: number; velocity: number } | null {
    const rect = this.keysElement().nativeElement.getBoundingClientRect();
    const notes = this.notes();
    if (rect.width <= 0 || notes.length === 0) {
      return null;
    }
    const index = Math.floor(((event.clientX - rect.left) / rect.width) * notes.length);
    const pitch = notes[Math.min(notes.length - 1, Math.max(0, index))];
    return pitch === undefined
      ? null
      : { pitch, velocity: touchVelocity(rect.height > 0 ? (event.clientY - rect.top) / rect.height : 0.75) };
  }

  protected press(event: PointerEvent): void {
    if (this.disabled() || (event.pointerType === 'mouse' && event.button !== 0)) {
      return;
    }
    event.preventDefault();
    const hit = this.hit(event);
    if (!hit) {
      return;
    }
    try {
      this.keysElement().nativeElement.setPointerCapture(event.pointerId);
    } catch {
      // A pointer the browser no longer tracks: the key still plays.
    }
    this.lift(event.pointerId);
    this.focusPitch.set(hit.pitch);
    this.start(hit.pitch, hit.velocity);
    this.pointers.set(event.pointerId, hit.pitch);
  }

  /** A finger sliding onto another key plays it instead. */
  protected slide(event: PointerEvent): void {
    const held = this.pointers.get(event.pointerId);
    if (held === undefined) {
      return;
    }
    const hit = this.hit(event);
    if (!hit || hit.pitch === held) {
      return;
    }
    this.lift(event.pointerId);
    this.start(hit.pitch, hit.velocity);
    this.pointers.set(event.pointerId, hit.pitch);
  }

  protected release(event: PointerEvent): void {
    this.lift(event.pointerId);
  }

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

  protected keyDown(event: KeyboardEvent, pitch: number, index: number): void {
    if (event.key === ' ' || event.key === 'Enter') {
      event.preventDefault();
      if (!event.repeat) {
        this.start(pitch, 100);
      }
    } else if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
      event.preventDefault();
      const notes = this.notes();
      const next = notes[Math.min(notes.length - 1, Math.max(0, index + (event.key === 'ArrowLeft' ? -1 : 1)))];
      if (next !== undefined) {
        this.focusPitch.set(next);
        this.keysElement().nativeElement.querySelector<HTMLElement>(`[data-pitch="${next}"]`)?.focus();
      }
    }
  }

  protected keyUp(event: KeyboardEvent, pitch: number): void {
    if (event.key === ' ' || event.key === 'Enter') {
      event.preventDefault();
      this.stop(pitch);
    }
  }

  private start(pitch: number, velocity: number): void {
    if (this.held().has(pitch)) {
      return;
    }
    this.held.update(held => new Set(held).add(pitch));
    this.noteOn.emit({ pitch, velocity });
  }

  protected stop(pitch: number): void {
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
