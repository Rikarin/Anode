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

describe('FakeAnodeServer, stretchRegion', () => {
  it('stretches a region without touching its frames, scaling its flex markers', async () => {
    const server = new FakeAnodeServer();
    const { file, trackId } = await audioProject(server);
    let p = await run(
      server,
      { type: 'addRegion', trackId, fileId: file.id, startTick: 0, length: 48_000 },
      { type: 'setTrackFlex', trackId, on: true }
    );
    const regionId = p.regions[0]?.id ?? '';
    await run(server, { type: 'setFlexMarkers', regionId, markers: [{ fileFrame: 24_000, tick: 960 }] });
    p = await run(server, { type: 'stretchRegion', regionId, stretch: 2, startTick: 480 });
    expect(p.regions[0]).toMatchObject({ startTick: 480, stretch: 2, length: 48_000, fileOffset: 0 });
    expect(p.regions[0]?.flexMarkers).toEqual([{ fileFrame: 24_000, tick: 1920 }]);
  });

  it('keeps the stretch in its range', async () => {
    const server = new FakeAnodeServer();
    const { file, trackId } = await audioProject(server);
    const p = await run(server, { type: 'addRegion', trackId, fileId: file.id, startTick: 0, length: 48_000 });
    await expect(
      run(server, { type: 'stretchRegion', regionId: p.regions[0]?.id ?? '', stretch: 5 })
    ).rejects.toMatchObject({ status: 422 });
  });
});

describe('FakeAnodeServer, editing time', () => {
  it('inserts time on every track, with markers, the cycle and the tempo track', async () => {
    const server = new FakeAnodeServer();
    const { file, trackId } = await audioProject(server);
    await run(
      server,
      { type: 'addRegion', trackId, fileId: file.id, startTick: 0, length: 192_000 },
      { type: 'addMarker', name: 'Later', tick: Bar * 2, lengthTicks: 0 },
      { type: 'setCycle', enabled: true, startTick: Bar * 2, endTick: Bar * 4 },
      {
        type: 'setTempoEvents',
        events: [
          { tick: 0, bpm: 120, curve: 'step', shape: 0 },
          { tick: Bar * 3, bpm: 90, curve: 'step', shape: 0 }
        ]
      }
    );
    const p = await run(server, { type: 'insertTime', atTick: Bar, lengthTicks: Bar });
    const regions = [...p.regions].sort((a, b) => a.startTick - b.startTick);
    expect(regions.map(r => [r.startTick, r.fileOffset, r.length])).toEqual([
      [0, 0, 96_000],
      [Bar * 2, 96_000, 96_000]
    ]);
    expect(p.markers[0]?.tick).toBe(Bar * 3);
    expect(p.settings.cycle).toMatchObject({ startTick: Bar * 3, endTick: Bar * 5 });
    expect(p.settings.tempoEvents.map(e => e.tick)).toEqual([0, Bar * 4]);
  });

  it('cuts time, keeping what crosses it and what was in force at its end', async () => {
    const server = new FakeAnodeServer();
    const { file, trackId } = await audioProject(server);
    await run(
      server,
      { type: 'addRegion', trackId, fileId: file.id, startTick: 0, length: 192_000 },
      { type: 'addMarker', name: 'Gone', tick: Bar, lengthTicks: 0 },
      {
        type: 'setTempoEvents',
        events: [
          { tick: 0, bpm: 120, curve: 'step', shape: 0 },
          { tick: Bar, bpm: 90, curve: 'step', shape: 0 }
        ]
      }
    );
    const p = await run(server, { type: 'cutTime', startTick: Bar / 2, endTick: Bar + Bar / 2 });
    expect(p.regions).toHaveLength(2);
    expect(p.markers).toEqual([]);
    expect(p.settings.tempoEvents.map(e => [e.tick, e.bpm])).toEqual([
      [0, 120],
      [Bar / 2, 90]
    ]);
  });

  it('ripple deletes, leaving what crosses on other tracks', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Test' });
    await server.applyCommands({
      commands: [
        { type: 'addTrack', stereo: false },
        { type: 'addTrack', stereo: false }
      ]
    });
    const file = await server.importAudioFile('/audio/take.wav');
    const [a = '', b = ''] = (server.current?.tracks ?? []).map(t => t.id);
    let p = await run(
      server,
      { type: 'addRegion', trackId: a, fileId: file.id, startTick: Bar, length: 96_000 },
      { type: 'addRegion', trackId: a, fileId: file.id, startTick: Bar * 3, length: 96_000 },
      { type: 'addRegion', trackId: b, fileId: file.id, startTick: 0, length: 192_000 }
    );
    const gone = p.regions.find(r => r.startTick === Bar)?.id ?? '';
    p = await run(server, { type: 'rippleDelete', regionIds: [gone] });
    expect(p.regions.map(r => [r.trackId, r.startTick]).sort()).toEqual([
      [a, Bar * 2],
      [b, 0]
    ]);
  });
});

describe('FakeAnodeServer, count-in and pre-roll', () => {
  it('keeps the count-in length and the pre-roll, in their ranges', async () => {
    const server = new FakeAnodeServer();
    await audioProject(server);
    const p = await run(
      server,
      { type: 'setMetronome', countIn: true, countInLength: 2, countInUnit: 'beats' },
      { type: 'setRecording', preRollLength: 1, preRollUnit: 'bars' }
    );
    expect(p.settings.metronome).toMatchObject({ countIn: true, countInLength: 2, countInUnit: 'beats' });
    expect(p.settings.recording).toMatchObject({ preRollLength: 1, preRollUnit: 'bars' });
    await expect(run(server, { type: 'setMetronome', countInLength: 0 })).rejects.toMatchObject({ status: 422 });
    await expect(run(server, { type: 'setRecording', preRollLength: 33 })).rejects.toMatchObject({ status: 422 });
  });

  it('records from a stop after its pre-roll, the take at the playhead', async () => {
    const server = new FakeAnodeServer();
    const { trackId } = await audioProject(server);
    await run(
      server,
      { type: 'setTrack', trackId, input: null, recordArmed: true },
      { type: 'setRecording', preRollLength: 1, preRollUnit: 'bars' }
    );
    const status = await server.controlTransport({ command: 'record', tick: Bar * 2 });
    expect(status.positionTick).toBe(Bar);
    await server.controlTransport({ command: 'stop' });
  });

  it('has no pre-roll to take at the project start', async () => {
    const server = new FakeAnodeServer();
    const { trackId } = await audioProject(server);
    await run(
      server,
      { type: 'setTrack', trackId, input: null, recordArmed: true },
      { type: 'setRecording', preRollLength: 2, preRollUnit: 'bars' }
    );
    expect((await server.controlTransport({ command: 'record', tick: 960 })).positionTick).toBe(0);
    await server.controlTransport({ command: 'stop' });
  });
});
