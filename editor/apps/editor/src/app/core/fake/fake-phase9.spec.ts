import { ApiError, DefaultRegionNotation, type EditCommand, EmptyProjectScore } from '@anode/api-client';
import { FakeAnodeServer } from './fake-server';

async function status(run: () => Promise<unknown>): Promise<number> {
  try {
    await run();
    return 200;
  } catch (error) {
    return error instanceof ApiError ? error.status : 500;
  }
}

// The Phase 9 contracts (docs/10-phase-9.md) before the editor workstream implements them: the fake Server refuses the
// new commands and endpoints, starts projects with an empty score, and gives new MIDI regions the default notation.
describe('FakeAnodeServer, Phase 9 placeholders', () => {
  it('starts projects with an empty score and regions with the default notation', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Score' });
    expect(server.current?.score).toEqual(EmptyProjectScore);
    await server.applyCommands({ commands: [{ type: 'addTrack', kind: 'instrument', stereo: false }] });
    const trackId = server.current?.tracks[0]?.id ?? '';
    await server.applyCommands({ commands: [{ type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: 3840 }] });
    expect(server.current?.midiRegions[0]?.notation).toEqual(DefaultRegionNotation);
    expect(server.current?.tracks[0]?.staffStyleId ?? null).toBeNull();
  });

  it('answers the new commands and endpoints 422', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Score' });
    await server.applyCommands({ commands: [{ type: 'addTrack', kind: 'instrument', stereo: false }] });
    const trackId = server.current?.tracks[0]?.id ?? '';
    const commands: EditCommand[] = [
      {
        type: 'setStaffStyle',
        style: {
          id: 'sts_1',
          name: 'Mine',
          staves: [{ clef: 'treble', transpose: 0, voices: 1 }],
          displayQuantizeTicks: 240,
          tripletQuantizeTicks: 0,
          brace: false
        }
      },
      { type: 'deleteStaffStyle', styleId: 'sts_1' },
      { type: 'assignStaffStyle', trackIds: [trackId], styleId: 'factory.piano' },
      { type: 'setRegionNotation', regionIds: ['mid_1'], maxDots: 2 },
      { type: 'addScoreSymbols', symbols: [{ kind: 'dynamic', trackId, tick: 0, text: 'mf' }] },
      { type: 'editScoreSymbols', symbols: [{ id: 'sym_1', kind: 'staccato', trackId, noteId: 1 }] },
      { type: 'deleteScoreSymbols', symbolIds: ['sym_1'] },
      { type: 'setLyrics', verse: 1, syllables: [{ noteId: 1, text: 'la', syllabic: 'single', extender: false }] },
      {
        type: 'addChordSymbols',
        chords: [{ trackId, tick: 0, root: { step: 'c', alter: 0 }, quality: '', bass: null, showGrid: false }]
      },
      {
        type: 'editChordSymbols',
        chords: [
          { id: 'chd_1', trackId, tick: 0, root: { step: 'a', alter: 0 }, quality: 'm7', bass: null, showGrid: false }
        ]
      },
      { type: 'deleteChordSymbols', chordIds: ['chd_1'] },
      { type: 'setScoreSet', set: { id: 'scs_1', name: 'All', trackIds: [trackId] } },
      { type: 'deleteScoreSet', setId: 'scs_1' },
      { type: 'setScoreLayout', layout: { ...EmptyProjectScore.layout, title: 'Song' } },
      { type: 'moveScoreNotes', regionId: 'mid_1', noteIds: [1], steps: 1, ticks: 0 },
      { type: 'addScoreNote', trackId, tick: 960, lengthTicks: 960, staffPosition: 4, staff: 0 },
      { type: 'importTracks', content: { tracks: [], score: null } }
    ];
    for (const command of commands) {
      expect(await status(() => server.applyCommands({ commands: [command] }))).toBe(422);
    }
    const file = new File(['x'], 'x.mid');
    const calls: (() => Promise<unknown>)[] = [
      () => server.score({ view: 'page' }),
      () => server.staffStyles(),
      () => server.exportMusicXml({ fileName: 'Song', compressed: false }),
      () => server.exportAaf({ fileName: 'Song', embedMedia: true }),
      () => server.exportMidi({ fileName: 'Song', format: 1 }),
      () => server.importMusicXml({ path: '/x.musicxml' }),
      () => server.uploadMusicXml(file),
      () => server.importAaf({ path: '/x.aaf' }),
      () => server.uploadAaf(file),
      () => server.importMidi({ path: '/x.mid', importTempo: true, importMarkers: true, merge: false }),
      () => server.uploadMidi(file, { merge: true })
    ];
    for (const call of calls) {
      expect(await status(call)).toBe(422);
    }
  });
});
