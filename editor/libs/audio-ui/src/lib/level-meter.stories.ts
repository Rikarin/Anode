import type { Meta, StoryObj } from '@storybook/angular-vite';
import { AnLevelMeter } from './level-meter';

/** A single-channel LED level meter (the placeholder the canvas meters will replace): green, yellow near −6 dB, red at 0. */
const meta: Meta<AnLevelMeter> = {
  title: 'Audio UI/Level meter',
  component: AnLevelMeter,
  args: { db: -18, label: 'Audio 1' },
  render: args => ({ props: args, template: `<an-level-meter class="h-40" [db]="db" [label]="label" />` })
};
export default meta;

type Story = StoryObj<AnLevelMeter>;

export const Nominal: Story = {};
export const Hot: Story = { args: { db: -3 } };
export const Clipping: Story = { args: { db: 0.5 } };
