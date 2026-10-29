import { ApiError, type EditCommand, type Project, type ServerMessage } from '@anode/api-client';
import { scriptParameters, versionPath } from './fake-phase7';
import { FakeAnodeServer } from './fake-server';

async function status(server: FakeAnodeServer, command: EditCommand): Promise<number> {
  try {
    await server.applyCommands({ commands: [command], label: 'Test' });
    return 200;
  } catch (error) {
    return error instanceof ApiError ? error.status : 500;
  }
}

async function run(server: FakeAnodeServer, ...commands: EditCommand[]) {
  return server.applyCommands({ commands, label: 'Test' });
}

function project(server: FakeAnodeServer): Project {
  const p = server.current;
  if (!p) {
    throw new Error('No project');
  }
  return p;
}

/** Lets the fake's deferred `/ws` messages out. */
const settle = () => new Promise(resolve => setTimeout(resolve, 0));

async function withRegion(): Promise<{ server: FakeAnodeServer; trackId: string; regionId: string }> {
  const server = new FakeAnodeServer();
  await server.createProject({ name: 'Edits' });
  await run(server, { type: 'addTrack', kind: 'instrument', stereo: false });
  const trackId = server.current?.tracks[0]?.id ?? '';
  await run(server, { type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: 3840 });
  const regionId = server.current?.midiRegions[0]?.id ?? '';
  return { server, trackId, regionId };
}

