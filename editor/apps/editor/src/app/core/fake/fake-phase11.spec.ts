import {
  ApiError,
  ChordQualities,
  DefaultMasteringSettings,
  DrumKitNotes,
  type EditCommand,
  EmptyChordTrack,
  EmptyEnvironment,
  EnvironmentObjectKinds,
  MasteringBands,
  pitchClass,
  rootInterval,
  sectionKind
} from '@anode/api-client';
import { FakeAnodeServer } from './fake-server';

async function status(run: () => Promise<unknown>): Promise<number> {
  try {
    await run();
    return 200;
  } catch (error) {
    return error instanceof ApiError ? error.status : 500;
  }
}

// The Phase 11 contracts (docs/12-phase-11.md) before the editor workstream implements them: the fake Server refuses the
// new commands, endpoints and track kinds, and starts projects with an empty chord track and Environment.
describe('FakeAnodeServer, Phase 11 placeholders', () => {
  it('starts projects without chords, Session Player regions or an Environment', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Song' });
    expect(server.current?.chordTrack).toEqual(EmptyChordTrack);
    expect(server.current?.sessionPlayerRegions).toEqual([]);
    expect(server.current?.environment).toEqual(EmptyEnvironment);
    await server.applyCommands({ commands: [{ type: 'addTrack', kind: 'instrument', stereo: true }] });
    const trackId = server.current?.tracks[0]?.id ?? '';
    await server.applyCommands({ commands: [{ type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: 3840 }] });
    const region = server.current?.midiRegions[0];
    expect([region?.pitchSource, region?.key]).toEqual(['off', null]);
  });

  it('answers the new commands, track kinds and endpoints 422', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Song' });
    const c = { step: 'c', alter: 0 } as const;
    const commands: EditCommand[] = [
      { type: 'addTrack', kind: 'drummer', stereo: true },
      { type: 'addChords', chords: [{ tick: 0, root: c, quality: 'm7', bass: null }] },
      { type: 'editChords', chords: [{ id: 'chd_1', tick: 0, root: c, quality: '', bass: null }] },
      { type: 'deleteChords', chordIds: ['chd_1'] },
      { type: 'setScales', scales: [{ tick: 0, tonic: c, scale: 'dorian' }] },
      {
        type: 'applyChordId',
        result: { startTick: 0, endTick: 3840, write: 'replace', chords: [], keys: [] }
      },
      { type: 'setPitchSource', regionIds: ['mid_1'], mode: 'root' },
      { type: 'setRegionKey', regionIds: ['mid_1'], key: { tonic: c, mode: 'major' } },
      { type: 'addSessionPlayerTrack', player: 'drummer', regions: true },
      { type: 'addSessionPlayerRegion', trackId: 'trk_1', startTick: 0, parameters: null },
      { type: 'setSessionPlayerRegion', regionId: 'spr_1', complexity: 0.5, drums: null, bass: null, keys: null },
      { type: 'regenerateSessionPlayer', regionIds: ['spr_1'] },
      { type: 'convertSessionPlayerRegions', regions: [] },
      { type: 'setMastering', character: 'valve' },
      {
        type: 'setMasteringAnalysis',
        insertId: 'ins_1',
        analysis: {
          startTick: 0,
          endTick: 3840,
          revision: 1,
          integratedLufs: -18,
          maxShortTermLufs: -15,
          loudnessRange: 5,
          truePeakDbtp: -2,
          crestFactorDb: 12,
          width: 0.4,
          spectrumDb: MasteringBands.map(() => 0),
          curveDb: MasteringBands.map(() => 0)
        }
      },
      { type: 'addEnvironmentLayer' },
      { type: 'setEnvironmentLayer', layerId: 'env_1', name: 'Input' },
      { type: 'removeEnvironmentLayer', layerId: 'env_1' },
      { type: 'addEnvironmentObject', kind: 'monitor', x: 0, y: 0 },
      { type: 'setEnvironmentObject', objectId: 'eob_1', x: 40 },
      { type: 'removeEnvironmentObjects', objectIds: ['eob_1'] },
      { type: 'connectEnvironment', fromObjectId: 'eob_1', outlet: 0, toObjectId: 'eob_2', inlet: 0 },
      { type: 'disconnectEnvironment', cableIds: ['ecb_1'] }
    ];
    for (const command of commands) {
      expect(await status(() => server.applyCommands({ commands: [command] }))).toBe(422);
    }
    const calls: (() => Promise<unknown>)[] = [
      () => server.chordTrack(),
      () => server.detectChords({ regionIds: ['mid_1'], write: 'replace', detectKeys: true }),
      () => server.sessionPlayerCatalog(),
      () => server.sessionPlayerNotes('spr_1'),
      () => server.regenerateSessionPlayer('spr_1', {}),
      () => server.convertSessionPlayer('spr_1', { target: 'midi' }),
      () => server.mastering(),
      () => server.setMastering(DefaultMasteringSettings),
      () => server.analyseMastering({ add: true }),
      () => server.environment(),
      () => server.environmentInput('eob_1', { events: [{ status: 0x90, data1: 60, data2: 100 }] })
    ];
    for (const call of calls) {
      expect(await status(call)).toBe(422);
    }
  });

  it('mirrors the contract arithmetic and data', () => {
    expect(rootInterval(0, 7)).toBe(-5);
    expect(rootInterval(0, 5)).toBe(5);
    expect(rootInterval(0, 6)).toBe(-6);
    expect(pitchClass({ step: 'c', alter: -1 })).toBe(11);
    expect(pitchClass({ step: 'b', alter: 1 })).toBe(0);
    expect(sectionKind('Chorus 2')).toBe('chorus');
    expect(sectionKind('Solo')).toBe('verse');
    expect(ChordQualities.map(q => q.quality)).toContain('m7b5');
    expect(MasteringBands).toHaveLength(30);
    expect(EnvironmentObjectKinds.channelSplitter.outlets).toHaveLength(17);
    expect(DrumKitNotes.kick[0]).toBe(36);
  });
});
