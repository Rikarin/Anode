import {
  ChangeDetectionStrategy,
  Component,
  type ElementRef,
  booleanAttribute,
  effect,
  input,
  output,
  viewChild
} from '@angular/core';

/** A cell of an event row, as text. */
export interface EventCell {
  /** The column's key ("position", "val"…): the editor's `data-testid` is `event-edit-<key>`. */
  key: string;
  /** The column's name: the cell's accessible name and its editor's. */
  label: string;
  text: string;
  editable: boolean;
  /** A choice cell (an articulation): its editor is a select of these options. */
  options?: readonly { value: string; label: string }[];
  /** The value the editor starts from; the text when left out. */
  value?: string;
}

/** A click on a row: which cell, and the selection modifiers held. */
export interface EventRowClick {
  key: string;
  shift: boolean;
  /** ⌘ (or Ctrl): toggle the row in or out of the selection. */
  toggle: boolean;
}

/** A cell's typed text, committed with Enter or by leaving it. */
export interface EventCellCommit {
  key: string;
  text: string;
}

/**
 * A row of Logic's Event List: one event's cells (position, status, channel, number, value, length, articulation) on
 * the table's column grid. Presentational: a `row` of `gridcell`s; the owner says which cell is being edited, and the
 * row turns it into an input (or a select for a choice) that commits on Enter or blur and cancels on Escape. A click
 * on a cell selects the row (`pick`) and, on an editable cell without modifiers, asks to edit it (`edit`); Enter on
 * a focused cell asks too. ←/→ move between cells; ↑/↓ and Delete come out as `move` and `remove` for the owner.
 * `playing` marks the row at the playhead, `selected` the selection.
 */
@Component({
  selector: 'an-event-row',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    role: 'row',
    class: 'border-hairline text-foreground grid h-6 items-center border-b text-[12px]',
    '[style.grid-template-columns]': 'columns()',
    '[attr.aria-selected]': 'selected()',
    '[attr.data-selected]': 'selected() || null',
    '[attr.data-playing]': 'playing() || null',
    '[class.bg-primary/25]': 'selected()',
    '[class.bg-surface-raised]': 'playing() && !selected()'
  },
  template: `
    @for (cell of cells(); track cell.key; let i = $index) {
      <div
        role="gridcell"
        class="focus-visible:outline-focus flex h-full min-w-0 items-center px-1.5 focus-visible:outline-2 focus-visible:-outline-offset-2"
        [class.readout]="cell.key !== 'status' && cell.key !== 'articulation'"
        [class.cursor-text]="cell.editable"
        [class.text-foreground-muted]="cell.key === 'status' && !selected()"
        [attr.data-column]="cell.key"
        [attr.aria-label]="cell.label + ' ' + (cell.text || 'none')"
        [attr.aria-readonly]="!cell.editable"
        [attr.tabindex]="active() && i === activeCell() ? 0 : -1"
        (click)="click(cell, $event)"
        (keydown)="key($event)"
      >
        @if (editing() === cell.key && cell.editable) {
          @if (cell.options) {
            <select
              #editor
              class="bg-surface-inset text-foreground border-primary h-5 w-full min-w-0 rounded-[4px] border px-1 text-[12px] outline-none"
              [attr.aria-label]="cell.label"
              [attr.data-testid]="'event-edit-' + cell.key"
              [value]="cell.value ?? cell.text"
              (change)="choose(cell, $event)"
              (keydown)="editorKey(cell, $event)"
              (blur)="cancel()"
              (click)="$event.stopPropagation()"
            >
              @for (option of cell.options; track option.value) {
                <option [value]="option.value" [selected]="option.value === (cell.value ?? cell.text)">
                  {{ option.label }}
                </option>
              }
            </select>
          } @else {
            <input
              #editor
              type="text"
              class="bg-surface-inset text-foreground border-primary readout h-5 w-full min-w-0 rounded-[4px] border px-1 text-[12px] outline-none"
              spellcheck="false"
              autocomplete="off"
              [attr.aria-label]="cell.label"
              [attr.data-testid]="'event-edit-' + cell.key"
              [value]="cell.value ?? cell.text"
              (keydown)="editorKey(cell, $event)"
              (blur)="blur(cell, $event)"
              (click)="$event.stopPropagation()"
            />
          }
        } @else {
          <span class="truncate">{{ cell.text }}</span>
        }
      </div>
    }
  `
})
export class AnEventRow {
  readonly cells = input.required<readonly EventCell[]>();
  /** The CSS grid template of the table's columns, the same for the header and every row. */
  readonly columns = input.required<string>();
  readonly selected = input(false, { transform: booleanAttribute });
  /** The row at the playhead while following it. */
  readonly playing = input(false, { transform: booleanAttribute });
  /** The key of the cell being edited, or null. */
  readonly editing = input<string | null>(null);
  /** This row holds the table's keyboard focus stop (its {@link activeCell}). */
  readonly active = input(false, { transform: booleanAttribute });
  /** The index of the cell that takes focus when the row is active. */
  readonly activeCell = input(0);

