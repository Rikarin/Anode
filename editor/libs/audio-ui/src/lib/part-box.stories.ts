import { signal } from '@angular/core';
import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, fn, userEvent, waitFor, within } from 'storybook/test';
import { AnPartBox, type PartBoxTool } from './part-box';

/**
 * Logic's Part box for the Score Editor: note and rest values (dotted, triplet), articulations, dynamics, lines, text,
 * lyrics, chord symbols and pedal. A chip arms its tool; the armed chip again disarms it.
 */
const meta: Meta<AnPartBox> = {
  title: 'Audio UI/Part box',
  component: AnPartBox,
  args: {
    tool: null,
    dotted: false,
    triplet: false,
    disabled: false,
    toolChange: fn(),
    dottedChange: fn(),
    tripletChange: fn()
  },
  render: args => ({
    props: args,
    template: `
      <an-part-box
        class="bg-surface h-[36rem] w-44"
        [tool]="tool"
        [dotted]="dotted"
        [triplet]="triplet"
        [disabled]="disabled"
        (toolChange)="toolChange($event)"
        (dottedChange)="dottedChange($event)"
        (tripletChange)="tripletChange($event)"
      />
    `
  })
};

export default meta;
type Story = StoryObj<AnPartBox>;

export const Pointer: Story = {};

/** A quarter note armed, triplets on. */
export const QuarterTriplet: Story = {
  args: { tool: { kind: 'note', value: 'quarter', dots: 0, triplet: true }, triplet: true }
};

/** The mf dynamic armed. */
export const Dynamic: Story = { args: { tool: { kind: 'symbol', symbol: 'dynamic', text: 'mf' } } };

export const Disabled: Story = { args: { disabled: true } };

/** Wired to local state: a chip arms its tool, arrows move within its group, the armed chip disarms. */
export const Keyboard: Story = {
  render: () => {
    const tool = signal<PartBoxTool | null>(null);
    const dotted = signal(false);
    return {
      props: { tool, dotted, describe: (t: PartBoxTool | null) => JSON.stringify(t) },
      template: `
        <an-part-box
          class="bg-surface h-[36rem] w-44"
          [tool]="tool()"
          [dotted]="dotted()"
          (toolChange)="tool.set($event)"
          (dottedChange)="dotted.set($event)"
        />
        <output data-testid="tool">{{ describe(tool()) }}</output>
      `
    };
  },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    await userEvent.click(canvas.getByRole('button', { name: 'Dotted' }));
    await userEvent.click(canvas.getByRole('radio', { name: 'Eighth note' }));
    await waitFor(() =>
      expect(canvas.getByTestId('tool')).toHaveTextContent('{"kind":"note","value":"eighth","dots":1,"triplet":false}')
    );
    await expect(canvas.getByRole('radio', { name: 'Eighth note' })).toHaveAttribute('aria-checked', 'true');
    canvas.getByRole('radio', { name: 'Staccato' }).focus();
    await userEvent.keyboard('{ArrowRight}');
    await expect(canvas.getByRole('radio', { name: 'Staccatissimo' })).toHaveFocus();
    await userEvent.keyboard(' ');
    await waitFor(() => expect(canvas.getByTestId('tool')).toHaveTextContent('staccatissimo'));
    await userEvent.click(canvas.getByRole('radio', { name: 'Staccatissimo' }));
    await waitFor(() => expect(canvas.getByTestId('tool')).toHaveTextContent('null'));
    await userEvent.click(canvas.getByRole('radio', { name: 'Dynamic mf' }));
    await waitFor(() => expect(canvas.getByTestId('tool')).toHaveTextContent('"text":"mf"'));
  }
};
