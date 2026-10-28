import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, fireEvent, fn, userEvent, waitFor, within } from 'storybook/test';
import { AnPianoKeyboard } from './piano-keyboard';

/**
 * The on-screen keyboard, ivory and ebony keys in either theme: press keys to play (lower on a key is louder), drag for
 * a glissando. Keys the engine reports
 * sounding light up. Every key is a button named by its note (C3 is middle C); arrows move between them.
 */
const meta: Meta<AnPianoKeyboard> = {
  title: 'Audio UI/Piano keyboard',
  component: AnPianoKeyboard,
  args: { from: 48, octaves: 2, label: 'Keyboard', noteOn: fn(), noteOff: fn() },
  render: args => ({
    props: args,
    template: `<an-piano-keyboard class="h-24" [from]="from" [octaves]="octaves" [sounding]="sounding" [label]="label" [disabled]="disabled" (noteOn)="noteOn($event)" (noteOff)="noteOff($event)" />`
  })
};

export default meta;
type Story = StoryObj<AnPianoKeyboard>;

export const TwoOctaves: Story = { args: { sounding: new Set<number>() } };

/** A C major chord sounding on the track (from `midiActivity`). */
export const Sounding: Story = { args: { sounding: new Set([60, 64, 67, 70]) } };

export const Wide: Story = { args: { from: 36, octaves: 4, sounding: new Set<number>() } };

export const Disabled: Story = { args: { disabled: true, sounding: new Set<number>() } };

/** Space on a focused key plays it until released. */
export const Keyboard: Story = {
  args: { sounding: new Set<number>() },
  play: async ({ args, canvasElement }) => {
    const key = within(canvasElement).getByRole('button', { name: 'C3' });
    key.focus();
    await fireEvent.keyDown(key, { key: ' ' });
    await expect(args.noteOn).toHaveBeenCalledWith({ pitch: 60, velocity: 100 });
    await waitFor(() => expect(key).toHaveAttribute('aria-pressed', 'true'));
    await fireEvent.keyUp(key, { key: ' ' });
    await expect(args.noteOff).toHaveBeenCalledWith(60);
    await userEvent.keyboard('{ArrowRight}');
    await expect(within(canvasElement).getByRole('button', { name: 'C♯3' })).toHaveFocus();
  }
};
