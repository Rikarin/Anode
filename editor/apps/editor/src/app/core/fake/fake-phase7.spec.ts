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

// The Phase 7 contracts (docs/08-phase-7.md) before the editor workstream implements them: the fake Server refuses the
// new commands and the groove and articulation fields of existing ones, leaves every new field at its default, and
// keeps applying the existing commands without them.
describe('FakeAnodeServer, Phase 7 placeholders', () => {
  async function withRegion(): Promise<{ server: FakeAnodeServer; trackId: string; regionId: string }> {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Edits' });
    await server.applyCommands({ commands: [{ type: 'addTrack', kind: 'instrument', stereo: false }] });
    const trackId = server.current?.tracks[0]?.id ?? '';
    await server.applyCommands({
      commands: [{ type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: 3840 }]
    });
    const regionId = server.current?.midiRegions[0]?.id ?? '';
    return { server, trackId, regionId };
  }

  it('starts projects, tracks and notes with the Phase 7 defaults', async () => {
    const { server, regionId } = await withRegion();
    await server.applyCommands({
      commands: [
        {
          type: 'addNotes',
          regionId,
          notes: [{ pitch: 60, velocity: 100, channel: 1, startTick: 0, lengthTicks: 480 }]
        }
      ]
    });
    const project = server.current;
    expect(project).toMatchObject({ grooves: [], articulationSets: [], laneSets: [] });
    expect(project?.settings.grooveTrackId ?? null).toBeNull();
    expect(project?.tracks[0]).toMatchObject({ matchGroove: false });
    expect(project?.tracks[0]?.articulationSetId ?? null).toBeNull();
    expect(project?.midiRegions[0]?.notes[0]?.articulationId).toBe(0);
  });

  it('answers the new commands, and grooves and articulations on existing ones, 422', async () => {
    const { server, trackId, regionId } = await withRegion();
    const commands: EditCommand[] = [
      { type: 'setControls', regionId, changes: [{ id: 1, value: 64 }] },
      { type: 'makeGroove', regionId },
      { type: 'setGrooveTrack', trackId },
      { type: 'setMatchGroove', trackId, matchGroove: true },
      { type: 'assignArticulationSet', trackId, setId: null },
      { type: 'stepNotes', pitches: [60] },
      { type: 'setScript', trackId, slotId: 'ins_1', source: 'function HandleMIDI(e) { e.send(); }' },
      { type: 'quantizeNotes', regionId, gridTicks: 240, strength: 1, swing: 0, grooveId: 'grv_1' },
      { type: 'setMidiRegion', regionId, grooveId: 'grv_1' },
      {
        type: 'addNotes',
        regionId,
        notes: [{ pitch: 60, velocity: 100, channel: 1, startTick: 0, lengthTicks: 480, articulationId: 2 }]
      }
    ];
    for (const command of commands) {
      expect(await status(server, command)).toBe(422);
    }
    expect(await status(server, { type: 'quantizeNotes', regionId, gridTicks: 240, strength: 1, swing: 0 })).toBe(200);
  });
});
