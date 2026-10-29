import { Injectable, computed, inject, signal } from '@angular/core';
import type { CommandResult, StepInputRequest, StepInputState } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { DefaultStepInput, stepLength } from '../midi-edit/step-input';
import { ProjectStore } from './project.store';

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

  readonly state = signal<StepInputState>(DefaultStepInput);
  readonly on = computed(() => this.state().on);
  /** The step's length in ticks (dotted and triplet applied). */
  readonly stepTicks = computed(() => stepLength(this.state()));

  constructor() {
    const events = inject(EventStream);
    events.events$.subscribe(message => {
      if (message.type === 'stepInput') {
        this.state.set(message.state);
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

  /** Changes the state; left-out properties stay. */
  async set(request: StepInputRequest): Promise<void> {
    try {
      this.state.set(await this.api.setStepInput(request));
    } catch (error) {
      this.notify.failure(error, 'Could not change step input');
    }
  }

  /** Turns step input on or off: on, into `regionId` (or a new region on `trackId`) from `positionTick`. */
  toggle(
    on: boolean,
    at?: { regionId?: string | null; trackId?: string | null; positionTick?: number }
  ): Promise<void> {
    return this.set({ on, ...(at ?? {}) });
  }

  /** A step of notes (a chord when several), at the step position. */
  step(pitches: number[], velocity?: number): Promise<CommandResult | null> {
    return this.project.execute(
      [{ type: 'stepNotes', pitches, ...(velocity === undefined ? {} : { velocity }) }],
      'Step Input'
    );
  }

  /** A tie: lengthens the last step by one step. */
  tie(): Promise<CommandResult | null> {
    return this.project.execute([{ type: 'stepTie' }], 'Step Input Tie');
  }

  /** A rest: advances by one step (not an edit). */
  async rest(): Promise<void> {
    try {
      this.state.set(await this.api.stepRest());
    } catch (error) {
      this.notify.failure(error, 'Could not enter a rest');
    }
  }
}
