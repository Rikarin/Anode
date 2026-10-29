import type { DrumPad, Pattern } from '@anode/api-client';
import { defaultRow } from '../music/pattern';
import {
  applyTemplate,
  newRow,
  PatternTemplates,
  randomise,
  rateLabel,
  recordLive,
  recordStep,
  rowName,
  setRowLength,
  setRowNote,
  setRowPlayMode,
  setRowRate,
  setStepValue,
  setSwing,
  stepAtTick,
  toggleStep,
  unusedPads
} from './pattern-edit';

const Pads: DrumPad[] = [
  { note: 36, name: 'Kick', trackId: 'trk_k' },
  { note: 38, name: 'Snare', trackId: 'trk_s' },
  { note: 39, name: 'Clap', trackId: 'trk_c' },
  { note: 42, name: 'Closed Hat', trackId: 'trk_h' },
  { note: 46, name: 'Open Hat', trackId: 'trk_o' }
];

function drums(): Pattern {
  return {
    rows: [
      defaultRow('r1', 'pad', 36, 'Kick'),
      defaultRow('r2', 'pad', 38, 'Snare'),
      defaultRow('r3', 'pad', 42, 'Closed Hat')
    ],
    swing: 0,
    seed: 1,
    stepTicks: 240
  };
}

function template(id: string) {
  const found = PatternTemplates.find(t => t.id === id);
  if (!found) {
    throw new Error(id);
  }
  return found;
}

const onSteps = (pattern: Pattern, rowId: string) =>
  pattern.rows.find(r => r.id === rowId)?.steps.flatMap((s, i) => (s.on ? [i] : [])) ?? [];

