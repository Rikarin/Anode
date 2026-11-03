import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnObjectPanner } from './object-panner';
import { Layout714 } from './story-layouts';

/**
 * Logic's 3D Object Panner: an object's position in a top view (azimuth and distance, the layout's speakers around
 * it, ahead at the top) and a side view (elevation, seen from the right with the front to the right), and its size.
 * Drag in either view; arrows move it (top: ←/→ azimuth, ↑/↓ distance; side: ↑/↓ elevation, ←/→ distance).
 */
const meta: Meta<AnObjectPanner> = {
  title: 'Audio UI/3D Object Panner',
  component: AnObjectPanner,
  args: { speakers: Layout714, label: 'Bird object', value: { azimuth: 30, elevation: 0, distance: 1, size: 0 } }
};

export default meta;
type Story = StoryObj<AnObjectPanner>;

export const FrontLeft: Story = {};

/** Rear right, overhead, with some size. */
export const RearRightOverhead: Story = {
  args: { value: { azimuth: -135, elevation: 45, distance: 1, size: 0.3 } }
};

export const Disabled: Story = { args: { disabled: true } };

/** The keyboard moves the azimuth in the top view and the elevation in the side view. */
export const Keyboard: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    const top = canvas.getByRole('slider', { name: 'Bird object top view' });
    top.focus();
    await userEvent.keyboard('{ArrowRight}{ArrowDown}');
    await expect(top).toHaveAttribute('aria-valuetext', '25° L, distance 95 %');
    const side = canvas.getByRole('slider', { name: 'Bird object side view' });
    side.focus();
    await userEvent.keyboard('{ArrowUp}{ArrowUp}');
    await expect(side).toHaveAttribute('aria-valuenow', '10');
  }
};
