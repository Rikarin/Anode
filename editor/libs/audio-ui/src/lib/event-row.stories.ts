import { signal } from '@angular/core';
import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnEventRow, type EventCell, type EventCellCommit } from './event-row';

const columns = '7.5rem 6.5rem 2.75rem 4.5rem 4.5rem 6.5rem minmax(7rem, 1fr)';
const headers = ['Position', 'Status', 'Ch', 'Num', 'Val', 'Length', 'Articulation'];

function noteCells(position: string, velocity: string, articulation = ''): EventCell[] {
  return [
    { key: 'position', label: 'Position', text: position, editable: true },
    { key: 'status', label: 'Status', text: 'Note', editable: false },
    { key: 'channel', label: 'Ch', text: '1', editable: true },
    { key: 'num', label: 'Num', text: 'C3', editable: true },
    { key: 'val', label: 'Val', text: velocity, editable: true },
    { key: 'length', label: 'Length', text: '0 0 2 0', editable: true },
    {
      key: 'articulation',
      label: 'Articulation',
      text: articulation,
      editable: true,
      value: articulation === 'Legato' ? '1' : '0',
      options: [
        { value: '0', label: 'None' },
        { value: '1', label: 'Legato' }
      ]
    }
  ];
}

const cc: EventCell[] = [
  { key: 'position', label: 'Position', text: '1 1 3 1', editable: true },
  { key: 'status', label: 'Status', text: 'Control', editable: false },
  { key: 'channel', label: 'Ch', text: '1', editable: true },
  { key: 'num', label: 'Num', text: '74', editable: true },
  { key: 'val', label: 'Val', text: '64', editable: true },
  { key: 'length', label: 'Length', text: '', editable: false },
  { key: 'articulation', label: 'Articulation', text: '', editable: false }
];

const header = `
  <div role="rowgroup" class="bg-surface-raised border-hairline border-b">
    <div role="row" class="grid h-6 items-center" style="grid-template-columns: ${columns}">
      ${headers.map(h => `<div role="columnheader" class="text-foreground-muted px-1.5 text-[10px] font-semibold uppercase tracking-wide">${h}</div>`).join('')}
    </div>
  </div>
`;

/**
 * A row of the Event List: one event's cells on the table's column grid. The owner says which cell is being edited;
 * the row shows an input (or a select for an articulation) that commits on Enter or blur and cancels on Escape.
 * Selected rows are tinted, the row at the playhead raised.
 */
const meta: Meta<AnEventRow> = {
  title: 'Audio UI/Event Row',
  component: AnEventRow,
  args: { cells: noteCells('1 1 1 1', '100', 'Legato'), columns, selected: false, playing: false, editing: null },
  render: args => ({
    props: { ...args },
    template: `
      <div role="grid" aria-label="Events" class="bg-surface w-[640px]">
        ${header}
        <div role="rowgroup">
          <an-event-row [cells]="cells" [columns]="columns" [selected]="selected" [playing]="playing" [editing]="editing" />
        </div>
      </div>
    `
  })
};

export default meta;
type Story = StoryObj<AnEventRow>;

export const Note: Story = {};

export const Selected: Story = { args: { selected: true } };

/** The row at the playhead while the list follows it. */
export const Playing: Story = { args: { playing: true } };

/** Editing the velocity: an input with the value selected. */
export const Editing: Story = { args: { editing: 'val' } };

export const ControlChange: Story = { args: { cells: cc } };

/** Wired to local state: click a cell to edit it, type, Enter commits; Escape cancels; ↓ comes out as `move`. */
export const Keyboard: Story = {
  render: () => {
    const cells = signal(noteCells('1 1 1 1', '100'));
    const editing = signal<string | null>(null);
    const selected = signal(false);
    const last = signal('');
    return {
      props: {
        cells,
        editing,
        selected,
        last,
        columns,
        commit: (event: EventCellCommit) => {
          editing.set(null);
          cells.update(list => list.map(c => (c.key === event.key ? { ...c, text: event.text } : c)));
        }
      },
      template: `
        <div role="grid" aria-label="Events" class="bg-surface w-[640px]">
          ${header}
          <div role="rowgroup">
            <an-event-row
              [cells]="cells()"
              [columns]="columns"
              [selected]="selected()"
              [editing]="editing()"
              active
              (pick)="selected.set(true)"
              (edit)="editing.set($event)"
              (commit)="commit($event)"
              (cancelled)="editing.set(null)"
              (move)="last.set('move ' + $event.by)"
            />
          </div>
        </div>
        <output class="text-foreground text-xs" data-testid="last">{{ last() }}</output>
      `
    };
  },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    await userEvent.click(canvas.getByRole('gridcell', { name: 'Val 100' }));
    await expect(canvas.getByRole('row', { selected: true })).toBeInTheDocument();
    const input = canvas.getByRole('textbox', { name: 'Val' });
    await expect(input).toHaveFocus();
    await userEvent.clear(input);
    await userEvent.type(input, '64{Enter}');
    const cell = canvas.getByRole('gridcell', { name: 'Val 64' });
    cell.focus();
    await userEvent.keyboard('{Enter}');
    await userEvent.keyboard('{Escape}');
    await expect(canvas.getByRole('gridcell', { name: 'Val 64' })).toBeInTheDocument();
    canvas.getByRole('gridcell', { name: 'Position 1 1 1 1' }).focus();
    await userEvent.keyboard('{ArrowRight}');
    await expect(canvas.getByRole('gridcell', { name: 'Status Note' })).toHaveFocus();
    await userEvent.keyboard('{ArrowDown}');
    await expect(canvas.getByTestId('last')).toHaveTextContent('move 1');
  }
};
