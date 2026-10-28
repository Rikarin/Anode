import type { MidiRegion, Track } from '@anode/api-client';
import { ItemFlag } from '@anode/timeline';
import { NeutralPlayback } from '../midi/midi-math';
import { moveCommands, newMidiRegion, splitSpan, trimMidiEnd, trimMidiStart } from './region-edit';
import { buildScene } from './scene';

const region = { startTick: 3840, lengthTicks: 3840 };

describe('MIDI region edits in the Tracks area', () => {
  it('trims a MIDI region’s start on the grid, not past its end less a step', () => {
    expect(trimMidiStart(region, 4900, 960)).toEqual({ startTick: 4800, lengthTicks: 2880 });
    expect(trimMidiStart(region, 9000, 960)).toEqual({ startTick: 6720, lengthTicks: 960 });
    expect(trimMidiStart(region, -500, 960)).toEqual({ startTick: 0, lengthTicks: 7680 });
  });

  it('trims its end freely to the right, at least a step long', () => {
    expect(trimMidiEnd(region, 12_000, 960)).toEqual({ startTick: 3840, lengthTicks: 8640 });
    expect(trimMidiEnd(region, 100, 960)).toEqual({ startTick: 3840, lengthTicks: 960 });
    expect(trimMidiEnd(region, 4000, 0)).toEqual({ startTick: 3840, lengthTicks: 160 });
  });

  it('splits strictly inside, and makes a new region from the bar line', () => {
    expect(splitSpan(3840, 7680, 5000, 960)).toBe(4800);
    expect(splitSpan(3840, 7680, 3900, 960)).toBeNull();
    expect(newMidiRegion(5000, 3840)).toEqual({ startTick: 3840, lengthTicks: 3840 });
  });

  it('keeps a region on its own track when dropped on one of the other kind', () => {
    const regions = new Map([
      ['mid_1', { startTick: 0, trackId: 'trk_inst' }],
      ['rgn_1', { startTick: 0, trackId: 'trk_audio' }]
    ]);
    const kinds: Record<string, string> = { trk_inst: 'instrument', trk_audio: 'audio' };
    const fits = (id: string, trackId: string) => (kinds[trackId] === 'audio') === !id.startsWith('mid_');
    expect(
      moveCommands(
        [
          { id: 'mid_1', startTick: 960, lane: 1 },
          { id: 'rgn_1', startTick: 960, lane: 0 }
        ],
        regions,
        lane => ['trk_inst', 'trk_audio'][lane],
        fits
      )
    ).toEqual([
      { type: 'moveRegion', regionId: 'mid_1', startTick: 960 },
      { type: 'moveRegion', regionId: 'rgn_1', startTick: 960 }
    ]);
  });

  it('draws MIDI regions after the audio ones, flagged, with their notes transposed as they play', () => {
    const track: Track = {
      id: 'trk_1',
      name: 'Keys',
      kind: 'instrument',
      color: '#46a758',
      input: { channel: 1, stereo: false },
      recordArmed: false,
      inputMonitoring: false,
      strip: { volumeDb: 0, pan: 0, mute: false, solo: false },
      instrument: null,
      midiInput: { device: null, channel: 0 },
      midiOutput: null,
      parentId: null,
      collapsed: false,
      notes: ''
    };
    const midi: MidiRegion = {
      id: 'mid_1',
      trackId: 'trk_1',
      name: 'Chords',
      startTick: 960,
      lengthTicks: 3840,
      playback: { ...NeutralPlayback, transpose: 12 },
      notes: [{ id: 1, pitch: 60, velocity: 90, channel: 1, startTick: 0, lengthTicks: 480 }],
      controls: [],
      muted: false
    };
    const { scene, regionIds } = buildScene({
      tracks: [track],
      regions: [],
      midiRegions: [midi],
      framesPerTick: 25,
      beatsPerBar: 4,
      selected: new Set(['mid_1']),
      waveformKey: () => 0
    });
    expect(regionIds).toEqual(['mid_1']);
    expect(scene.items.length[0]).toBe(3840);
    expect(scene.regions?.flags[0]).toBe(ItemFlag.Midi | ItemFlag.Selected);
    expect(scene.regions?.waveform[0]).toBe(-1);
    expect(Array.from(scene.regions?.notes?.pitch ?? [])).toEqual([72]);
  });
});
