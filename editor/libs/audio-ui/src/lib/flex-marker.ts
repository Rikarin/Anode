import { ChangeDetectionStrategy, Component, booleanAttribute, input, output } from '@angular/core';

/** A key nudge of a flex marker: one step left (−1) or right (1), `big` with ⇧. */
export interface FlexNudge {
  direction: -1 | 1;
  big: boolean;
}

/**
 * A flex marker (Logic's Flex Time): a vertical line through a region's waveform with a handle at its top, where a
 * file position is pinned to a time; the audio either side stretches as it moves. The owner places it (its host's
 * `left`, `height`) and keeps the maths. It is a slider the keyboard nudges (←/→, ⇧ for a bigger step) and Delete or
 * a double-click removes; the owner handles pointer drags on the whole region, so presses pass through to it.
 */
@Component({
  selector: 'an-flex-marker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'an-flex-marker group absolute top-0 block h-full w-0',
    '[class.an-flex-marker-active]': 'active()'
  },
  template: `
    <div
      class="focus-visible:outline-focus absolute top-0 -left-1.5 h-full w-3 cursor-ew-resize outline-none focus-visible:outline-2"
      role="slider"
      tabindex="0"
      data-testid="flex-marker"
      [attr.aria-label]="label()"
      [attr.aria-valuemin]="min()"
      [attr.aria-valuemax]="max()"
      [attr.aria-valuenow]="value()"
      [attr.aria-valuetext]="valueText()"
      (keydown)="key($event)"
      (dblclick)="remove.emit()"
    >
      <span class="an-flex-marker-line absolute top-0 left-1.5 h-full w-px -translate-x-1/2" aria-hidden="true"></span>
      <span
        class="an-flex-marker-handle absolute top-0 left-1.5 h-2.5 w-2.5 -translate-x-1/2 rounded-b-[3px]"
        aria-hidden="true"
      ></span>
    </div>
  `
})
export class AnFlexMarker {
  /** The accessible name ("Flex marker 2"). */
  readonly label = input('Flex marker');
  /** Where it pins its file position, in ticks from the region's start, and the range it can move in. */
  readonly value = input(0);
  readonly min = input(0);
  readonly max = input(0);
  /** The position as people read it ("3 2 1 1"). */
  readonly valueText = input('');
  /** Being dragged or just placed: drawn lit. */
  readonly active = input(false, { transform: booleanAttribute });

  readonly nudge = output<FlexNudge>();
  readonly remove = output();

  protected key(event: KeyboardEvent): void {
    switch (event.key) {
      case 'ArrowLeft':
      case 'ArrowRight':
        this.nudge.emit({ direction: event.key === 'ArrowLeft' ? -1 : 1, big: event.shiftKey });
        break;
      case 'Delete':
      case 'Backspace':
        this.remove.emit();
        break;
      default:
        return;
    }
    event.preventDefault();
    event.stopPropagation();
  }
}
