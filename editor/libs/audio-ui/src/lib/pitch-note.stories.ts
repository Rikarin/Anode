import { signal } from '@angular/core';
import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnPitchNote, type HotspotDrag } from './pitch-note';

/**
 * A Flex Pitch note in the Audio Track Editor: the blob at its pitch with its played curve through it, and four hotspots
 * (drift, vibrato, formant, gain) on a selected or hovered note. ↑/↓ nudge the pitch a semitone (⌥: a cent); the
 * hotspots take arrows too. The owner places and sizes it.
 */
const meta: Meta<AnPitchNote> = {
  title: 'Audio UI/Pitch Note',
  component: AnPitchNote,
  args: { width: 120, height: 16, cents: 6012, label: 'C3 +12', color: '#30a0e0' },
  render: args => ({
    props: args,
    template: `
      <div class="bg-surface relative h-16 w-[200px]">
        <an-pitch-note
          class="left-10 top-6"
          [width]="width"
          [height]="height"
          [cents]="cents"
          [label]="label"
          [color]="color"
          [selected]="selected"
          [curve]="curve"
        />
      </div>
    `
  })
};

export default meta;
type Story = StoryObj<AnPitchNote>;

export const Note: Story = {};

/** Selected: a white edge and its hotspots showing. */
export const Selected: Story = { args: { selected: true, curve: '0,9 20,7 40,10 60,6 80,9 100,7 120,8' } };

export const Green: Story = { args: { color: '#4cb763', label: 'E3 −8', cents: 6392 } };

/** ↑ nudges a semitone, ⌥↑ a cent; a hotspot's ↓ lowers it (5 pixels' worth). */
export const Keyboard: Story = {
  render: () => {
    const cents = signal(6000);
    const vibrato = signal(1);
    const nudge = (by: number) => cents.update(c => c + by);
    const hotspot = (drag: HotspotDrag) => {
      if (drag.hotspot === 'vibrato') {
        vibrato.update(v => Math.round(Math.max(0, v - drag.dy * 0.02) * 100) / 100);
      }
    };
    return {
      props: { cents, vibrato, nudge, hotspot },
      template: `
        <div class="bg-surface relative h-16 w-[200px]">
          <an-pitch-note
            class="left-10 top-6"
            [width]="120"
            [height]="16"
            [cents]="cents()"
            [label]="'pitch ' + cents()"
            [vibrato]="vibrato()"
            [selected]="true"
            (nudge)="nudge($event)"
            (hotspotDrag)="hotspot($event)"
          />
        </div>
      `
    };
  },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    const note = canvas.getByTestId('pitch-note');
    note.focus();
    await userEvent.keyboard('{ArrowUp}');
    await expect(note).toHaveAttribute('aria-valuenow', '6100');
    await userEvent.keyboard('{Alt>}{ArrowDown}{/Alt}');
    await expect(note).toHaveAttribute('aria-valuenow', '6099');
    const vibrato = canvas.getByRole('slider', { name: 'Vibrato' });
    vibrato.focus();
    await userEvent.keyboard('{ArrowDown}');
    await expect(vibrato).toHaveAttribute('aria-valuetext', '90 %');
  }
};
