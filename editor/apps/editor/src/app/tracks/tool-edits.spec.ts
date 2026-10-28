import type { AudioRegion, MidiRegion } from '@anode/api-client';
import {
  adjustmentCommands,
  crossfadeCommands,
  marqueeDeleteCommands,
  muteCommands,
  type RegionLookup,
  renameCommand
} from './tool-edits';

// 25 frames per tick: 48 kHz at 120 bpm.
const audio = (id: string, startTick: number, length: number, fileOffset = 0): AudioRegion => ({
  id,
  trackId: 't',
  fileId: 'f',
  name: id,
  startTick,
  fileOffset,
  length,
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
  pitchNotes: []
});
const midi: MidiRegion = {
  id: 'mid_1',
  trackId: 'k',
  name: 'Keys',
  startTick: 0,
  lengthTicks: 3840,
  playback: { transpose: 0, velocity: 0, quantizeTicks: 0, quantizeStrength: 1, swing: 0 },
  notes: [],
  controls: [],
  muted: true
};

function lookup(regions: AudioRegion[]): RegionLookup {
  return {
    regions: new Map(regions.map(r => [r.id, r])),
    midiRegions: new Map([[midi.id, midi]]),
    takeFolders: new Map(),
    endOf: r => r.startTick + r.length / 25,
    frames: (from, to) => Math.round((to - from) * 25),
    fileFrames: () => 1_000_000
  };
}

describe('tool edits', () => {
  it('mutes or unmutes after the first region, for every kind', () => {
    const l = lookup([audio('a', 0, 2500)]);
    expect(muteCommands(['a', 'mid_1'], l)).toEqual([
      { type: 'setRegion', regionId: 'a', muted: true },
      { type: 'setMidiRegion', regionId: 'mid_1', muted: true }
    ]);
    expect(muteCommands(['mid_1'], l)).toEqual([{ type: 'setMidiRegion', regionId: 'mid_1', muted: false }]);
    expect(renameCommand('mid_1', 'Chords', l)).toEqual({ type: 'setMidiRegion', regionId: 'mid_1', name: 'Chords' });
  });

  it('turns drag-mode adjustments into commands', () => {
    const l = lookup([audio('a', 0, 25_000), audio('b', 2000, 25_000)]);
    const moves = [{ type: 'moveRegion' as const, regionId: 'b', startTick: 800 }];
    expect(
      adjustmentCommands(
        moves,
        [
          { kind: 'move', id: 'b', startTick: 1000 },
          { kind: 'trimEnd', id: 'a', endTick: 500 },
          { kind: 'fade', id: 'b', fadeInTicks: 100 }
        ],
        l
      )
    ).toEqual([
      { type: 'moveRegion', regionId: 'b', startTick: 1000 },
      { type: 'resizeRegion', regionId: 'a', startTick: 0, fileOffset: 0, length: 12_500 },
      { type: 'setRegion', regionId: 'b', fadeIn: 2500 }
    ]);
  });

  it('crossfades touching regions by extending them over each other', () => {
    const l = lookup([]);
    const commands = crossfadeCommands(audio('a', 0, 25_000), audio('b', 1000, 25_000, 50_000), 200, l);
    expect(commands).toEqual([
      { type: 'resizeRegion', regionId: 'a', startTick: 0, fileOffset: 0, length: 27_500 },
      { type: 'resizeRegion', regionId: 'b', startTick: 900, fileOffset: 47_500, length: 27_500 },
      { type: 'setRegion', regionId: 'a', fadeOut: 5000 },
      { type: 'setRegion', regionId: 'b', fadeIn: 5000 }
    ]);
    // Already overlapping: fades over the overlap.
    expect(crossfadeCommands(audio('a', 0, 25_000), audio('b', 800, 25_000), 200, l)).toEqual([
      { type: 'setRegion', regionId: 'a', fadeOut: 5000 },
      { type: 'setRegion', regionId: 'b', fadeIn: 5000 }
    ]);
  });

  it('deletes a marquee selection: inside goes, edges trim, a region across both splits', () => {
    const regions = [audio('in', 1200, 2500), audio('left', 0, 30_000), audio('across', 0, 100_000)];
    const l = lookup(regions);
    const spans = regions.map(r => ({ id: r.id, startTick: r.startTick, endTick: l.endOf(r) }));
    expect(marqueeDeleteCommands(1000, 2000, spans, l)).toEqual([
      { type: 'resizeRegion', regionId: 'left', startTick: 0, fileOffset: 0, length: 25_000 },
      { type: 'splitRegion', regionId: 'across', atTick: 2000 },
      { type: 'resizeRegion', regionId: 'across', startTick: 0, fileOffset: 0, length: 25_000 },
      { type: 'deleteRegions', regionIds: ['in'] }
    ]);
  });
});
