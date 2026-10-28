import type { AudioRegion, Track } from '@anode/api-client';
import { hitTest, ItemFlag, LaneIntervalIndex, RULER_HEIGHT, type TimelineView } from '@anode/timeline';
import {
  type EditContext,
  fadeInTo,
  fadeOutFrom,
  MinRegionFrames,
  moveCommands,
  moveRegions,
  plural,
  splitPoint,
  trimEnd,
  trimStart
} from './region-edit';
import { buildScene, trackColor } from './scene';

// 25 frames per tick: 48 kHz at 120 bpm. A beat (960 ticks) is 24,000 frames.
const snapped: EditContext = { framesPerTick: 25, snap: 240 };
const free: EditContext = { framesPerTick: 25, snap: 0 };

function region(change: Partial<AudioRegion> = {}): AudioRegion {
  return {
    id: 'rgn_1',
    trackId: 'trk_1',
    fileId: 'aud_1',
    name: 'Take',
    startTick: 1920,
    fileOffset: 24_000, // a beat into its file
    length: 48_000, // two beats
    gainDb: 0,
    fadeIn: 0,
    fadeOut: 0,
    fadeInCurve: 0,
    fadeOutCurve: 0,
    muted: false,
    follow: 'off',
    flexMarkers: [],
    transpose: 0,
    fineTune: 0,
    pitchNotes: [],
    ...change
  };
}

describe('moveRegions', () => {
  const selection = [
    { id: 'a', startTick: 960, lane: 0 },
    { id: 'b', startTick: 1920, lane: 1 }
  ];

  it('moves the selection together, snapping the grabbed region’s start', () => {
    const moved = moveRegions(selection, { startTick: 960 }, 500, 1, 3, snapped);
    // 960 + 500 snaps to 1440 (a sixteenth grid): +480 for both.
    expect(moved).toEqual([
      { id: 'a', startTick: 1440, lane: 1 },
      { id: 'b', startTick: 2400, lane: 2 }
    ]);
  });

  it('stops at the project start and the last lane', () => {
    expect(moveRegions(selection, { startTick: 960 }, -5_000, 5, 3, free)).toEqual([
      { id: 'a', startTick: 0, lane: 1 },
      { id: 'b', startTick: 960, lane: 2 }
    ]);
  });

  it('turns into commands only for regions that moved, with the track when it changed', () => {
    const regions = new Map([
      ['a', region({ id: 'a', startTick: 960 })],
      ['b', region({ id: 'b', startTick: 1920, trackId: 'trk_2' })]
    ]);
    const tracks = ['trk_1', 'trk_2', 'trk_3'];
    const commands = moveCommands(
      [
        { id: 'a', startTick: 1440, lane: 0 },
        { id: 'b', startTick: 1920, lane: 2 },
        { id: 'c', startTick: 0, lane: 0 }
      ],
      regions,
      lane => tracks[lane]
    );
    expect(commands).toEqual([
      { type: 'moveRegion', regionId: 'a', startTick: 1440 },
      { type: 'moveRegion', regionId: 'b', startTick: 1920, trackId: 'trk_3' }
    ]);
  });
});

