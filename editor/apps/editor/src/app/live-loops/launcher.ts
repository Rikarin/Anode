import type { Cell, CellPlayMode, CellState, LaunchQuantize } from '@anode/api-client';
import type { MeterMap } from '@anode/timeline';

/** A launch quantise value (the contract's enum, which the OpenAPI document makes nullable). */
export type Quantize = NonNullable<LaunchQuantize>;

/** Ticks per quarter note. */
const Ppq = 960;

/** The launch quantise values in menu order, with their names (Logic's). */
export const LaunchQuantizes: readonly { value: Quantize; label: string }[] = [
  { value: 'off', label: 'Off' },
  { value: 'sixteenth', label: '1/16' },
  { value: 'eighth', label: '1/8' },
  { value: 'quarter', label: '1/4' },
  { value: 'half', label: '1/2' },
  { value: 'bar', label: '1 Bar' },
  { value: 'twoBars', label: '2 Bars' },
  { value: 'fourBars', label: '4 Bars' }
];

export function quantizeLabel(quantize: LaunchQuantize | null | undefined): string {
  return LaunchQuantizes.find(q => q.value === quantize)?.label ?? 'Grid';
}

const NoteTicks: Partial<Record<Quantize, number>> = {
  sixteenth: Ppq / 4,
  eighth: Ppq / 2,
  quarter: Ppq,
  half: 2 * Ppq
};

const Bars: Partial<Record<Quantize, number>> = { bar: 1, twoBars: 2, fourBars: 4 };

/**
 * The tick a launch (or stop) at `tick` takes effect at (docs/07-phase-6.md, "Live Loops"): at once for `off`; else
 * the next boundary of the note value (counted from the bar line) or of 1, 2 or 4 bars (counted from bar 1), under the
 * signature track. A boundary closer than `lateTicks` (1 ms of the tempo there) is late: the launch takes the next.
 */
export function nextBoundary(tick: number, quantize: Quantize, meter: MeterMap, lateTicks = 0): number {
  if (quantize === 'off') {
    return tick;
  }
  const earliest = tick + Math.max(lateTicks, 1e-9);
  const note = NoteTicks[quantize];
  if (note !== undefined) {
    const bar = Math.floor(meter.barAt(tick) + 1e-9);
    const barStart = meter.barStart(bar);
    const nextBar = meter.barStart(bar + 1);
    const within = barStart + Math.ceil((earliest - barStart) / note - 1e-9) * note;
    return Math.min(within, nextBar < earliest ? Infinity : nextBar);
  }
  const bars = Bars[quantize] ?? 1;
  let bar = Math.ceil(meter.barAt(earliest) - 1e-9);
  bar = Math.ceil(bar / bars) * bars;
  return meter.barStart(bar);
}

/** A cell as the launcher needs it. */
export type LauncherCell = Pick<Cell, 'id' | 'trackId' | 'lengthTicks' | 'playMode'> & {
  quantize?: LaunchQuantize | null;
};

/** A cell's launcher state change, at a tick (for queued and stopping, the boundary it waits for). */
export interface CellChange {
  cellId: string;
  state: CellState;
  tick: number;
}

interface Slot {
  cell: LauncherCell;
  state: CellState;
  /** Queued, stopping: the boundary; playing: where it (re)started. */
  tick: number;
  /** A retrigger waiting to restart a playing cell at `restart`. */
  restart?: number;
}

/** What the launcher needs to place boundaries. */
export interface LauncherClock {
  meter: MeterMap;
  /** The grid's quantise (a cell's own overrides it). */
  quantize: Quantize;
  /** Ticks that 1 ms spans at `tick`. */
  lateTicks(tick: number): number;
}

/**
 * Live Loops' launch and quantise state machine (docs/07-phase-6.md): per track at most one cell plays; a launch is
 * queued to its quantise boundary, where it starts and the track's playing cell stops; a stop waits for the grid's
 * boundary; one-shot and retrigger cells stop at their end; a loop cell launched while it plays keeps playing, a
 * retrigger restarts at the boundary. The fake engine runs it to send `cell` events; the Live Loops store reads the
 * same states from them.
 */
export class Launcher {
  private readonly slots = new Map<string, Slot>();

  constructor(private readonly clock: () => LauncherClock) {}

  /** The state of a cell (stopped when it has none). */
  state(cellId: string): CellState {
    return this.slots.get(cellId)?.state ?? 'stopped';
  }

  /** The cells not stopped, by id. */
  active(): Map<string, { state: CellState; tick: number }> {
    const out = new Map<string, { state: CellState; tick: number }>();
    for (const [id, slot] of this.slots) {
      if (slot.state !== 'stopped') {
        out.set(id, { state: slot.state, tick: slot.tick });
      }
    }
    return out;
  }

  /** The cell playing on a track, if any. */
  playingOn(trackId: string): LauncherCell | null {
    for (const slot of this.slots.values()) {
      if (slot.cell.trackId === trackId && (slot.state === 'playing' || slot.state === 'stopping')) {
        return slot.cell;
      }
    }
    return null;
  }

  /** Launches cells at `now`, each at its quantise boundary. */
  launch(cells: readonly LauncherCell[], now: number): CellChange[] {
    const changes: CellChange[] = [];
    for (const cell of cells) {
      changes.push(...this.launchOne(cell, now));
    }
    return changes;
  }

