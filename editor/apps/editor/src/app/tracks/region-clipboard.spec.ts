import type { AudioRegion, MidiRegion, Track } from '@anode/api-client';
import {
  type ClipboardTrack,
  laneSlices,
  marqueeContent,
  mergedRanges,
  NoContent,
  pasteCommand,
  pasteLaneCommands,
  placeTracks,
  trackNeeds
} from './region-clipboard';
import type { RegionLookup } from './tool-edits';

const Bar = 3840;
const track = (id: string, kind: Track['kind'], drumMachine: Track['drumMachine'] = null) => ({
  id,
  kind,
  drumMachine
});
const source = (trackId: string, row: number, ...needs: ClipboardTrack['needs']): ClipboardTrack => ({
  trackId,
  row,
  needs
});

describe('placeTracks', () => {
  const tracks = [track('a', 'audio'), track('b', 'instrument'), track('c', 'audio'), track('d', 'instrument')];

  it('puts the topmost on the anchor and keeps the others below it', () => {
    const places = placeTracks([source('x', 0, 'audio'), source('y', 1, 'midi')], 'a', tracks);
    expect([...(places ?? [])]).toEqual([
      ['x', 'a'],
      ['y', 'b']
    ]);
  });

  it('keeps the rows between them', () => {
    const places = placeTracks([source('x', 0, 'audio'), source('y', 3, 'midi')], 'a', tracks);
    expect(places?.get('y')).toBe('d');
  });

  it('moves on down past a track that cannot hold the region', () => {
    const places = placeTracks([source('x', 0, 'midi'), source('y', 1, 'midi')], 'b', tracks);
    expect(places?.get('y')).toBe('d');
  });

  it('refuses an anchor that cannot hold it, and tracks that run out', () => {
    expect(placeTracks([source('x', 0, 'midi')], 'a', tracks)).toBeNull();
    expect(placeTracks([source('x', 0, 'midi'), source('y', 1, 'midi')], 'd', tracks)).toBeNull();
    expect(placeTracks([source('x', 0, 'midi')], 'gone', tracks)).toBeNull();
  });

  it('takes a Session Player region to a track of its player only, and MIDI to a Drum Machine Designer', () => {
    const list = [track('s', 'stack', { pads: [] }), track('t', 'drummer')];
    expect(placeTracks([source('x', 0, 'midi')], 's', list)).not.toBeNull();
    expect(placeTracks([source('x', 0, 'drummer')], 't', list)).not.toBeNull();
    expect(placeTracks([source('x', 0, 'bassPlayer')], 't', list)).toBeNull();
  });
});

describe('trackNeeds', () => {
  it('reads what each track held', () => {
    const needs = trackNeeds(
      {
        ...NoContent,
        regions: [{ trackId: 'a' } as AudioRegion],
        midiRegions: [{ trackId: 'b' } as MidiRegion]
      },
      () => 'audio'
    );
    expect([...(needs.get('a') ?? [])]).toEqual(['audio']);
    expect([...(needs.get('b') ?? [])]).toEqual(['midi']);
  });
});

describe('mergedRanges', () => {
  it('merges touching spans per track', () => {
    expect(
      mergedRanges([
        { trackId: 'a', startTick: Bar, endTick: 2 * Bar },
        { trackId: 'a', startTick: 0, endTick: Bar },
        { trackId: 'a', startTick: 3 * Bar, endTick: 4 * Bar },
        { trackId: 'b', startTick: 0, endTick: Bar }
      ])
    ).toEqual([
      { trackId: 'a', startTick: 0, endTick: 2 * Bar },
      { trackId: 'a', startTick: 3 * Bar, endTick: 4 * Bar },
      { trackId: 'b', startTick: 0, endTick: Bar }
    ]);
  });
});

