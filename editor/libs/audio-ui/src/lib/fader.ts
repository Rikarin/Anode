import {
  ChangeDetectionStrategy,
  Component,
  type ElementRef,
  booleanAttribute,
  computed,
  input,
  model,
  output,
  viewChild
} from '@angular/core';
import { MaxFaderDb, SilenceDb, faderDb, faderPosition, formatDb } from './taper';

/** Scale marks beside the travel, in dB. */
const Marks = [6, 0, -6, -12, -20, -30, -45, -60];

/**
 * A channel fader with a dB taper (0 dB at three quarters of the travel, −∞ at the bottom). An ARIA slider: arrows
 * step 1 dB (⇧: 0.1 dB), Page keys 6 dB, Home goes to −∞ and End to +6 dB. Drag the cap or the track vertically
 * (⇧ for fine); double-click or ⌥-click sets 0 dB. `value` changes while it moves; `committed` fires when a gesture
 * ends.
 */
@Component({
  selector: 'an-fader',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'inline-flex h-52 flex-col items-center gap-1 select-none'
  },
  template: `
    <span
      class="readout text-foreground bg-surface-inset border-hairline min-w-10 rounded-[4px] border px-1 py-px text-center text-[10px] font-medium shadow-[inset_0_1px_1px_rgb(0_0_0/0.2)]"
      aria-hidden="true"
      >{{ shortDisplay() }}</span
    >
    <div class="relative flex min-h-0 flex-1 gap-0.5 py-2">
      <svg class="fill-foreground-subtle h-full w-6 overflow-visible text-[9px] font-medium" aria-hidden="true">
        @for (mark of marks; track mark) {
          <text
            x="15"
            text-anchor="end"
            dominant-baseline="middle"
            [class.fill-foreground]="mark === 0"
            [attr.y]="(1 - position(mark)) * 100 + '%'"
          >
            {{ mark > 0 ? '+' + mark : mark }}
          </text>
          <line
            x1="18"
            x2="23"
            class="stroke-foreground-subtle"
            stroke-width="1"
            [attr.y1]="(1 - position(mark)) * 100 + '%'"
            [attr.y2]="(1 - position(mark)) * 100 + '%'"
          />
        }
      </svg>
      <div
        #track
        role="slider"
        class="focus-visible:outline-focus relative w-7 cursor-ns-resize touch-none rounded-sm outline-offset-2 focus-visible:outline-2"
        [class.opacity-50]="disabled()"
        aria-orientation="vertical"
        [attr.tabindex]="disabled() ? -1 : 0"
        [attr.aria-label]="label()"
        [attr.aria-valuemin]="silence"
        [attr.aria-valuemax]="max"
        [attr.aria-valuenow]="rounded()"
        [attr.aria-valuetext]="display()"
        [attr.aria-disabled]="disabled() || null"
        [attr.title]="label() + ': ' + display() + ' (double-click for 0 dB)'"
        (pointerdown)="grab($event)"
        (pointermove)="drag($event)"
        (pointerup)="release($event)"
        (pointercancel)="release($event)"
        (dblclick)="reset()"
        (keydown)="key($event)"
      >
        <div class="well absolute -inset-y-1 left-1/2 w-1 -translate-x-1/2 rounded-full"></div>
        <div class="fader-cap absolute left-0 h-4.5 w-7 -translate-y-1/2" [style.top.%]="(1 - travel()) * 100"></div>
      </div>
    </div>
  `
})
export class AnFader {
  private readonly track = viewChild.required<ElementRef<HTMLElement>>('track');
  private dragStart: { y: number; position: number } | null = null;

  /** The level in dB (the contract's floor, −144, is −∞). */
  readonly value = model(0);
  readonly label = input('Volume');
  readonly disabled = input(false, { transform: booleanAttribute });
  /** A gesture ended (pointer up, a key, a reset): a good moment for an undo point. */
  readonly committed = output<number>();

  protected readonly marks = Marks;
  protected readonly silence = SilenceDb;
  protected readonly max = MaxFaderDb;
  protected readonly position = faderPosition;
  protected readonly travel = computed(() => faderPosition(this.value()));
  protected readonly rounded = computed(() => Math.round(this.value() * 10) / 10);
  protected readonly display = computed(() => formatDb(this.value()));
  protected readonly shortDisplay = computed(() => formatDb(this.value(), false));

  protected grab(event: PointerEvent): void {
    if (this.disabled() || event.button !== 0) {
      return;
    }

    if (event.altKey) {
      this.reset();
      return;
    }

    const element = this.track().nativeElement;
    element.setPointerCapture(event.pointerId);
    element.focus();
    // A click on the track away from the cap jumps there; on the cap it grabs it where it is.
    const rect = element.getBoundingClientRect();
    const clicked = rect.height > 0 ? 1 - (event.clientY - rect.top) / rect.height : this.travel();
    const position = Math.abs(clicked - this.travel()) * rect.height > 10 ? clicked : this.travel();
    this.dragStart = { y: event.clientY, position };
    this.setPosition(position);
  }

  protected drag(event: PointerEvent): void {
    if (!this.dragStart) {
      return;
    }

    const height = this.track().nativeElement.getBoundingClientRect().height || 160;
    const scale = event.shiftKey ? height * 10 : height;
    const position = Math.min(1, Math.max(0, this.dragStart.position + (this.dragStart.y - event.clientY) / scale));
    this.dragStart = { y: event.clientY, position };
    this.setPosition(position);
  }

  protected release(event: PointerEvent): void {
    if (this.dragStart) {
      this.dragStart = null;
      this.track().nativeElement.releasePointerCapture(event.pointerId);
      this.committed.emit(this.value());
    }
  }

  protected key(event: KeyboardEvent): void {
    if (this.disabled()) {
      return;
    }

    const db = Math.max(-60, this.value());
    const fine = event.shiftKey ? 0.1 : 1;
    const steps: Record<string, number> = {
      ArrowUp: fine,
      ArrowRight: fine,
      ArrowDown: -fine,
      ArrowLeft: -fine,
      PageUp: 6,
      PageDown: -6
    };
    const step = steps[event.key];
    if (step !== undefined) {
      const next = db + step;
      this.value.set(next < -60 ? SilenceDb : Math.min(MaxFaderDb, Math.round(next * 10) / 10));
    } else if (event.key === 'Home' || event.key === 'End') {
      this.value.set(event.key === 'Home' ? SilenceDb : MaxFaderDb);
    } else {
      return;
    }

    event.preventDefault();
    this.committed.emit(this.value());
  }

  protected reset(): void {
    if (!this.disabled()) {
      this.value.set(0);
      this.committed.emit(0);
    }
  }

  private setPosition(position: number): void {
    this.value.set(Math.round(faderDb(position) * 10) / 10);
  }
}
