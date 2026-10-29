import type { Cell, Track } from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import { TempoMap } from '../music/tempo-map';
import type { LaneRow } from '../state/lanes';
import {
  CellDragType,
  LoopDragType,
  acceptsDrag,
  barTicks,
  canHold,
  cellProgress,
  gridRows,
  lengthLabel,
  midiContent,
  patternContent,
  readDrop,
  slotLabel,
  slotState
} from './grid-model';

function track(id: string, kind: Track['kind'], extra: Partial<Track> = {}): Track {
  return { id, kind, name: id, drumMachine: null, ...extra } as Track;
}

const meter44 = new MeterMap([{ tick: 0, numerator: 4, denominator: 4 }]);
const tempo120 = new TempoMap([], 120);

function transfer(data: Record<string, string>): Pick<DataTransfer, 'getData' | 'types'> {
  return { types: Object.keys(data), getData: (type: string) => data[type] ?? '' };
}

describe('Live Loops grid model', () => {
  it('lays one grid row beside each Tracks-area row: cells for tracks that hold them, spacers otherwise', () => {
    const audio = track('a', 'audio');
    const synth = track('s', 'instrument');
    const bus = track('x', 'aux');
    const rows: LaneRow[] = [
      { kind: 'track', id: 'a', track: audio, depth: 0 },
      { kind: 'automation', id: 'a~volume', track: audio, target: 'volume', depth: 0 },
      { kind: 'track', id: 's', track: synth, depth: 0 },
      { kind: 'track', id: 'x', track: bus, depth: 0 }
    ];
    expect(gridRows(rows).map(r => [r.kind, r.id])).toEqual([
      ['track', 'a'],
      ['spacer', 'a~volume'],
      ['track', 's'],
      ['spacer', 'x']
    ]);
  });

  it('says which tracks can hold which cells (as the Server does)', () => {
    const dmd = track('d', 'stack', { drumMachine: { pads: [] } as unknown as Track['drumMachine'] });
    expect(canHold(track('a', 'audio'), 'audio')).toBe(true);
    expect(canHold(track('a', 'audio'), 'midi')).toBe(false);
    expect(canHold(track('s', 'instrument'), 'pattern')).toBe(true);
    expect(canHold(track('m', 'midi'), 'midi')).toBe(true);
    expect(canHold(track('m', 'midi'), 'pattern')).toBe(false);
    expect(canHold(dmd, 'pattern')).toBe(true);
    expect(canHold(track('f', 'stack'), 'midi')).toBe(false);
  });

  it('names slots for the accessibility tree, and reads their state', () => {
    const cell = { id: 'c1', name: 'Bass Line' } as Cell;
    expect(slotLabel(cell, 'Bass', 0)).toBe('Bass Line, Bass, scene 1');
    expect(slotLabel(undefined, 'Drums', 1)).toBe('Empty cell, Drums, scene 2');
    expect(slotState(undefined, () => 'playing')).toBe('empty');
    expect(slotState(cell, id => (id === 'c1' ? 'queued' : 'stopped'))).toBe('queued');
  });

  it('measures progress from the start sample: none while queued, wrapping for loops, held at the end for one-shots', () => {
    // 120 bpm at 48 kHz: one beat (960 ticks) is 24,000 samples. The cell started at bar 2 (3840 ticks, 96,000).
    const at = (state: 'queued' | 'playing' | 'stopping', tick: number, mode: Cell['playMode'] = 'loop') =>
      cellProgress(state, state === 'queued' ? undefined : 96_000, 3840, mode, tick, tempo120, 48_000);
    expect(at('queued', 3000)).toBeNull();
    expect(at('playing', 3000)).toBe(0);
    expect(at('playing', 3840 + 960)).toBeCloseTo(0.25);
    expect(at('playing', 3840 + 3840 + 1920)).toBeCloseTo(0.5);
    expect(at('stopping', 3840 + 2880)).toBeCloseTo(0.75);
    expect(at('playing', 3840 + 3840 + 1920, 'oneShot')).toBe(1);
    expect(cellProgress('stopped', 96_000, 3840, 'loop', 5000, tempo120, 48_000)).toBeNull();
  });

  it('writes lengths in bars and beats', () => {
    expect(barTicks(meter44)).toBe(3840);
    expect(lengthLabel(7680, meter44, 960)).toBe('2 bars');
    expect(lengthLabel(3840 + 1920, meter44, 960)).toBe('1 bar 2 beats');
    expect(lengthLabel(960, meter44, 960)).toBe('1 beat');
  });

  it('makes one-bar pattern content: a row per pad on a Drum Machine Designer, else C3', () => {
    const pads = [
      { note: 36, name: 'Kick' },
      { note: 38, name: 'Snare' }
    ];
    const dmd = track('d', 'stack', { drumMachine: { pads } as unknown as Track['drumMachine'] });
    const content = patternContent(dmd, 3840);
    expect(content.audio).toBeNull();
    expect(content.midi).toBeNull();
    expect(content.pattern).toMatchObject({ trackId: 'd', startTick: 0, lengthTicks: 3840, name: 'Pattern' });
    expect(content.pattern?.pattern.rows.map(r => [r.kind, r.note, r.name, r.length])).toEqual([
      ['pad', 36, 'Kick', 16],
      ['pad', 38, 'Snare', 16]
    ]);
    expect(new Set(content.pattern?.pattern.rows.map(r => r.id)).size).toBe(2);
    const synth = patternContent(track('s', 'instrument'), 2880);
    expect(synth.pattern?.pattern.rows.map(r => [r.kind, r.note, r.length])).toEqual([['note', 60, 12]]);
    expect(midiContent(track('s', 'instrument'), 3840).midi).toMatchObject({ lengthTicks: 3840, notes: [] });
  });

  it('takes loops and cells by drag and drop', () => {
    expect(acceptsDrag([LoopDragType])).toBe(true);
    expect(acceptsDrag([CellDragType])).toBe(true);
    expect(acceptsDrag(['Files'])).toBe(false);
    expect(readDrop(transfer({ [LoopDragType]: 'test-pack:bass-line' }))).toEqual({
      kind: 'loop',
      loopId: 'test-pack:bass-line'
    });
    expect(readDrop(transfer({ [CellDragType]: 'cel_1' }))).toEqual({ kind: 'cell', cellId: 'cel_1' });
    expect(readDrop(transfer({ [LoopDragType]: ' ' }))).toBeNull();
    expect(readDrop(transfer({ 'text/plain': 'x' }))).toBeNull();
  });
});
