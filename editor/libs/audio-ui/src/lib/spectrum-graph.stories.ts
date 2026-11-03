import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, within } from 'storybook/test';
import { AnSpectrumGraph } from './spectrum-graph';

/** The Mastering Assistant's analysis: ISO 266 third-octave bands, 25 Hz … 20 kHz. */
const Bands = [
  25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000,
  5000, 6300, 8000, 10000, 12500, 16000, 20000
];
const tilt = (slope: number, ripple: number) =>
  Bands.map((hz, i) => Math.round((slope * Math.log2(hz / 1000) + Math.sin(i * 0.8) * ripple) * 10) / 10);

/**
 * A spectrum against a reference curve, as the Mastering Assistant shows its analysis: the measured spectrum filled,
 * the character's curve dashed.
 */
const meta: Meta<AnSpectrumGraph> = {
  title: 'Audio UI/Spectrum graph',
  component: AnSpectrumGraph,
  args: { bands: Bands, spectrum: tilt(-1.2, 1.5), curve: tilt(-0.45, 0), label: 'Spectrum against the Clean curve' },
  render: args => ({
    props: args,
    template: `
      <div class="bg-surface w-96 p-3">
        <an-spectrum-graph [bands]="bands" [spectrum]="spectrum" [curve]="curve" [label]="label" />
      </div>
    `
  })
};

export default meta;
type Story = StoryObj<AnSpectrumGraph>;

export const AgainstCurve: Story = {
  play: async ({ canvasElement }) => {
    const graph = within(canvasElement).getByRole('img', { name: 'Spectrum against the Clean curve' });
    await expect(graph.querySelectorAll('polyline')).toHaveLength(2);
  }
};

/** Before the first analysis: the grid only. */
export const NotAnalysed: Story = { args: { spectrum: [], curve: [], label: 'Spectrum (not analysed)' } };
