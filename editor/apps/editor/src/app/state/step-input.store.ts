import { Injectable, computed, inject, signal } from '@angular/core';
import type { CommandResult, StepInputRequest, StepInputState } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { DefaultStepInput, stepLength, stepTarget } from '../midi-edit/step-input';
import { ProjectStore } from './project.store';
import { SelectionStore } from './selection.store';
import { TransportStore } from './transport.store';

/**
 * Step input (docs/08-phase-7.md, "Step input"): the Server holds the state (on, where the next step goes, the note
 * value, velocity, chord mode, MIDI input) and reports every change as a `stepInput` event, so the Step Input Keyboard,
 * the Piano Roll's step position and MIDI step input all follow it. Steps are the `stepNotes` and `stepTie` commands
 * (one undo step each, the Server filling in the rest from the state); a rest is `POST /project/step-input/rest`.
 */
@Injectable({ providedIn: 'root' })
export class StepInputStore {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly notify = inject(Notify);
  private readonly selection = inject(SelectionStore);
  private readonly transport = inject(TransportStore);

  readonly state = signal<StepInputState>(DefaultStepInput);
  /** The changes not answered yet, and the last thing sent (each waits for the one before). */
  private pending = 0;
  private queue: Promise<unknown> = Promise.resolve();
  readonly on = computed(() => this.state().on);
  /** The step's length in ticks (dotted and triplet applied). */
  readonly stepTicks = computed(() => stepLength(this.state()));

  constructor() {
    const events = inject(EventStream);
    events.events$.subscribe(message => {
      if (message.type === 'stepInput') {
        // While changes are on their way, their answers (in order) say what the state is.
        if (this.pending === 0) {
          this.state.set(message.state);
        }
      } else if (message.type === 'projectOpened') {
        void this.refresh();
      } else if (message.type === 'projectClosed') {
        this.state.set(DefaultStepInput);
      }
    });
    events.connected$.subscribe(() => void this.refresh());
  }

  /** Fetches the state (no project open: the default). */
  async refresh(): Promise<void> {
    try {
      this.state.set(await this.api.stepInput());
    } catch {
      this.state.set(DefaultStepInput);
    }
  }

  /**
   * Changes the state; left-out properties stay. Changes, steps and rests go to the Server one at a time, in order, and
   * a change shows at once, so quick presses build on each other (Chord on and off again ends off; a key pressed right
   * after Chord stacks).
   */
  set(request: StepInputRequest): Promise<void> {
    const previous = this.state();
    this.state.update(state => withRequest(state, request));
    return this.answered(
      () => this.api.setStepInput(request),
      () => previous,
      'Could not change step input'
    );
  }

  /** Turns step input on or off: on, into `regionId` (or a new region on `trackId`) from `positionTick`. */
  toggle(
    on: boolean,
    at?: { regionId?: string | null; trackId?: string | null; positionTick?: number }
  ): Promise<void> {
    return this.set({ on, ...(at ?? {}) });
  }

  /**
   * Turns step input on at the playhead into the selected MIDI region (or a new region on the selected instrument or
   * MIDI track); false when nothing is selected that takes steps. With `midiInput`, steps come from MIDI input too.
   */
  async start(midiInput?: boolean): Promise<boolean> {
    const target = stepTarget(
      this.project.midiRegions(),
      this.project.tracks(),
      this.selection.regions(),
      this.selection.track()
    );
    if (!target) {
      return false;
    }
    await this.set({
      on: true,
      regionId: target.regionId ?? '',
      trackId: target.trackId,
      positionTick: Math.max(0, Math.round(this.transport.positionAt(performance.now()))),
      ...(midiInput === undefined ? {} : { midiInput })
    });
    return true;
  }

  /**
   * MIDI step input on or off (Logic's MIDI In button; the `midi.stepInput` key command): on, it starts step input
   * from MIDI where {@link start} does (or keeps the position when step input is already on); off, step input stays
   * on only while the Step Input Keyboard is open (`keyboardOpen`).
   */
  async toggleMidi(keyboardOpen: boolean): Promise<void> {
    const state = this.state();
    if (state.on && state.midiInput) {
      await this.set({ midiInput: false, on: keyboardOpen });
    } else if (state.on) {
      await this.set({ midiInput: true });
    } else if (!(await this.start(true))) {
      this.notify.info('Select a MIDI region, or an instrument or MIDI track, for MIDI step input.');
    }
  }

  /** A step of notes (a chord when several), at the step position. */
  step(pitches: number[], velocity?: number): Promise<CommandResult | null> {
    return this.inTurn(() =>
      this.project.execute(
        [{ type: 'stepNotes', pitches, ...(velocity === undefined ? {} : { velocity }) }],
        'Step Input'
      )
    );
  }

  /** A tie: lengthens the last step by one step. */
  tie(): Promise<CommandResult | null> {
    return this.inTurn(() => this.project.execute([{ type: 'stepTie' }], 'Step Input Tie'));
  }

  /** A rest: advances by one step (not an edit). */
  rest(): Promise<void> {
    return this.answered(
      () => this.api.stepRest(),
      () => this.state(),
      'Could not enter a rest'
    );
  }

  /** Runs `send` after everything sent before it. */
  private inTurn<T>(send: () => Promise<T>): Promise<T> {
    const sent = this.queue.then(send);
    this.queue = sent.then(
      () => undefined,
      () => undefined
    );
    return sent;
  }

  /**
   * Sends a change in turn; the last answer sets the state (events in the meantime describe states it overtakes). On
   * failure the state goes back to `fallback`'s.
   */
  private answered(
    send: () => Promise<StepInputState>,
    fallback: () => StepInputState,
    failure: string
  ): Promise<void> {
    const before = fallback();
    this.pending++;
    return this.inTurn(async () => {
      try {
        const answer = await send();
        if (this.pending === 1) {
          this.state.set(answer);
        }
      } catch (error) {
        if (this.pending === 1) {
          this.state.set(before);
        }
        this.notify.failure(error, failure);
      } finally {
        this.pending--;
      }
    });
  }
}

/** The state as a request will leave it (its left-out properties stay; an empty region or track id is none). */
export function withRequest(state: StepInputState, request: StepInputRequest): StepInputState {
  const next: StepInputState = { ...state };
  for (const [key, value] of Object.entries(request) as [keyof StepInputRequest, unknown][]) {
    if (value === undefined || value === null) {
      continue;
    }
    (next as Record<string, unknown>)[key] = (key === 'regionId' || key === 'trackId') && value === '' ? null : value;
  }
  return next;
}
