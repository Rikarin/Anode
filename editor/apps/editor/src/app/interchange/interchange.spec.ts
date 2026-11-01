import {
  aafRequest,
  exportFileName,
  exportProblems,
  midiImportRequest,
  midiRequest,
  musicXmlRequest,
  type ExportForm
} from './interchange';

const Form: ExportForm = {
  fileName: 'Song',
  directory: '',
  source: { kind: 'all' },
  compressed: false,
  embedMedia: true,
  trackIds: ['a', 'b'],
  format: 1,
  regionId: null
};

describe('interchange dialogs', () => {
  it('names the files the exports write', () => {
    expect(exportFileName('musicxml', Form)).toBe('Song.musicxml');
    expect(exportFileName('musicxml', { ...Form, compressed: true })).toBe('Song.mxl');
    expect(exportFileName('aaf', { ...Form, fileName: 'Mix.aaf' })).toBe('Mix.aaf');
    expect(exportFileName('midi', Form)).toBe('Song.mid');
  });

  it('says what keeps an export from starting', () => {
    expect(exportProblems('musicxml', Form)).toEqual([]);
    expect(exportProblems('musicxml', { ...Form, fileName: ' ' })).toEqual(['Give the file a name.']);
    expect(exportProblems('aaf', { ...Form, fileName: 'a/b' })[0]).toMatch(/folders/);
    expect(exportProblems('aaf', { ...Form, trackIds: [] })).toEqual(['Choose at least one audio track.']);
    expect(exportProblems('midi', { ...Form, trackIds: [], regionId: 'mid_1' })).toEqual([]);
  });

  it('builds the requests (all tracks of a kind as null; nullable properties sent as null)', () => {
    expect(musicXmlRequest({ ...Form, source: { kind: 'set', setId: 'scs_1' }, directory: ' /out ' })).toEqual({
      fileName: 'Song',
      directory: '/out',
      setId: 'scs_1',
      partTrackId: null,
      compressed: false
    });
    expect(musicXmlRequest({ ...Form, source: { kind: 'part', trackId: 't' } })).toMatchObject({
      partTrackId: 't',
      setId: null
    });
    expect(aafRequest(Form, ['a', 'b'])).toEqual({
      fileName: 'Song',
      directory: null,
      trackIds: null,
      embedMedia: true
    });
    expect(aafRequest({ ...Form, trackIds: ['b'], embedMedia: false }, ['a', 'b'])).toMatchObject({
      trackIds: ['b'],
      embedMedia: false
    });
    expect(midiRequest({ ...Form, format: 0 }, ['a', 'b', 'c'])).toEqual({
      fileName: 'Song',
      directory: null,
      format: 0,
      trackIds: ['a', 'b'],
      regionId: null
    });
    expect(midiRequest({ ...Form, regionId: 'mid_1' }, ['a'])).toMatchObject({ trackIds: null, regionId: 'mid_1' });
    expect(
      midiImportRequest({ path: '/x.mid', file: null, importTempo: false, importMarkers: true, merge: true })
    ).toEqual({
      path: '/x.mid',
      importTempo: false,
      importMarkers: true,
      merge: true
    });
  });
});
