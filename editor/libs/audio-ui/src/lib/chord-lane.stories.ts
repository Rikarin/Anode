import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, fn, userEvent, within } from 'storybook/test';
import { AnChordLane, type ChordLaneChord } from './chord-lane';
import { chordBlock, chordLabel, chordSpans, chordSymbol, scaleText, type ChordSpelling } from './chord-symbols';

const Bar = 3840;
const PixelsPerTick = 80 / Bar;
const x = (tick: number) => tick * PixelsPerTick;

const progression: (ChordSpelling & { id: string; tick: number })[] = [
  { id: 'c1', tick: 0, root: { step: 'c', alter: 0 }, quality: '', bass: null },
  { id: 'c2', tick: Bar, root: { step: 'a', alter: 0 }, quality: 'm7', bass: null },
  { id: 'c3', tick: 2 * Bar, root: { step: 'f', alter: 0 }, quality: 'maj7', bass: null },
  { id: 'c4', tick: 3 * Bar, root: { step: 'g', alter: 0 }, quality: '7', bass: null },
  { id: 'c5', tick: 4 * Bar, root: { step: 'e', alter: 0 }, quality: 'm', bass: null },
  { id: 'c6', tick: 5 * Bar, root: { step: 'a', alter: 0 }, quality: '7', bass: null },
  { id: 'c7', tick: 6 * Bar, root: { step: 'd', alter: 0 }, quality: 'm7', bass: null },
  { id: 'c8', tick: 7 * Bar, root: { step: 'g', alter: 0 }, quality: '', bass: { step: 'b', alter: 0 } }
];

const opened = fn();
const nudged = fn();
const removed = fn();
const added = fn();

function blocks(selected: string | null = null): ChordLaneChord[] {
  return chordSpans(progression, Bar).map(span => ({
    id: span.chord.id,
    symbol: chordSymbol(span.chord),
    label: `${chordLabel(span.chord)}, bar ${span.startTick / Bar + 1}`,
    selected: span.chord.id === selected,
    ...chordBlock(span, x)
  }));
}

/**
 * The chord track's lane (Phase 11): key and scale changes on a thin top row, chords as blocks from their tick to the
 * next with their symbols. Click a chord or press Return to edit it, drag to move, ←/→ to nudge, Delete to remove;
 * double-click an empty stretch to add a chord or a key change.
 */
const meta: Meta<AnChordLane> = {
  title: 'Audio UI/Chord Lane',
  component: AnChordLane,
  render: args => ({
    props: { ...args, opened, nudged, removed, added },
    template: `
      <div class="bg-surface border-hairline h-9 w-[48rem] border-y">
        <an-chord-lane
          [chords]="chords"
          [scales]="scales"
          (opened)="opened($event.id)"
          (nudged)="nudged($event)"
          (removed)="removed($event)"
          (added)="added($event)"
        />
      </div>
    `
  }),
  args: {
    chords: blocks(),
    scales: [
      { index: 0, text: scaleText({ tonic: { step: 'c', alter: 0 }, scale: 'major' }), x: 0 },
      { index: 1, text: scaleText({ tonic: { step: 'e', alter: 0 }, scale: 'minor' }), x: x(4 * Bar) }
    ]
  }
};

export default meta;
type Story = StoryObj<AnChordLane>;

export const Progression: Story = {};

export const Selected: Story = { args: { chords: blocks('c3') } };

export const Empty: Story = { args: { chords: [], scales: [] } };

/** Return edits the focused chord, arrows nudge it, Delete removes it. */
export const Keyboard: Story = {
  play: async ({ canvasElement }) => {
    const chord = within(canvasElement).getByRole('button', { name: 'Am7, A minor 7th, bar 2' });
    await expect(chord).toHaveTextContent('Am7');
    chord.focus();
    await userEvent.keyboard('{Enter}');
    await expect(opened).toHaveBeenCalledWith('c2');
    await userEvent.keyboard('{Shift>}{ArrowRight}{/Shift}');
    await expect(nudged).toHaveBeenCalledWith({ id: 'c2', steps: 4 });
    await userEvent.keyboard('{Delete}');
    await expect(removed).toHaveBeenCalledWith('c2');
    await expect(within(canvasElement).getByRole('button', { name: 'G/B, G major over B, bar 8' })).toHaveTextContent(
      'G/B'
    );
  }
};
