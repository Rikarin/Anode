import { DefaultRegionNotation, type MidiRegion, type Track } from '@anode/api-client';
import { ItemFlag } from '@anode/timeline';
import { NeutralPlayback } from '../midi/midi-math';
import { loopAt, loopCommand, loopEnd, loopRepeats, nextStartAfter, repeatsOf } from './region-loop';
import { buildScene } from './scene';

describe('loopEnd', () => {
  it('runs the loop out, or stops at the next region', () => {
    expect(loopEnd(3840, 1920, null)).toBe(5760);
    expect(loopEnd(3840, 1920, 4800)).toBe(4800);
    expect(loopEnd(3840, 1920, 7680)).toBe(5760);
  });

  it('never ends before the region', () => {
    expect(loopEnd(3840, 1920, 2000)).toBe(3840);
  });
});

describe('loopRepeats', () => {
  it('lays repeats end to end, the last one cut', () => {
    expect(loopRepeats(0, 1920, 5000)).toEqual([
      { startTick: 1920, lengthTicks: 1920 },
      { startTick: 3840, lengthTicks: 1160 }
    ]);
  });

  it('has none without a period or room', () => {
    expect(loopRepeats(0, 0, 5000)).toEqual([]);
    expect(loopRepeats(0, 1920, 1920)).toEqual([]);
  });
});

describe('the rest of a loop', () => {
  it('finds the next start after a tick', () => {
    expect(nextStartAfter([0, 960, 1920], 960)).toBe(1920);
    expect(nextStartAfter([0], 0)).toBeNull();
  });

  it('reads a corner drag as what lies past the region', () => {
    expect(loopAt(3840, 5760.4)).toBe(1920);
    expect(loopAt(3840, 1000)).toBe(0);
  });

  it('sets each kind by its own command', () => {
    expect(loopCommand('rgn_1', 'audio', 960.2)).toEqual({ type: 'setRegion', regionId: 'rgn_1', loopTicks: 960 });
    expect(loopCommand('mid_1', 'midi', 960)).toEqual({ type: 'setMidiRegion', regionId: 'mid_1', loopTicks: 960 });
    expect(loopCommand('pat_1', 'pattern', 960)).toEqual({
      type: 'setPatternRegion',
      regionId: 'pat_1',
      loopTicks: 960
    });
  });

  it('counts repeats to two places', () => {
    expect(repeatsOf(5760, 1920)).toBe(3);
    expect(repeatsOf(1000, 3000)).toBe(0.33);
    expect(repeatsOf(1000, 0)).toBe(0);
  });
});

describe('the scene of a looped region', () => {
  const track = { id: 'trk_1', color: '#00ff00', strip: { mute: false } } as Track;
  const region = (id: string, startTick: number, loopTicks: number): MidiRegion => ({
    id,
    trackId: 'trk_1',
    name: id,
    startTick,
    lengthTicks: 1920,
    playback: NeutralPlayback,
    notation: DefaultRegionNotation,
    notes: [],
    controls: [],
    muted: false,
    pitchSource: 'off',
    key: null,
    loopTicks
  });
  const scene = (regions: MidiRegion[], draft?: { id: string; loopTicks: number }) =>
    buildScene({
      tracks: [track],
      regions: [],
      midiRegions: regions,
      framesPerTick: 25,
      beatsPerBar: 4,
      selected: new Set(['mid_1']),
      waveformKey: () => 0,
      ...(draft ? { loopDraft: draft } : {})
    });

  it('draws repeats after the region, dimmed, belonging to it', () => {
    const { scene: built, regionIds, indexOf } = scene([region('mid_1', 0, 3000)]);
    expect(regionIds).toEqual(['mid_1', 'mid_1', 'mid_1']);
    expect(indexOf.get('mid_1')).toBe(0);
    expect(Array.from(built.items.start)).toEqual([0, 1920, 3840]);
    expect(Array.from(built.items.length)).toEqual([1920, 1920, 1080]);
    expect((built.regions?.flags[1] ?? 0) & ItemFlag.Loop).toBe(ItemFlag.Loop);
    expect((built.regions?.flags[1] ?? 0) & ItemFlag.Selected).toBe(0);
    expect((built.regions?.flags[0] ?? 0) & ItemFlag.Loop).toBe(0);
  });

  it('stops repeats at the next region on the track', () => {
    const { scene: built } = scene([region('mid_1', 0, 9600), region('mid_2', 3000, 0)]);
    expect(Array.from(built.items.start)).toEqual([0, 1920, 3000]);
    expect(Array.from(built.items.length)).toEqual([1920, 1080, 1920]);
  });

  it('draws the loop being dragged in place of its own', () => {
    expect(scene([region('mid_1', 0, 0)], { id: 'mid_1', loopTicks: 1920 }).regionIds).toHaveLength(2);
    expect(scene([region('mid_1', 0, 1920)], { id: 'mid_1', loopTicks: 0 }).regionIds).toHaveLength(1);
  });
});
