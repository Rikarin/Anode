import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnKnob } from './knob';

/**
 * A plugin parameter's knob: a turned-metal body with a highlight, its value as an accent arc on a sunken track. Any
 * range and unit, logarithmic for frequencies and times, stepped when discrete. Drag
 * (⇧ for fine), arrows and Page keys, Home/End; double-click or ⌥-click for the default.
 */
const meta: Meta<AnKnob> = {
  title: 'Audio UI/Knob',
  component: AnKnob,
  args: { value: 0.5, min: 0, max: 1, label: 'Resonance' }
};

export default meta;
type Story = StoryObj<AnKnob>;

export const Fraction: Story = {};

/** A cutoff: 20 Hz … 20 kHz on a logarithmic travel, so 632 Hz sits in the middle. */
export const Frequency: Story = {
  args: { value: 2400, min: 20, max: 20_000, defaultValue: 2400, unit: 'Hz', label: 'Cutoff' }
};

/** A bipolar range fills from the centre. */
export const Bipolar: Story = { args: { value: -35, min: -100, max: 100, unit: 'ct', label: 'Osc 2 Fine' } };

/** A discrete parameter moves a step per key. */
export const Stepped: Story = {
  args: { value: 0, min: -2, max: 2, steps: 4, unit: 'oct', label: 'Osc 1 Octave' },
  play: async ({ canvasElement }) => {
    const knob = within(canvasElement).getByRole('slider', { name: 'Osc 1 Octave' });
    knob.focus();
    await userEvent.keyboard('{ArrowUp}{ArrowUp}{ArrowUp}');
    await expect(knob).toHaveAttribute('aria-valuetext', '+2 oct');
    await userEvent.keyboard('{Home}');
    await expect(knob).toHaveAttribute('aria-valuenow', '-2');
  }
};

export const Disabled: Story = { args: { disabled: true, value: 0.2, label: 'Glide', unit: 's', max: 2 } };

/** The keyboard moves it 1 % of the travel; a double-click returns it to the default. */
export const Keyboard: Story = {
  args: { value: 0.4, label: 'Attack', unit: 's', min: 0.001, max: 10, defaultValue: 0.005 },
  play: async ({ canvasElement }) => {
    const knob = within(canvasElement).getByRole('slider', { name: 'Attack' });
    knob.focus();
    await userEvent.keyboard('{End}');
    await expect(knob).toHaveAttribute('aria-valuetext', '10.00 s');
    await userEvent.dblClick(knob);
    await expect(knob).toHaveAttribute('aria-valuetext', '5.0 ms');
  }
};
