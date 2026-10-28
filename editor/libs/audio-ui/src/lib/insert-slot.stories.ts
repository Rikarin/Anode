import { signal } from '@angular/core';
import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnInsertSlot } from './insert-slot';

/**
 * An insert slot of a channel strip (Logic's Audio FX): the bypass key, the plugin's name (opens its editor) and a
 * chevron for its menu; an empty slot is a sunken well that chooses a plugin. ⌥↑ and ⌥↓ move the insert.
 */
const meta: Meta<AnInsertSlot> = {
  title: 'Audio UI/Insert slot',
  component: AnInsertSlot,
  args: { name: 'Compressor', channel: 'Vocals', index: 0, bypassed: false, problem: false },
  render: args => ({
    props: args,
    template: `
      <div class="strip-body w-21 p-1">
        <an-insert-slot [name]="name" [channel]="channel" [index]="index" [bypassed]="bypassed" [problem]="problem" />
      </div>
    `
  })
};

export default meta;
type Story = StoryObj<AnInsertSlot>;

export const Filled: Story = {};

/** Bypassed: the name greys out and the power key is off. */
export const Bypassed: Story = { args: { bypassed: true } };

/** The host bypassed it after a crash: a red edge, the name struck through. */
export const Problem: Story = { args: { name: 'Crashy', problem: true } };

/** An empty well; hovering or focusing it shows "Audio FX". */
export const Empty: Story = { args: { name: null, index: 2 } };

/** A strip's slot list, wired to local state: bypass keys toggle, ⌥↓ moves an insert down. */
export const List: Story = {
  render: () => {
    const inserts = signal([
      { name: 'Channel EQ', bypassed: false },
      { name: 'Compressor', bypassed: false },
      { name: 'Test Gain', bypassed: true }
    ]);
    const bypass = (i: number) =>
      inserts.update(list => list.map((x, j) => (i === j ? { ...x, bypassed: !x.bypassed } : x)));
    const move = (i: number, by: number) =>
      inserts.update(list => {
        const next = [...list];
        const [moved] = next.splice(i, 1);
        if (moved) {
          next.splice(Math.min(next.length, Math.max(0, i + by)), 0, moved);
        }
        return next;
      });
    return {
      props: { inserts, bypass, move },
      template: `
        <div class="strip-body flex w-21 flex-col gap-0.5 p-1">
          @for (insert of inserts(); track insert.name; let i = $index) {
            <an-insert-slot
              channel="Vocals"
              [name]="insert.name"
              [index]="i"
              [bypassed]="insert.bypassed"
              (bypassToggle)="bypass(i)"
              (move)="move(i, $event)"
            />
          }
          <an-insert-slot channel="Vocals" [name]="null" [index]="inserts().length" />
        </div>
      `
    };
  },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    const bypass = canvas.getByRole('button', { name: 'Bypass Compressor on Vocals' });
    await expect(bypass).toHaveAttribute('aria-pressed', 'false');
    await userEvent.click(bypass);
    await expect(bypass).toHaveAttribute('aria-pressed', 'true');
    canvas.getByRole('button', { name: 'Insert 1 on Vocals: Channel EQ' }).focus();
    await userEvent.keyboard('{Alt>}{ArrowDown}{/Alt}');
    await expect(canvas.getByRole('button', { name: 'Insert 2 on Vocals: Channel EQ' })).toBeInTheDocument();
    await expect(
      canvas.getByRole('button', { name: 'Insert 4 on Vocals: empty, choose a plugin' })
    ).toBeInTheDocument();
  }
};
