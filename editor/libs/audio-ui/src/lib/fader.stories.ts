import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnFader } from './fader';

/**
 * A channel fader with a dB taper: 0 dB sits at three quarters of the travel. Drag (⇧ for fine), use the arrow and
 * Page keys, Home for −∞ and End for +6 dB; double-click or ⌥-click for 0 dB.
 */
const meta: Meta<AnFader> = {
  title: 'Audio UI/Fader',
  component: AnFader,
  args: { value: 0, label: 'Audio 1 volume' }
};

export default meta;
type Story = StoryObj<AnFader>;

export const Unity: Story = {};

export const Quiet: Story = { args: { value: -18.5 } };

export const Silent: Story = { args: { value: -144 } };

export const Disabled: Story = { args: { disabled: true, value: -6 } };

/** The keyboard moves it in dB steps. */
export const Keyboard: Story = {
  play: async ({ canvasElement }) => {
    const slider = within(canvasElement).getByRole('slider', { name: 'Audio 1 volume' });
    slider.focus();
    await userEvent.keyboard('{ArrowDown}{ArrowDown}{PageDown}');
    await expect(slider).toHaveAttribute('aria-valuenow', '-8');
    await userEvent.keyboard('{End}');
    await expect(slider).toHaveAttribute('aria-valuetext', '+6.0 dB');
  }
};
