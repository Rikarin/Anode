import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, fireEvent, fn, userEvent, within } from 'storybook/test';
import { AnLiveLoopsCell } from './live-loops-cell';

/**
 * A Live Loops cell, as the grid beside the Tracks area shows it: empty (a sunken well), stopped, queued (a dashed ring
 * in the track's colour), playing (lit, with a progress ring) and stopping (dimmed). A gridcell needs a grid and a row
 * around it, so each story renders one.
 */
const meta: Meta<AnLiveLoopsCell> = {
  title: 'Audio UI/Live Loops Cell',
  component: AnLiveLoopsCell,
  args: {
    name: 'Drum Loop 100',
    kind: 'audio',
    state: 'stopped',
    progress: null,
    color: '#3d8ce8',
    label: 'Drum Loop 100, Drums, scene 1',
    selected: false,
    recording: false,
    dropTarget: false,
    activate: fn(),
    menu: fn()
  },
  render: args => ({
    props: args,
    template: `
      <div class="bg-surface p-3" role="grid" aria-label="Live Loops">
        <div role="row" class="flex">
          <an-live-loops-cell
            class="h-14 w-20"
            [name]="name"
            [kind]="kind"
            [state]="state"
            [progress]="progress"
            [color]="color"
            [label]="label"
            [selected]="selected"
            [recording]="recording"
            [dropTarget]="dropTarget"
            (activate)="activate($event)"
            (menu)="menu($event)"
          />
        </div>
      </div>
    `
  })
};

export default meta;
type Story = StoryObj<AnLiveLoopsCell>;

export const Stopped: Story = {};

export const Empty: Story = {
  args: { name: null, kind: null, state: 'empty', label: 'Empty cell, Drums, scene 2' }
};

export const Queued: Story = {
  args: { name: 'Bass Line', kind: 'midi', state: 'queued', color: '#4cb763', label: 'Bass Line, Bass, scene 1' }
};

export const Playing: Story = {
  args: {
    name: 'Beat Pattern',
    kind: 'pattern',
    state: 'playing',
    progress: 0.4,
    color: '#ee8a35',
    label: 'Beat Pattern, Beat, scene 1'
  }
};

export const Stopping: Story = { args: { state: 'stopping', progress: 0.85 } };

export const Selected: Story = { args: { selected: true } };

export const Recording: Story = {
  args: { name: null, kind: null, state: 'empty', recording: true, label: 'Empty cell, Synth, scene 1' }
};

/** A row of every state, in several track colours. */
export const AllStates: Story = {
  render: () => ({
    props: {
      cells: [
        { name: null, kind: null, state: 'empty', color: '#3d8ce8' },
        { name: 'Drum Loop 100', kind: 'audio', state: 'stopped', color: '#3d8ce8' },
        { name: 'Bass Line', kind: 'midi', state: 'queued', color: '#4cb763' },
        { name: 'Synth Chords', kind: 'midi', state: 'playing', color: '#e9b937', progress: 0.6 },
        { name: 'Hat Groove', kind: 'pattern', state: 'stopping', color: '#a865d9', progress: 0.9 }
      ]
    },
    template: `
      <div class="bg-surface p-3" role="grid" aria-label="Live Loops">
        <div role="row" class="flex">
          @for (cell of cells; track $index) {
            <an-live-loops-cell
              class="h-14 w-20"
              [name]="cell.name"
              [kind]="cell.kind"
              [state]="cell.state"
              [color]="cell.color"
              [progress]="cell.progress ?? null"
              [label]="(cell.name ?? 'Empty cell') + ', Track, scene ' + ($index + 1)"
            />
          }
        </div>
      </div>
    `
  }),
  play: async ({ canvasElement }) => {
    await expect(within(canvasElement).getAllByRole('gridcell')).toHaveLength(5);
  }
};

/** Enter launches; ⇧F10 asks for the menu; ⇧-click extends the selection. */
export const Keyboard: Story = {
  play: async ({ args, canvasElement }) => {
    const canvas = within(canvasElement);
    await expect(canvas.getByRole('gridcell', { name: 'Drum Loop 100, Drums, scene 1' })).toHaveAttribute(
      'data-state',
      'stopped'
    );
    const button = canvas.getByRole('button', { name: 'Drum Loop 100, Drums, scene 1' });
    button.focus();
    await userEvent.keyboard('{Enter}');
    await expect(args.activate).toHaveBeenCalledWith({ extend: false });
    await userEvent.keyboard('{Shift>}{F10}{/Shift}');
    await expect(args.menu).toHaveBeenCalled();
    await fireEvent.click(button, { shiftKey: true });
    await expect(args.activate).toHaveBeenLastCalledWith({ extend: true });
  }
};
