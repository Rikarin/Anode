import { MeterMap } from '@anode/timeline';
import { type CellChange, Launcher, type LauncherCell, nextBoundary, type Quantize } from './launcher';

const Bar = 3840;
const Q = 960;
const meter = MeterMap.constant(4, 4);
/** 1 ms at 120 bpm: 2 ticks per ms... 960 × 2 / 1000 = 1.92 ticks. */
const Late = (120 * 960 * 0.001) / 60;

function launcher(quantize: Quantize = 'bar'): Launcher {
  return new Launcher(() => ({ meter, quantize, lateTicks: () => Late }));
}

function cell(id: string, trackId: string, overrides: Partial<LauncherCell> = {}): LauncherCell {
  return { id, trackId, lengthTicks: Bar, playMode: 'loop', quantize: null, ...overrides };
}

const states = (changes: CellChange[]) => changes.map(c => `${c.cellId}:${c.state}@${c.tick}`);

describe('Live Loops launch quantise', () => {
  it('waits for the next boundary of each quantise', () => {
    expect(nextBoundary(100, 'off', meter)).toBe(100);
    expect(nextBoundary(100, 'sixteenth', meter)).toBe(240);
    expect(nextBoundary(100, 'eighth', meter)).toBe(480);
    expect(nextBoundary(100, 'quarter', meter)).toBe(Q);
    expect(nextBoundary(100, 'half', meter)).toBe(2 * Q);
    expect(nextBoundary(100, 'bar', meter)).toBe(Bar);
    expect(nextBoundary(Bar + 100, 'twoBars', meter)).toBe(2 * Bar);
    expect(nextBoundary(2 * Bar + 100, 'twoBars', meter)).toBe(4 * Bar);
    expect(nextBoundary(100, 'fourBars', meter)).toBe(4 * Bar);
  });

  it('takes the next boundary for a launch within 1 ms of one (or on it)', () => {
    expect(nextBoundary(Bar - 1, 'bar', meter, Late)).toBe(2 * Bar);
    expect(nextBoundary(Bar, 'bar', meter, Late)).toBe(2 * Bar);
    expect(nextBoundary(Bar - 3, 'bar', meter, Late)).toBe(Bar);
    expect(nextBoundary(Q - 1, 'quarter', meter, Late)).toBe(2 * Q);
  });

  it('follows the signature track: bars of 3/4 and note values from the bar line', () => {
    const waltz = new MeterMap([
      { tick: 0, numerator: 4, denominator: 4 },
      { tick: Bar, numerator: 3, denominator: 4 }
    ]);
    expect(nextBoundary(Bar + 100, 'bar', waltz)).toBe(Bar + 3 * Q);
    expect(nextBoundary(Bar + 3 * Q + 10, 'half', waltz)).toBe(Bar + 3 * Q + 2 * Q);
  });
});

