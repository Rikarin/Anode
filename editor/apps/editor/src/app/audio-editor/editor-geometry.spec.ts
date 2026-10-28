import type { AudioRegion, PitchNote } from '@anode/api-client';
import type { Waveform } from '@anode/timeline';
import { centsRange, curvePolylines, editedRegion, rowHeight, waveformPath, yAtCents } from './editor-geometry';

const note = (cents: number): PitchNote => ({
  startFrame: 0,
  endFrame: 100,
  detectedCents: cents,
  targetCents: cents,
  drift: 1,
  vibrato: 1,
  formantCents: 0,
  gainDb: 0
});

describe('audio editor geometry', () => {
  it('shows the notes with room around them, at least an octave and a half', () => {
    expect(centsRange([])).toEqual([4800, 8400]);
    expect(centsRange([note(6012), note(6390)])).toEqual([5300, 7100]);
    expect(centsRange([note(6000), note(8000)])).toEqual([5700, 8300]);
  });

  it('places pitches top to bottom', () => {
    expect(yAtCents(6900, [5100, 6900], 180)).toBe(0);
    expect(yAtCents(6000, [5100, 6900], 180)).toBe(90);
    expect(rowHeight([5100, 6900], 180)).toBe(10);
  });

  it('draws the pitch curve as one line per voiced run', () => {
    const lines = curvePolylines(
      [6000, 6010, null, 6100, 6110, 6120],
      10,
      0,
      60,
      f => f,
      c => c - 6000
    );
    expect(lines).toEqual(['0.0,0.0 10.0,10.0', '30.0,100.0 40.0,110.0 50.0,120.0']);
  });

  it('draws a waveform envelope', () => {
    const waveform: Waveform = {
      channels: 1,
      framesPerPeak: 10,
      firstPeak: 0,
      peakCount: 2,
      data: new Int16Array([-32767, 32767, 0, 0])
    };
    expect(waveformPath(waveform, x => x * 10, 2, 20)).toBe('M0,0.0 L1,10.0 L1,10.0 L0,20.0 Z');
    expect(waveformPath(waveform, x => 1000 + x, 2, 20)).toBe('');
  });

  it('edits the selected audio region, else the selected track’s first', () => {
    const a = { id: 'a', trackId: 't', startTick: 960 } as AudioRegion;
    const b = { id: 'b', trackId: 't', startTick: 0 } as AudioRegion;
    const regions = new Map([
      ['a', a],
      ['b', b]
    ]);
    expect(editedRegion(regions, new Set(['mid_1', 'a']), null)).toBe(a);
    expect(editedRegion(regions, new Set(), 't')).toBe(b);
    expect(editedRegion(regions, new Set(), null)).toBeNull();
  });
});
