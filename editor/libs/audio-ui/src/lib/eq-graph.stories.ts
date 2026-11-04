import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, within } from 'storybook/test';
import { AnEqGraph } from './eq-graph';
import type { EqBand } from './eq-response';

const band = (id: string, label: string, kind: EqBand['kind'], frequency: number, gainDb = 0, on = true): EqBand => ({
  id,
  label,
  kind,
  on,
  frequency,
  gainDb,
  q: 1,
  slope: 1
});

const Bands: readonly EqBand[] = [
  band('hpf', 'Low Cut', 'highPass', 40),
  band('lowShelf', 'Low Shelf', 'lowShelf', 90, 3),
  band('peak1', 'Peak 1', 'bell', 240, -4),
  band('peak2', 'Peak 2', 'bell', 800),
  band('peak3', 'Peak 3', 'bell', 2_800, 5),
  band('peak4', 'Peak 4', 'bell', 6_000),
  band('highShelf', 'High Shelf', 'highShelf', 11_000, 2),
  band('lpf', 'High Cut', 'lowPass', 18_000, 0, false)
];

/** A program's spectrum, falling with frequency. */
const Spectrum = Array.from({ length: 64 }, (_, i) => -24 - i * 0.6 + 4 * Math.sin(i * 0.7));

/** The Channel EQ's and the Linear Phase EQ's display: the curve over the output's spectrum, with a handle per band. */
const meta: Meta<AnEqGraph> = {
  title: 'Audio UI/EQ graph',
  component: AnEqGraph,
  args: { bands: Bands, spectrum: Spectrum, label: 'EQ curve' },
  render: args => ({
    props: args,
    template: `
      <div class="bg-surface w-96 p-3">
        <an-eq-graph [bands]="bands" [spectrum]="spectrum" [label]="label" />
      </div>
    `
  })
};

export default meta;
type Story = StoryObj<AnEqGraph>;

export const WithAnalyser: Story = {
  play: async ({ canvasElement }) => {
    const graph = within(canvasElement);
    await expect(graph.getAllByRole('slider')).toHaveLength(8);
    await expect(graph.getByRole('slider', { name: 'Peak 3' })).toHaveAttribute(
      'aria-valuetext',
      '2.80 kHz, +5.0 dB, Q 1.00'
    );
  }
};

/** Nothing playing: the curve alone. */
export const NoSignal: Story = { args: { spectrum: [] } };
