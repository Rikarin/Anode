import { ApiError, type EditCommand } from '@anode/api-client';
import { FakeAnodeServer } from './fake-server';

async function status(run: () => Promise<unknown>): Promise<number> {
  try {
    await run();
    return 200;
  } catch (error) {
    return error instanceof ApiError ? error.status : 500;
  }
}

// The Phase 8 contracts (docs/09-phase-8.md) before the editor workstream implements them: the fake Server refuses the
// new commands and endpoints, and starts projects with the default sync settings.
describe('FakeAnodeServer, Phase 8 placeholders', () => {
  it('starts projects with internal sync, sending nothing', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Sync' });
    expect(server.current?.settings.sync).toEqual({
      source: 'internal',
      destinations: [],
      frameRate: 'fps25',
      startOffsetSeconds: 0,
      linkStartStop: false
    });
  });

  it('answers the new commands and endpoints 422', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Control' });
    await server.applyCommands({ commands: [{ type: 'addTrack', stereo: true }] });
    const trackId = server.current?.tracks[0]?.id ?? '';
    const commands: EditCommand[] = [
      { type: 'setSyncSettings', source: 'midiClock' },
      { type: 'addStems', regionId: 'reg_1', stems: [{ kind: 'vocals', fileId: 'fil_1' }], keepSource: false }
    ];
    for (const command of commands) {
      expect(await status(() => server.applyCommands({ commands: [command] }))).toBe(422);
    }
    const calls: (() => Promise<unknown>)[] = [
      () => server.controllerAssignments(),
      () => server.setControllerAssignments({ zones: [] }),
      () => server.learn(),
      () => server.startLearn({ target: null }),
      () => server.stopLearn(),
      () => server.surfaces(),
      () => server.surfaceStatus(),
      () => server.addSurface({ kind: 'mackieControl', inputPort: 'anode:in', outputPort: 'anode:out' }),
      () => server.removeSurface('srf_1'),
      () => server.reorderSurfaces([]),
      () => server.osc(),
      () => server.setOsc({ enabled: true, port: 8000, clients: [] }),
      () => server.setSelection(trackId),
      () => server.syncState(),
      () => server.splitStems('reg_1', { keepSource: false })
    ];
    for (const call of calls) {
      expect(await status(call)).toBe(422);
    }
  });
});
