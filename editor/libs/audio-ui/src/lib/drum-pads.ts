import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, output, signal } from '@angular/core';
import { pitchName } from './piano-keyboard';
import { type DrumPadInfo, padRows, touchVelocity } from './scales';

/**
 * The remote's drum pads (Logic Remote's, docs/11-phase-10.md): a 4 × 4 grid, lowest note at the bottom left, each pad
 * named by its sound and note and tinted by its colour. Each finger plays its own pad (several at once), how low on the
 * pad it lands sets the velocity, and a pad lights while held or sounding. Pads are buttons: Space or Return plays the
 * focused one.
 */
@Component({
  selector: 'an-drum-pads',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block select-none' },
  template: `
    <div
      class="well grid h-full touch-none grid-cols-4 gap-1.5 rounded-md p-1.5"
      role="group"
      [attr.aria-label]="label()"
      [class.opacity-50]="disabled()"
    >
      @for (row of rows(); track $index) {
        @for (pad of row; track $index) {
          @if (pad) {
            <button
              type="button"
              class="bezel relative flex min-h-11 flex-col items-start justify-between overflow-hidden rounded-md p-2 text-start"
              [class.key-lit]="lit(pad.note)"
              [class.text-foreground]="!lit(pad.note)"
              [attr.data-note]="pad.note"
              [attr.aria-pressed]="lit(pad.note)"
              [disabled]="disabled()"
              (pointerdown)="press($event, pad.note)"
              (pointerup)="release($event)"
              (pointercancel)="release($event)"
              (lostpointercapture)="release($event)"
              (keydown)="keyDown($event, pad.note)"
              (keyup)="keyUp($event, pad.note)"
              (blur)="stop(pad.note)"
            >
              <span
                class="absolute inset-x-0 top-0 h-1"
                aria-hidden="true"
                [style.background]="pad.color || 'var(--color-primary)'"
              ></span>
              <span class="text-[12px] leading-tight font-semibold">{{ pad.name }}</span>
              <span class="readout text-[10px]">{{ name(pad.note) }}</span>
            </button>
          } @else {
            <span aria-hidden="true"></span>
          }
        }
      }
    </div>
  `
})
export class AnDrumPads {
  readonly pads = input<readonly DrumPadInfo[]>([]);
  readonly sounding = input<ReadonlySet<number>>(new Set());
  readonly label = input('Drum pads');
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly noteOn = output<{ pitch: number; velocity: number }>();
  readonly noteOff = output<number>();

  protected readonly rows = computed(() => padRows(this.pads().slice(0, 16)));
  private readonly held = signal<ReadonlySet<number>>(new Set());
  private readonly pointers = new Map<number, number>();
  protected readonly name = pitchName;

  protected lit(note: number): boolean {
    return this.held().has(note) || this.sounding().has(note);
  }

  protected press(event: PointerEvent, note: number): void {
    if (this.disabled() || (event.pointerType === 'mouse' && event.button !== 0)) {
      return;
    }
    event.preventDefault();
    const pad = event.currentTarget as HTMLElement;
    try {
      pad.setPointerCapture(event.pointerId);
    } catch {
      // A pointer the browser no longer tracks: the pad still plays.
    }
    const rect = pad.getBoundingClientRect();
    this.pointers.set(event.pointerId, note);
    this.start(note, touchVelocity(rect.height > 0 ? (event.clientY - rect.top) / rect.height : 0.75));
  }

  protected release(event: PointerEvent): void {
    const note = this.pointers.get(event.pointerId);
    if (note === undefined) {
      return;
    }
    this.pointers.delete(event.pointerId);
    if (![...this.pointers.values()].includes(note)) {
      this.stop(note);
    }
  }

  protected keyDown(event: KeyboardEvent, note: number): void {
    if ((event.key === ' ' || event.key === 'Enter') && !event.repeat) {
      event.preventDefault();
      this.start(note, 100);
    }
  }

  protected keyUp(event: KeyboardEvent, note: number): void {
    if (event.key === ' ' || event.key === 'Enter') {
      event.preventDefault();
      this.stop(note);
    }
  }

  private start(note: number, velocity: number): void {
    if (this.held().has(note)) {
      return;
    }
    this.held.update(held => new Set(held).add(note));
    this.noteOn.emit({ pitch: note, velocity });
  }

  protected stop(note: number): void {
    if (!this.held().has(note)) {
      return;
    }
    this.held.update(held => {
      const next = new Set(held);
      next.delete(note);
      return next;
    });
    this.noteOff.emit(note);
  }
}
