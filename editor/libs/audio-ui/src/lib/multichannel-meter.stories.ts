import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, waitFor, within } from 'storybook/test';
import { AnMultichannelMeter, type MultiMeterSource } from './multichannel-meter';

/** Levels that move like a mix: the front loudest, the LFE pulsing, the surrounds and heights quieter. */
function moving(channels: number, scale = 0.5): MultiMeterSource {
  return () => {
    const t = performance.now() / 1000;
    return Array.from(
      { length: channels },
      (_, c) => scale * (0.35 + 0.45 * Math.abs(Math.sin(t * (1.3 + c * 0.37)))) * (c < 3 ? 1 : 0.55)
    );
  };
}

const Labels51 = ['M+030', 'M-030', 'M+000', 'LFE1', 'M+110', 'M-110'];
const Labels714 = [
  'M+030',
  'M-030',
  'M+000',
  'LFE1',
  'M+090',
  'M-090',
  'M+135',
  'M-135',
  'U+045',
  'U-045',
  'U+135',
  'U-135'
];

/**
 * A surround strip's meter: one LED bar per speaker in the layout's channel order, each labelled (L R C LFE Ls Rs …),
 * with the stereo meter's ballistics and one clip light. A channel strip draws it without labels.
 */
const meta: Meta<AnMultichannelMeter> = {
  title: 'Audio UI/Multichannel meter',
  component: AnMultichannelMeter,
  args: { label: 'Stereo Out level', speakers: Labels51, source: moving(6) }
};

export default meta;
type Story = StoryObj<AnMultichannelMeter>;

export const Surround51: Story = {};

export const Immersive714: Story = { args: { speakers: Labels714, source: moving(12), barWidth: 5 } };

/** In a channel strip: narrow bars, no labels. */
export const Strip: Story = { args: { speakers: Labels714, source: moving(12), labels: false, barWidth: 3 } };

/** A clipped channel lights the clip LED until it is clicked. */
export const Clipped: Story = {
  args: { source: () => [1.2, 0.5, 0.4, 0.1, 0.2, 0.2] },
  play: async ({ canvasElement }) => {
    const clip = within(canvasElement).getByRole('button', { name: 'Stereo Out level: reset clip indicator' });
    // The meter draws in animation frames: the clip lights on the first one.
    await waitFor(() => expect(clip).toHaveAttribute('aria-pressed', 'true'));
    await userEvent.click(clip);
    await expect(clip).toHaveAttribute('aria-pressed', 'false');
  }
};
