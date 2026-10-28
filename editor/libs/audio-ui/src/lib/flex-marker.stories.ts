import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, fn, userEvent, within } from 'storybook/test';
import { AnFlexMarker } from './flex-marker';

/** The story's args: the inputs, and spies for the outputs. */
type Args = Pick<AnFlexMarker, 'label' | 'value' | 'min' | 'max' | 'valueText' | 'active'> & {
  nudge: ReturnType<typeof fn>;
  remove: ReturnType<typeof fn>;
};

/**
 * A flex marker over a region's waveform (the Tracks area with Flex shown): a hairline with a tab at its top, lit while
 * it moves. ←/→ nudge it (⇧ further), Delete or a double-click removes it; pointer drags are the owner's.
 */
const meta: Meta<Args> = {
  title: 'Audio UI/Flex Marker',
  component: AnFlexMarker,
  args: {
    label: 'Flex marker 1',
    value: 960,
    min: 1,
    max: 3839,
    valueText: '2 2 1 1',
    nudge: fn(),
    remove: fn()
  },
  render: args => ({
    props: args,
    template: `
      <div class="bg-track-4 relative h-16 w-64 rounded-[4px]">
        <an-flex-marker
          style="left: 64px"
          [label]="label"
          [value]="value"
          [min]="min"
          [max]="max"
          [valueText]="valueText"
          [active]="active"
          (nudge)="nudge($event)"
          (remove)="remove()"
        />
      </div>
    `
  })
};

export default meta;
type Story = StoryObj<Args>;

export const Default: Story = {};

/** Being dragged: lit. */
export const Active: Story = { args: { active: true } };

/** The keyboard nudges and removes it. */
export const Keyboard: Story = {
  play: async ({ canvasElement, args }) => {
    const marker = within(canvasElement).getByRole('slider', { name: 'Flex marker 1' });
    await expect(marker).toHaveAttribute('aria-valuetext', '2 2 1 1');
    marker.focus();
    await userEvent.keyboard('{ArrowRight}');
    await userEvent.keyboard('{Shift>}{ArrowLeft}{/Shift}');
    await userEvent.keyboard('{Delete}');
    await expect(args.nudge).toHaveBeenNthCalledWith(1, { direction: 1, big: false });
    await expect(args.nudge).toHaveBeenNthCalledWith(2, { direction: -1, big: true });
    await expect(args.remove).toHaveBeenCalled();
  }
};
