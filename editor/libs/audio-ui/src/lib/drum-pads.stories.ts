import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, fireEvent, fn, waitFor, within } from 'storybook/test';
import { AnDrumPads } from './drum-pads';
import { gmDrumPads } from './scales';

/**
 * The remote's drum pads: 4 × 4, the lowest note at the bottom left, each named by its sound and note. Several fingers
 * play several pads; lower on a pad is louder. Space plays the focused pad.
 */
const meta: Meta<AnDrumPads> = {
  title: 'Audio UI/Remote/Drum pads',
  component: AnDrumPads,
  args: { pads: gmDrumPads(), label: 'Drum pads', noteOn: fn(), noteOff: fn() },
  render: args => ({
    props: args,
    template: `<an-drum-pads class="block h-80 w-80" [pads]="pads" [sounding]="sounding" [label]="label" [disabled]="disabled" (noteOn)="noteOn($event)" (noteOff)="noteOff($event)" />`
  })
};

export default meta;
type Story = StoryObj<AnDrumPads>;

/** The 16 General MIDI pads (no Drum Machine Designer on the track). */
export const GeneralMidi: Story = { args: { sounding: new Set<number>() } };

/** A kit's pads with their colours; the snare sounding. */
export const Kit: Story = {
  args: {
    sounding: new Set([38]),
    pads: [
      { note: 36, name: 'Kick', color: '#e5483a' },
      { note: 38, name: 'Snare', color: '#e6b422' },
      { note: 42, name: 'Hat', color: '#3fae4a' },
      { note: 46, name: 'Open Hat', color: '#3fae4a' },
      { note: 39, name: 'Clap', color: '#3b82c4' }
    ]
  }
};

export const Disabled: Story = { args: { disabled: true, sounding: new Set<number>() } };

/** The kick is at the bottom left; Space plays the focused pad, two fingers two pads. */
export const Play: Story = {
  args: { sounding: new Set<number>() },
  play: async ({ args, canvasElement }) => {
    const canvas = within(canvasElement);
    const kick = canvas.getByRole('button', { name: 'Kick C1' });
    const buttons = canvas.getAllByRole('button');
    await expect(buttons[12]).toBe(kick);
    kick.focus();
    await fireEvent.keyDown(kick, { key: ' ' });
    await expect(args.noteOn).toHaveBeenCalledWith({ pitch: 36, velocity: 100 });
    await waitFor(() => expect(kick).toHaveAttribute('aria-pressed', 'true'));
    await fireEvent.keyUp(kick, { key: ' ' });
    await expect(args.noteOff).toHaveBeenCalledWith(36);
    const snare = canvas.getByRole('button', { name: 'Snare D1' });
    const rect = snare.getBoundingClientRect();
    await fireEvent.pointerDown(snare, {
      pointerId: 7,
      pointerType: 'touch',
      clientX: rect.left + 4,
      clientY: rect.bottom - 1
    });
    await expect(args.noteOn).toHaveBeenCalledWith(expect.objectContaining({ pitch: 38 }));
    await fireEvent.pointerUp(snare, { pointerId: 7, pointerType: 'touch' });
    await expect(args.noteOff).toHaveBeenCalledWith(38);
  }
};
