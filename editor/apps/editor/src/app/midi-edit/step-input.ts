import type { StepInputState } from '@anode/api-client';

/** Step input when nothing has set it: off, a 1/16 at velocity 100, at the project start (StepInputState). */
export const DefaultStepInput: StepInputState = {
  on: false,
  regionId: null,
  trackId: null,
  positionTick: 0,
  noteValueTicks: 240,
  dotted: false,
  triplet: false,
  velocity: 100,
  chord: false,
  midiInput: false
};

/** The Step Input Keyboard's note values (Logic's), longest first: ticks and labels. */
export const NoteValues: readonly { ticks: number; label: string; name: string }[] = [
  { ticks: 3840, label: '1', name: 'Whole note' },
  { ticks: 1920, label: '1/2', name: 'Half note' },
  { ticks: 960, label: '1/4', name: 'Quarter note' },
  { ticks: 480, label: '1/8', name: 'Eighth note' },
  { ticks: 240, label: '1/16', name: 'Sixteenth note' },
  { ticks: 120, label: '1/32', name: 'Thirty-second note' },
  { ticks: 60, label: '1/64', name: 'Sixty-fourth note' },
  { ticks: 30, label: '1/128', name: 'Hundred-twenty-eighth note' }
];

/** A step's length: the note value, × 1.5 dotted, × 2/3 triplet (rounded to a tick). */
export function stepLength(state: Pick<StepInputState, 'noteValueTicks' | 'dotted' | 'triplet'>): number {
  return Math.max(1, Math.round(state.noteValueTicks * (state.dotted ? 1.5 : 1) * (state.triplet ? 2 / 3 : 1)));
}

/** A step to enter: the pitches of a chord (one key: a single note), and the highest velocity among them. */
export interface StepChord {
  pitches: number[];
  velocity: number;
}

/**
 * Turns a MIDI keyboard's notes into steps as the host does while MIDI step input is on (docs/08-phase-7.md, IPC:
 * `StepInputMidiMessage`): the keys that went down since the previous step make one step, sent when the first of them
 * is released, at the highest of their velocities. Keys released after that do nothing.
 */
export class StepChordCollector {
  private readonly down = new Map<number, number>();
  private readonly stale = new Set<number>();

  /** A key went down. */
  noteOn(pitch: number, velocity: number): void {
    this.stale.delete(pitch);
    this.down.set(pitch, Math.max(velocity, this.down.get(pitch) ?? 0));
  }

  /** A key went up: the step, when it is the first release since the keys went down; else null. */
  noteOff(pitch: number): StepChord | null {
    if (this.stale.delete(pitch) || !this.down.has(pitch)) {
      return null;
    }
    const pitches = [...this.down.keys()].sort((a, b) => a - b);
    const velocity = Math.max(...this.down.values());
    for (const p of pitches) {
      if (p !== pitch) {
        this.stale.add(p);
      }
    }
    this.down.clear();
    return { pitches, velocity };
  }

  /** Forgets every key (step input turned off). */
  reset(): void {
    this.down.clear();
    this.stale.clear();
  }
}
