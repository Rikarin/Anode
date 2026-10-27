import type { Meta, StoryObj } from '@storybook/angular-vite';
import { AnPeakMeter, type MeterSource } from './peak-meter';

/** A level that moves like music, so the ballistics show: instant rise, steady fall, a held peak. */
function moving(scale: number): MeterSource {
  return () => {
    const t = performance.now() / 1000;
    const level = scale * (0.55 + 0.4 * Math.abs(Math.sin(t * 2.3)) * Math.abs(Math.sin(t * 0.7)));
    return { left: level, right: level * 0.85 };
  };
}

/**
 * A stereo peak meter that reads its level from a function every animation frame (no change detection). Green to
 * amber to red at 0 dBFS, a held peak line, and a clip light that stays on until clicked.
 */
const meta: Meta<AnPeakMeter> = {
  title: 'Audio UI/Peak meter',
  component: AnPeakMeter,
  args: { label: 'Audio 1 level', source: moving(0.5) }
};

export default meta;
type Story = StoryObj<AnPeakMeter>;

export const Playing: Story = {};

export const Hot: Story = { args: { source: moving(1.15) } };

export const Mono: Story = { args: { mono: true } };

export const Silent: Story = { args: { source: () => ({ left: 0, right: 0 }) } };
