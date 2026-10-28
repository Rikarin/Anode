import { signal } from '@angular/core';
import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnSendKnob } from './send-knob';

/**
 * A send of a channel strip (Logic's Sends): the bus (a button for the bus menu), pre or post fader, and a small level
 * knob on the fader's taper. Drag vertically (⇧ for fine), arrows and Page keys, Home/End; double-click for 0 dB.
 */
const meta: Meta<AnSendKnob> = {
  title: 'Audio UI/Send knob',
  component: AnSendKnob,
  args: { busName: 'Bus 1', channel: 'Vocals', index: 0, levelDb: -12, preFader: false },
  render: args => ({
    props: args,
    template: `
      <div class="strip-body w-21 p-1">
        <an-send-knob [busName]="busName" [channel]="channel" [index]="index" [levelDb]="levelDb" [preFader]="preFader" />
      </div>
    `
  })
};

export default meta;
type Story = StoryObj<AnSendKnob>;

export const PostFader: Story = {};

export const PreFader: Story = { args: { preFader: true, busName: 'Reverb', levelDb: -6 } };

export const Silent: Story = { args: { levelDb: -144 } };

/** Wired to local state: End, Home and a double-click set the level; the key toggles pre/post. */
export const Keyboard: Story = {
  render: () => {
    const level = signal(-12);
    const pre = signal(false);
    return {
      props: { level, pre },
      template: `
        <div class="strip-body w-21 p-1">
          <an-send-knob
            busName="Bus 1"
            channel="Vocals"
            [levelDb]="level()"
            [preFader]="pre()"
            (levelChange)="level.set($event)"
            (preToggle)="pre.set(!pre())"
          />
        </div>
      `
    };
  },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    const knob = canvas.getByRole('slider', { name: 'Bus 1 send level of Vocals' });
    await expect(knob).toHaveAttribute('aria-valuetext', '−12.0 dB');
    knob.focus();
    await userEvent.keyboard('{End}');
    await expect(knob).toHaveAttribute('aria-valuetext', '+6.0 dB');
    await userEvent.keyboard('{Home}');
    await expect(knob).toHaveAttribute('aria-valuetext', '−∞ dB');
    await userEvent.dblClick(knob);
    await expect(knob).toHaveAttribute('aria-valuetext', '0.0 dB');
    const pre = canvas.getByRole('button', { name: 'Pre-fader send to Bus 1 on Vocals' });
    await userEvent.click(pre);
    await expect(pre).toHaveAttribute('aria-pressed', 'true');
  }
};
