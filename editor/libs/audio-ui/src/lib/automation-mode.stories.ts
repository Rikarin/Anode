import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, within } from 'storybook/test';
import { AnAutomationMode } from './automation-mode';

/**
 * A track's automation mode as on Logic's track headers and strips: an LED in the mode's colour and its name. Its
 * owner attaches the mode menu to the button.
 */
const meta: Meta<AnAutomationMode> = {
  title: 'Audio UI/Automation mode',
  component: AnAutomationMode,
  args: { mode: 'read', channel: 'Vocals' },
  render: args => ({
    props: args,
    template: `<div class="bg-surface p-3"><button anAutomationMode [mode]="mode" [channel]="channel"></button></div>`
  })
};

export default meta;
type Story = StoryObj<AnAutomationMode>;

export const Read: Story = {
  play: async ({ canvasElement }) => {
    const button = within(canvasElement).getByRole('button', { name: 'Automation mode of Vocals: Read' });
    await expect(button).toHaveTextContent('Read');
    await expect(button).toHaveAttribute('aria-haspopup', 'menu');
    button.focus();
    await expect(button).toHaveFocus();
  }
};

/** Every mode, in Logic's colours. */
export const AllModes: Story = {
  render: () => ({
    props: { modes: ['off', 'read', 'touch', 'latch', 'write'] },
    template: `
      <div class="bg-surface flex gap-2 p-3">
        @for (mode of modes; track mode) {
          <button anAutomationMode [mode]="mode" channel="Vocals"></button>
        }
      </div>
    `
  }),
  play: async ({ canvasElement }) => {
    await expect(within(canvasElement).getAllByRole('button')).toHaveLength(5);
    await expect(within(canvasElement).getByRole('button', { name: 'Automation mode of Vocals: Latch' })).toBeVisible();
  }
};
