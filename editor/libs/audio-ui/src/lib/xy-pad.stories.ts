import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnXyPad } from './xy-pad';

/**
 * An XY pad, as Remix FX and the Smart Controls pad show it: a sunken square with a puck. Drag on it (it is engaged
 * while held); arrows move the puck by 5 % (⇧: 1 %), Home and End go to the corners, Space holds it engaged.
 */
const meta: Meta<AnXyPad> = {
  title: 'Audio UI/XY Pad',
  component: AnXyPad,
  args: { value: { x: 0.5, y: 0.5 }, label: 'Filter', xLabel: 'Cutoff', yLabel: 'Resonance' }
};

export default meta;
type Story = StoryObj<AnXyPad>;

export const Centred: Story = {};

export const Corner: Story = { args: { value: { x: 0.9, y: 0.15 }, label: 'Repeater', xLabel: 'Rate', yLabel: 'Mix' } };

/** Without axis names: the readout is left out. */
export const Plain: Story = { args: { value: { x: 0.3, y: 0.7 }, label: 'Pad', xLabel: '', yLabel: '', size: 96 } };

export const Disabled: Story = { args: { disabled: true } };

/** Arrows move the puck; Home and End reach the corners. */
export const Keyboard: Story = {
  play: async ({ canvasElement }) => {
    const pad = within(canvasElement).getByRole('slider', { name: 'Filter' });
    pad.focus();
    await userEvent.keyboard('{ArrowRight}{ArrowRight}{ArrowUp}');
    await expect(pad).toHaveAttribute('aria-valuetext', 'Cutoff 60 %, Resonance 55 %');
    await userEvent.keyboard('{Home}');
    await expect(pad).toHaveAttribute('aria-valuenow', '0');
    await userEvent.keyboard('{End}');
    await expect(pad).toHaveAttribute('aria-valuetext', 'Cutoff 100 %, Resonance 100 %');
  }
};
