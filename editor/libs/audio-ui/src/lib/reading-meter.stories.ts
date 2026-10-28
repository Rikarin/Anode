import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, within } from 'storybook/test';
import { AnReadingMeter } from './reading-meter';

/**
 * A plugin's reading in its editor: a compressor's gain reduction (from 0 dB at the right edge, as Logic shows it) or a
 * level such as a loudness meter's LUFS.
 */
const meta: Meta<AnReadingMeter> = {
  title: 'Audio UI/Reading meter',
  component: AnReadingMeter,
  args: { label: 'Gain reduction', value: -6.5, kind: 'reduction', min: -30, max: 0, unit: 'dB' },
  render: args => ({
    props: args,
    template: `
      <div class="bg-surface w-64 p-3">
        <an-reading-meter [label]="label" [value]="value" [kind]="kind" [min]="min" [max]="max" [unit]="unit" />
      </div>
    `
  })
};

export default meta;
type Story = StoryObj<AnReadingMeter>;

export const GainReduction: Story = {
  play: async ({ canvasElement }) => {
    const meter = within(canvasElement).getByRole('meter', { name: 'Gain reduction' });
    await expect(meter).toHaveAttribute('aria-valuenow', '-6.5');
    await expect(meter).toHaveAttribute('aria-valuetext', '−6.5 dB');
  }
};

export const Loudness: Story = {
  args: { label: 'Short-term', value: -14.2, kind: 'level', min: -60, max: 0, unit: 'LUFS' }
};

/** No reading yet (the engine reports none). */
export const NoReading: Story = { args: { value: null } };