  /**
   * Launches a scene: its cells, and stops the tracks (of `tracks`) that have a cell playing or queued but none in the
   * scene, at the grid's boundary.
   */
  launchScene(cells: readonly LauncherCell[], now: number): CellChange[] {
    const tracks = new Set(cells.map(c => c.trackId));
    const others = [...this.slots.values()]
      .filter(s => s.state !== 'stopped' && !tracks.has(s.cell.trackId))
      .map(s => s.cell.trackId);
    return [...this.launch(cells, now), ...this.stop([...new Set(others)], now)];
  }

  /** Stops the cells of `trackIds` (every track when left out) at the grid's boundary; queued ones at once. */
  stop(trackIds: readonly string[] | null | undefined, now: number): CellChange[] {
    const clock = this.clock();
    const at = nextBoundary(now, clock.quantize, clock.meter, clock.lateTicks(now));
    const changes: CellChange[] = [];
    for (const slot of this.slots.values()) {
      if (trackIds && !trackIds.includes(slot.cell.trackId)) {
        continue;
      }
      if (slot.state === 'queued') {
        changes.push(this.set(slot, 'stopped', now));
      } else if (slot.state === 'playing') {
        delete slot.restart;
        changes.push(this.set(slot, 'stopping', at));
      }
    }
    return changes;
  }

  /** Everything stops at once (the transport stopped). */
  reset(now: number): CellChange[] {
    const changes: CellChange[] = [];
    for (const slot of this.slots.values()) {
      if (slot.state !== 'stopped') {
        delete slot.restart;
        changes.push(this.set(slot, 'stopped', now));
      }
    }
    return changes;
  }

  /** Forgets cells that no longer exist (deleted), stopping them. */
  forget(cellIds: ReadonlySet<string>, now: number): CellChange[] {
    const changes: CellChange[] = [];
    for (const [id, slot] of this.slots) {
      if (!cellIds.has(id)) {
        if (slot.state !== 'stopped') {
          changes.push({ cellId: id, state: 'stopped', tick: now });
        }
        this.slots.delete(id);
      }
    }
    return changes;
  }

  /**
   * Moves the launcher on to `now`: stops due (boundaries reached, one-shots at their end) first, then starts due, in
   * time order.
   */
  advance(now: number): CellChange[] {
    const changes: CellChange[] = [];
    for (;;) {
      const due = this.nextDue(now);
      if (!due) {
        break;
      }
      changes.push(due());
    }
    return changes;
  }

  private nextDue(now: number): (() => CellChange) | null {
    let best: { tick: number; order: number; run: () => CellChange } | null = null;
    const consider = (tick: number, order: number, run: () => CellChange) => {
      if (tick <= now && (!best || tick < best.tick || (tick === best.tick && order < best.order))) {
        best = { tick, order, run };
      }
    };
    for (const slot of this.slots.values()) {
      if (slot.state === 'stopping') {
        consider(slot.tick, 0, () => this.set(slot, 'stopped', slot.tick));
      } else if (slot.state === 'queued') {
        consider(slot.tick, 1, () => this.set(slot, 'playing', slot.tick));
      } else if (slot.state === 'playing') {
        const restart = slot.restart;
        if (restart !== undefined) {
          consider(restart, 1, () => {
            delete slot.restart;
            return this.set(slot, 'playing', restart);
          });
        }
        if (finite(slot.cell.playMode) && slot.cell.lengthTicks > 0) {
          const end = slot.tick + slot.cell.lengthTicks;
          if (restart === undefined || end <= restart) {
            consider(end, 0, () => this.set(slot, 'stopped', end));
          }
        }
      }
    }
    const found = best as { run: () => CellChange } | null;
    return found ? found.run : null;
  }

  private launchOne(cell: LauncherCell, now: number): CellChange[] {
    const clock = this.clock();
    const at = nextBoundary(now, cell.quantize ?? clock.quantize, clock.meter, clock.lateTicks(now));
    const existing = this.slots.get(cell.id);
    const changes: CellChange[] = [];
    if (existing && (existing.state === 'playing' || existing.state === 'queued')) {
      existing.cell = cell;
      if (existing.state === 'playing' && cell.playMode === 'retrigger') {
        existing.restart = at;
        changes.push({ cellId: cell.id, state: 'queued', tick: at });
      }
      // A loop or one-shot launched while it plays (or waits) keeps doing so.
      return changes;
    }
    for (const slot of this.slots.values()) {
      if (slot.cell.trackId !== cell.trackId || slot.cell.id === cell.id) {
        continue;
      }
      if (slot.state === 'queued') {
        changes.push(this.set(slot, 'stopped', now));
      } else if (slot.state === 'playing') {
        delete slot.restart;
        changes.push(this.set(slot, 'stopping', at));
      } else if (slot.state === 'stopping' && slot.tick > at) {
        changes.push(this.set(slot, 'stopping', at));
      }
    }
    const slot: Slot = existing ?? { cell, state: 'stopped', tick: now };
    slot.cell = cell;
    this.slots.set(cell.id, slot);
    changes.push(this.set(slot, 'queued', at));
    if (at <= now) {
      changes.push(...this.advance(now));
    }
    return changes;
  }

  private set(slot: Slot, state: CellState, tick: number): CellChange {
    slot.state = state;
    slot.tick = tick;
    return { cellId: slot.cell.id, state, tick };
  }
}

/** Whether a play mode ends by itself (one shot, retrigger). */
function finite(mode: CellPlayMode): boolean {
  return mode === 'oneShot' || mode === 'retrigger';
}
