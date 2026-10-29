import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, waitFor, within } from 'storybook/test';
import { AnMappingGraph } from './mapping-graph';

/**
 * A Smart Control's mapping graph, as the Smart Controls inspector shows it: the control's value (x) to its parameter's
 * (y). Points are sliders: ↑/↓ for the output, ←/→ along x, Enter adds a point after the focused one, Delete removes a
 * middle one; click the well to add a point on the curve, double-click a point to remove it.
 */
const meta: Meta<AnMappingGraph> = {
  title: 'Audio UI/Mapping Graph',
  component: AnMappingGraph,
  args: {
    graph: [
      { x: 0, y: 0 },
      { x: 1, y: 1 }
    ],
    label: 'Cutoff mapping'
  }
};

export default meta;
type Story = StoryObj<AnMappingGraph>;

export const Linear: Story = {};

/** A curve that rises fast, then levels off. */
export const Curve: Story = {
  args: {
    graph: [
      { x: 0, y: 0 },
      { x: 0.25, y: 0.6 },
      { x: 0.6, y: 0.85 },
      { x: 1, y: 1 }
    ]
  }
};

/** Inverted: drawn (and read out) as 1 − y, as the mapping applies it. */
export const Inverted: Story = {
  args: {
    invert: true,
    graph: [
      { x: 0, y: 0 },
      { x: 0.5, y: 0.8 },
      { x: 1, y: 1 }
    ]
  }
};

/** A step: two points on one x. */
export const Step: Story = {
  args: {
    width: 120,
    height: 72,
    graph: [
      { x: 0, y: 0 },
      { x: 0.5, y: 0 },
      { x: 0.5, y: 1 },
      { x: 1, y: 1 }
    ]
  }
};

export const Disabled: Story = { args: { disabled: true } };

/** ↑ raises the first point; Enter adds one halfway to the next; ←/→ move it; Delete removes it again. */
export const Keyboard: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    const first = canvas.getByRole('slider', { name: 'Point 1' });
    first.focus();
    await userEvent.keyboard('{ArrowUp}{ArrowUp}');
    await expect(first).toHaveAttribute('aria-valuenow', '10');
    await userEvent.keyboard('{ArrowRight}');
    await expect(first).toHaveAttribute('aria-valuetext', 'In 0 %, out 10 %');
    await userEvent.keyboard('{Enter}');
    await expect(canvas.getAllByRole('slider')).toHaveLength(3);
    const added = canvas.getByRole('slider', { name: 'Point 2' });
    await waitFor(() => expect(added).toHaveFocus());
    await expect(added).toHaveAttribute('aria-valuetext', 'In 50 %, out 55 %');
    await userEvent.keyboard('{ArrowLeft}{End}');
    await expect(added).toHaveAttribute('aria-valuetext', 'In 45 %, out 100 %');
    await userEvent.keyboard('{Delete}');
    await expect(canvas.getAllByRole('slider')).toHaveLength(2);
  }
};
