import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnSessionPad } from './session-pad';

/**
 * A Session Player's XY pad (Phase 11), as the Session Player Editor shows it: Simple to Complex left to right, Soft to
 * Loud bottom to top, the region's puck lit and the track's other regions as faint rings. Arrows move the puck by 5 %
 * (⇧: 1 %); Home and End go to the corners.
 */
const meta: Meta<AnSessionPad> = {
  title: 'Audio UI/Session Player Pad',
  component: AnSessionPad,
  args: { value: { complexity: 0.45, intensity: 0.5 }, label: 'Verse' }
};

export default meta;
type Story = StoryObj<AnSessionPad>;

export const Verse: Story = {};

/** The track's other regions (intro, chorus) as rings. */
export const WithOtherRegions: Story = {
  args: {
    value: { complexity: 0.65, intensity: 0.75 },
    label: 'Chorus',
    others: [
      { id: 'spr_1', name: 'Intro', value: { complexity: 0.3, intensity: 0.35 } },
      { id: 'spr_2', name: 'Verse', value: { complexity: 0.45, intensity: 0.5 } }
    ]
  }
};

export const Small: Story = { args: { width: 160, height: 108, label: 'Bridge' } };

export const Disabled: Story = { args: { disabled: true } };

/** Arrows move the puck and read out the zone; Home and End reach the corners. */
export const Keyboard: Story = {
  play: async ({ canvasElement }) => {
    const pad = within(canvasElement).getByRole('slider', { name: 'Verse' });
    pad.focus();
    await userEvent.keyboard('{ArrowRight}{ArrowRight}{ArrowUp}');
    await expect(pad).toHaveAttribute('aria-valuetext', 'Complexity 55 %, intensity 55 % (moderate, medium)');
    await userEvent.keyboard('{End}');
    await expect(pad).toHaveAttribute('aria-valuetext', 'Complexity 100 %, intensity 100 % (complex, loud)');
    await userEvent.keyboard('{Home}');
    await expect(pad).toHaveAttribute('aria-valuenow', '0');
  }
};
