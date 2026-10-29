import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

/** A column of the Transform grid: an event field (`position`, `pitch` …) and its heading. */
export interface TransformGridColumn {
  readonly field: string;
  readonly label: string;
}

/** A choice of a cell's operator menu, with how many values (`a`, `b`) it takes. */
export interface TransformGridOption {
  readonly value: string;
  readonly label: string;
  readonly operands: 0 | 1 | 2;
}

/** A value picked from a menu instead of typed (the Status column's event types). */
export interface TransformGridChoice {
  readonly value: string;
  readonly label: string;
}

/** One cell: its operator, its values as text, and which of them the owner could not read. */
export interface TransformGridCell {
  readonly op: string;
  readonly options: readonly TransformGridOption[];
  readonly a: string;
  readonly b: string;
  /** When given, `a` and `b` are menus of these instead of text fields. */
  readonly choices?: readonly TransformGridChoice[];
  /** The values that do not parse: shown as typed, marked invalid. */
  readonly invalid?: readonly ('a' | 'b')[];
  /** How a value is typed, e.g. `bar beat division tick`, for the fields' descriptions. */
  readonly hint?: string;
}

/**
 * A row of the grid: `id` (`condition`, `operation`) names its controls' test ids, `noun` their accessible names
 * ("Pitch condition"), and `cells` follow the columns (null: the field has no cell in this row).
 */
export interface TransformGridRow {
  readonly id: string;
  readonly label: string;
  readonly noun: string;
  readonly cells: readonly (TransformGridCell | null)[];
}

/** An edit of a cell: its operator (`part` `op`) or a value, as chosen or typed. */
export interface TransformGridChange {
  readonly row: string;
  readonly field: string;
  readonly part: 'op' | 'a' | 'b';
  readonly value: string;
}

const Field =
  'border-border bg-surface-inset text-foreground focus-visible:outline-focus h-6 w-full min-w-0 rounded-[5px] border px-1 text-[11px] focus-visible:outline-2';

/**
 * Logic's MIDI Transform grid: one column per event field and a row per stage ("Select Events by Conditions",
 * "Operations on Selected Events"). Each cell is an operator menu with one or two value fields under it, shown as the
 * operator needs them; values are text the owner formats and parses (positions as `bar beat division tick`), or menus
 * when a cell gives `choices`. A table of labelled native controls: Tab moves through them. Presentational: it emits
 * `changed` and shows what it is given. Test ids: `{testIdPrefix}-{row}-{field}` on the menu, `-a` and `-b` on the
 * values.
 */
@Component({
  selector: 'an-transform-grid',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <table class="w-full table-fixed border-separate border-spacing-x-1 border-spacing-y-1 text-[11px]">
      <caption class="sr-only">
        {{
          label()
        }}
      </caption>
      <thead>
        <tr>
          <th scope="col" class="w-28"><span class="sr-only">Stage</span></th>
          @for (column of columns(); track column.field) {
            <th
              scope="col"
              class="text-foreground-muted truncate px-0.5 text-start text-[10px] font-semibold tracking-wide uppercase"
            >
              {{ column.label }}
            </th>
          }
        </tr>
      </thead>
      <tbody>
        @for (row of rows(); track row.id) {
          <tr>
            <th scope="row" class="text-foreground pe-1 text-start align-top text-[11px] leading-tight font-semibold">
              {{ row.label }}
            </th>
            @for (column of columns(); track column.field; let c = $index) {
              <td class="align-top">
                @if (row.cells[c]; as cell) {
                  <div class="flex flex-col gap-0.5">
                    <select
                      [class]="field"
                      [attr.aria-label]="column.label + ' ' + row.noun"
                      [attr.data-testid]="testIdPrefix() + '-' + row.id + '-' + column.field"
                      [disabled]="disabled()"
                      (change)="emit(row.id, column.field, 'op', $event)"
                    >
                      @for (option of cell.options; track option.value) {
                        <option [value]="option.value" [selected]="option.value === cell.op">{{ option.label }}</option>
                      }
                    </select>
                    @for (part of parts(cell); track part) {
                      @if (cell.choices) {
                        <select
                          [class]="field"
                          [attr.aria-label]="valueLabel(column, row, part)"
                          [attr.data-testid]="testIdPrefix() + '-' + row.id + '-' + column.field + '-' + part"
                          [disabled]="disabled()"
                          (change)="emit(row.id, column.field, part, $event)"
                        >
                          @for (choice of cell.choices; track choice.value) {
                            <option [value]="choice.value" [selected]="choice.value === cell[part]">
                              {{ choice.label }}
                            </option>
                          }
                        </select>
                      } @else {
                        <input
                          type="text"
                          spellcheck="false"
                          autocomplete="off"
                          [class]="field + ' readout aria-invalid:border-transport-record'"
                          [attr.aria-label]="valueLabel(column, row, part)"
                          [attr.aria-description]="cell.hint ?? null"
                          [attr.title]="cell.hint ?? null"
                          [attr.aria-invalid]="cell.invalid?.includes(part) || null"
                          [attr.data-testid]="testIdPrefix() + '-' + row.id + '-' + column.field + '-' + part"
                          [disabled]="disabled()"
                          [value]="cell[part]"
                          (change)="emit(row.id, column.field, part, $event)"
                          (keydown.enter)="commit($event)"
                        />
                      }
                    }
                  </div>
                }
              </td>
            }
          </tr>
        }
      </tbody>
    </table>
  `
})
export class AnTransformGrid {
  readonly columns = input.required<readonly TransformGridColumn[]>();
  readonly rows = input.required<readonly TransformGridRow[]>();
  /** The table's accessible name. */
  readonly label = input('Transform conditions and operations');
  readonly testIdPrefix = input('transform');
  readonly disabled = input(false);
  readonly changed = output<TransformGridChange>();

  protected readonly field = Field;

  protected parts(cell: TransformGridCell): ('a' | 'b')[] {
    const operands = cell.options.find(o => o.value === cell.op)?.operands ?? 0;
    return operands === 2 ? ['a', 'b'] : operands === 1 ? ['a'] : [];
  }

  protected valueLabel(column: TransformGridColumn, row: TransformGridRow, part: 'a' | 'b'): string {
    return `${column.label} ${row.noun} ${part === 'a' ? 'value' : 'second value'}`;
  }

  protected emit(row: string, field: string, part: 'op' | 'a' | 'b', event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLSelectElement).value;
    this.changed.emit({ row, field, part, value });
  }

  /** Return commits a typed value (as leaving the field does) without leaving it. */
  protected commit(event: Event): void {
    const target = event.target as HTMLInputElement;
    target.dispatchEvent(new Event('change'));
  }
}
