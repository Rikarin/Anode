import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnChoice } from './choice';

/** A parameter with named values: a drop-down labelled like a knob, its value the index of the name chosen. */
const meta: Meta<AnChoice> = {
  title: 'Audio UI/Choice',
  component: AnChoice,
  args: { value: 0, names: ['Saw', 'Square', 'Triangle', 'Sine', 'Noise'], label: 'Osc 1 Wave' }
};

export default meta;
type Story = StoryObj<AnChoice>;

export const Wave: Story = {};

export const FilterType: Story = {
  args: { value: 2, names: ['Low-pass 24', 'High-pass', 'Band-pass'], label: 'Filter Type' }
};

export const Disabled: Story = { args: { disabled: true } };

/** Opening it lists the names; picking one sets the value. */
export const Choose: Story = {
  play: async ({ canvasElement }) => {
    const select = within(canvasElement).getByRole('combobox', { name: 'Osc 1 Wave' });
    await expect(select).toHaveTextContent('Saw');
    await userEvent.click(select);
    const option = await within(document.body).findByRole('option', { name: 'Triangle' });
    await userEvent.click(option);
    await expect(select).toHaveTextContent('Triangle');
  }
};