describe('FakeAnodeServer, Phase 7', () => {
  it('starts projects, tracks and notes with the Phase 7 defaults', async () => {
    const { server, regionId } = await withRegion();
    await run(server, {
      type: 'addNotes',
      regionId,
      notes: [{ pitch: 60, velocity: 100, channel: 1, startTick: 0, lengthTicks: 480 }]
    });
    const p = project(server);
    expect(p).toMatchObject({ grooves: [], articulationSets: [], laneSets: [] });
    expect(p.settings.grooveTrackId ?? null).toBeNull();
    expect(p.tracks[0]).toMatchObject({ matchGroove: false });
    expect(p.midiRegions[0]?.notes[0]?.articulationId).toBe(0);
  });

  it('edits controller events for the Event List, and notes’ articulations', async () => {
    const { server, regionId } = await withRegion();
    await run(
      server,
      { type: 'addControls', regionId, events: [{ kind: 'programChange', channel: 1, number: 0, value: 5, tick: 0 }] },
      {
        type: 'addNotes',
        regionId,
        notes: [{ pitch: 60, velocity: 90, channel: 1, startTick: 0, lengthTicks: 240, articulationId: 2 }]
      }
    );
    const control = project(server).midiRegions[0]?.controls[0];
    const note = project(server).midiRegions[0]?.notes[0];
    expect(note?.articulationId).toBe(2);
    await run(
      server,
      { type: 'setControls', regionId, changes: [{ id: control?.id ?? 0, value: 7, tick: 960, channel: 2 }] },
      { type: 'editNotes', regionId, changes: [{ id: note?.id ?? 0, articulationId: 0 }] }
    );
    expect(project(server).midiRegions[0]?.controls[0]).toMatchObject({ value: 7, tick: 960, channel: 2 });
    expect(project(server).midiRegions[0]?.notes[0]?.articulationId).toBe(0);
    expect(
      await status(server, { type: 'setControls', regionId, changes: [{ id: control?.id ?? 0, value: 300 }] })
    ).toBe(422);
    expect(await status(server, { type: 'setControls', regionId, changes: [{ id: 999, value: 1 }] })).toBe(422);
    expect(
      await status(server, { type: 'editNotes', regionId, changes: [{ id: note?.id ?? 0, articulationId: 255 }] })
    ).toBe(422);
  });

  it('transforms a region’s events in one undo step and refuses select only', async () => {
    const { server, regionId } = await withRegion();
    await run(server, {
      type: 'addNotes',
      regionId,
      notes: [48, 60, 74, 79].map((pitch, i) => ({
        pitch,
        velocity: 64,
        channel: 1,
        startTick: i * 480,
        lengthTicks: 960
      }))
    });
    const set = {
      id: 'trs_x',
      name: 'Above C4',
      conditions: [{ field: 'pitch' as const, op: 'greaterEqual' as const, a: 73, b: 0 }],
      operations: [
        { field: 'velocity' as const, op: 'fix' as const, a: 100, b: 0 },
        { field: 'length' as const, op: 'fix' as const, a: 240, b: 0 }
      ],
      map: null,
      mode: 'operateOnly' as const,
      seed: 1
    };
    const before = (await server.history()).undo.length;
    await run(server, { type: 'transform', regionIds: [regionId], set });
    expect(project(server).midiRegions[0]?.notes.map(n => [n.pitch, n.velocity, n.lengthTicks])).toEqual([
      [48, 64, 960],
      [60, 64, 960],
      [74, 100, 240],
      [79, 100, 240]
    ]);
    expect((await server.history()).undo.length).toBe(before + 1);
    expect(
      await status(server, { type: 'transform', regionIds: [regionId], set: { ...set, mode: 'selectOnly' } })
    ).toBe(422);
  });

  it('makes grooves from regions, quantises to them, and follows the groove track', async () => {
    const { server, trackId, regionId } = await withRegion();
    // A swung bar of 1/8s on the drum track.
    await run(server, {
      type: 'addNotes',
      regionId,
      notes: Array.from({ length: 8 }, (_, i) => ({
        pitch: 36,
        velocity: i % 2 ? 64 : 127,
        channel: 1,
        startTick: i * 480 + (i % 2 ? 80 : 0),
        lengthTicks: 120
      }))
    });
    await run(server, { type: 'makeGroove', regionId, name: 'Swing', stepTicks: 480 });
    const groove = project(server).grooves[0];
    expect(groove).toMatchObject({ name: 'Swing', lengthTicks: 3840, stepTicks: 480 });
    expect(groove?.points.map(p => p.tick)).toEqual([0, 560, 960, 1520, 1920, 2480, 2880, 3440]);

    // A straight region on another track quantised to it.
    await run(server, { type: 'addTrack', kind: 'instrument', stereo: false });
    const other = project(server).tracks[1]?.id ?? '';
    await run(server, { type: 'addMidiRegion', trackId: other, startTick: 0, lengthTicks: 3840 });
    const straight = project(server).midiRegions[1]?.id ?? '';
    const notes = [0, 480, 960, 1440].map(startTick => ({
      pitch: 60,
      velocity: 100,
      channel: 1,
      startTick,
      lengthTicks: 240
    }));
    await run(server, { type: 'addNotes', regionId: straight, notes });
    await run(server, {
      type: 'quantizeNotes',
      regionId: straight,
      gridTicks: 240,
      strength: 1,
      swing: 0,
      grooveId: groove?.id ?? ''
    });
    expect(project(server).midiRegions[1]?.notes.map(n => n.startTick)).toEqual([0, 560, 960, 1520]);

    // The groove track: a matching track's quantise without a groove follows it (its 1/16 groove).
    await run(
      server,
      { type: 'setGrooveTrack', trackId },
      { type: 'setMatchGroove', trackId: other, matchGroove: true }
    );
    expect(project(server).settings.grooveTrackId).toBe(trackId);
    await run(server, { type: 'addMidiRegion', trackId: other, startTick: 3840, lengthTicks: 3840 });
    const third = project(server).midiRegions[2]?.id ?? '';
    await run(server, {
      type: 'addNotes',
      regionId: third,
      notes: [{ pitch: 60, velocity: 100, channel: 1, startTick: 480, lengthTicks: 240 }]
    });
    await run(server, { type: 'quantizeNotes', regionId: third, gridTicks: 240, strength: 1, swing: 0 });
    expect(project(server).midiRegions[2]?.notes[0]?.startTick).toBe(560);

    // Region playback and deletion.
    await run(server, { type: 'setMidiRegion', regionId: straight, grooveId: groove?.id ?? null });
    expect(project(server).midiRegions[1]?.playback.grooveId).toBe(groove?.id);
    await run(server, { type: 'deleteGroove', grooveId: groove?.id ?? '' });
    expect(project(server).grooves).toEqual([]);
    expect(project(server).midiRegions[1]?.playback.grooveId ?? null).toBeNull();
    expect(await status(server, { type: 'setMidiRegion', regionId: straight, grooveId: 'grv_missing' })).toBe(422);
  });

  it('keeps articulation sets and lane sets, with their deltas', async () => {
    const { server, trackId } = await withRegion();
    const messages: ServerMessage[] = [];
    server.subscribe(m => messages.push(m));
    const set = {
      id: 'ars_1',
      name: 'Strings',
      articulations: [
        { id: 1, name: 'Sustain', symbol: '—' },
        { id: 2, name: 'Staccato', symbol: '.' }
      ],
      switches: [{ articulationId: 2, kind: 'note' as const, number: 24, value: null }],
      outputs: [
        { articulationId: 1, kind: 'note' as const, number: 24, value: 100 },
        { articulationId: 2, kind: 'note' as const, number: 25, value: 100 }
      ]
    };
    await run(server, { type: 'setArticulationSet', set }, { type: 'assignArticulationSet', trackId, setId: 'ars_1' });
    expect(project(server).tracks[0]?.articulationSetId).toBe('ars_1');
    expect(
      await status(server, { type: 'setArticulationSet', set: { ...set, articulations: [{ id: 0, name: 'x' }] } })
    ).toBe(422);
    await run(server, { type: 'deleteArticulationSet', setId: 'ars_1' });
    expect(project(server).tracks[0]?.articulationSetId ?? null).toBeNull();

    const lanes = {
      id: 'lns_1',
      name: 'Synth',
      lanes: [{ kind: 'controlChange' as const, number: 74, name: 'Cutoff', resolutionTicks: 120 }]
    };
    await run(server, { type: 'setLaneSet', set: lanes });
    expect(project(server).laneSets).toEqual([lanes]);
    expect(
      await status(server, {
        type: 'setLaneSet',
        set: { ...lanes, lanes: [{ kind: 'pitchBend', number: 3, name: 'x', resolutionTicks: 10 }] }
      })
    ).toBe(422);
    await run(server, { type: 'deleteLaneSet', setId: 'lns_1' });
    const ops = messages.flatMap(m => (m.type === 'projectChanged' ? m.delta.ops.map(o => o.op) : []));
    expect(ops).toEqual(
      expect.arrayContaining(['articulationSetUpserted', 'articulationSetRemoved', 'laneSetUpserted', 'laneSetRemoved'])
    );
  });

  it('enters steps, chords, rests and ties, and step-enters from MIDI input', async () => {
    const { server, trackId } = await withRegion();
    const states: unknown[] = [];
    server.subscribe(m => m.type === 'stepInput' && states.push(m.state));
    await server.setStepInput({ on: true, trackId, positionTick: 3840, noteValueTicks: 480, velocity: 90 });
    await run(server, { type: 'stepNotes', pitches: [60] });
    const region = project(server).midiRegions.find(r => r.startTick === 3840);
    expect(region).toBeDefined();
    expect((await server.stepInput()).regionId).toBe(region?.id);
    await server.setStepInput({ chord: true });
    await run(server, { type: 'stepNotes', pitches: [64] }, { type: 'stepNotes', pitches: [67] });
    await server.setStepInput({ chord: false });
    await server.stepRest();
    await run(server, { type: 'stepNotes', pitches: [72], velocity: 120 });
    await run(server, { type: 'stepTie' });
    await server.setStepInput({ midiInput: true });
    await server.sendLiveMidi(trackId, [
      { status: 0x90, data1: 48, data2: 80 },
      { status: 0x90, data1: 55, data2: 100 },
      { status: 0x80, data1: 48, data2: 0 },
      { status: 0x80, data1: 55, data2: 0 }
    ]);
    const notes = project(server).midiRegions.find(r => r.id === region?.id)?.notes ?? [];
    expect(notes.map(n => [n.pitch, n.startTick, n.lengthTicks, n.velocity])).toEqual([
      [60, 0, 480, 90],
      [64, 480, 480, 90],
      [67, 480, 480, 90],
      [72, 1440, 960, 120],
      [48, 2400, 480, 100],
      [55, 2400, 480, 100]
    ]);
    expect((await server.stepInput()).positionTick).toBe(3840 + 2880);
    await settle();
    expect(states.length).toBeGreaterThan(3);
  });

  it('edits a file as a job with versions and undo, answers samples, loop search and range previews', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Audio' });
    await run(server, { type: 'addTrack', kind: 'audio', stereo: false });
    const trackId = project(server).tracks[0]?.id ?? '';
    const file = await server.importAudioFile('/Users/me/Music/tone.wav');
    await run(server, { type: 'addRegion', trackId, fileId: file.id, startTick: 0 });

    const before = await server.fileSamples(file.id, 1000, 100);
    expect(before.samples?.[0]?.some(v => v !== 0)).toBe(true);
    const job = await server.editFile(file.id, { kind: 'silence', startFrame: 1000, endFrame: 1100 });
    expect(job.kind).toBe('audioEdit');
    server.runJobs();
    const edited = project(server).audioFiles.find(f => f.id === file.id);
    expect(edited).toMatchObject({ version: 1, path: versionPath(file, 1) });
    expect((await server.fileSamples(file.id, 1000, 100)).samples?.[0]?.every(v => v === 0)).toBe(true);
    await server.undo();
    expect(project(server).audioFiles.find(f => f.id === file.id)?.version).toBe(0);
    expect((await server.fileSamples(file.id, 1000, 100)).samples).toEqual(before.samples);

    const buckets = await server.fileSamples(file.id, 0, 48_000, 480);
    expect(buckets).toMatchObject({ bucket: 480, samples: null });
    expect(buckets.min?.[0]).toHaveLength(100);
    await expect(server.fileSamples(file.id, 0, 100_000, 1)).rejects.toMatchObject({ status: 422 });

    const loops = await server.searchLoops(file.id, { startFrame: 0, endFrame: 48_000, count: 3 });
    expect(loops.length).toBeGreaterThan(0);
    await server.previewFileRange(file.id, { startFrame: 0, endFrame: 4800, loop: true });
    expect(server.previewRange).toMatchObject({ fileId: file.id, request: { loop: true } });

    await server.editFile(file.id, { kind: 'trim', startFrame: 4800, endFrame: 9600 });
    server.runJobs();
    expect(project(server).audioFiles.find(f => f.id === file.id)?.frames).toBe(4800);
    expect(project(server).regions[0]).toMatchObject({ fileOffset: 0, length: 4800 });
  });

  it('sets a Scripter slot’s script: its parameters, its trace lines, and a runaway bypassed', async () => {
    const { server, trackId } = await withRegion();
    const messages: ServerMessage[] = [];
    server.subscribe(m => messages.push(m));
    await run(server, { type: 'addInsert', pluginId: 'anode.scripter', trackId, chain: 'midiEffects' });
    const slotId = project(server).tracks[0]?.strip.midiEffects[0]?.id ?? '';
    const source = `
var PluginParameters = [{ name: 'Interval', type: 'lin', minValue: 0, maxValue: 24, numberOfSteps: 24, defaultValue: 12 }];
Trace('ready');
function HandleMIDI(event) { event.send(); }`;
    await run(server, { type: 'setScript', trackId, slotId, source });
    const slot = project(server).tracks[0]?.strip.midiEffects[0];
    expect(slot?.source).toBe(source);
    expect(slot?.runtimeParameters).toEqual([
      {
        id: 'Interval',
        name: 'Interval',
        min: 0,
        max: 24,
        default: 12,
        unit: '',
        steps: 24,
        valueNames: null,
        macro: null
      }
    ]);
    expect(slot?.parameters).toEqual({ Interval: 12 });
    await settle();
    expect(messages).toContainEqual(expect.objectContaining({ type: 'parameters', trackId, slotId }));
    expect(messages).toContainEqual({ type: 'scriptLog', trackId, slotId, lines: ['ready'], dropped: 0 });
    await run(server, { type: 'setScript', trackId, slotId, source: 'function HandleMIDI(e) { while (true) {} }' });
    await settle();
    expect(messages).toContainEqual(expect.objectContaining({ type: 'pluginProblem', insertId: slotId }));
    expect(await status(server, { type: 'setScript', trackId, slotId: 'mfx_missing', source: '' })).toBe(422);
  });

  it('reads menus, checkboxes and comments in PluginParameters', () => {
    const parameters = scriptParameters(`var PluginParameters = [
      // a menu
      { name: "Mode", type: "menu", valueStrings: ["Up", "Down"], defaultValue: 1 },
      { name: 'On', type: 'checkbox', defaultValue: 1, },
    ];`);
    expect(parameters.map(p => [p.name, p.min, p.max, p.default, p.steps, p.valueNames])).toEqual([
      ['Mode', 0, 1, 1, 1, ['Up', 'Down']],
      ['On', 0, 1, 1, 1, null]
    ]);
    expect(scriptParameters('function HandleMIDI() {}')).toEqual([]);
  });

  it('keeps the library’s grooves, Transform sets (factory first) and articulation sets', async () => {
    const { server } = await withRegion();
    const sets = await server.transformSets();
    expect(sets[0]?.id).toBe('factory.humanize');
    await expect(server.saveTransformSet({ ...(sets[0] ?? ({} as never)) })).rejects.toMatchObject({ status: 422 });
    await server.saveTransformSet({ ...(sets[0] ?? ({} as never)), id: 'trs_mine', name: 'Mine' });
    expect((await server.transformSets()).map(s => s.id)).toContain('trs_mine');
    await server.deleteTransformSet('trs_mine');
    await expect(server.deleteTransformSet('factory.humanize')).rejects.toMatchObject({ status: 422 });

    const groove = {
      id: 'grv_lib',
      name: 'Lib',
      lengthTicks: 480,
      stepTicks: 240,
      points: [
        { tick: 0, velocityScale: 1 },
        { tick: 260, velocityScale: 0.5 }
      ]
    };
    await server.saveLibraryGroove(groove);
    expect(await server.libraryGrooves()).toEqual([groove]);
    await server.deleteLibraryGroove('grv_lib');
    expect(await server.libraryGrooves()).toEqual([]);

    const set = { id: 'ars_lib', name: 'Lib', articulations: [{ id: 1, name: 'A' }], switches: [], outputs: [] };
    await server.saveLibraryArticulationSet(set);
    expect(await server.libraryArticulationSets()).toEqual([set]);
    await server.deleteLibraryArticulationSet('ars_lib');
  });

  it('matches the Server: the groove track never matches itself, library ids are file-safe, trims refuse early', async () => {
    const { server, trackId } = await withRegion();
    await run(server, { type: 'setMatchGroove', trackId, matchGroove: true });
    await run(server, { type: 'setGrooveTrack', trackId });
    expect(project(server).tracks[0]?.matchGroove).toBe(false);
    expect(await status(server, { type: 'setMatchGroove', trackId, matchGroove: true })).toBe(422);

    await expect(
      server.saveLibraryGroove({
        id: 'grv/../x',
        name: 'x',
        lengthTicks: 240,
        stepTicks: 240,
        points: [{ tick: 0, velocityScale: 1 }]
      })
    ).rejects.toMatchObject({ status: 422 });
    await expect(
      server.saveLibraryArticulationSet({ id: 'ars 1', name: 'x', articulations: [], switches: [], outputs: [] })
    ).rejects.toMatchObject({ status: 422 });

    await run(server, { type: 'addTrack', kind: 'audio', stereo: false });
    const audio = project(server).tracks[1]?.id ?? '';
    const file = await server.importAudioFile('/Users/me/Music/trim.wav');
    await run(server, { type: 'addRegion', trackId: audio, fileId: file.id, startTick: 0, length: 4800 });
    await expect(server.editFile(file.id, { kind: 'trim', startFrame: 9600, endFrame: 19_200 })).rejects.toMatchObject({
      status: 422
    });
    await server.editFile(file.id, { kind: 'silence', startFrame: 0, endFrame: 100 });
    server.runJobs();
    expect((await server.history()).undo[0]?.label).toBe('Silence');
  });
});
