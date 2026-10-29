import type {
  Cell,
  CellContent,
  CellKind,
  CellPlayMode,
  CellState,
  MidiRegion,
  Pattern,
  PatternRegion,
  Track
} from '@anode/api-client';
import type { MeterMap } from '@anode/timeline';
import { NeutralPlayback } from '../midi/midi-math';
import { defaultRow } from '../music/pattern';
import type { TempoMap } from '../music/tempo-map';
import type { LaneRow } from '../state/lanes';

/** The drag type of a loop from the Loop Browser (its data: the loop id). */
export const LoopDragType = 'application/x-anode-loop';
/** The drag type of a Live Loops cell (its data: the cell id). */
export const CellDragType = 'application/x-anode-cell';

/** The cell play modes in menu order, with Logic's names. */
export const CellPlayModes: readonly { value: CellPlayMode; label: string }[] = [
  { value: 'loop', label: 'Loop' },
  { value: 'oneShot', label: 'One Shot' },
  { value: 'retrigger', label: 'Retrigger' }
];

export function playModeLabel(mode: CellPlayMode): string {
  return CellPlayModes.find(m => m.value === mode)?.label ?? mode;
}

/** A cell slot's state as the grid shows it (`data-state`): `empty` when the slot has no cell. */
export type SlotState = CellState | 'empty';

/**
 * A row of the grid beside a row of the Tracks area: a track's (its cells), or a blank spacer for a take or automation
 * row, or a track that holds no cells (a folder stack, an aux), so the grid's rows stay level with the lanes.
 */
export type GridRow = { kind: 'track'; id: string; track: Track } | { kind: 'spacer'; id: string };

/** Whether a track plays MIDI (instrument, external MIDI, Drum Machine Designer). */
export function playsMidi(track: Track): boolean {
  return track.kind === 'instrument' || track.kind === 'midi' || track.drumMachine !== null;
}

/** Whether a track can hold a cell of a kind (the Server's rule): audio on audio tracks, MIDI or patterns on MIDI ones. */
export function canHold(track: Track, kind: CellKind): boolean {
  if (kind === 'audio') {
    return track.kind === 'audio';
  }
  return kind === 'midi' ? playsMidi(track) : playsMidi(track) && track.kind !== 'midi';
}

/** Whether a track has cells at all. */
export function hasCells(track: Track): boolean {
  return track.kind === 'audio' || playsMidi(track);
}

/** The grid's rows for the Tracks area's rows, one to one. */
export function gridRows(rows: readonly LaneRow[]): GridRow[] {
  return rows.map(row =>
    row.kind === 'track' && hasCells(row.track)
      ? { kind: 'track', id: row.id, track: row.track }
      : { kind: 'spacer', id: row.id }
  );
}

/** A slot's accessible name: `<cell>, <track>, scene <n>` (`Empty cell, …` when empty); scenes count from 1. */
export function slotLabel(cell: Pick<Cell, 'name'> | undefined, trackName: string, sceneIndex: number): string {
  return `${cell ? cell.name || 'Cell' : 'Empty cell'}, ${trackName}, scene ${sceneIndex + 1}`;
}

/** A slot's state: its cell's launcher state, or `empty`. */
export function slotState(cell: Cell | undefined, state: (cellId: string) => CellState): SlotState {
  return cell ? state(cell.id) : 'empty';
}

/**
 * How far a cell has played through its length, 0…1, at the transport's `positionTick` (null when it is not playing
 * or stopping). `startSample` is the project sample it started at (the `cell` event's); a loop wraps, a one-shot or
 * retrigger cell stays at its end.
 */
export function cellProgress(
  state: CellState,
  startSample: number | undefined,
  lengthTicks: number,
  playMode: CellPlayMode,
  positionTick: number,
  tempo: Pick<TempoMap, 'tickAt'>,
  sampleRate: number
): number | null {
  if ((state !== 'playing' && state !== 'stopping') || startSample === undefined || lengthTicks <= 0) {
    return null;
  }
  const start = tempo.tickAt(startSample / sampleRate);
  const played = positionTick - start;
  if (played <= 0) {
    return 0;
  }
  if (playMode === 'loop') {
    return (played % lengthTicks) / lengthTicks;
  }
  return Math.min(1, played / lengthTicks);
}

/** The length of bar 1 under the signature track, in ticks. */
export function barTicks(meter: Pick<MeterMap, 'barStart'>): number {
  return meter.barStart(1) - meter.barStart(0);
}

/** A cell length as Logic's cell inspector writes it: `2 bars`, `1 bar 2 beats`, `3 beats`. */
export function lengthLabel(lengthTicks: number, meter: Pick<MeterMap, 'barStart'>, beatTicks: number): string {
  const bar = barTicks(meter);
  const bars = Math.floor(lengthTicks / bar + 1e-9);
  const beats = Math.round((lengthTicks - bars * bar) / beatTicks);
  const parts = [];
  if (bars) {
    parts.push(`${bars} ${bars === 1 ? 'bar' : 'bars'}`);
  }
  if (beats || !bars) {
    parts.push(`${beats} ${beats === 1 ? 'beat' : 'beats'}`);
  }
  return parts.join(' ');
}

let nextRow = 0;

/**
 * A new pattern cell's content: one bar of 16ths (a row's steps fill the bar), on a Drum Machine Designer one pad row
 * per pad, else one note row on C3 (the fake Server's `defaultPattern`). Ids are placeholders; the Server renumbers.
 */
export function patternContent(track: Track, lengthTicks: number): CellContent {
  const stepTicks = 240;
  const steps = Math.max(1, Math.min(64, Math.round(lengthTicks / stepTicks)));
  const pads = track.drumMachine?.pads ?? [];
  const rows = pads.length
    ? pads.map(pad => defaultRow(`row_new${nextRow++}`, 'pad', pad.note, pad.name, steps))
    : [defaultRow(`row_new${nextRow++}`, 'note', 60, 'C3', steps)];
  const pattern: Pattern = { rows, swing: 0, seed: 1, stepTicks };
  const region: PatternRegion = {
    id: 'new',
    trackId: track.id,
    name: 'Pattern',
    startTick: 0,
    lengthTicks,
    muted: false,
    pattern
  };
  return { audio: null, midi: null, pattern: region };
}

/** A new MIDI cell's content: an empty region of `lengthTicks`. */
export function midiContent(track: Track, lengthTicks: number): CellContent {
  const region: MidiRegion = {
    id: 'new',
    trackId: track.id,
    name: 'MIDI',
    startTick: 0,
    lengthTicks,
    playback: { ...NeutralPlayback },
    notes: [],
    controls: [],
    muted: false
  };
  return { audio: null, midi: region, pattern: null };
}

/** What a drag over a cell slot carries: a loop from the Loop Browser, another cell, or nothing the grid takes. */
export type SlotDrop = { kind: 'loop'; loopId: string } | { kind: 'cell'; cellId: string } | null;

/** Whether a drag (by its types, which is all `dragover` can read) can land on a slot. */
export function acceptsDrag(types: readonly string[]): boolean {
  return types.includes(LoopDragType) || types.includes(CellDragType);
}

/** What a drop carries (read at `drop`, when the data is readable). */
export function readDrop(data: Pick<DataTransfer, 'getData' | 'types'>): SlotDrop {
  const types = [...data.types];
  if (types.includes(CellDragType)) {
    const cellId = data.getData(CellDragType);
    return cellId ? { kind: 'cell', cellId } : null;
  }
  if (types.includes(LoopDragType)) {
    const loopId = data.getData(LoopDragType).trim();
    return loopId ? { kind: 'loop', loopId } : null;
  }
  return null;
}
