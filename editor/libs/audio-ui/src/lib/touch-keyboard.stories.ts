import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, fireEvent, fn, waitFor, within } from 'storybook/test';
import { AnTouchKeyboard } from './touch-keyboard';

/**
 * The remote's touch keyboard: equal keys for the notes of a scale from its root (marked), so every key is in key.
 * Each finger plays its own note, sliding plays the next key, and lower on a key is louder. Space plays the focused key;
 * arrows move between keys.
 */
const meta: Meta<AnTouchKeyboard> = {
  title: 'Audio UI/Remote/Touch keyboard',
  component: AnTouchKeyboard,
  args: { scale: 'major', root: 0, base: 48, octaves: 2, label: 'Keyboard', noteOn: fn(), noteOff: fn() },
  render: args => ({
    props: args,
    template: `<an-touch-keyboard class="block h-40 w-[40rem]" [scale]="scale" [root]="root" [base]="base" [octaves]="octaves" [sounding]="sounding" [label]="label" [disabled]="disabled" (noteOn)="noteOn($event)" (noteOff)="noteOff($event)" />`
  })
};

export default meta;
type Story = StoryObj<AnTouchKeyboard>;

export const CMajor: Story = { args: { sounding: new Set<number>() } };

/** A minor pentatonic on A, with a chord sounding on the track. */
export const APentatonic: Story = {
  args: { scale: 'minorPentatonic', root: 9, sounding: new Set([57, 60, 64]) }
};

export const Chromatic: Story = { args: { scale: 'chromatic', octaves: 1, sounding: new Set<number>() } };

export const Disabled: Story = { args: { disabled: true, sounding: new Set<number>() } };

/** Space on a focused key plays it; arrows move along the scale. */
export const Keyboard: Story = {
  args: { sounding: new Set<number>() },
  play: async ({ args, canvasElement }) => {
    const canvas = within(canvasElement);
    const key = canvas.getByRole('button', { name: 'C3' });
    key.focus();
    await fireEvent.keyDown(key, { key: ' ' });
    await expect(args.noteOn).toHaveBeenCalledWith({ pitch: 60, velocity: 100 });
    await waitFor(() => expect(key).toHaveAttribute('aria-pressed', 'true'));
    await fireEvent.keyUp(key, { key: ' ' });
    await expect(args.noteOff).toHaveBeenCalledWith(60);
    await fireEvent.keyDown(key, { key: 'ArrowRight' });
    await expect(canvas.getByRole('button', { name: 'D3' })).toHaveFocus();
  }
};

/** Two fingers hold a chord; a touch low on a key is louder than one near its top. */
export const Chord: Story = {
  args: { sounding: new Set<number>() },
  play: async ({ args, canvasElement }) => {
    const group = within(canvasElement).getByRole('group', { name: 'Keyboard' });
    const rect = group.getBoundingClientRect();
    const keyWidth = rect.width / 15;
    const at = (index: number, fraction: number) => ({
      clientX: rect.left + keyWidth * (index + 0.5),
      clientY: rect.top + rect.height * fraction,
      pointerType: 'touch',
      isPrimary: index === 0
    });
    await fireEvent.pointerDown(group, { pointerId: 1, ...at(0, 0.95) });
    await fireEvent.pointerDown(group, { pointerId: 2, ...at(2, 0.05) });
    await expect(args.noteOn).toHaveBeenCalledWith({ pitch: 48, velocity: 123 });
    await expect(args.noteOn).toHaveBeenCalledWith({ pitch: 52, velocity: 44 });
    await fireEvent.pointerUp(group, { pointerId: 1, ...at(0, 0.95) });
    await fireEvent.pointerUp(group, { pointerId: 2, ...at(2, 0.05) });
    await expect(args.noteOff).toHaveBeenCalledWith(48);
    await expect(args.noteOff).toHaveBeenCalledWith(52);
  }
};
