import type { EditCommand, Project } from '@anode/api-client';
import { FakeAnodeServer } from './fake-server';

const Bar = 3840;

async function audioProject(server: FakeAnodeServer) {
  await server.createProject({ name: 'Test' });
  await server.applyCommands({ commands: [{ type: 'addTrack', stereo: false }] });
  const file = await server.importAudioFile('/audio/take.wav');
  return { file, trackId: server.current?.tracks[0]?.id ?? '' };
}

async function run(server: FakeAnodeServer, ...commands: EditCommand[]): Promise<Project> {
  await server.applyCommands({ commands, label: 'Test' });
  return server.current as Project;
}

async function instrumentProject(server: FakeAnodeServer) {
  await server.createProject({ name: 'Test' });
  await server.applyCommands({ commands: [{ type: 'addTrack', stereo: true, kind: 'instrument' }] });
  return server.current?.tracks[0]?.id ?? '';
}

describe('FakeAnodeServer, region loops', () => {
  it('keeps each kind of region’s loop and refuses a negative one', async () => {
    const server = new FakeAnodeServer();
    const trackId = await instrumentProject(server);
    let p = await run(
      server,
      { type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: Bar },
      { type: 'addPatternRegion', trackId, startTick: Bar * 4, lengthTicks: Bar, pattern: null }
    );
    const midi = p.midiRegions[0]?.id ?? '';
    const pattern = p.patternRegions[0]?.id ?? '';
    p = await run(
      server,
      { type: 'setMidiRegion', regionId: midi, loopTicks: Bar },
      { type: 'setPatternRegion', regionId: pattern, loopTicks: Bar * 2 }
    );
    expect(p.midiRegions[0]?.loopTicks).toBe(Bar);
    expect(p.patternRegions[0]?.loopTicks).toBe(Bar * 2);
    await expect(run(server, { type: 'setMidiRegion', regionId: midi, loopTicks: -1 })).rejects.toMatchObject({
      status: 422
    });
  });

  it('converts repeats to regions, stopping at the next region, in one undo step', async () => {
    const server = new FakeAnodeServer();
    const trackId = await instrumentProject(server);
    let p = await run(
      server,
      { type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: Bar },
      { type: 'addMidiRegion', trackId, startTick: Bar * 2 + Bar / 2, lengthTicks: Bar }
    );
    const first = p.midiRegions[0];
    await run(
      server,
      {
        type: 'addNotes',
        regionId: first?.id ?? '',
        notes: [{ pitch: 60, velocity: 100, channel: 1, startTick: 0, lengthTicks: 60 }]
      },
      { type: 'setMidiRegion', regionId: first?.id ?? '', loopTicks: Bar * 8 }
    );
    p = await run(server, { type: 'convertLoops', regionIds: [first?.id ?? ''] });
    const spans = [...p.midiRegions].sort((a, b) => a.startTick - b.startTick);
    expect(spans.map(r => [r.startTick, r.lengthTicks, r.loopTicks])).toEqual([
      [0, Bar, 0],
      [Bar, Bar, 0],
      [Bar * 2, Bar / 2, 0],
      [Bar * 2 + Bar / 2, Bar, 0]
    ]);
    const ids = spans.flatMap(r => r.notes.map(n => n.id));
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('refuses to split or join a looped region', async () => {
    const server = new FakeAnodeServer();
    const { file, trackId } = await audioProject(server);
    const p = await run(
      server,
      { type: 'addRegion', trackId, fileId: file.id, startTick: 0, length: 48_000 },
      { type: 'addRegion', trackId, fileId: file.id, startTick: Bar * 4, fileOffset: 48_000, length: 48_000 }
    );
    const [a = '', b = ''] = p.regions.map(r => r.id);
    await run(server, { type: 'setRegion', regionId: a, loopTicks: 960 });
    await expect(run(server, { type: 'splitRegion', regionId: a, atTick: 960 })).rejects.toMatchObject({ status: 422 });
    await expect(run(server, { type: 'joinRegions', regionIds: [a, b] })).rejects.toMatchObject({ status: 422 });
  });
});
