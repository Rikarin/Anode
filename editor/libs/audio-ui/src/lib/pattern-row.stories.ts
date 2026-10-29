import { signal } from '@angular/core';
import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnPatternRow, type StepEdit } from './pattern-row';
import type { StepEditMode, StepValues } from './step-cell';

function steps(length: number, on: readonly number[] = []): StepValues[] {
  return Array.from({ length }, (_, i) => ({
    on: on.includes(i),
    velocity: 100,
    gate: 1,
    repeats: 1,
    chance: 1,
    offset: 0
  }));
}

/**
 * A Step Sequencer row: the row header (projected) and its steps on the shared grid. Rows of different lengths line up
 * from the left; ←/→ move between steps; a muted row is dimmed and the playhead's step outlined.
 */
const meta: Meta<AnPatternRow> = {
  title: 'Audio UI/Pattern Row',
  component: AnPatternRow,
  args: { name: 'Kick', steps: steps(16, [0, 4, 8, 12]), mode: 'onOff', playhead: null, muted: false },
  render: args => ({
    props: args,
    template: `
      <div role="grid" aria-label="Pattern" class="bg-surface w-fit">
        <an-pattern-row [name]="name" [steps]="steps" [mode]="mode" [playhead]="playhead" [muted]="muted" [headerWidth]="96">
          <span class="text-foreground truncate text-[11px] font-semibold">{{ name }}</span>
        </an-pattern-row>
      </div>
    `
  })
};

export default meta;
type Story = StoryObj<AnPatternRow>;

export const FourOnTheFloor: Story = {};

/** The playhead on step 5. */
export const Playing: Story = { args: { playhead: 4 } };

export const Muted: Story = { args: { muted: true } };

/** Rows of 16, 7 and 12 steps line up on one grid; the second in Velocity mode, the third selected. */
export const Lengths: Story = {
  render: () => ({
    props: { kick: steps(16, [0, 4, 8, 12]), hat: steps(7, [0, 2, 4, 6]), snare: steps(12, [4]) },
    template: `
      <div role="grid" aria-label="Pattern" class="bg-surface w-fit">
        <an-pattern-row name="Kick" [steps]="kick" [headerWidth]="96">
          <span class="text-foreground text-[11px] font-semibold">Kick</span>
        </an-pattern-row>
        <an-pattern-row name="Closed Hat" [steps]="hat" mode="velocity" [headerWidth]="96">
          <span class="text-foreground text-[11px] font-semibold">Closed Hat</span>
        </an-pattern-row>
        <an-pattern-row name="Snare" [steps]="snare" [headerWidth]="96" selected>
          <span class="text-foreground text-[11px] font-semibold">Snare</span>
        </an-pattern-row>
      </div>
    `
  })
};

/** Wired to local state: a click lights a step, → moves to the next, Space lights it; Velocity's ↑ raises one. */
export const Keyboard: Story = {
  render: () => {
    const row = signal(steps(8));
    const mode = signal<StepEditMode>('onOff');
    return {
      props: {
        row,
        mode,
        toggle: (i: number) => row.update(list => list.map((s, j) => (i === j ? { ...s, on: !s.on } : s))),
        commit: (edit: StepEdit) =>
          row.update(list => list.map((s, j) => (edit.index === j ? { ...s, on: true, velocity: edit.value } : s)))
      },
      template: `
        <div role="grid" aria-label="Pattern" class="bg-surface w-fit">
          <an-pattern-row name="Snare" [steps]="row()" [mode]="mode()" [headerWidth]="96" (toggled)="toggle($event)" (committed)="commit($event)">
            <button type="button" class="bezel text-foreground h-5 px-2 text-[11px]" (click)="mode.set('velocity')">Velocity</button>
          </an-pattern-row>
        </div>
      `
    };
  },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    await userEvent.click(canvas.getByRole('button', { name: 'Snare step 1' }));
    await expect(canvas.getByRole('button', { name: 'Snare step 1' })).toHaveAttribute('aria-pressed', 'true');
    await userEvent.keyboard('{ArrowRight}');
    await expect(canvas.getByRole('button', { name: 'Snare step 2' })).toHaveFocus();
    await userEvent.keyboard(' ');
    await expect(canvas.getByRole('button', { name: 'Snare step 2' })).toHaveAttribute('aria-pressed', 'true');
    await userEvent.click(canvas.getByRole('button', { name: 'Velocity' }));
    const slider = canvas.getByRole('slider', { name: 'Snare step 3 velocity' });
    slider.focus();
    await userEvent.keyboard('{ArrowUp}');
    await expect(slider).toHaveAttribute('aria-valuenow', '101');
    await expect(slider).toHaveAttribute('aria-valuetext', '101');
  }
};