describe('trims, fades and splits', () => {
  it('trims the start, revealing the file but never before its first frame', () => {
    expect(trimStart(region(), 1440, snapped)).toEqual({ startTick: 1440, fileOffset: 12_000, length: 60_000 });
    expect(trimStart(region(), 0, snapped)).toEqual({ startTick: 960, fileOffset: 0, length: 72_000 });
    const hidden = trimStart(region(), 99_999, free);
    expect(hidden.length).toBe(MinRegionFrames);
  });

  it('trims the end, not past the file', () => {
    expect(trimEnd(region(), 2880, 200_000, snapped)).toEqual({ startTick: 1920, fileOffset: 24_000, length: 24_000 });
    expect(trimEnd(region(), 1e6, 96_000, snapped).length).toBe(72_000);
    expect(trimEnd(region(), 0, 96_000, snapped).length).toBe(MinRegionFrames);
  });

  it('drags fades in frames, each limited by the other', () => {
    expect(fadeInTo(region(), 2400, free)).toBe(12_000);
    expect(fadeInTo(region({ fadeOut: 40_000 }), 3800, free)).toBe(8_000);
    expect(fadeOutFrom(region(), 3360, free)).toBe(12_000);
    expect(fadeInTo(region(), 0, free)).toBe(0);
  });

  it('splits strictly inside a region', () => {
    expect(splitPoint(region(), 2900, snapped)).toBe(2880);
    expect(splitPoint(region(), 1925, snapped)).toBeNull(); // snaps to the start
    expect(splitPoint(region(), 3840, free)).toBeNull(); // the end
  });

  it('labels undo steps by count', () => {
    expect(plural('Move', 1)).toBe('Move Region');
    expect(plural('Delete', 3)).toBe('Delete 3 Regions');
  });
});

describe('buildScene', () => {
  const track = (id: string, color: string, mute = false): Track => ({
    id,
    name: id,
    kind: 'audio',
    color,
    input: { channel: 1, stereo: false },
    recordArmed: false,
    inputMonitoring: false,
    strip: {
      volumeDb: 0,
      pan: 0,
      mute,
      solo: false,
      inserts: [],
      sends: [],
      output: 'master',
      groups: [],
      vcaId: null,
      midiEffects: []
    },
    instrument: null,
    midiInput: { device: null, channel: 0 },
    midiOutput: null,
    parentId: null,
    collapsed: false,
    notes: '',
    inputBus: 0,
    automationMode: 'read',
    frozenFileId: null,
    flexOn: false,
    flexMode: 'polyphonic',
    alternatives: [],
    alternativeId: null,
    drumMachine: null,
    smartControls: null,
    liveLoopsActive: false
  });

  it('lays regions on their tracks’ lanes in their colours, with selection, mute and file mapping', () => {
    const { scene, regionIds, indexOf } = buildScene({
      tracks: [track('trk_1', '#ff0000'), track('trk_2', '#0f0', true)],
      regions: [region(), region({ id: 'rgn_2', trackId: 'trk_2', gainDb: -6 }), region({ id: 'x', trackId: 'gone' })],
      framesPerTick: 25,
      beatsPerBar: 4,
      selected: new Set(['rgn_1']),
      waveformKey: fileId => (fileId === 'aud_1' ? 7 : -1)
    });

    expect(regionIds).toEqual(['rgn_1', 'rgn_2']);
    expect(indexOf.get('rgn_2')).toBe(1);
    expect(Array.from(scene.items.lane)).toEqual([0, 1]);
    expect(Array.from(scene.items.length)).toEqual([1920, 1920]);
    expect(scene.items.color[1]).toBe(trackColor('#00ff00'));
    expect(Array.from(scene.regions?.flags ?? [])).toEqual([ItemFlag.Selected, ItemFlag.Muted]);
    expect(scene.regions?.gain[1]).toBeCloseTo(0.501, 3);
    expect(scene.regions?.waveform[0]).toBe(7);
    expect(scene.regions?.fileOffset[0]).toBe(24_000);
  });

  it('hit-tests the regions it built', () => {
    const { scene, regionIds } = buildScene({
      tracks: [track('trk_1', '#ff0000')],
      regions: [region()],
      framesPerTick: 25,
      beatsPerBar: 4,
      selected: new Set(),
      waveformKey: () => -1
    });
    const view: TimelineView = { originTick: 0, pixelsPerTick: 0.05, firstLane: 0, laneHeight: 60, playheadTick: 0 };
    const hit = hitTest(scene, new LaneIntervalIndex(scene.items, 1), view, 130, RULER_HEIGHT + 50);
    expect(hit && regionIds[hit.index]).toBe('rgn_1');
    expect(trackColor('rgb(1 2 3)')).toBe(trackColor('#0090ff'));
  });
});
