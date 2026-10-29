import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnDrawbar, DrawbarFootages } from './drawbar';

/**
 * A tonewheel organ's drawbar (the Drawbar Organ's panel): pulled out 0…8, its tab brown, white or black by footage.
 * ↑/→ pull it out a step, ↓/← push it in, Home and End reach 0 and 8; drag it to place it.
 */
const meta: Meta<AnDrawbar> = {
  title: 'Audio UI/Drawbar',
  component: AnDrawbar,
  args: { value: 8, label: '8′', tone: 'white' }
};

export default meta;
type Story = StoryObj<AnDrawbar>;

export const White: Story = {};

export const Brown: Story = { args: { value: 6, label: '16′', tone: 'brown' } };

export const Black: Story = { args: { value: 3, label: '2⅔′', tone: 'black' } };

export const In: Story = { args: { value: 0, label: '1′', tone: 'white' } };

export const Disabled: Story = { args: { value: 4, label: '4′', tone: 'white', disabled: true } };

/** The nine drawbars of the classic "888000000" registration. */
export const Registration: Story = {
  render: () => ({
    props: { bars: DrawbarFootages.map((f, i) => ({ ...f, value: i < 3 ? 8 : 0 })) },
    moduleMetadata: { imports: [AnDrawbar] },
    template: `
      <div class="flex gap-1" role="group" aria-label="Drawbars">
        @for (bar of bars; track bar.label) {
          <an-drawbar [label]="bar.label" [tone]="bar.tone" [value]="bar.value" />
        }
      </div>
    `
  })
};

/** Arrows step it; Home and End reach the ends. */
export const Keyboard: Story = {
  args: { value: 4, label: '4′', tone: 'white' },
  play: async ({ canvasElement }) => {
    const bar = within(canvasElement).getByRole('slider', { name: '4′' });
    bar.focus();
    await userEvent.keyboard('{ArrowUp}{ArrowUp}');
    await expect(bar).toHaveAttribute('aria-valuenow', '6');
    await userEvent.keyboard('{ArrowDown}');
    await expect(bar).toHaveAttribute('aria-valuenow', '5');
    await userEvent.keyboard('{End}');
    await expect(bar).toHaveAttribute('aria-valuenow', '8');
    await userEvent.keyboard('{Home}');
    await expect(bar).toHaveAttribute('aria-valuenow', '0');
  }
};