describe('automation on the clipboard', () => {
  const lanes = [
    {
      trackId: 'a',
      target: 'volume',
      points: [
        { tick: 0, value: -12, curve: 0 },
        { tick: 960, value: -6, curve: 0 },
        { tick: 1920, value: 0, curve: 0 }
      ]
    },
    { trackId: 'a', target: 'pan', points: [{ tick: 5000, value: 1, curve: 0 }] }
  ];
  const slices = laneSlices([{ trackId: 'a', startTick: 480, endTick: 1440 }], lanes, () => false);

  it('takes lanes with points in the range, with values at its edges', () => {
    expect(slices.map(s => s.target)).toEqual(['volume']);
    expect(slices[0]?.points.map(q => [q.tick, q.value])).toEqual([
      [480, -9],
      [960, -6],
      [1440, -3]
    ]);
  });

  it('lays them over the lanes of the tracks they land on', () => {
    const { commands, skipped } = pasteLaneCommands(slices, 3360, new Map([['a', 'b']]), [], () => true);
    expect(skipped).toBe(0);
    expect(commands).toEqual([expect.objectContaining({ type: 'setAutomation', trackId: 'b', target: 'volume' })]);
    const first = commands[0];
    expect(first?.type === 'setAutomation' ? first.points.map(q => q.tick) : []).toEqual([3840, 4320, 4800]);
  });

  it('counts what it leaves out', () => {
    const { commands, skipped } = pasteLaneCommands(slices, 0, new Map([['a', 'b']]), [], () => false);
    expect(commands).toEqual([]);
    expect(skipped).toBe(1);
  });
});

describe('pasteCommand', () => {
  it('moves takes and the comp with their folder', () => {
    const folder = {
      id: 'tkf_1',
      trackId: 'a',
      name: 'Takes',
      startTick: 100,
      lengthTicks: 200,
      takes: [{ id: 'tak_1', name: 'Take', fileId: 'aud_1', startTick: 100, fileOffset: 0, length: 10 }],
      comp: [{ startTick: 100, endTick: 300, takeId: 'tak_1' }],
      crossfade: 0,
      muted: false
    };
    const command = pasteCommand({ ...NoContent, takeFolders: [folder] }, 50, new Map([['a', 'b']]));
    expect(command).toMatchObject({
      type: 'pasteRegions',
      takeFolders: [
        { trackId: 'b', startTick: 150, takes: [{ startTick: 150 }], comp: [{ startTick: 150, endTick: 350 }] }
      ]
    });
  });
});

describe('marqueeContent', () => {
  const audio = {
    id: 'rgn_1',
    trackId: 'a',
    fileId: 'aud_1',
    name: 'Vox',
    startTick: 0,
    fileOffset: 1000,
    length: 4000,
    gainDb: 0,
    fadeIn: 100,
    fadeOut: 100,
    fadeInCurve: 0,
    fadeOutCurve: 0,
    muted: false,
    follow: 'off',
    flexMarkers: [
      { fileFrame: 1500, tick: 500 },
      { fileFrame: 2500, tick: 1500 }
    ],
    transpose: 0,
    fineTune: 0,
    pitchNotes: [],
    pitchSource: 'off',
    key: null
  } as unknown as AudioRegion;
  const lookup = {
    regions: new Map([[audio.id, audio]]),
    midiRegions: new Map(),
    takeFolders: new Map(),
    endOf: () => 4000,
    frames: (from: number, to: number) => to - from,
    fileFrames: () => 100_000
  } as RegionLookup;

  it('cuts an audio region across an edge to it, with its fades and flex markers', () => {
    const content = marqueeContent(1000, 2000, [{ id: audio.id, startTick: 0, endTick: 4000 }], lookup);
    expect(content.regions).toHaveLength(1);
    expect(content.regions[0]).toMatchObject({
      startTick: 1000,
      fileOffset: 2000,
      length: 1000,
      fadeIn: 0,
      fadeOut: 0,
      flexMarkers: [{ fileFrame: 2500, tick: 500 }]
    });
  });

  it('takes a region inside it whole', () => {
    const content = marqueeContent(0, 8000, [{ id: audio.id, startTick: 0, endTick: 4000 }], lookup);
    expect(content.regions).toEqual([audio]);
  });
});