describe('Pattern edits', () => {
  it('toggles a step and sets values, turning an edited step on and clamping to its range', () => {
    let p = toggleStep(drums(), 'r1', 0);
    expect(onSteps(p, 'r1')).toEqual([0]);
    p = setStepValue(p, 'r1', 4, 'velocity', 200);
    expect(p.rows[0]?.steps[4]).toMatchObject({ on: true, velocity: 127 });
    p = setStepValue(p, 'r1', 4, 'chance', 0.555);
    expect(p.rows[0]?.steps[4]?.chance).toBe(0.56);
    p = setStepValue(p, 'r1', 5, 'offset', -0.9);
    expect(p.rows[0]?.steps[5]?.offset).toBe(-0.5);
    p = setStepValue(p, 'r1', 6, 'repeats', 3.4);
    expect(p.rows[0]?.steps[6]?.repeats).toBe(3);
    p = setStepValue(p, 'r1', 7, 'gate', 0);
    expect(p.rows[0]?.steps[7]?.gate).toBe(0.1);
    // Other rows are untouched.
    expect(p.rows[1]?.steps.every(s => !s.on)).toBe(true);
  });

  it('changes a row’s length, rate, play mode and note', () => {
    let p = toggleStep(drums(), 'r3', 15);
    p = setRowLength(p, 'r3', 7);
    expect(p.rows[2]).toMatchObject({ length: 7 });
    expect(p.rows[2]?.steps).toHaveLength(7);
    p = setRowLength(p, 'r3', 100);
    expect(p.rows[2]?.length).toBe(64);
    expect(p.rows[2]?.steps[15]?.on).toBe(false);
    p = setRowRate(p, 'r3', 160);
    expect(p.rows[2]?.stepTicks).toBe(160);
    expect(rateLabel(160)).toBe('1/16T');
    expect(rateLabel(null)).toBe('Pattern');
    p = setRowPlayMode(p, 'r2', 'pingPong');
    expect(p.rows[1]?.playMode).toBe('pingPong');
    p = setRowNote(p, 'r2', 39, Pads);
    expect(p.rows[1]).toMatchObject({ note: 39, name: 'Clap' });
    expect(setSwing(p, 1.4).swing).toBe(1);
  });

  it('names rows by pad, note or degree', () => {
    expect(rowName({ kind: 'pad', note: 42, name: '' }, Pads)).toBe('Closed Hat');
    expect(rowName({ kind: 'note', note: 60, name: 'x' })).toBe('C3');
    expect(rowName({ kind: 'chordDegree', note: 5, name: '' })).toBe('Degree 5');
    const row = newRow(drums(), 'pad', 46, Pads);
    expect(row).toMatchObject({ id: 'row_4', name: 'Open Hat', length: 16, kind: 'pad' });
    expect(unusedPads(drums(), Pads).map(p => p.name)).toEqual(['Clap', 'Open Hat']);
  });

  it('applies templates to the GM pad rows, adding a row for a pad the pattern lacks', () => {
    let p = applyTemplate(drums(), template('four'), Pads);
    expect(onSteps(p, 'r1')).toEqual([0, 4, 8, 12]);
    p = applyTemplate(p, template('backbeat'), Pads);
    expect(onSteps(p, 'r1')).toEqual([0, 8]);
    expect(onSteps(p, 'r2')).toEqual([4, 12]);
    p = applyTemplate(p, template('hats8'), Pads);
    expect(onSteps(p, 'r3')).toEqual([0, 2, 4, 6, 8, 10, 12, 14]);
    // Without a hat row, Hats 16ths adds the DMD's Closed Hat pad.
    const noHat = { ...drums(), rows: drums().rows.slice(0, 2) };
    const added = applyTemplate(noHat, template('hats16'), Pads);
    expect(added.rows.at(-1)).toMatchObject({ kind: 'pad', note: 42, name: 'Closed Hat' });
    expect(onSteps(added, added.rows.at(-1)?.id ?? '')).toHaveLength(16);
    // On an instrument track the parts are note rows.
    const keys: Pattern = { rows: [defaultRow('n1', 'note', 60, 'C3')], swing: 0, seed: 1, stepTicks: 240 };
    expect(applyTemplate(keys, template('four'), null).rows.at(-1)).toMatchObject({ kind: 'note', note: 36 });
    const cleared = applyTemplate(p, template('clear'), Pads);
    expect(cleared.rows.every(r => r.steps.every(s => !s.on))).toBe(true);
  });

  it('randomises deterministically from a seed, all rows or one', () => {
    const a = randomise(drums(), 'steps', 7);
    expect(randomise(drums(), 'steps', 7)).toEqual(a);
    expect(randomise(drums(), 'steps', 8)).not.toEqual(a);
    expect(a.rows.some(r => r.steps.some(s => s.on))).toBe(true);
    const one = randomise(drums(), 'velocity', 3, 'r2');
    expect(one.rows[0]).toEqual(drums().rows[0]);
    expect(one.rows[1]?.steps.every(s => s.velocity >= 40 && s.velocity <= 127)).toBe(true);
    expect(new Set(one.rows[1]?.steps.map(s => s.velocity)).size).toBeGreaterThan(1);
  });

  it('step-records into the matching row, adding one on an instrument, skipping a note a DMD has no pad for', () => {
    const first = recordStep(drums(), 38, 90, 0, Pads);
    expect(first?.rowId).toBe('r2');
    expect(first?.pattern.rows[1]?.steps[0]).toMatchObject({ on: true, velocity: 90 });
    expect(recordStep(drums(), 50, 90, 0, Pads)).toBeNull();
    const clap = recordStep(drums(), 39, 100, 17, Pads);
    expect(clap?.pattern.rows.at(-1)).toMatchObject({ name: 'Clap' });
    expect(onSteps(clap?.pattern ?? drums(), clap?.rowId ?? '')).toEqual([1]);
    const keys: Pattern = { rows: [], swing: 0, seed: 1, stepTicks: 240 };
    const note = recordStep(keys, 64, 100, 2, null);
    expect(note?.pattern.rows[0]).toMatchObject({ kind: 'note', note: 64, name: 'E3' });
  });

  it('live-records a note into the step under the playhead, by the row’s rate and play mode', () => {
    const p = setRowPlayMode(drums(), 'r1', 'reverse');
    // Tick 1000 is nearest step 4 (960); reversed over 16 steps that is step 11.
    expect(stepAtTick(p.rows[0] ?? defaultRow('x', 'pad', 36, ''), p, 960)).toBe(11);
    const live = recordLive(p, 36, 80, 1000, Pads);
    expect(onSteps(live ?? p, 'r1')).toEqual([11]);
    expect(recordLive(p, 38, 80, 2100, Pads)?.rows[1]?.steps[9]?.on).toBe(true);
    expect(recordLive(p, 51, 80, 0, Pads)).toBeNull();
  });
});
