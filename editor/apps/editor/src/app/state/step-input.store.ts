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
