import { Injectable, computed, inject, signal } from '@angular/core';
import type { CycleRecordMode, TransportRequest, TransportState, TransportStatus } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { Ppq, ProjectStore } from './project.store';

export { Ppq } from './project.store';

export interface TimeSignature {
  numerator: number;
  denominator: number;
}

/** A musical position as Logic's LCD shows it: bar, beat, division and tick, all counted from 1. */
export interface BarsBeats {
  bar: number;
  beat: number;
  division: number;
  tick: number;
}

/**
 * Splits `ticks` into bars, beats, divisions (`division` notes, 16ths by default) and ticks under a constant time
 * signature. A tempo map with signature changes replaces this once the Server owns the project's global tracks.
 */
export function barsBeats(ticks: number, signature: TimeSignature, division = 16): BarsBeats {
  const whole = Math.max(0, Math.floor(ticks));
  const beatTicks = (Ppq * 4) / signature.denominator;
  const barTicks = beatTicks * signature.numerator;
  const divisionTicks = Math.min(beatTicks, (Ppq * 4) / division);
  const inBar = whole % barTicks;
  const inBeat = inBar % beatTicks;
  return {
    bar: Math.floor(whole / barTicks) + 1,
    beat: Math.floor(inBar / beatTicks) + 1,
    division: Math.floor(inBeat / divisionTicks) + 1,
    tick: (inBeat % divisionTicks) + 1
  };
}

/** Ticks per bar under a signature. */
export function barTicks(signature: TimeSignature): number {
  return ((Ppq * 4) / signature.denominator) * signature.numerator;
}

/** `hh:mm:ss.mmm` for a time in seconds. */
export function clockTime(seconds: number): string {
  const ms = Math.max(0, Math.round(seconds * 1000));
  const pad = (value: number, width = 2) => String(value).padStart(width, '0');
  return `${pad(Math.floor(ms / 3_600_000))}:${pad(Math.floor(ms / 60_000) % 60)}:${pad(Math.floor(ms / 1000) % 60)}.${pad(ms % 1000, 3)}`;
}

/** "3/4" or "6 / 8" to a signature, or null. */
export function parseSignature(text: string): TimeSignature | null {
  const match = /^\s*(\d{1,2})\s*\/\s*(\d{1,2})\s*$/.exec(text);
  if (!match) {
    return null;
  }

  const numerator = Number(match[1]);
  const denominator = Number(match[2]);
  return numerator >= 1 && [1, 2, 4, 8, 16, 32].includes(denominator) ? { numerator, denominator } : null;
}

/**
 * The transport. The engine host owns it: the Server streams `transport` frames (about 30 Hz while it moves, once when
 * it stops) and the store extrapolates the playhead between them at the project's tempo, so it moves smoothly at the
 * display rate (docs/01-implementation-plan.md §4.2). The playhead is not a signal while playing: the LCD and the
 * timeline read {@link positionAt} from their own animation frames. Commands go to `ControlTransport` and show at once
 * (optimistically), then follow what the Server answers. Times are `performance.now()` milliseconds, passed in so
 * tests control the clock.
 */
@Injectable({ providedIn: 'root' })
export class TransportStore {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly notify = inject(Notify);

  readonly state = signal<TransportState>('stopped');
  readonly playing = computed(() => this.state() !== 'stopped');
  readonly recording = computed(() => this.state() === 'recording');
  readonly cycle = computed(() => this.project.settings().cycle.enabled);
  readonly metronome = computed(() => this.project.settings().metronome.enabled);
  readonly countIn = computed(() => this.project.settings().metronome.countIn);
  readonly tempo = computed(() => this.project.settings().tempo);
  readonly signature = computed<TimeSignature>(() => this.project.settings().signature);
  readonly ticksPerSecond = computed(() => (this.tempo() / 60) * Ppq);
  readonly recordingSettings = computed(() => this.project.settings().recording);

  /** The playhead at the last frame (or where it was located), in ticks. */
  readonly anchorTicks = signal(0);
  private anchorTime = 0;

  constructor() {
    const events = inject(EventStream);
    events.events$.subscribe(message => {
      if (message.type === 'transport') {
        this.applyStatus(message.transport);
      }
    });
    events.connected$.subscribe(() => {
      this.api
        .transport()
        .then(status => this.applyStatus(status))
        .catch(() => undefined); // the next frame says it
    });
  }

  /** The playhead at `now`: extrapolated while the transport runs, wrapping at the cycle's end while cycling. */
  positionAt(now: number): number {
    const anchor = this.anchorTicks();
    if (!this.playing()) {
      return anchor;
    }

    const map = this.project.tempoMap();
    const tick = map.tickAt(map.secondsAt(anchor) + Math.max(0, now - this.anchorTime) / 1000);
    const cycle = this.project.settings().cycle;
    if (this.state() === 'playing' && cycle.enabled && cycle.endTick > cycle.startTick && anchor < cycle.endTick) {
      if (tick >= cycle.endTick) {
        return cycle.startTick + ((tick - cycle.startTick) % (cycle.endTick - cycle.startTick));
      }
    }

    return tick;
  }