  readonly pick = output<EventRowClick>();
  /** Asks to edit the cell with this key. */
  readonly edit = output<string>();
  readonly commit = output<EventCellCommit>();
  /** The edit was cancelled (Escape). */
  readonly cancelled = output();
  /** ↑ (-1) or ↓ (+1) on a cell: the owner moves the selection. */
  readonly move = output<{ by: number; column: number; shift: boolean }>();
  /** Delete or Backspace on a cell. */
  readonly remove = output();

  /** Set while an editor closes by Enter or Escape, so its blur does not commit again. */
  private closing = false;
  private readonly editor = viewChild<ElementRef<HTMLInputElement | HTMLSelectElement>>('editor');

  constructor() {
    // An editor that opens takes the focus, its text selected.
    effect(() => {
      const element = this.editor()?.nativeElement;
      if (element) {
        this.closing = false;
        element.focus();
        if (element instanceof HTMLInputElement) {
          element.select();
        }
      }
    });
  }

  protected click(cell: EventCell, event: MouseEvent): void {
    const toggle = event.metaKey || event.ctrlKey;
    this.pick.emit({ key: cell.key, shift: event.shiftKey, toggle });
    if (cell.editable && !toggle && !event.shiftKey) {
      this.edit.emit(cell.key);
    }
  }

  protected key(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    if (target.getAttribute('role') !== 'gridcell') {
      return;
    }
    const cells = [...(target.parentElement as HTMLElement).querySelectorAll<HTMLElement>('[role="gridcell"]')];
    const index = cells.indexOf(target);
    const cell = this.cells()[index];
    switch (event.key) {
      case 'ArrowLeft':
      case 'ArrowRight': {
        const next = cells[index + (event.key === 'ArrowRight' ? 1 : -1)];
        if (next) {
          target.tabIndex = -1;
          next.tabIndex = 0;
          next.focus();
        }
        break;
      }
      case 'ArrowUp':
      case 'ArrowDown':
        this.move.emit({ by: event.key === 'ArrowUp' ? -1 : 1, column: index, shift: event.shiftKey });
        break;
      case 'Enter':
      case 'F2':
        if (cell?.editable) {
          this.edit.emit(cell.key);
        }
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

  protected editorKey(cell: EventCell, event: KeyboardEvent): void {
    event.stopPropagation();
    if (event.key === 'Enter') {
      event.preventDefault();
      this.closing = true;
      this.commit.emit({ key: cell.key, text: (event.target as HTMLInputElement | HTMLSelectElement).value });
    } else if (event.key === 'Escape') {
      event.preventDefault();
      this.closing = true;
      this.cancelled.emit();
    }
  }

  protected blur(cell: EventCell, event: FocusEvent): void {
    if (this.closing) {
      this.closing = false;
      return;
    }
    this.commit.emit({ key: cell.key, text: (event.target as HTMLInputElement).value });
  }

  protected choose(cell: EventCell, event: Event): void {
    this.closing = true;
    this.commit.emit({ key: cell.key, text: (event.target as HTMLSelectElement).value });
  }

  protected cancel(): void {
    if (this.closing) {
      this.closing = false;
      return;
    }
    this.cancelled.emit();
  }
}
