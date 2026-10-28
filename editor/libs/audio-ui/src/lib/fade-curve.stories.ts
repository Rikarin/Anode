import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnFadeCurve } from './fade-curve';

/**
 * A fade's curve, as the Region inspector shows it: the gain shape in a sunken well. Drag up or down (⇧ for fine),
 * arrows and Page keys, Home/End for the extremes; a double-click returns it to linear.
 */
const meta: Meta<AnFadeCurve> = {
  title: 'Audio UI/Fade Curve',
  component: AnFadeCurve,
  args: { value: 0, label: 'Fade In' }
};

export default meta;
type Story = StoryObj<AnFadeCurve>;

export const Linear: Story = {};

/** −1: a slow, exponential-looking start. */
export const SlowStart: Story = { args: { value: -1, label: 'Fade In' } };

/** A fade-out falls; +0.6 drops fast at first. */
export const FadeOut: Story = { args: { value: 0.6, out: true, label: 'Fade Out' } };

export const Disabled: Story = { args: { value: 0.3, disabled: true, label: 'Fade In' } };

/** Keys change it by 0.05 (Page by 0.25); Home and End reach the ends; a double-click is linear again. */
export const Keyboard: Story = {
  args: { value: 0, label: 'Curve' },
  play: async ({ canvasElement }) => {
    const curve = within(canvasElement).getByRole('slider', { name: 'Curve' });
    curve.focus();
    await userEvent.keyboard('{ArrowUp}{ArrowUp}');
    await expect(curve).toHaveAttribute('aria-valuenow', '0.1');
    await userEvent.keyboard('{Home}');
    await expect(curve).toHaveAttribute('aria-valuetext', '-1.00');
    await userEvent.dblClick(curve);
    await expect(curve).toHaveAttribute('aria-valuetext', 'Linear');
  }
};
