import { signal } from '@angular/core';
import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, fn, userEvent, waitFor, within } from 'storybook/test';
import { AnScoreToolbar, type ScoreToolbarView } from './score-toolbar';

/** The Score Editor's toolbar: the Part box toggle, what it shows, Linear/Page, zoom, Follow, the score windows, Print. */
const meta: Meta<AnScoreToolbar> = {
  title: 'Audio UI/Score toolbar',
  component: AnScoreToolbar,
  args: {
    title: 'Piano',
    view: 'linear',
    zoom: 100,
    follow: true,
    partBox: true,
    printDisabled: false,
    viewChange: fn(),
    zoomBy: fn(),
    followChange: fn(),
    partBoxChange: fn(),
    staffStyles: fn(),
    scoreSets: fn(),
    layout: fn(),
    print: fn()
  },
  render: args => ({
    props: args,
    template: `
      <an-score-toolbar
        class="w-[56rem]"
        [title]="title"
        [view]="view"
        [zoom]="zoom"
        [follow]="follow"
        [partBox]="partBox"
        [printDisabled]="printDisabled"
        (viewChange)="viewChange($event)"
        (zoomBy)="zoomBy($event)"
        (followChange)="followChange($event)"
        (partBoxChange)="partBoxChange($event)"
        (staffStyles)="staffStyles()"
        (scoreSets)="scoreSets()"
        (layout)="layout()"
        (print)="print()"
      />
    `
  })
};

export default meta;
type Story = StoryObj<AnScoreToolbar>;

export const Linear: Story = {};

/** A score set in the page view, zoomed in, not following. */
export const Page: Story = {
  args: { title: 'Score set: Full Score', view: 'page', zoom: 150, follow: false, partBox: false }
};

/** Nothing to print yet. */
export const Empty: Story = { args: { title: '', printDisabled: true } };

/** Wired to local state: the view radios and zoom. */
export const Keyboard: Story = {
  render: () => {
    const view = signal<ScoreToolbarView>('linear');
    const zoom = signal(100);
    return {
      props: { view, zoom, onZoom: (d: 1 | -1) => zoom.update(z => z + 25 * d) },
      template: `
        <an-score-toolbar class="w-[56rem]" title="Piano" [view]="view()" [zoom]="zoom()" (viewChange)="view.set($event)" (zoomBy)="onZoom($event)" />
      `
    };
  },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    canvas.getByRole('radio', { name: 'Linear' }).focus();
    await userEvent.keyboard('{ArrowRight}');
    await waitFor(() => expect(canvas.getByRole('radio', { name: 'Page' })).toHaveAttribute('aria-checked', 'true'));
    await userEvent.click(canvas.getByRole('button', { name: 'Zoom in' }));
    await waitFor(() => expect(canvas.getByTestId('score-zoom')).toHaveTextContent('125 %'));
  }
};
