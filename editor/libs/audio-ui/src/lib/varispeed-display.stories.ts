import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnVarispeedDisplay } from './varispeed-display';

/**
 * The Varispeed display in the control bar: the speed in percent, the resulting tempo or the detune. Arrows change it
 * by 0.1 % (⇧ 1 %), Page keys by 5 %, Home/End reach 50 % and 150 %; drag up or down; Return or a double-click types.
 */
const meta: Meta<AnVarispeedDisplay> = {
  title: 'Audio UI/Varispeed Display',
  component: AnVarispeedDisplay,
  args: { speed: 1, unit: 'percent', bpm: 120, enabled: true }
};

export default meta;
type Story = StoryObj<AnVarispeedDisplay>;

export const Percent: Story = { args: { speed: 0.9 } };

/** The resulting tempo: 90 % of 120 bpm. */
export const ResultingTempo: Story = { args: { speed: 0.9, unit: 'tempo' } };

/** Tape-style detune: 90 % is 1.82 semitones down. */
export const Detune: Story = { args: { speed: 0.9, unit: 'detune' } };

/** Varispeed off: dimmed. */
export const Off: Story = { args: { speed: 1.1, enabled: false } };

export const Keyboard: Story = {
  args: { speed: 1 },
  play: async ({ canvasElement }) => {
    const display = within(canvasElement).getByRole('spinbutton', { name: 'Varispeed' });
    display.focus();
    await userEvent.keyboard('{ArrowDown}{ArrowDown}');
    await expect(display).toHaveAttribute('aria-valuenow', '-0.2');
    await userEvent.keyboard('{Shift>}{ArrowUp}{/Shift}');
    await expect(display).toHaveAttribute('aria-valuenow', '0.8');
    await userEvent.keyboard('{PageDown}{PageDown}');
    await expect(display).toHaveAttribute('aria-valuetext', '−9.20 %');
    await userEvent.keyboard('{Home}');
    await expect(display).toHaveAttribute('aria-valuenow', '-50');
  }
};
