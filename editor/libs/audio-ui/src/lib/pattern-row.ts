import { ChangeDetectionStrategy, Component, booleanAttribute, input, numberAttribute, output } from '@angular/core';
import { AnStepCell, type StepEditMode, type StepValues } from './step-cell';

/** A step's edit in a row: which step, and its new value in the row's edit mode. */
export interface StepEdit {
  index: number;
  value: number;
}

/**
 * A row of Logic's Step Sequencer: a row header (projected: the row's name and its controls) and one step cell per
 * step, on a shared grid so rows of different lengths line up from the left. A `row` in a `grid`, named by the row;
 * ←/→ move between its steps. `playhead` lights the step playing; a muted row's steps are dimmed.
 */
@Component({
  selector: 'an-pattern-row',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnStepCell],
  host: {
    role: 'row',
    class: 'border-hairline flex h-7 items-center border-b',
    '[attr.aria-label]': 'name()',
    '[attr.data-selected]': 'selected() || null',
    '[class.bg-surface-raised]': 'selected()',
    '(keydown)': 'arrow($event)'
  },
  template: `
    <div role="rowheader" class="flex h-full shrink-0 items-center gap-1 px-1.5" [style.width.px]="headerWidth()">
      <ng-content />
    </div>
    <div class="flex items-center gap-0.5 px-1" [class.opacity-45]="muted()">
      @for (step of steps(); track $index) {
        <div role="gridcell" class="flex" [class.ms-1]="$index > 0 && $index % stepsPerBeat() === 0">
          <an-step-cell
            [step]="step"
            [mode]="mode()"
            [label]="name() + ' step ' + ($index + 1)"
            [playing]="playhead() === $index"
            [beat]="$index % stepsPerBeat() === 0"
            [disabled]="disabled()"
            (toggled)="toggled.emit($index)"
            (valueChange)="valueChange.emit({ index: $index, value: $event })"
            (committed)="committed.emit({ index: $index, value: $event })"
          />
        </div>
      }
    </div>
  `
})
export class AnPatternRow {
  /** The row's name ("Kick", "C3", "Degree 1"): the row's accessible name and its steps' prefix. */
  readonly name = input.required<string>();
  readonly steps = input.required<readonly StepValues[]>();
  readonly mode = input<StepEditMode>('onOff');
  /** The step playing now, or null. */
  readonly playhead = input<number | null>(null);
  /** Steps per beat: a beat's first step is drawn lighter and set a little apart. */
  readonly stepsPerBeat = input(4, { transform: numberAttribute });
  /** The row header's width in CSS pixels, the same for every row so the steps line up. */
  readonly headerWidth = input(240, { transform: numberAttribute });
  readonly muted = input(false, { transform: booleanAttribute });
  readonly selected = input(false, { transform: booleanAttribute });
  readonly disabled = input(false, { transform: booleanAttribute });
  /** On/Off mode: step `index` was clicked. */
  readonly toggled = output<number>();
  /** A value mode: a step's value during a drag. */
  readonly valueChange = output<StepEdit>();
  /** A value mode: a step's value at the end of a drag or after a key press. */
  readonly committed = output<StepEdit>();

  /** ←/→ move focus to the step before or after. */
  protected arrow(event: KeyboardEvent): void {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') {
      return;
    }
    const cells = [
      ...(event.currentTarget as HTMLElement).querySelectorAll<HTMLElement>(
        '[role="gridcell"] button, [role="gridcell"] [role="slider"]'
      )
    ];
    const index = cells.indexOf(event.target as HTMLElement);
    const next = cells[index + (event.key === 'ArrowRight' ? 1 : -1)];
    if (index >= 0 && next) {
      event.preventDefault();
      next.focus();
    }
  }
}
