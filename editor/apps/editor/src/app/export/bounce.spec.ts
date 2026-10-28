import { MeterMap } from '@anode/timeline';
import { DefaultSettings } from '../state/project.store';
import { bounceFileNames, bounceProblems, bounceRequest, defaultBounceForm } from './bounce';

const bars = MeterMap.constant(4, 4);
const bar = 4 * 960;

describe('bounce form', () => {
  it('starts with the project name, the whole project and a 24-bit WAV with TPDF dither', () => {
    const form = defaultBounceForm('Song', DefaultSettings, [], bars);
    expect(form).toMatchObject({
      fileName: 'Song',
      startBar: 1,
      endBar: 33,
      source: 'mix',
      format: 'wav',
      bitDepth: 24,
      dither: 'tpdf',
      normalize: 'off',
      loudnessLufs: -14,
      peakCeilingDb: -1
    });
  });

  it('takes the cycle when it is on, rounded out to whole bars', () => {
    const settings = { ...DefaultSettings, cycle: { enabled: true, startTick: 4 * bar + 100, endTick: 8 * bar - 100 } };
    const form = defaultBounceForm('Song', settings, [], bars);
    expect([form.startBar, form.endBar]).toEqual([5, 9]);
  });

  it('makes the request: ticks from bars, dither off at 32-bit float, stems with their tracks', () => {
    const form = { ...defaultBounceForm('Mix', DefaultSettings, [], bars), startBar: 2, endBar: 4 };
    expect(bounceRequest(form, bars)).toMatchObject({
      fileName: 'Mix',
      directory: null,
      startTick: bar,
      endTick: 3 * bar,
      trackIds: null,
      dither: 'tpdf'
    });
    const float = bounceRequest({ ...form, bitDepth: 32, source: 'stems', trackIds: ['t1'] }, bars);
    expect(float.dither).toBe('none');
    expect(float.trackIds).toEqual(['t1']);
  });

  it('says what is wrong before bouncing', () => {
    const form = defaultBounceForm('Mix', DefaultSettings, [], bars);
    expect(bounceProblems(form)).toEqual([]);
    expect(bounceProblems({ ...form, fileName: ' ' })).toEqual(['Name the file.']);
    expect(bounceProblems({ ...form, fileName: 'a/b' })).toHaveLength(1);
    expect(bounceProblems({ ...form, endBar: 1 })).toEqual(['The end must come after the start.']);
    expect(bounceProblems({ ...form, source: 'stems', trackIds: [] })).toEqual(['Pick the tracks to bounce as stems.']);
    expect(bounceProblems({ ...form, normalize: 'loudness', loudnessLufs: 3 })).toHaveLength(1);
    expect(bounceProblems({ ...form, normalize: 'peak', peakCeilingDb: -30 })).toHaveLength(1);
  });

  it('previews the files it writes', () => {
    const form = defaultBounceForm('Mix', DefaultSettings, [], bars);
    expect(bounceFileNames(form, [])).toEqual(['Mix.wav']);
    const tracks = [
      { id: 'a', name: 'Bass' },
      { id: 'b', name: 'Keys' }
    ] as never[];
    expect(bounceFileNames({ ...form, source: 'stems', format: 'aiff', trackIds: ['b'] }, tracks)).toEqual([
      'Mix - Keys.aif'
    ]);
  });
});
