import { ApiError, type EditCommand } from '@anode/api-client';
import { FakeAnodeServer } from './fake-server';

async function status(server: FakeAnodeServer, command: EditCommand): Promise<number> {
  try {
    await server.applyCommands({ commands: [command], label: 'Test' });
    return 200;
  } catch (error) {
    return error instanceof ApiError ? error.status : 500;
  }
}

// The Phase 6 contracts (docs/07-phase-6.md) before the editor workstream implements them: the fake Server refuses the
// new commands and MIDI FX slots, leaves every new field at its default, and keeps applying the insert commands.
describe('FakeAnodeServer, Phase 6 placeholders', () => {
  it('starts projects and tracks with the Phase 6 defaults', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Beats' });
    await server.applyCommands({ commands: [{ type: 'addTrack', kind: 'instrument', stereo: false }] });
    const project = server.current;
    expect(project?.patternRegions).toEqual([]);
    expect(project?.liveLoops).toEqual({ scenes: [], cells: [], quantize: 'bar', shown: false });
    expect(project?.tracks[0]).toMatchObject({ drumMachine: null, smartControls: null, liveLoopsActive: false });
    expect(project?.tracks[0]?.strip.midiEffects).toEqual([]);
  });

  it('answers the new commands 422', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Beats' });
    await server.applyCommands({ commands: [{ type: 'addTrack', kind: 'instrument', stereo: false }] });
    const trackId = server.current?.tracks[0]?.id ?? '';
    const commands: EditCommand[] = [
      { type: 'addPatternRegion', trackId, startTick: 0, pattern: null },
      { type: 'createDrumMachine' },
      { type: 'setSmartControl', trackId, controlId: 'knob1', value: 0.5 },
      { type: 'addScene' },
      { type: 'setTrackActivation', trackIds: [trackId], liveLoopsActive: true },
      { type: 'addInsert', pluginId: 'anode.arpeggiator', trackId, chain: 'midiEffects' }
    ];
    for (const command of commands) {
      expect(await status(server, command)).toBe(422);
    }
    expect(await status(server, { type: 'addInsert', pluginId: 'anode.channel-eq', trackId, chain: 'inserts' })).toBe(
      200
    );
  });
});
