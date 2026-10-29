import { Injectable, computed, inject, signal } from '@angular/core';
import type { Cell, CellState, LaunchQuantize } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { ProjectStore } from '../state/project.store';

/** A cell's launcher state as the last `cell` event reported it, at a project sample. */
export interface CellStatus {
  state: CellState;
  sample: number;
}

const Stopped: CellStatus = { state: 'stopped', sample: 0 };

/**
 * Live Loops (docs/07-phase-6.md): the launcher's cell states from the Server's `cell` events (queued, playing,
 * stopping, stopped, with the sample), and launching, stopping and recording, which go to `/project/live-loops`.
 * The grid itself (scenes, cells, quantise, shown) is the project's (`ProjectStore.liveLoops`), edited by commands.
 */
@Injectable({ providedIn: 'root' })
export class LiveLoopsStore {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly notify = inject(Notify);

  /** Cells that are not stopped, by id. */
  readonly states = signal<ReadonlyMap<string, CellStatus>>(new Map());
  /** A performance recording is on (this window started it). */
  readonly recordingPerformance = signal(false);
  /** The cell slot being recorded into, as `trackId/sceneId`, or null. */
  readonly recordingCell = signal<string | null>(null);

  readonly grid = this.project.liveLoops;
  readonly shown = computed(() => this.project.liveLoops().shown);

  constructor() {
    const events = inject(EventStream);
    events.events$.subscribe(message => {
      if (message.type === 'cell') {
        this.states.update(map => {
          const next = new Map(map);
          if (message.state === 'stopped') {
            next.delete(message.cellId);
          } else {
            next.set(message.cellId, { state: message.state, sample: message.sample });
          }
          return next;
        });
      } else if (message.type === 'projectOpened' || message.type === 'projectClosed') {
        this.states.set(new Map());
        this.recordingPerformance.set(false);
        this.recordingCell.set(null);
      } else if (message.type === 'transport' && message.transport.state === 'stopped' && this.states().size) {
        // The engine stops every cell with the transport.
        this.states.set(new Map());
      }
    });
  }

  /** A cell's state and the sample of its last change (stopped when the launcher has not reported it). */
  status(cellId: string): CellStatus {
    return this.states().get(cellId) ?? Stopped;
  }

  state(cellId: string): CellState {
    return this.status(cellId).state;
  }

  /** The cell of a track in a scene, if any. */
  cellAt(trackId: string, sceneId: string): Cell | undefined {
    return this.project.liveLoops().cells.find(c => c.trackId === trackId && c.sceneId === sceneId);
  }

  /** The cell playing (or about to stop) on a track, if any. */
  playingOn(trackId: string): Cell | undefined {
    return this.project.liveLoops().cells.find(c => {
      const state = this.state(c.id);
      return c.trackId === trackId && (state === 'playing' || state === 'stopping');
    });
  }

  /**
   * Launches cells, each at its quantise boundary. Their tracks are activated for Live Loops first (as Logic does when
   * a cell is clicked), in one undo step.
   */
  async launch(cellIds: readonly string[]): Promise<void> {
    const cells = this.project.liveLoops().cells.filter(c => cellIds.includes(c.id));
    await this.activate(cells.map(c => c.trackId));
    await this.notify.run(() => this.api.launchCells({ cellIds: [...cellIds] }), 'Could not launch the cells');
  }

  /** Launches a scene: its cells, and stops the tracks without one in it. */
  async launchScene(sceneId: string): Promise<void> {
    const cells = this.project.liveLoops().cells.filter(c => c.sceneId === sceneId);
    await this.activate(cells.map(c => c.trackId));
    await this.notify.run(() => this.api.launchCells({ sceneId }), 'Could not launch the scene');
  }

  /** Stops the cells of tracks (every track when left out) at the grid's quantise boundary. */
  async stop(trackIds?: readonly string[]): Promise<void> {
    await this.notify.run(
      () => this.api.stopCells(trackIds ? { trackIds: [...trackIds] } : {}),
      'Could not stop the cells'
    );
  }

  /** Starts or stops a performance recording (cells and Remix FX into the Tracks area, one undo step at the end). */
  async recordPerformance(on: boolean): Promise<void> {
    const done = await this.notify.run(
      () => this.api.recordLiveLoops({ on }),
      on ? 'Could not start recording' : 'Could not stop recording'
    );
    if (done !== null) {
      this.recordingPerformance.set(on);
    }
  }

  /** Starts or stops recording into the cell of a track and a scene. */
  async recordCell(trackId: string, sceneId: string, on: boolean): Promise<void> {
    const done = await this.notify.run(
      () => this.api.recordLiveLoops({ on, trackId, sceneId }),
      on ? 'Could not record into the cell' : 'Could not stop recording'
    );
    if (done !== null) {
      this.recordingCell.set(on ? `${trackId}/${sceneId}` : null);
    }
  }

  /** Shows or hides the grid beside the Tracks area. */
  setShown(shown: boolean): Promise<unknown> {
    return this.project.execute([{ type: 'setLiveLoops', shown }], shown ? 'Show Live Loops' : 'Hide Live Loops');
  }

  setQuantize(quantize: NonNullable<LaunchQuantize>): Promise<unknown> {
    return this.project.execute([{ type: 'setLiveLoops', quantize }], 'Live Loops Quantize');
  }

  /** Track activation: the tracks play their cells (on) or their Tracks-area regions (off). */
  setActivation(trackIds: readonly string[], liveLoopsActive: boolean): Promise<unknown> {
    return this.project.execute(
      [{ type: 'setTrackActivation', trackIds: [...trackIds], liveLoopsActive }],
      liveLoopsActive ? 'Activate Live Loops' : 'Activate Tracks Area'
    );
  }

  private async activate(trackIds: readonly string[]): Promise<void> {
    const inactive = [...new Set(trackIds)].filter(id => this.project.track(id)?.liveLoopsActive === false);
    if (inactive.length) {
      await this.setActivation(inactive, true);
    }
  }
}
