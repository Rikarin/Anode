import { signal } from '@angular/core';
import type { Meta, StoryObj } from '@storybook/angular-vite';
import { AnChannelStrip } from './channel-strip';
import type { MeterSource } from './peak-meter';

const playing: MeterSource = () => {
  const t = performance.now() / 1000;
  const level = 0.3 + 0.35 * Math.abs(Math.sin(t * 3.1));
  return { left: level, right: level * 0.9 };
};

/** A mixer channel strip: pan, fader with meter, mute and solo, and the channel's name on its colour. */
const meta: Meta<AnChannelStrip> = {
  title: 'Audio UI/Channel strip',
  component: AnChannelStrip,
  args: { name: 'Guitar', color: '#46a758', volumeDb: -3.5, pan: -0.25, mute: false, solo: false, meter: playing }
};

export default meta;
type Story = StoryObj<AnChannelStrip>;

export const Default: Story = {};

export const MutedAndSoloed: Story = { args: { mute: true, solo: true } };

export const Master: Story = { args: { name: 'Stereo Out', master: true, color: 'var(--color-foreground-muted)' } };

/** A small mixer: the strips are wired to local state, as the editor wires them to the Server. */
export const Mixer: Story = {
  render: () => {
    const strips = signal([
      { name: 'Drums', color: '#e5484d', volumeDb: 0, pan: 0, mute: false, solo: false },
      { name: 'Bass', color: '#f76b15', volumeDb: -4, pan: -0.1, mute: false, solo: false },
      { name: 'Vocals', color: '#0090ff', volumeDb: -2, pan: 0.15, mute: false, solo: true }
    ]);
    const update = (i: number, change: object) =>
      strips.update(list => list.map((s, j) => (i === j ? { ...s, ...change } : s)));
    return {
      props: { strips, update, playing },
      template: `
        <div class="flex gap-2">
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
