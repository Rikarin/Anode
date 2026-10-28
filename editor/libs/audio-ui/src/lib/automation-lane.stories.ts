import { signal } from '@angular/core';
import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, userEvent, within } from 'storybook/test';
import { AnAutomationLane, type AutomationHandle, type AutomationPointKey } from './automation-lane';

const points: AutomationHandle[] = [
  { x: 20, y: 40, selected: false, valueText: '−6.0 dB' },
  { x: 140, y: 18, selected: true, valueText: '0.0 dB' },
  { x: 260, y: 52, selected: false, valueText: '−12.0 dB' },
  { x: 380, y: 30, selected: false, valueText: '−3.0 dB' }
];
const path = 'M 0 40 L 20 40 L 140 18 Q 200 18 260 52 L 380 30 L 480 30';
const area = `${path} L 480 72 L 0 72 Z`;

/**
 * An automation lane as Logic draws it: the curve over a faint fill, its points as handles (filled when selected) and a
 * selected range as a band. Its owner computes the geometry and handles the pointer; keys on a handle come out as
 * `pointKey`.
 */
const meta: Meta<AnAutomationLane> = {
  title: 'Audio UI/Automation lane',
  component: AnAutomationLane,
  args: {
    label: 'Vocals Volume automation',
    path,
    area,
    points,
    height: 72,
    mode: 'read',
    range: null,
    color: 'var(--color-primary)'
  },
  render: args => ({
    props: args,
    template: `
      <div class="bg-surface w-[480px]">
        <an-automation-lane
          [label]="label"
          [path]="path"
          [area]="area"
          [points]="points"
          [height]="height"
          [mode]="mode"
          [range]="range"
          [color]="color"
        />
      </div>
    `
  })
};

export default meta;
type Story = StoryObj<AnAutomationLane>;

export const Volume: Story = {};

export const SelectedRange: Story = { args: { range: { x0: 120, x1: 300 } } };

/** Mode Off: the lane does not play, and is drawn dimmed. */
export const Off: Story = { args: { mode: 'off' } };

/** In a track's colour. */
export const TrackColour: Story = { args: { color: 'var(--track-6)', label: 'Vocals Pan automation' } };

/** Arrow keys on a focused point come out as `pointKey`; here ArrowUp raises it. */
export const Keyboard: Story = {
  render: () => {
    const handles = signal(points);
    const last = signal('');
    const onPoint = (event: AutomationPointKey) => {
      last.set(`${event.key} ${event.index}${event.fine ? ' fine' : ''}`);
      handles.update(list =>
        list.map((p, i) => (i === event.index && event.key === 'up' ? { ...p, y: p.y - 4, valueText: 'up' } : p))
      );
    };
    return {
      props: { handles, last, onPoint, path, area },
      template: `
        <div class="bg-surface w-[480px]">
          <an-automation-lane
            label="Vocals Volume automation"
            [path]="path"
            [area]="area"
            [points]="handles()"
            [height]="72"
            (pointKey)="onPoint($event)"
          />
          <output class="text-foreground text-xs" data-testid="last">{{ last() }}</output>
        </div>
      `
    };
  },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    const point = canvas.getByRole('slider', { name: 'Vocals Volume automation point 2' });
    await expect(point).toHaveAttribute('aria-valuetext', '0.0 dB');
    point.focus();
    await userEvent.keyboard('{ArrowUp}');
    await expect(canvas.getByTestId('last')).toHaveTextContent('up 1');
    await expect(point).toHaveAttribute('aria-valuetext', 'up');
    await userEvent.keyboard('{Shift>}{Delete}{/Shift}');
    await expect(canvas.getByTestId('last')).toHaveTextContent('delete 1 fine');
  }
};
