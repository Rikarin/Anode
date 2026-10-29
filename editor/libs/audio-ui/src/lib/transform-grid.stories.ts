import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, fn, userEvent, within } from 'storybook/test';
import {
  AnTransformGrid,
  type TransformGridCell,
  type TransformGridColumn,
  type TransformGridOption,
  type TransformGridRow
} from './transform-grid';

const columns: TransformGridColumn[] = [
  { field: 'status', label: 'Status' },
  { field: 'position', label: 'Position' },
  { field: 'length', label: 'Length' },
  { field: 'pitch', label: 'Pitch' },
  { field: 'velocity', label: 'Velocity' }
];

const conditionOps: TransformGridOption[] = [
  { value: 'all', label: 'All', operands: 0 },
  { value: 'equal', label: 'Equal', operands: 1 },
  { value: 'greaterEqual', label: 'Greater or Equal', operands: 1 },
  { value: 'inside', label: 'Inside', operands: 2 }
];

const operationOps: TransformGridOption[] = [
  { value: 'thru', label: 'Thru', operands: 0 },
  { value: 'fix', label: 'Fix', operands: 1 },
  { value: 'plusMinusRandom', label: '± Random', operands: 1 },
  { value: 'range', label: 'Range', operands: 2 }
];

function cell(op: string, a = '', b = '', extra: Partial<TransformGridCell> = {}): TransformGridCell {
  return {
    op,
    a,
    b,
    options: op === 'thru' || operationOps.some(o => o.value === op) ? operationOps : conditionOps,
    ...extra
  };
}

const statuses = [
  { value: '144', label: 'Note' },
  { value: '176', label: 'Control' }
];

/** "Notes above C4 get velocity 100 and a 1/16 length", as the golden path builds it. */
const rows: TransformGridRow[] = [
  {
    id: 'condition',
    label: 'Select Events by Conditions',
    noun: 'condition',
    cells: [
      { ...cell('equal', '144'), choices: statuses },
      cell('all'),
      cell('all'),
      cell('greaterEqual', '73'),
      cell('all')
    ]
  },
  {
    id: 'operation',
    label: 'Operations on Selected Events',
    noun: 'operation',
    cells: [
      null,
      cell('thru'),
      cell('fix', '0 0 1 0', '', { hint: 'bars beats divisions ticks' }),
      cell('thru'),
      cell('fix', '100')
    ]
  }
];

/**
 * Logic's MIDI Transform grid: an operator menu per field and stage, with the values the operator needs under it
 * (positions typed as bar beat division tick). Presentational: the window owns the set and formats its values.
 */
const meta: Meta<AnTransformGrid> = {
  title: 'Audio UI/Transform Grid',
  component: AnTransformGrid,
  args: { columns, rows }
};

export default meta;
type Story = StoryObj<AnTransformGrid>;

export const Custom: Story = {};

/** A value the owner could not read stays as typed, marked invalid. */
export const Invalid: Story = {
  args: {
    rows: [
      rows[0] as TransformGridRow,
      {
        ...(rows[1] as TransformGridRow),
        cells: [
          null,
          cell('range', '1 1 1 1', '1 x', { invalid: ['b'] }),
          cell('thru'),
          cell('thru'),
          cell('plusMinusRandom', '10')
        ]
      }
    ]
  }
};

export const Disabled: Story = { args: { disabled: true } };

/** Tab reaches every control by name; a chosen operator and a typed value (Return) are emitted. */
export const Keyboard: Story = {
  args: { changed: fn() },
  play: async ({ args, canvasElement }) => {
    const canvas = within(canvasElement);
    const pitch = canvas.getByRole('combobox', { name: 'Pitch condition' });
    await expect(pitch).toHaveValue('greaterEqual');
    const value = canvas.getByRole('textbox', { name: 'Pitch condition value' });
    await expect(value).toHaveValue('73');
    await userEvent.click(value);
    await userEvent.clear(value);
    await userEvent.type(value, '60{Enter}');
    await expect(args.changed).toHaveBeenCalledWith({ row: 'condition', field: 'pitch', part: 'a', value: '60' });
    await userEvent.tab();
    await expect(canvas.getByRole('combobox', { name: 'Velocity condition' })).toHaveFocus();
    await userEvent.selectOptions(canvas.getByRole('combobox', { name: 'Velocity operation' }), 'range');
    await expect(args.changed).toHaveBeenCalledWith({
      row: 'operation',
      field: 'velocity',
      part: 'op',
      value: 'range'
    });
    await expect(canvas.queryByRole('combobox', { name: 'Status operation' })).toBeNull();
    await expect(canvas.getByRole('textbox', { name: 'Length operation value' })).toHaveAccessibleDescription(
      'bars beats divisions ticks'
    );
  }
};