describe('Live Loops launcher state machine', () => {
  it('queues a launch to its boundary, then plays it', () => {
    const l = launcher();
    expect(states(l.launch([cell('a', 't1')], 100))).toEqual([`a:queued@${Bar}`]);
    expect(l.state('a')).toBe('queued');
    expect(l.advance(Bar - 1)).toEqual([]);
    expect(states(l.advance(Bar))).toEqual([`a:playing@${Bar}`]);
    expect(l.state('a')).toBe('playing');
  });

  it('plays at once with quantise off, and a cell’s own quantise overrides the grid’s', () => {
    const l = launcher();
    expect(states(l.launch([cell('a', 't1', { quantize: 'off' })], 100))).toEqual(['a:queued@100', 'a:playing@100']);
    expect(states(l.launch([cell('b', 't2', { quantize: 'quarter' })], 100))).toEqual([`b:queued@${Q}`]);
  });

  it('plays one cell per track: a launch on a playing track stops its cell at the same boundary', () => {
    const l = launcher();
    l.launch([cell('a', 't1')], 0);
    l.advance(Bar);
    expect(states(l.launch([cell('b', 't1')], Bar + 10))).toEqual([`a:stopping@${2 * Bar}`, `b:queued@${2 * Bar}`]);
    expect(states(l.advance(2 * Bar))).toEqual([`a:stopped@${2 * Bar}`, `b:playing@${2 * Bar}`]);
    // A queued launch replaced before its boundary simply stops.
    l.launch([cell('c', 't2')], 2 * Bar + 5);
    expect(states(l.launch([cell('d', 't2')], 2 * Bar + 6))).toEqual([
      `c:stopped@${2 * Bar + 6}`,
      `d:queued@${3 * Bar}`
    ]);
  });

  it('keeps a loop playing when launched again; restarts a retrigger at the boundary', () => {
    const l = launcher();
    l.launch([cell('a', 't1'), cell('r', 't2', { playMode: 'retrigger', lengthTicks: 2 * Bar })], 0);
    l.advance(Bar);
    expect(l.launch([cell('a', 't1')], Bar + 10)).toEqual([]);
    expect(states(l.launch([cell('r', 't2', { playMode: 'retrigger', lengthTicks: 2 * Bar })], Bar + 10))).toEqual([
      `r:queued@${2 * Bar}`
    ]);
    expect(states(l.advance(2 * Bar))).toEqual([`r:playing@${2 * Bar}`]);
    // It plays its length from the restart, then stops.
    expect(l.advance(3 * Bar)).toEqual([]);
    expect(states(l.advance(4 * Bar))).toEqual([`r:stopped@${4 * Bar}`]);
  });

  it('stops a one-shot at its end', () => {
    const l = launcher();
    l.launch([cell('o', 't1', { playMode: 'oneShot', lengthTicks: 2 * Q })], 0);
    expect(states(l.advance(Bar + 3 * Q))).toEqual([`o:playing@${Bar}`, `o:stopped@${Bar + 2 * Q}`]);
  });

  it('launches a scene: its cells, and stops the tracks without a cell in it', () => {
    const l = launcher();
    l.launch([cell('a1', 't1'), cell('b1', 't2')], 0);
    l.advance(Bar);
    const changes = states(l.launchScene([cell('a2', 't1'), cell('c2', 't3')], Bar + 100));
    expect(changes).toEqual([
      `a1:stopping@${2 * Bar}`,
      `a2:queued@${2 * Bar}`,
      `c2:queued@${2 * Bar}`,
      `b1:stopping@${2 * Bar}`
    ]);
    l.advance(2 * Bar);
    expect([...l.active().keys()].sort()).toEqual(['a2', 'c2']);
    expect(l.playingOn('t2')).toBeNull();
    expect(l.playingOn('t1')?.id).toBe('a2');
  });

  it('stops tracks at the grid boundary, queued cells at once; resets when the transport stops', () => {
    const l = launcher('quarter');
    l.launch([cell('a', 't1'), cell('b', 't2', { quantize: 'fourBars' })], 0);
    l.advance(Q);
    expect(states(l.stop(['t1', 't2'], Q + 10))).toEqual([`a:stopping@${2 * Q}`, `b:stopped@${Q + 10}`]);
    expect(states(l.advance(2 * Q))).toEqual([`a:stopped@${2 * Q}`]);
    l.launch([cell('a', 't1')], 2 * Q + 10);
    l.advance(3 * Q);
    expect(states(l.reset(3 * Q + 5))).toEqual([`a:stopped@${3 * Q + 5}`]);
    expect(l.active().size).toBe(0);
  });

  it('forgets deleted cells, stopping them', () => {
    const l = launcher('off');
    l.launch([cell('a', 't1'), cell('b', 't2')], 0);
    expect(states(l.forget(new Set(['b']), 10))).toEqual(['a:stopped@10']);
    expect(l.state('a')).toBe('stopped');
    expect(l.state('b')).toBe('playing');
  });
});
