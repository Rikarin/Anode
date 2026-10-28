import type { EditCommand, Project } from '@anode/api-client';
import { FakeHome } from './fake-files';
import { FakeAnodeServer } from './fake-server';

const Bar = 3840;

async function audioProject(server: FakeAnodeServer, tracks = 1) {
  await server.createProject({ name: 'Test' });
  for (let i = 0; i < tracks; i++) {
    await server.applyCommands({ commands: [{ type: 'addTrack', stereo: false }] });
  }
  const file = await server.importAudioFile('/audio/take.wav');
  const trackIds = (server.current?.tracks ?? []).map(t => t.id);
  return { file, trackIds, trackId: trackIds[0] ?? '' };
}

async function run(server: FakeAnodeServer, ...commands: EditCommand[]): Promise<Project> {
  await server.applyCommands({ commands, label: 'Test' });
  return server.current as Project;
}

describe('FakeAnodeServer, Phase 3 commands', () => {
  it('keeps the tempo and signature tracks and their first events in step', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Test' });
    let p = await run(server, {
      type: 'setTempoEvents',
      events: [
        { tick: 0, bpm: 100, curve: 'ramp', shape: 0 },
        { tick: 2 * Bar, bpm: 140, curve: 'step', shape: 0 }
      ]
    });
    expect(p.settings.tempo).toBe(100);
    p = await run(server, { type: 'setTempo', bpm: 90 });
    expect(p.settings.tempoEvents.map(e => e.bpm)).toEqual([90, 140]);
    p = await run(server, {
      type: 'setSignatures',
      events: [
        { tick: 0, numerator: 4, denominator: 4 },
        { tick: Bar, numerator: 3, denominator: 4 }
      ]
    });
    expect(p.settings.signatures).toHaveLength(2);
    await expect(
      server.applyCommands({
        commands: [
          {
            type: 'setSignatures',
            events: [
              { tick: 0, numerator: 4, denominator: 4 },
              { tick: 1000, numerator: 3, denominator: 4 }
            ]
          }
        ]
      })
    ).rejects.toThrow('bar line');
    await expect(
      server.applyCommands({
        commands: [{ type: 'setTempoEvents', events: [{ tick: 5, bpm: 100, curve: 'step', shape: 0 }] }]
      })
    ).rejects.toThrow('tick 0');
  });

  it('adds, edits and deletes markers, and sends their deltas', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Test' });
    const result = await server.applyCommands({
      commands: [{ type: 'addMarker', name: 'Verse', tick: Bar, lengthTicks: 0 }]
    });
    expect(result.delta.ops.map(op => op.op)).toEqual(['markerUpserted']);
    const id = server.current?.markers[0]?.id ?? '';
    const p = await run(server, { type: 'setMarker', markerId: id, text: 'Big', tick: 2 * Bar });
    expect(p.markers[0]).toMatchObject({ name: 'Verse', text: 'Big', tick: 2 * Bar });
    const removed = await server.applyCommands({ commands: [{ type: 'deleteMarkers', markerIds: [id] }] });
    expect(removed.delta.ops).toEqual([{ op: 'markerRemoved', id }]);
  });

  it('moves, copies and deletes arrangement sections with their content', async () => {
    const server = new FakeAnodeServer();
    const { file, trackId } = await audioProject(server);
    let p = await run(
      server,
      { type: 'addSection', name: 'A', startTick: 0, lengthTicks: 2 * Bar },
      { type: 'addSection', name: 'B', startTick: 2 * Bar, lengthTicks: Bar },
      { type: 'addRegion', trackId, fileId: file.id, startTick: 0, length: 1000 },
      { type: 'addRegion', trackId, fileId: file.id, startTick: 2 * Bar, length: 1000 },
      { type: 'addMarker', name: 'in B', tick: 2 * Bar + 10, lengthTicks: 0 }
    );
    const [a, b] = p.arrangement;
    await expect(
      server.applyCommands({ commands: [{ type: 'addSection', name: 'X', startTick: Bar, lengthTicks: Bar }] })
    ).rejects.toThrow('overlap');

    // Move B before A: B's region and marker go to 0, A's content after it.
    p = await run(server, { type: 'moveSection', sectionId: b?.id ?? '', toTick: 0, copy: false });
    expect(p.arrangement.map(s => [s.name, s.startTick])).toEqual([
      ['B', 0],
      ['A', Bar]
    ]);
    expect(p.regions.map(r => r.startTick).sort((x, y) => x - y)).toEqual([0, Bar]);
    expect(p.markers[0]?.tick).toBe(10);

    // Copy A to the end.
    p = await run(server, { type: 'moveSection', sectionId: a?.id ?? '', toTick: 3 * Bar, copy: true });
    expect(p.arrangement.map(s => s.name)).toEqual(['B', 'A', 'A']);
    expect(p.regions).toHaveLength(3);

    // Delete B with its content: the rest closes up.
    p = await run(server, { type: 'deleteSections', sectionIds: [p.arrangement[0]?.id ?? ''], removeContent: true });
    expect(p.arrangement.map(s => s.startTick)).toEqual([0, 2 * Bar]);
    expect(p.regions.map(r => r.startTick).sort((x, y) => x - y)).toEqual([0, 2 * Bar]);
    expect(p.markers).toHaveLength(0);
    await expect(
      server.applyCommands({ commands: [{ type: 'moveSection', sectionId: a?.id ?? '', toTick: 5, copy: false }] })
    ).rejects.toThrow('boundary');
  });

  it('packs regions into a take folder, comps, selects, deletes takes and flattens', async () => {
    const server = new FakeAnodeServer();
    const { file, trackId } = await audioProject(server);
    let p = await run(
      server,
      { type: 'addRegion', trackId, fileId: file.id, startTick: 0, length: 192_000 },
      { type: 'addRegion', trackId, fileId: file.id, startTick: 0, length: 192_000 }
    );
    p = await run(server, { type: 'packTakeFolder', regionIds: p.regions.map(r => r.id) });
    expect(p.regions).toHaveLength(0);
    const folder = p.takeFolders[0];
    expect(folder?.takes).toHaveLength(2);
    expect(folder?.comp).toEqual([{ startTick: 0, endTick: 2 * Bar, takeId: folder?.takes[1]?.id }]);
    const [t1, t2] = folder?.takes ?? [];

    p = await run(server, {
      type: 'setComp',
      folderId: folder?.id ?? '',
      segments: [
        { startTick: 0, endTick: Bar, takeId: t1?.id ?? '' },
        { startTick: Bar, endTick: 2 * Bar, takeId: t2?.id ?? '' }
      ]
    });
    expect(p.takeFolders[0]?.comp).toHaveLength(2);
    await expect(
      server.applyCommands({
        commands: [
          { type: 'setComp', folderId: folder?.id ?? '', segments: [{ startTick: 0, endTick: 9 * Bar, takeId: 'x' }] }
        ]
      })
    ).rejects.toMatchObject({ status: 422 });

    p = await run(server, { type: 'flattenTakeFolder', folderId: folder?.id ?? '' });
    expect(p.takeFolders).toHaveLength(0);
    expect(p.regions).toHaveLength(2);
    const [left, right] = [...p.regions].sort((a, b) => a.startTick - b.startTick);
    expect(left?.fadeOut).toBeGreaterThan(0);
    expect(right?.fadeIn).toBeGreaterThan(0);
    expect(right?.startTick).toBeLessThan(Bar); // crossfaded over the boundary

    await server.undo();
    p = await run(server, { type: 'selectTake', folderId: folder?.id ?? '', takeId: t1?.id ?? '' });
    expect(p.takeFolders[0]?.comp).toEqual([{ startTick: 0, endTick: 2 * Bar, takeId: t1?.id }]);
    p = await run(server, { type: 'deleteTake', folderId: folder?.id ?? '', takeId: t1?.id ?? '' });
    expect(p.takeFolders[0]?.comp).toEqual([]);
    await expect(
      server.applyCommands({ commands: [{ type: 'deleteTake', folderId: folder?.id ?? '', takeId: t2?.id ?? '' }] })
    ).rejects.toThrow('last take');
    p = await run(server, { type: 'moveRegion', regionId: folder?.id ?? '', startTick: Bar });
    expect(p.takeFolders[0]).toMatchObject({ startTick: Bar });
    expect(p.takeFolders[0]?.takes[0]?.startTick).toBe(Bar);
  });

  it('stacks tracks, keeps members after their stack, and unstacks', async () => {
    const server = new FakeAnodeServer();
    const { trackIds } = await audioProject(server, 3);
    let p = await run(server, { type: 'createStack', trackIds: [trackIds[0] ?? '', trackIds[2] ?? ''], name: 'Drums' });
    expect(p.tracks.map(t => [t.kind, t.parentId !== null])).toEqual([
      ['stack', false],
      ['audio', true],
      ['audio', true],
      ['audio', false]
    ]);
    const stack = p.tracks[0];
    p = await run(server, { type: 'setTrackParent', trackId: trackIds[1] ?? '', parentId: stack?.id ?? '' });
    expect(p.tracks.at(-1)?.parentId).toBe(stack?.id);
    p = await run(server, { type: 'setTrack', trackId: stack?.id ?? '', input: null, collapsed: true, notes: 'kit' });
    expect(p.tracks[0]).toMatchObject({ collapsed: true, notes: 'kit' });
    await expect(
      server.applyCommands({ commands: [{ type: 'createStack', trackIds: [stack?.id ?? ''] }] })
    ).rejects.toThrow('nest');
    p = await run(server, { type: 'removeStack', stackId: stack?.id ?? '' });
    expect(p.tracks.every(t => t.parentId === null && t.kind === 'audio')).toBe(true);
  });

  it('copies, joins and mutes regions, and keeps fade curves', async () => {
    const server = new FakeAnodeServer();
    const { file, trackId } = await audioProject(server);
    let p = await run(server, { type: 'addRegion', trackId, fileId: file.id, startTick: 0, length: 48_000 });
    const id = p.regions[0]?.id ?? '';
    p = await run(server, { type: 'setRegion', regionId: id, fadeIn: 100, fadeInCurve: -1, muted: true });
    expect(p.regions[0]).toMatchObject({ fadeInCurve: -1, muted: true });
    await expect(
      server.applyCommands({ commands: [{ type: 'setRegion', regionId: id, fadeOutCurve: 2 }] })
    ).rejects.toThrow('curve');
    p = await run(server, { type: 'copyRegions', regionIds: [id], offsetTicks: Bar });
    expect(p.regions).toHaveLength(2);
    // Two pieces of one stretch of the file join; others need a bounce.
    p = await run(server, { type: 'splitRegion', regionId: id, atTick: 960 });
    const pieces = p.regions.filter(r => r.startTick < Bar).map(r => r.id);
    p = await run(server, { type: 'joinRegions', regionIds: pieces });
    expect(p.regions.find(r => r.startTick === 0)?.length).toBe(48_000);
    await expect(
      server.applyCommands({ commands: [{ type: 'joinRegions', regionIds: p.regions.map(r => r.id) }] })
    ).rejects.toThrow('bounce');
  });

  it('cycle-records a take per pass into a take folder, comping the last', async () => {
    let now = 0;
    const server = new FakeAnodeServer({ now: () => now });
    const { trackId } = await audioProject(server);
    await run(
      server,
      { type: 'setCycle', enabled: true, startTick: 0, endTick: Bar },
      {
        type: 'setTrack',
        trackId,
        input: null,
        recordArmed: true
      }
    );
    await server.controlTransport({ command: 'record', tick: 0 });
    now += 2_000 * 2.5; // two and a half bars at 120 bpm
    await server.controlTransport({ command: 'stop' });
    const folder = server.current?.takeFolders[0];
    expect(folder?.takes.map(t => t.startTick)).toEqual([0, 0, 0]);
    expect(folder?.comp).toEqual([{ startTick: 0, endTick: Bar / 2, takeId: folder?.takes[2]?.id }]);
    expect(server.current?.audioFiles.at(-1)?.frames).toBe(5 * 48_000);
    expect((await server.history()).undo[0]?.label).toBe('Record');
  });

  it('makes a region per pass in regions mode, and keeps only the punch range', async () => {
    let now = 0;
    const server = new FakeAnodeServer({ now: () => now });
    const { trackId } = await audioProject(server);
    await run(
      server,
      { type: 'setTrack', trackId, input: null, recordArmed: true },
      { type: 'setRecording', autopunch: true, punchInTick: Bar, punchOutTick: 2 * Bar }
    );
    await server.controlTransport({ command: 'record', tick: 0 });
    now += 2_000 * 3;
    await server.controlTransport({ command: 'stop' });
    const region = server.current?.regions[0];
    expect(region).toMatchObject({ startTick: Bar, fileOffset: 96_000, length: 96_000 });
  });

  it('replaces what a recording covers in replace mode', async () => {
    let now = 0;
    const server = new FakeAnodeServer({ now: () => now });
    const { file, trackId } = await audioProject(server);
    await run(
      server,
      { type: 'addRegion', trackId, fileId: file.id, startTick: 0, length: 96_000 * 2 },
      { type: 'setTrack', trackId, input: null, recordArmed: true },
      { type: 'setRecording', replace: true }
    );
    await server.controlTransport({ command: 'record', tick: Bar });
    now += 2_000;
    await server.controlTransport({ command: 'stop' });
    const regions = [...(server.current?.regions ?? [])].sort((a, b) => a.startTick - b.startTick);
    expect(regions.map(r => r.startTick)).toEqual([0, Bar]);
    expect(regions[0]?.length).toBe(96_000);
  });

  it('captures what was played with Flashback Capture, as one undoable step', async () => {
    let now = 0;
    const server = new FakeAnodeServer({ now: () => now });
    const { trackId } = await audioProject(server);
    await expect(server.flashbackCapture()).rejects.toThrow('play');
    await run(server, { type: 'setTrack', trackId, input: null, recordArmed: true });
    await server.controlTransport({ command: 'play', tick: 0 });
    now += 2_000;
    await server.controlTransport({ command: 'stop' });
    await server.flashbackCapture();
    expect(server.current?.regions[0]).toMatchObject({ startTick: 0, length: 96_000 });
    expect((await server.history()).undo[0]?.label).toBe('Flashback Capture');
  });
});

