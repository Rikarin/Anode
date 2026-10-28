import { MeterMap } from '@anode/timeline';
import { RegionTiming, type TimedRegion } from '../music/region-timing';
import { TempoMap } from '../music/tempo-map';
import { beginFlexDrag, deleteFlexMarker, dragFlexMarker, flexHit } from './flex-markers';

const context = { tempo: TempoMap.constant(120), meter: MeterMap.constant(4, 4), sampleRate: 48_000 };

/** 2 s from bar 2 (3 840 ticks long at 120 bpm), flex on. */
function timing(markers: TimedRegion['flexMarkers'] = []): RegionTiming {
  const region: TimedRegion = {
    startTick: 3840,
    fileOffset: 0,
    length: 96_000,
    follow: 'off',
    flexMarkers: markers,
    transpose: 0,
    fineTune: 0,
    pitchNotes: []
  };
  return new RegionTiming(region, true, null, context);
}

/** Transients every quarter note (24 000 frames). */
const transients = [24_000, 48_000, 72_000];
/** 4 ticks per pixel. */
const tpp = 4;

describe('flex marker hit-testing', () => {
  it('grabs a flex marker within a few pixels, in either half', () => {
    const t = timing([{ fileFrame: 30_000, tick: 1000 }]);
    expect(flexHit(t, transients, 3840 + 1012, true, tpp)).toEqual({ zone: 'marker', index: 0 });
    expect(flexHit(t, transients, 3840 + 992, false, tpp)).toEqual({ zone: 'marker', index: 0 });
    expect(flexHit(t, transients, 3840 + 1040, false, tpp).zone).toBe('single');
  });

  it('grabs a transient in the upper half only', () => {
    const t = timing();
    expect(flexHit(t, transients, 3840 + 962, true, tpp)).toEqual({ zone: 'transient', frame: 24_000 });
    expect(flexHit(t, transients, 3840 + 962, false, tpp)).toEqual({ zone: 'single', frame: 24_050 });
  });

  it('makes a marker with its neighbours elsewhere in the upper half', () => {
    expect(flexHit(timing(), transients, 3840 + 1440, true, tpp)).toEqual({ zone: 'neighbours', frame: 36_000 });
  });
});

describe('flex marker drags', () => {
  it('adds a marker that changes nothing until it moves', () => {
    const drag = beginFlexDrag(timing(), transients, { zone: 'single', frame: 36_000 });
    expect(drag).toEqual({ markers: [{ fileFrame: 36_000, tick: 1440 }], index: 0 });
  });

  it('adds the neighbouring transients and drags the middle marker', () => {
    const drag = beginFlexDrag(timing(), transients, { zone: 'neighbours', frame: 36_000 });
    expect(drag?.markers).toEqual([
      { fileFrame: 24_000, tick: 960 },
      { fileFrame: 36_000, tick: 1440 },
      { fileFrame: 48_000, tick: 1920 }
    ]);
    expect(drag?.index).toBe(1);
    if (!drag) {
      throw new Error('no drag');
    }
    // Dragged past a neighbour, it stops a tick short of it.
    expect(dragFlexMarker(drag, 5000, 3840)[1]?.tick).toBe(1919);
    expect(dragFlexMarker(drag, -50, 3840)[1]?.tick).toBe(961);
  });

  it('puts a marker on a transient', () => {
    const drag = beginFlexDrag(timing(), transients, { zone: 'transient', frame: 72_000 });
    expect(drag?.markers).toEqual([{ fileFrame: 72_000, tick: 2880 }]);
  });

  it('moves an existing marker, inside the region', () => {
    const t = timing([{ fileFrame: 48_000, tick: 1920 }]);
    const drag = beginFlexDrag(t, transients, { zone: 'marker', index: 0 });
    if (!drag) {
      throw new Error('no drag');
    }
    expect(dragFlexMarker(drag, 1500, t.lengthTicks)).toEqual([{ fileFrame: 48_000, tick: 1500 }]);
    expect(dragFlexMarker(drag, 99_999, t.lengthTicks)[0]?.tick).toBe(3839);
  });

  it('refuses a new marker outside the region', () => {
    expect(beginFlexDrag(timing(), transients, { zone: 'single', frame: 200_000 })).toBeNull();
  });

  it('deletes a marker', () => {
    const markers = [
      { fileFrame: 48_000, tick: 1920 },
      { fileFrame: 24_000, tick: 900 }
    ];
    expect(deleteFlexMarker(markers, 0)).toEqual([{ fileFrame: 48_000, tick: 1920 }]);
  });
});
