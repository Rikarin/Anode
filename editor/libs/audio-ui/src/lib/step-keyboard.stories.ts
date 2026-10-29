import { signal } from '@angular/core';
import type { Meta, StoryObj } from '@storybook/angular-vite';
import { expect, fireEvent, fn, userEvent, waitFor, within } from 'storybook/test';
import { AnStepKeyboard } from './step-keyboard';

/**
 * Logic's Step Input Keyboard: the note value (1 … 1/128), dotted and triplet, the velocity (ppp … fff or a value),
 * Rest, Tie and Chord, and keys with an octave shift. Each key entered is a step; keys held together make a chord.
 */
const meta: Meta<AnStepKeyboard> = {
  title: 'Audio UI/Step keyboard',
  component: AnStepKeyboard,
  args: {
    noteValue: 240,
    dotted: false,
    triplet: false,
    velocity: 80,
    chord: false,
    disabled: false,
    noteValueChange: fn(),
    dottedChange: fn(),
    tripletChange: fn(),
    velocityChange: fn(),
    chordChange: fn(),
    rest: fn(),
    tie: fn(),
    step: fn()
  },
  render: args => ({
    props: args,
    template: `
      <an-step-keyboard
        class="bg-surface w-[34rem] p-2"
        [noteValue]="noteValue"
        [dotted]="dotted"
        [triplet]="triplet"
        [velocity]="velocity"
        [chord]="chord"
        [disabled]="disabled"
        (noteValueChange)="noteValueChange($event)"
        (dottedChange)="dottedChange($event)"
        (tripletChange)="tripletChange($event)"
        (velocityChange)="velocityChange($event)"
        (chordChange)="chordChange($event)"
        (rest)="rest()"
        (tie)="tie()"
        (step)="step($event)"
      />
    `
  })
};

export default meta;
type Story = StoryObj<AnStepKeyboard>;

export const Sixteenths: Story = {};

/** A dotted eighth at fff, in chord mode. */
export const DottedChord: Story = { args: { noteValue: 480, dotted: true, velocity: 127, chord: true } };

export const Disabled: Story = { args: { disabled: true } };

/** Wired to local state: the arrows change the note value; Space on a key enters it; Rest, Tie and Chord report. */
export const Keyboard: Story = {
  render: () => {
    const value = signal(240);
    const log = signal<string[]>([]);
    return {
      props: {
        value,
        log,
        onRest: () => log.update(l => [...l, 'rest']),
        onTie: () => log.update(l => [...l, 'tie']),
        onStep: (pitches: number[]) => log.update(l => [...l, pitches.join('+')])
      },
      template: `
        <an-step-keyboard
          class="bg-surface w-[34rem] p-2"
          [noteValue]="value()"
          (noteValueChange)="value.set($event)"
          (rest)="onRest()"
          (tie)="onTie()"
          (step)="onStep($event)"
        />
        <output data-testid="steps">{{ log().join(' ') }}</output>
      `
    };
  },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    const sixteenth = canvas.getByRole('radio', { name: '1/16' });
    await expect(sixteenth).toHaveAttribute('aria-checked', 'true');
    sixteenth.focus();
    await userEvent.keyboard('{ArrowLeft}');
    await expect(canvas.getByRole('radio', { name: '1/8' })).toHaveAttribute('aria-checked', 'true');
    await expect(canvas.getByRole('radio', { name: '1/8' })).toHaveFocus();

    // Two keys down together, then released: one chord.
    const c = canvas.getByRole('button', { name: 'C3' });
    const e = canvas.getByRole('button', { name: 'E3' });
    await fireEvent.keyDown(c, { key: ' ' });
    await fireEvent.keyDown(e, { key: ' ' });
    await fireEvent.keyUp(c, { key: ' ' });
    await fireEvent.keyUp(e, { key: ' ' });
    // Then one key.
    await fireEvent.keyDown(e, { key: 'Enter' });
    await fireEvent.keyUp(e, { key: 'Enter' });
    await waitFor(() => expect(canvas.getByTestId('steps')).toHaveTextContent('60+64 64'));

    canvas.getByRole('button', { name: 'Rest' }).focus();
    await userEvent.keyboard('{Enter}');
    await userEvent.tab();
    await expect(canvas.getByRole('button', { name: 'Tie' })).toHaveFocus();
    await userEvent.keyboard(' ');
    await waitFor(() => expect(canvas.getByTestId('steps')).toHaveTextContent('60+64 64 rest tie'));
    await userEvent.click(canvas.getByRole('button', { name: 'Octave up' }));
    await waitFor(() => expect(canvas.getByRole('button', { name: 'C6' })).toBeInTheDocument());
  }
};
