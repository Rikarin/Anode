import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';

/** A chord as the lane draws it: its symbol at a place, in CSS pixels from the lane's left edge. */
export interface ChordLaneChord {
  id: string;
  /** "Am7", "G/B". */
  symbol: string;
  /** Its spoken name and position ("A minor 7th, bar 2"). */
  label: string;
  x: number;
  width: number;
  selected?: boolean;
}

/** A key and scale change on the lane's top row. */
export interface ChordLaneScale {
  index: number;
  /** "C major", "D Dorian". */
  text: string;
  x: number;
}

/** Where on the lane something happened: an element to anchor an editor to, and its x. */
export interface ChordLaneTarget {
  id: string;
  element: HTMLElement;
}

/**
 * The chord track's lane (Phase 11): a thin row of key and scale changes over the chords, each chord a 4 px-rounded
 * block from its tick to the next with its symbol. Click a chord (or press Return) to edit it, drag it to move it,
 * ←/→ move it by a step, Delete removes it; double-click an empty stretch to add a chord or a key change there. The
 * owner turns pixels into ticks and edits.
 */
@Component({
  selector: 'an-chord-lane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'relative block h-full select-none',
    role: 'group',
    '[attr.aria-label]': 'label()',
    '(pointermove)': 'move($event)',
    '(pointerup)': 'up($event)',
    '(pointercancel)': 'drag.set(null)'
  },
  template: `
    <div class="absolute inset-x-0 top-0 h-3" data-row="scales" (dblclick)="addScale($event)">
      @for (scale of scales(); track scale.index) {
        <button
          type="button"
          class="text-foreground-muted hover:text-foreground focus-visible:outline-focus absolute top-0 h-3 border-s-2 border-(--lit) ps-1 text-[9px] leading-3 font-semibold focus-visible:outline-2"
          [style.left.px]="scale.x"
          [attr.aria-label]="'Key ' + scale.text"
          (click)="openScale(scale.index, $event)"
          (dblclick)="$event.stopPropagation()"
        >
          {{ scale.text }}
        </button>
      }
    </div>
    <div class="absolute inset-x-0 top-3 bottom-0.5" data-row="chords" (dblclick)="addChord($event)">
      @for (chord of chords(); track chord.id) {
        <button
          type="button"
          class="text-foreground focus-visible:outline-focus absolute top-0 bottom-0 truncate rounded-[4px] px-1.5 text-start text-[11px] font-semibold shadow-[inset_0_0_0_1px_var(--hairline-strong)] focus-visible:outline-2"
          [class.bg-surface-raised]="!chord.selected"
          [class.bg-primary/25]="chord.selected"
          [class.opacity-70]="drag()?.id === chord.id && drag()?.moved"
          [style.left.px]="chord.x + (drag()?.id === chord.id ? (drag()?.dx ?? 0) : 0)"
          [style.width.px]="chord.width"
          [attr.aria-label]="chord.symbol + ', ' + chord.label"
          [attr.aria-pressed]="chord.selected ?? false"
          [attr.data-chord]="chord.symbol"
          (pointerdown)="press(chord.id, $event)"
          (dblclick)="$event.stopPropagation()"
          (keydown)="key(chord.id, $event)"
        >
          {{ chord.symbol }}
        </button>
      }
    </div>
  `
})
export class AnChordLane {
  readonly label = input('Chords');
  readonly chords = input<readonly ChordLaneChord[]>([]);
  readonly scales = input<readonly ChordLaneScale[]>([]);

  /** A chord was clicked or Return pressed on it: edit it. */
  readonly opened = output<ChordLaneTarget>();
  /** A chord was dragged this far (pixels). */
  readonly moved = output<{ id: string; dx: number }>();
  /** ←/→ on a chord: move it by a step (−1 or 1; ⇧: a bar, ±4). */
  readonly nudged = output<{ id: string; steps: number }>();
  readonly removed = output<string>();
  /** A double-click on the chords' row: add a chord at this x. */
  readonly added = output<number>();
  /** A key and scale change was clicked: edit it (its index as the id). */
  readonly scaleOpened = output<ChordLaneTarget>();
  /** A double-click on the scales' row: add a key and scale change at this x. */
  readonly scaleAdded = output<number>();

  protected readonly drag = signal<{ id: string; x: number; dx: number; moved: boolean; element: HTMLElement } | null>(
    null
  );

  protected press(id: string, event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    event.stopPropagation();
    const element = event.currentTarget as HTMLElement;
    element.setPointerCapture(event.pointerId);
    this.drag.set({ id, x: event.clientX, dx: 0, moved: false, element });
  }

  protected move(event: PointerEvent): void {
    const drag = this.drag();
    if (!drag) {
      return;
    }
    const dx = event.clientX - drag.x;
    if (drag.moved || Math.abs(dx) >= 3) {
      this.drag.set({ ...drag, dx, moved: true });
    }
  }

  protected up(event: PointerEvent): void {
    const drag = this.drag();
    this.drag.set(null);
    if (!drag) {
      return;
    }
    if (drag.moved) {
      this.moved.emit({ id: drag.id, dx: event.clientX - drag.x });
    } else {
      this.opened.emit({ id: drag.id, element: drag.element });
    }
  }

  protected key(id: string, event: KeyboardEvent): void {
    const steps = { ArrowLeft: -1, ArrowRight: 1 }[event.key];
    if (steps !== undefined) {
      this.nudged.emit({ id, steps: event.shiftKey ? steps * 4 : steps });
    } else if (event.key === 'Delete' || event.key === 'Backspace') {
      this.removed.emit(id);
    } else if (event.key === 'Enter' || event.key === ' ') {
      this.opened.emit({ id, element: event.currentTarget as HTMLElement });
    } else {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
  }

  protected openScale(index: number, event: MouseEvent): void {
    this.scaleOpened.emit({ id: String(index), element: event.currentTarget as HTMLElement });
  }

  protected addChord(event: MouseEvent): void {
    event.stopPropagation();
    this.added.emit(this.offset(event));
  }

  protected addScale(event: MouseEvent): void {
    event.stopPropagation();
    this.scaleAdded.emit(this.offset(event));
  }

  private offset(event: MouseEvent): number {
    const row = event.currentTarget as HTMLElement;
    return event.clientX - row.getBoundingClientRect().left;
  }
}