describe('FakeAnodeServer, Phase 3 endpoints', () => {
  it('saves alternatives, switches between them and reverts to backups', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Song' });
    await server.applyCommands({ commands: [{ type: 'addTrack', stereo: false }] });
    await server.saveProject();
    expect((await server.alternatives()).map(a => a.name)).toEqual(['Song']);
    const alt = await server.createAlternative('Remix');
    await server.applyCommands({ commands: [{ type: 'addTrack', stereo: false }] });
    expect(server.current?.tracks).toHaveLength(2);
    const first = (await server.alternatives()).find(a => !a.current);
    const opened = await server.openAlternative(first?.id ?? '');
    expect(opened.tracks).toHaveLength(1);
    await server.openAlternative(alt.id);
    expect(server.current?.tracks).toHaveLength(2);
    await expect(server.deleteAlternative(alt.id)).rejects.toThrow('current');

    await server.saveProject();
    const backups = await server.backups();
    expect(backups.length).toBeGreaterThan(0);
    const reverted = await server.revertToBackup(backups[0]?.id ?? '');
    expect(reverted.tracks).toHaveLength(1);
  });

  it('keeps screensets in the project, and key commands and bookmarks per user', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Song' });
    await server.saveProject();
    await server.saveScreenset({ number: 2, name: 'Mix', locked: true, layout: '{}' });
    expect(await server.screensets()).toEqual([{ number: 2, name: 'Mix', locked: true, layout: '{}' }]);
    expect(server.current?.dirty).toBe(true);
    await expect(server.saveScreenset({ number: 10, name: '', locked: false, layout: '' })).rejects.toThrow('1 to 9');
    await server.saveKeyCommands({ bindings: { 'transport.cycle': ['L'] } });
    expect((await server.keyCommands()).bindings).toEqual({ 'transport.cycle': ['L'] });
    await server.saveBookmarks({ paths: ['/a', '/a', '/b'] });
    expect((await server.bookmarks()).paths).toEqual(['/a', '/b']);
  });

  it('lists the disk, previews WAV files and imports MIDI files', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Song' });
    const home = await server.listFiles();
    expect(home.parent).toBeNull();
    expect(home.entries[0]?.kind).toBe('folder');
    const midi = await server.listFiles(`${FakeHome}/Music/MIDI`);
    expect(midi.entries.every(e => e.kind === 'midi')).toBe(true);
    const loops = await server.listFiles(`${FakeHome}/Music/Loops`);
    expect(loops.entries[0]?.sampleRate).toBe(48_000);
    await expect(server.listFiles('/nowhere')).rejects.toMatchObject({ status: 404 });

    await server.previewFile(loops.entries[0]?.path ?? '');
    expect(server.previewing).toBe(loops.entries[0]?.path);
    await server.previewFile(null);
    await expect(server.previewFile(midi.entries[0]?.path ?? '')).rejects.toThrow('WAV');

    const result = await server.importMidiFile(midi.entries[0]?.path ?? '');
    expect(server.current?.tracks.filter(t => t.kind === 'instrument')).toHaveLength(2);
    expect(server.current?.midiRegions).toHaveLength(2);
    expect(result.delta.ops.some(op => op.op === 'trackUpserted')).toBe(true);
    expect(server.current?.settings.tempo).toBeGreaterThanOrEqual(84);
    expect((await server.history()).undo[0]?.label).toBe('Import MIDI File');
  });
});
