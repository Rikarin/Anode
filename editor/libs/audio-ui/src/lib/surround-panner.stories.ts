import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { Layout51, Layout714 } from './story-layouts';
import { AnSurroundPanner } from './surround-panner';

/**
 * Logic's Surround Panner: a puck in the circle of the output layout's speakers (ahead at the top). Its direction is
 * the angle, its distance from the centre the diversity (on the rim a point, in the middle every speaker). A stereo
 * input shows its two channels spread around the puck. Drag the puck; ←/→ turn it, ↑/↓ change the diversity, Home or
 * a double-click puts it front centre. Knobs for the elevation (height layouts), centre and LFE levels, and spread.
 */
const meta: Meta<AnSurroundPanner> = {
  title: 'Audio UI/Surround Panner',
  component: AnSurroundPanner,
  args: {
    speakers: Layout51,
    label: 'Voice surround panner',
    value: { angle: 0, diversity: 1, elevation: 0, centreLevelDb: 0, lfeLevelDb: -144, spread: 60 }
  }
};

export default meta;
type Story = StoryObj<AnSurroundPanner>;

export const FrontCentre: Story = {};

/** The puck at the left surround speaker. */
export const LeftSurround: Story = {
  args: { value: { angle: 110, diversity: 1, elevation: 0, centreLevelDb: 0, lfeLevelDb: -12, spread: 60 } }
};

/** 7.1.4: the upper layer drawn inside the ear-level ring, and the Elevation knob. */
export const Immersive714: Story = {
  args: {
    speakers: Layout714,
    value: { angle: -45, diversity: 0.6, elevation: 30, centreLevelDb: -3, lfeLevelDb: -144, spread: 60 }
  }
};

/** A stereo input: its two channels spread around the puck. */
export const StereoInput: Story = {
  args: {
    stereoInput: true,
    value: { angle: 0, diversity: 0.85, elevation: 0, centreLevelDb: 0, lfeLevelDb: -144, spread: 90 }
  }
};

/** The channel strip's compact panner, with its button to the full editor. */
export const Compact: Story = { args: { compact: true, size: 56 } };

export const Disabled: Story = { args: { disabled: true } };

/** Arrows turn the angle and move the diversity; Home returns front centre. */
export const Keyboard: Story = {
  play: async ({ canvasElement }) => {
    const panner = within(canvasElement).getByRole('slider', { name: 'Voice surround panner' });
    panner.focus();
    await userEvent.keyboard('{ArrowLeft}{ArrowLeft}{ArrowDown}');
    await expect(panner).toHaveAttribute('aria-valuetext', '10° L, diversity 95 %');
    await userEvent.keyboard('{ArrowRight}{ArrowRight}{ArrowRight}');
    await expect(panner).toHaveAttribute('aria-valuenow', '-5');
    await userEvent.keyboard('{Home}');
    await expect(panner).toHaveAttribute('aria-valuetext', '0°, diversity 100 %');
  }
};
