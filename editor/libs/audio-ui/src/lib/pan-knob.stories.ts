import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnPanKnob } from './pan-knob';

/** A channel's pan, shown as Logic does (L64 … C … R63). Drag, arrows, Home/End; double-click to centre. */
const meta: Meta<AnPanKnob> = {
  title: 'Audio UI/Pan knob',
  component: AnPanKnob,
  args: { value: 0, label: 'Audio 1 pan' }
};

export default meta;
type Story = StoryObj<AnPanKnob>;

export const Centre: Story = {};

export const Left: Story = { args: { value: -0.5 } };

export const HardRight: Story = { args: { value: 1 } };

export const Disabled: Story = { args: { disabled: true, value: 0.25 } };

/** Arrows step a sixty-fourth of the range each. */
export const Keyboard: Story = {
  play: async ({ canvasElement }) => {
    const slider = within(canvasElement).getByRole('slider', { name: 'Audio 1 pan' });
    slider.focus();
    await userEvent.keyboard('{ArrowLeft}{ArrowLeft}');
    await expect(slider).toHaveAttribute('aria-valuetext', 'L2');
    await userEvent.keyboard('{End}');
    await expect(slider).toHaveAttribute('aria-valuetext', 'R63');
  }
};
