import { signal } from '@angular/core';
import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, fireEvent, userEvent, waitFor, within } from 'storybook/test';
import { AnStepLane, type StepBar, type StepBarKey, type StepLaneStroke } from './step-lane';

/** Bars of a CC lane on a 24 px grid (a 1/16 at 96 px a quarter), as beams to the next event. */
function ramp(count: number, height = 64): StepBar[] {
  return Array.from({ length: count }, (_, i) => {
    const value = Math.round((127 * i) / Math.max(1, count - 1));
    return { x: i * 24, width: 23, y: 4 + (1 - value / 127) * (height - 8), value, valueText: `${value}` };
  });
}

const grid = Array.from({ length: 17 }, (_, i) => i * 24);
const bars = [0, 96 * 4];

/**
 * A lane of the Step Editor: a header (projected: the lane's name and its controls) and a body of value bars on the
 * Piano Roll's time scale over its grid. The pencil paints a value per step; the line draws from point to point
 * (previewed while dragging). Each bar is a slider: ←/→ move between bars, ↑/↓ change the value (as `barKey`).
 */
const meta: Meta<AnStepLane> = {
  title: 'Audio UI/Step Lane',
  component: AnStepLane,
  args: { label: 'CC 74', bars: ramp(16), tool: 'pencil', height: 64, gridLines: grid, barLines: bars },
  render: args => ({
    props: args,
    template: `
      <div class="bg-surface w-[520px]">
        <an-step-lane [label]="label" [bars]="bars" [tool]="tool" [height]="height" [gridLines]="gridLines" [barLines]="barLines">
          <span class="text-foreground text-[12px] font-semibold">{{ label }}</span>
        </an-step-lane>
      </div>
    `
  })
};

export default meta;
type Story = StoryObj<AnStepLane>;

export const ControlRamp: Story = {};

/** Note velocities: thin bars at the notes' starts, two selected. */
export const Velocity: Story = {
  args: {
    label: 'Velocity',
    min: 1,
    bars: [0, 96, 192, 288].map((x, i) => ({
      x,
      width: 3,
      y: 4 + (1 - (40 + i * 25) / 127) * 56,
      value: 40 + i * 25,
      valueText: `${40 + i * 25}`,
      selected: i % 2 === 1
    }))
  }
};

/** Pitch bend, with its centre line. */
export const PitchBend: Story = {
  args: { label: 'Pitch Bend', min: -8192, max: 8191, bars: [], zeroY: 32 }
};

/** Wired to local state: a line drag comes out as a stroke of its ends; ↑ on a bar raises it. */
export const Gestures: Story = {
  render: () => {
    const list = signal(ramp(4));
    const last = signal('');
    return {
      props: {
        list,
        last,
        grid,
        onStroke: (stroke: StepLaneStroke) =>
          last.set(`${stroke.tool} ${stroke.points.map(p => `${Math.round(p.x)},${Math.round(p.y)}`).join(' ')}`),
        onKey: (key: StepBarKey) =>
          list.update(bars =>
            bars.map((b, i) =>
              i === key.index ? { ...b, value: b.value + key.by, valueText: `${b.value + key.by}` } : b
            )
          )
      },
      template: `
        <div class="bg-surface w-[520px]">
          <an-step-lane label="CC 74" tool="line" testId="lane" [bars]="list()" [gridLines]="grid" (stroke)="onStroke($event)" (barKey)="onKey($event)">
            <span class="text-foreground text-[12px] font-semibold">CC 74</span>
          </an-step-lane>
          <output class="text-foreground text-xs" data-testid="last">{{ last() }}</output>
        </div>
      `
    };
  },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    const body = canvas.getByRole('group', { name: 'CC 74 lane' });
    const rect = body.getBoundingClientRect();
    await fireEvent.pointerDown(body, { button: 0, pointerId: 1, clientX: rect.left + 10, clientY: rect.top + 60 });
    await fireEvent.pointerMove(body, { pointerId: 1, clientX: rect.left + 100, clientY: rect.top + 30 });
    await fireEvent.pointerUp(body, { pointerId: 1, clientX: rect.left + 200, clientY: rect.top + 4 });
    await waitFor(() => expect(canvas.getByTestId('last')).toHaveTextContent('line 10,60 200,4'));

    const first = canvas.getByRole('slider', { name: 'CC 74 1' });
    first.focus();
    await userEvent.keyboard('{ArrowUp}');
    await expect(first).toHaveAttribute('aria-valuenow', '1');
    await userEvent.keyboard('{ArrowRight}');
    await expect(canvas.getByRole('slider', { name: 'CC 74 2' })).toHaveFocus();
  }
};
