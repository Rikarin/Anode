import type { FileTempo } from '@anode/api-client';
import {
  addBeat,
  averageBpm,
  doubleTempo,
  halveTempo,
  moveBeat,
  removeBeat,
  toggleDownbeat,
  variesTempo
} from './beat-draft';

const Sr = 48_000;
/** 100 bpm: a beat every 28 800 frames, downbeats every four. */
const tempo: FileTempo = {
  bpm: 100,
  variable: false,
  beats: [0, 28_800, 57_600, 86_400, 115_200, 144_000],
  downbeats: [0, 115_200]
};

describe('Smart Tempo beat drafts', () => {
  it('measures the tempo and whether it varies', () => {
    expect(averageBpm(tempo.beats, Sr, 120)).toBe(100);
    expect(averageBpm([5], Sr, 120)).toBe(120);
    expect(variesTempo(tempo.beats)).toBe(false);
    expect(variesTempo([0, 28_800, 62_000, 86_400])).toBe(true);
  });

  it('moves a beat between its neighbours, keeping a downbeat', () => {
    const moved = moveBeat(tempo, 4, 120_000, Sr);
    expect(moved.beats[4]).toBe(120_000);
    expect(moved.downbeats).toEqual([0, 120_000]);
    expect(moved.variable).toBe(true);
    expect(moveBeat(tempo, 1, 99_999, Sr).beats[1]).toBe(57_599);
  });

  it('adds and removes beats', () => {
    expect(addBeat(tempo, 14_400, Sr).beats.slice(0, 3)).toEqual([0, 14_400, 28_800]);
    const removed = removeBeat(tempo, 4, Sr);
    expect(removed.beats).not.toContain(115_200);
    expect(removed.downbeats).toEqual([0]);
  });

  it('toggles downbeats', () => {
    expect(toggleDownbeat(tempo, 2, Sr).downbeats).toEqual([0, 57_600, 115_200]);
    expect(toggleDownbeat(tempo, 0, Sr).downbeats).toEqual([115_200]);
  });

  it('doubles and halves the tempo', () => {
    const doubled = doubleTempo(tempo, Sr);
    expect(doubled.bpm).toBe(200);
    expect(doubled.beats).toHaveLength(11);
    expect(doubled.downbeats).toEqual([0, 115_200]);
    const halved = halveTempo(tempo, Sr);
    expect(halved.bpm).toBe(50);
    expect(halved.beats).toEqual([0, 57_600, 115_200]);
    expect(halved.downbeats).toEqual([0]);
  });
});
