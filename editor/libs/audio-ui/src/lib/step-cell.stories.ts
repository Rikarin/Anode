import { signal } from '@angular/core';
import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnStepCell, type StepEditMode, type StepValues } from './step-cell';

const Off: StepValues = { on: false, velocity: 100, gate: 1, repeats: 1, chance: 1, offset: 0 };
const On: StepValues = { ...Off, on: true };

/**
 * One step of the Step Sequencer. In On/Off mode a toggle (click, Space); in a value mode a slider: ↑/↓ change the value
 * a step (⇧ a larger one), drag up or down. Velocity fills the step; repeats show as ticks, chance as a dot, offset as
 * a shift.
 */
const meta: Meta<AnStepCell> = {
  title: 'Audio UI/Step Cell',
  component: AnStepCell,
  args: { step: On, mode: 'onOff', label: 'Kick step 1', playing: false, beat: true },
  render: args => ({
    props: args,
    template: `
      <div class="bg-surface p-3">
        <an-step-cell [step]="step" [mode]="mode" [label]="label" [playing]="playing" [beat]="beat" />
      </div>
    `
  })
};

export default meta;
type Story = StoryObj<AnStepCell>;

export const StepOn: Story = {};

export const StepOff: Story = { args: { step: Off } };

/** The playhead's step is outlined. */
export const Playing: Story = { args: { playing: true } };

/** A soft step with three repeats, a 50 % chance and a late offset. */
export const Shaped: Story = { args: { step: { ...On, velocity: 50, repeats: 3, chance: 0.5, offset: 0.25 } } };

/** Every value mode, on and off: the bar shows the value; an off step's is dimmed. */
export const ValueModes: Story = {
  render: () => {
    const modes: StepEditMode[] = ['velocity', 'gate', 'repeats', 'chance', 'offset'];
    const step: StepValues = { on: true, velocity: 90, gate: 2, repeats: 4, chance: 0.6, offset: -0.2 };
    return {
      props: { modes, step, off: { ...step, on: false } },
      template: `
        <div class="bg-surface flex gap-3 p-3">
          @for (mode of modes; track mode) {
            <div class="flex flex-col items-center gap-1">
              <an-step-cell [step]="step" [mode]="mode" [label]="'On step ' + mode" />
              <an-step-cell [step]="off" [mode]="mode" [label]="'Off step ' + mode" />
              <span class="text-foreground text-[10px]">{{ mode }}</span>
            </div>
          }
        </div>
      `
    };
  }
};

/** Wired to local state: Space toggles the step; in Velocity mode ↓ lowers it by 1 and ⇧↓ by 10. */
export const Keyboard: Story = {
  render: () => {
    const step = signal<StepValues>(Off);
    const mode = signal<StepEditMode>('onOff');
    return {
      props: {
        step,
        mode,
        toggle: () => step.update(s => ({ ...s, on: !s.on })),
        commit: (velocity: number) => step.update(s => ({ ...s, on: true, velocity }))
      },
      template: `
        <div class="bg-surface flex items-center gap-3 p-3">
          <an-step-cell [step]="step()" [mode]="mode()" label="Kick step 1" (toggled)="toggle()" (committed)="commit($event)" />
          <button type="button" class="bezel text-foreground h-6.5 px-2 text-[11px]" (click)="mode.set(mode() === 'onOff' ? 'velocity' : 'onOff')">
            Mode
          </button>
        </div>
      `
    };
  },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    const cell = canvas.getByRole('button', { name: 'Kick step 1' });
    cell.focus();
    await userEvent.keyboard(' ');
    await expect(cell).toHaveAttribute('aria-pressed', 'true');
    await userEvent.click(canvas.getByRole('button', { name: 'Mode' }));
    const slider = canvas.getByRole('slider', { name: 'Kick step 1 velocity' });
    slider.focus();
    await userEvent.keyboard('{ArrowDown}');
    await expect(slider).toHaveAttribute('aria-valuenow', '99');
    await userEvent.keyboard('{Shift>}{ArrowDown}{/Shift}');
    await expect(slider).toHaveAttribute('aria-valuenow', '89');
  }
};
