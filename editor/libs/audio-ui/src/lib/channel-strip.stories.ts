import { signal } from '@angular/core';
import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, within } from 'storybook/test';
import { AnChannelStrip } from './channel-strip';
import { AnInsertSlot } from './insert-slot';
import type { MeterSource } from './peak-meter';
import { AnSendKnob } from './send-knob';

const playing: MeterSource = () => {
  const t = performance.now() / 1000;
  const level = 0.3 + 0.35 * Math.abs(Math.sin(t * 3.1));
  return { left: level, right: level * 0.9 };
};

/**
 * A mixer channel strip as in Logic's Mixer: a graded column with the pan knob and its readout, the dB readout over a
 * metal-capped fader and its scale, the LED meter, mute and solo keys, and the channel's name under a bar of its colour.
 */
const meta: Meta<AnChannelStrip> = {
  title: 'Audio UI/Channel strip',
  component: AnChannelStrip,
  args: {
    name: 'Guitar',
    color: 'var(--track-2)',
    volumeDb: -3.5,
    pan: -0.25,
    mute: false,
    solo: false,
    meter: playing
  }
};

export default meta;
type Story = StoryObj<AnChannelStrip>;

export const Default: Story = {};

export const MutedAndSoloed: Story = { args: { mute: true, solo: true } };

/** The selected track's strip is lighter, as in Logic's Mixer. */
export const Selected: Story = { args: { selected: true } };

export const Master: Story = { args: { name: 'Stereo Out', master: true, color: 'var(--color-foreground-muted)' } };

/** A VCA strip: a fader for its members, with no pan and no meter of its own. */
export const Vca: Story = { args: { name: 'VCA 1', vca: true, color: 'var(--track-12)', volumeDb: -2 } };

/** On the selected strip's signal flow (Logic's signal-flow highlight). */
export const Flow: Story = { args: { name: 'Reverb', flow: true, color: 'var(--track-7)' } };

/** The owner's slot rows over the strip: inserts (one bypassed), an empty slot, a send and the output. */
export const WithSlots: Story = {
  render: args => ({
    props: { ...args, playing },
    template: `
      <div class="bg-surface-sunken flex h-[30rem]">
        <an-channel-strip [name]="name" [color]="color" [volumeDb]="volumeDb" [pan]="pan" [meter]="playing">
          <an-insert-slot channel="Guitar" name="Channel EQ" [index]="0" />
          <an-insert-slot channel="Guitar" name="Compressor" [index]="1" bypassed />
          <an-insert-slot channel="Guitar" [name]="null" [index]="2" />
          <an-send-knob class="mt-1" busName="Bus 1" channel="Guitar" [levelDb]="-8" />
          <button type="button" class="strip-slot text-foreground mt-1 w-full justify-center font-semibold">
            Stereo Out
          </button>
        </an-channel-strip>
      </div>
    `,
    moduleMetadata: { imports: [AnInsertSlot, AnSendKnob] }
  }),
  play: async ({ canvasElement }) => {
    const strip = within(canvasElement).getByRole('group', { name: 'Guitar channel strip' });
    await expect(within(strip).getByRole('button', { name: 'Insert 2 on Guitar: Compressor' })).toBeVisible();
    await expect(within(strip).getByRole('slider', { name: 'Guitar volume' })).toBeVisible();
  }
};

/** A small mixer: the strips are wired to local state, as the editor wires them to the Server. */
export const Mixer: Story = {
  render: () => {
    const strips = signal([
      { name: 'Drums', color: 'var(--track-1)', volumeDb: 0, pan: 0, mute: false, solo: false },
      { name: 'Bass', color: 'var(--track-4)', volumeDb: -4, pan: -0.1, mute: false, solo: false },
      { name: 'Vocals', color: 'var(--track-6)', volumeDb: -2, pan: 0.15, mute: false, solo: true }
    ]);
    const update = (i: number, change: object) =>
      strips.update(list => list.map((s, j) => (i === j ? { ...s, ...change } : s)));
    return {
      props: { strips, update, playing },
      template: `
        <div class="bg-surface-sunken flex h-96">
          @for (s of strips(); track s.name; let i = $index) {
            <an-channel-strip
              [name]="s.name"
              [color]="s.color"
              [volumeDb]="s.volumeDb"
              [pan]="s.pan"
              [mute]="s.mute"
              [solo]="s.solo"
              [meter]="playing"
              (volumeChange)="update(i, { volumeDb: $event })"
              (panChange)="update(i, { pan: $event })"
              (muteToggle)="update(i, { mute: !s.mute })"
              (soloToggle)="update(i, { solo: !s.solo })"
            />
          }
          <an-channel-strip name="Stereo Out" master [meter]="playing" />
        </div>
      `
    };
  }
};