  /** The playhead as seconds from the project start (the LCD's clock), under the tempo track. */
  secondsAt(now: number): number {
    return this.project.tempoMap().secondsAt(this.positionAt(now));
  }

  /** A transport frame from the Server (or the answer to a command). */
  applyStatus(status: TransportStatus, now = performance.now()): void {
    this.state.set(status.state);
    this.anchorTicks.set(status.positionTick);
    this.anchorTime = now;
  }

  play(now = performance.now()): void {
    if (this.playing()) {
      return;
    }

    this.optimistic('playing', this.anchorTicks(), now);
    void this.send({ command: 'play' }, 'Could not play');
  }

  /** Stops where the playhead is; stopping while stopped returns it to the start, as Logic's Stop does. */
  stop(now = performance.now()): void {
    if (!this.playing()) {
      this.goToStart(now);
      return;
    }

    this.optimistic('stopped', this.positionAt(now), now);
    void this.send({ command: 'stop' }, 'Could not stop');
  }

  togglePlay(now = performance.now()): void {
    if (this.playing()) {
      this.stop(now);
    } else {
      this.play(now);
    }
  }

  record(now = performance.now()): void {
    if (this.recording()) {
      return;
    }

    const from = this.positionAt(now);
    this.optimistic('recording', from, now);
    void this.send({ command: 'record' }, 'Could not record');
  }

  /** Moves the playhead, keeping the transport running from there. */
  locate(ticks: number, now = performance.now()): void {
    const tick = Math.max(0, Math.round(ticks));
    this.anchorTicks.set(tick);
    this.anchorTime = now;
    void this.send({ command: 'locate', tick }, 'Could not move the playhead');
  }

  goToStart(now = performance.now()): void {
    this.locate(0, now);
  }

  toggleCycle(): void {
    const on = !this.cycle();
    void this.project.execute([{ type: 'setCycle', enabled: on }], on ? 'Cycle On' : 'Cycle Off');
  }

  setCycleRange(startTick: number, endTick: number): void {
    void this.project.execute(
      [{ type: 'setCycle', enabled: true, startTick: Math.round(startTick), endTick: Math.round(endTick) }],
      'Set Cycle'
    );
  }

  toggleMetronome(): void {
    const on = !this.metronome();
    void this.project.execute([{ type: 'setMetronome', enabled: on }], on ? 'Metronome On' : 'Metronome Off');
  }

  toggleCountIn(): void {
    const on = !this.countIn();
    void this.project.execute([{ type: 'setMetronome', countIn: on }], on ? 'Count-in On' : 'Count-in Off');
  }

  setCycleMode(cycleMode: CycleRecordMode): void {
    if (cycleMode !== this.recordingSettings().cycleMode) {
      void this.project.execute(
        [{ type: 'setRecording', cycleMode }],
        cycleMode === 'takeFolder' ? 'Cycle Recording: Take Folders' : 'Cycle Recording: Regions'
      );
    }
  }

  toggleReplace(): void {
    const on = !this.recordingSettings().replace;
    void this.project.execute([{ type: 'setRecording', replace: on }], on ? 'Replace On' : 'Replace Off');
  }

  /** Autopunch on or off; turned on with the punch range unset, it starts as the cycle range. */
  toggleAutopunch(): void {
    const recording = this.recordingSettings();
    const on = !recording.autopunch;
    const cycle = this.project.settings().cycle;
    const unset = recording.punchOutTick <= recording.punchInTick;
    void this.project.execute(
      [
        {
          type: 'setRecording',
          autopunch: on,
          ...(on && unset ? { punchInTick: cycle.startTick, punchOutTick: cycle.endTick } : {})
        }
      ],
      on ? 'Autopunch On' : 'Autopunch Off'
    );
  }

  /** Flashback Capture: what was played since playback last started becomes a recording (an undoable step). */
  async flashbackCapture(): Promise<void> {
    try {
      await this.api.flashbackCapture();
    } catch (error) {
      this.notify.failure(error, 'Could not capture the performance');
    }
  }

  setTempo(bpm: number): void {
    if (Number.isFinite(bpm) && bpm !== this.tempo()) {
      void this.project.execute([{ type: 'setTempo', bpm: Math.round(bpm * 100) / 100 }], 'Tempo Change');
    }
  }

  setSignature(signature: TimeSignature): void {
    const current = this.signature();
    if (signature.numerator !== current.numerator || signature.denominator !== current.denominator) {
      void this.project.execute([{ type: 'setSignature', ...signature }], 'Time Signature Change');
    }
  }

  private optimistic(state: TransportState, tick: number, now: number): void {
    this.state.set(state);
    this.anchorTicks.set(tick);
    this.anchorTime = now;
  }

  /** Sends a command; when the Server refuses one that started the transport, it shows stopped again. */
  private async send(request: TransportRequest, failure: string): Promise<void> {
    try {
      this.applyStatus(await this.api.controlTransport(request));
    } catch (error) {
      if (request.command === 'play' || request.command === 'record') {
        this.state.set('stopped');
      }

      this.notify.failure(error, failure);
    }
  }
}
