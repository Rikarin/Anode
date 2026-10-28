import type { EditCommand, Project, ServerMessage, Track } from '@anode/api-client';
import { FakeAnodeServer } from './fake-server';

const Bar = 3840;

async function mixProject(tracks = 2): Promise<{ server: FakeAnodeServer; ids: string[] }> {
  let time = 0;
  const server = new FakeAnodeServer({ now: () => (time += 5_000) });
  await server.createProject({ name: 'Mix' });
  for (let i = 0; i < tracks; i++) {
    await server.applyCommands({ commands: [{ type: 'addTrack', stereo: true }] });
  }
  return { server, ids: (server.current?.tracks ?? []).map(t => t.id) };
}

async function run(server: FakeAnodeServer, ...commands: EditCommand[]): Promise<Project> {
  await server.applyCommands({ commands, label: 'Test' });
  return server.current as Project;
}

function track(project: Project, id: string): Track {
  const found = project.tracks.find(t => t.id === id);
  if (!found) {
    throw new Error(`no track ${id}`);
  }
  return found;
}

function messages(server: FakeAnodeServer): ServerMessage[] {
  const list: ServerMessage[] = [];
  server.subscribe(message => list.push(message));
  return list;
}

const tick = () => new Promise(resolve => setTimeout(resolve, 0));

describe('FakeAnodeServer, Phase 4 mixing commands', () => {
  it('applies and undoes every mixing command', async () => {
    const { server, ids } = await mixProject();
    const [a = '', b = ''] = ids;
    const steps: [EditCommand, (p: Project) => void][] = [
      [
        { type: 'addInsert', trackId: a, pluginId: 'anode.compressor' },
        p => expect(track(p, a).strip.inserts).toHaveLength(1)
      ],
      [
        { type: 'addInsert', trackId: a, pluginId: 'anode.channel-eq', index: 0 },
        p => expect(track(p, a).strip.inserts[0]?.pluginId).toBe('anode.channel-eq')
      ],
      [{ type: 'addInsert', pluginId: 'anode.limiter' }, p => expect(p.master.inserts[0]?.name).toBe('Limiter')],
      [
        { type: 'addAux', name: 'Verb' },
        p => expect(p.tracks.at(-1)).toMatchObject({ kind: 'aux', inputBus: 1, name: 'Verb' })
      ],
      [
        { type: 'addSend', trackId: a, bus: 1 },
        p => expect(track(p, a).strip.sends[0]).toMatchObject({ bus: 1, levelDb: -144, preFader: false })
      ],
      [{ type: 'setOutput', trackId: b, output: 'output:2' }, p => expect(track(p, b).strip.output).toBe('output:2')],
      [
        { type: 'setGroup', number: 3, name: 'Drums', properties: null },
        p => expect(p.groups[0]).toMatchObject({ number: 3, name: 'Drums', enabled: true })
      ],
      [
        { type: 'setTrackGroups', trackId: a, groups: [3, 5] },
        p => expect(p.groups.map(g => g.number)).toEqual([3, 5])
      ],
      [{ type: 'deleteGroup', number: 5 }, p => expect(track(p, a).strip.groups).toEqual([3])],
      [{ type: 'addVca' }, p => expect(p.tracks.at(-1)).toMatchObject({ kind: 'vca', name: 'VCA 1' })],
      [{ type: 'setAutomationMode', trackId: a, mode: 'touch' }, p => expect(track(p, a).automationMode).toBe('touch')],
      [
        { type: 'setMixerSettings', pdc: 'tracks', lowLatency: true },
        p => expect(p.settings.mixer).toMatchObject({ pdc: 'tracks', lowLatency: true })
      ],
      [
        {
          type: 'setAutomation',
          trackId: a,
          target: 'pan',
          points: [
            { tick: Bar, value: 3, curve: 2 },
            { tick: 0, value: -0.5, curve: 0 }
          ]
        },
        p =>
          expect(p.automation[0]?.points).toEqual([
            { tick: 0, value: -0.5, curve: 0 },
            { tick: Bar, value: 1, curve: 1 }
          ])
      ]
    ];
    for (const [command, check] of steps) {
      const before = structuredClone(server.current) as Project;
      check(await run(server, command));
      await server.undo();
      expect({ ...server.current, revision: 0, dirty: false }).toEqual({ ...before, revision: 0, dirty: false });
      await server.redo();
    }

    let p = server.current as Project;
    const insert = track(p, a).strip.inserts.find(i => i.pluginId === 'anode.compressor');
    const send = track(p, a).strip.sends[0];
    const vca = p.tracks.find(t => t.kind === 'vca');
    p = await run(server, {
      type: 'setInsertParameter',
      trackId: a,
      insertId: insert?.id ?? '',
      parameterId: 'ratio',
      value: 99
    });
    expect(track(p, a).strip.inserts.find(i => i.id === insert?.id)?.parameters['ratio']).toBe(30);
    p = await run(server, { type: 'setInsert', trackId: a, insertId: insert?.id ?? '', bypassed: true, sidechain: b });
    expect(track(p, a).strip.inserts.find(i => i.id === insert?.id)).toMatchObject({ bypassed: true, sidechain: b });
    p = await run(server, { type: 'moveInsert', trackId: a, insertId: insert?.id ?? '', index: 0 });
    expect(track(p, a).strip.inserts[0]?.id).toBe(insert?.id);
    p = await run(server, {
      type: 'setSend',
      trackId: a,
      sendId: send?.id ?? '',
      levelDb: -6,
      preFader: true,
      pan: -2
    });
    expect(track(p, a).strip.sends[0]).toMatchObject({ levelDb: -6, preFader: true, pan: -1 });
    p = await run(server, { type: 'assignVca', trackId: a, vcaId: vca?.id ?? null });
    expect(track(p, a).strip.vcaId).toBe(vca?.id);
    p = await run(server, { type: 'removeInsert', trackId: a, insertId: insert?.id ?? '' });
    expect(track(p, a).strip.inserts.some(i => i.id === insert?.id)).toBe(false);
    p = await run(server, { type: 'removeSend', trackId: a, sendId: send?.id ?? '' });
    expect(track(p, a).strip.sends).toEqual([]);
    p = await run(server, { type: 'setAutomation', trackId: a, target: 'pan', points: [] });
    expect(p.automation).toEqual([]);
    // Removing the VCA unassigns its members.
    p = await run(server, { type: 'removeTrack', trackId: vca?.id ?? '' });
    expect(track(p, a).strip.vcaId).toBeNull();
  });

  it('refuses what does not apply', async () => {
    const { server, ids } = await mixProject();
    const [a = ''] = ids;
    const refused = (command: EditCommand) => expect(server.applyCommands({ commands: [command] })).rejects.toThrow();
    await refused({ type: 'addInsert', trackId: a, pluginId: 'anode.synth' });
    await refused({ type: 'addInsert', trackId: a, pluginId: 'no.such' });
    await refused({ type: 'addSend', trackId: a, bus: 65 });
    await refused({ type: 'setOutput', trackId: a, output: 'output:33' });
    await refused({ type: 'setOutput', trackId: a, output: 'nowhere' });
    await refused({ type: 'setGroup', number: 33, properties: null });
    await refused({
      type: 'setAutomation',
      trackId: a,
      target: 'send:none',
      points: [{ tick: 0, value: 0, curve: 0 }]
    });
    await refused({ type: 'setAutomation', trackId: a, target: 'sparkle', points: [{ tick: 0, value: 0, curve: 0 }] });
    await refused({ type: 'unfreezeTrack', trackId: a });
    await refused({ type: 'applyPatch', trackId: a, patchId: 'pat_none' });
    await refused({ type: 'assignVca', trackId: a, vcaId: a });
  });

  it('refuses routing loops, directly and through other aux tracks', async () => {
    const { server } = await mixProject(0);
    let p = await run(server, { type: 'addAux', bus: 1 }, { type: 'addAux', bus: 2 }, { type: 'addAux', bus: 3 });
    const [aux1 = '', aux2 = '', aux3 = ''] = p.tracks.map(t => t.id);
    await expect(
      server.applyCommands({ commands: [{ type: 'setOutput', trackId: aux1, output: 'bus:1' }] })
    ).rejects.toThrow('loop');
    p = await run(
      server,
      { type: 'setOutput', trackId: aux1, output: 'bus:2' },
      { type: 'addSend', trackId: aux2, bus: 3 }
    );
    expect(track(p, aux1).strip.output).toBe('bus:2');
    // 1 → 2 → 3: aux 3 may not feed bus 1, by its output or a send.
    await expect(
      server.applyCommands({ commands: [{ type: 'setOutput', trackId: aux3, output: 'bus:1' }] })
    ).rejects.toThrow('loop');
    await expect(server.applyCommands({ commands: [{ type: 'addSend', trackId: aux3, bus: 2 }] })).rejects.toThrow(
      'loop'
    );
    p = await run(server, { type: 'addSend', trackId: aux3, bus: 4 });
    expect(track(p, aux3).strip.sends).toHaveLength(1);
  });

  it('links group members, keeping their volume offsets', async () => {
    const { server, ids } = await mixProject(3);
    const [a = '', b = '', c = ''] = ids;
    await run(
      server,
      { type: 'setTrackGroups', trackId: a, groups: [1] },
      { type: 'setTrackGroups', trackId: b, groups: [1] },
      { type: 'setStrip', trackId: b, volumeDb: -6 }
    );
    let p = await run(server, { type: 'setStrip', trackId: a, volumeDb: -3 });
    const before = [track(p, a).strip.volumeDb, track(p, b).strip.volumeDb];
    p = await run(server, { type: 'setStrip', trackId: a, volumeDb: (before[0] ?? 0) - 4, mute: true, pan: 0.5 });
    expect(track(p, a).strip.volumeDb - track(p, b).strip.volumeDb).toBeCloseTo((before[0] ?? 0) - (before[1] ?? 0));
    expect(track(p, b).strip).toMatchObject({ mute: true, pan: 0.5 });
    expect(track(p, c).strip).toMatchObject({ mute: false, volumeDb: 0 });
    p = await run(server, { type: 'setTrack', trackId: a, input: null, recordArmed: true });
    expect(track(p, b).recordArmed).toBe(true);
    await run(server, { type: 'setGroup', number: 1, enabled: false, properties: null });
    p = await run(server, { type: 'setStrip', trackId: a, solo: true });
    expect(track(p, b).strip.solo).toBe(false);
  });
});

describe('FakeAnodeServer, automation recording', () => {
  async function playing(mode: 'off' | 'read' | 'touch' | 'latch' | 'write') {
    let time = 0;
    const server = new FakeAnodeServer({ now: () => time });
    await server.createProject({ name: 'Auto' });
    await run(server, { type: 'addTrack', stereo: false }, { type: 'addInsert', pluginId: 'anode.gain' });
    const id = server.current?.tracks[0]?.id ?? '';
    await run(
      server,
      { type: 'setAutomationMode', trackId: id, mode },
      { type: 'addInsert', trackId: id, pluginId: 'anode.gain' },
      { type: 'addSend', trackId: id, bus: 1 }
    );
    await server.controlTransport({ command: 'play', tick: 0 });
    const move = async (ms: number, command: EditCommand) => {
      time += ms;
      server.advance();
      await run(server, command);
    };
    return { server, id, move, stop: () => server.controlTransport({ command: 'stop' }) };
  }

  it('records touch, latch and write moves at the playhead, one undo step per gesture', async () => {
    for (const mode of ['touch', 'latch', 'write'] as const) {
      const { server, id, move, stop } = await playing(mode);
      const insert = server.current?.tracks[0]?.strip.inserts[0]?.id ?? '';
      const send = server.current?.tracks[0]?.strip.sends[0]?.id ?? '';
      const undoBefore = (await server.history()).undo.length;
      await move(500, { type: 'setStrip', trackId: id, volumeDb: -3 });
      await move(250, { type: 'setStrip', trackId: id, volumeDb: -6 });
      await move(250, { type: 'setStrip', trackId: id, volumeDb: -9 });
      await move(100, { type: 'setInsertParameter', trackId: id, insertId: insert, parameterId: 'gain', value: -12 });
      await move(100, { type: 'setSend', trackId: id, sendId: send, levelDb: -20 });
      await stop();
      const lanes = server.current?.automation ?? [];
      const volume = lanes.find(l => l.target === 'volume');
      expect(volume?.points.map(p => p.value)).toEqual([-3, -6, -9]);
      expect(volume?.points[0]?.tick).toBeGreaterThan(0);
      expect(lanes.find(l => l.target === `insert:${insert}:gain`)?.points).toHaveLength(1);
      expect(lanes.find(l => l.target === `send:${send}`)?.points[0]?.value).toBe(-20);
      // The fader's three moves are one step; the parameter and send one each.
      expect((await server.history()).undo.length - undoBefore).toBe(3);
    }
  });

  it('records nothing in read or off, or while stopped', async () => {
    for (const mode of ['read', 'off'] as const) {
      const { server, id, move } = await playing(mode);
      await move(500, { type: 'setStrip', trackId: id, volumeDb: -3 });
      expect(server.current?.automation).toEqual([]);
    }
    const { server, id, stop } = await playing('touch');
    await stop();
    await run(server, { type: 'setStrip', trackId: id, volumeDb: -3 });
    expect(server.current?.automation).toEqual([]);
  });
});

describe('FakeAnodeServer, jobs', () => {
  it('validates a bounce', async () => {
    const { server, ids } = await mixProject();
    const bad = [
      { fileName: '' },
      { fileName: 'a/b' },
      { fileName: 'Mix', bitDepth: 20 },
      { fileName: 'Mix', sampleRate: 12_345 },
      { fileName: 'Mix', source: 'stems' as const, trackIds: [] },
      { fileName: 'Mix', source: 'stems' as const, trackIds: ['trk_none'] },
      { fileName: 'Mix', startTick: Bar, endTick: Bar },
      { fileName: 'Mix', normalize: 'loudness' as const, loudnessLufs: 3 },
      { fileName: 'Mix', peakCeilingDb: -30 }
    ];
    for (const request of bad) {
      await expect(server.bounce(bounceRequest(request))).rejects.toThrow();
    }
    await expect(
      server.bounce(bounceRequest({ fileName: 'Mix', source: 'stems', trackIds: ids }))
    ).resolves.toMatchObject({ state: 'queued' });
  });

  it('runs a bounce to done, with progress events, and adds the files to the project when asked', async () => {
    const { server, ids } = await mixProject();
    const events = messages(server);
    const job = await server.bounce(
      bounceRequest({ fileName: 'Stems', source: 'stems', trackIds: ids, format: 'aiff', addToProject: true })
    );
    await tick();
    server.runJobs(2);
    expect((await server.job(job.id)).state).toBe('running');
    server.runJobs();
    const done = await server.job(job.id);
    expect(done.state).toBe('done');
    expect(done.files).toEqual([
      '/Users/anode/Music/Anode/Mix.anode/Bounces/Stems - Audio 1.aif',
      '/Users/anode/Music/Anode/Mix.anode/Bounces/Stems - Audio 2.aif'
    ]);
    const progress = events.flatMap(m => (m.type === 'job' ? [m.job.progress] : []));
    expect(progress[0]).toBe(0);
    expect(progress.at(-1)).toBe(1);
    expect(server.current?.audioFiles.map(f => f.name)).toEqual(['Stems - Audio 1.aif', 'Stems - Audio 2.aif']);
    const changed = events.find(m => m.type === 'projectChanged');
    expect(changed?.type === 'projectChanged' && changed.delta.ops.some(op => op.op === 'audioFileUpserted')).toBe(
      true
    );
  });

  it('cancels a job', async () => {
    const { server } = await mixProject();
    const job = await server.bounce(bounceRequest({ fileName: 'Mix', directory: '/tmp/out' }));
    server.runJobs(1);
    expect((await server.cancelJob(job.id)).state).toBe('cancelled');
    server.runJobs();
    expect((await server.job(job.id)).state).toBe('cancelled');
    await expect(server.job('job_none')).rejects.toThrow();
  });

  it('freezes a track in one undo step and unfreezes it', async () => {
    const { server, ids } = await mixProject();
    const [a = ''] = ids;
    const job = await server.freezeTrack(a);
    await expect(server.freezeTrack(a)).rejects.toThrow('being frozen');
    server.runJobs();
    expect((await server.job(job.id)).state).toBe('done');
    const frozen = track(server.current as Project, a).frozenFileId;
    expect(server.current?.audioFiles.find(f => f.id === frozen)?.name).toBe('Audio 1 Freeze.wav');
    expect((await server.history()).undo[0]?.label).toBe('Freeze Track');
    await expect(server.freezeTrack(a)).rejects.toThrow('frozen already');
    const p = await run(server, { type: 'unfreezeTrack', trackId: a });
    expect(track(p, a).frozenFileId).toBeNull();
    const aux = await run(server, { type: 'addAux' });
    await expect(server.freezeTrack(aux.tracks.at(-1)?.id ?? '')).rejects.toThrow();
  });
});

describe('FakeAnodeServer, patches, I/O labels, plugin editors and problems', () => {
  it('saves a strip as a patch and applies it to another track', async () => {
    const { server, ids } = await mixProject();
    const [a = '', b = ''] = ids;
    await run(
      server,
      { type: 'addInsert', trackId: a, pluginId: 'anode.reverb' },
      { type: 'addSend', trackId: a, bus: 2, levelDb: -10 }
    );
    await expect(server.savePatch({ trackId: a, name: ' ', category: '' })).rejects.toThrow();
    await expect(server.savePatch({ trackId: 'trk_none', name: 'X', category: '' })).rejects.toThrow();
    const patch = await server.savePatch({ trackId: a, name: 'Big Room', category: 'Spaces' });
    expect(patch).toMatchObject({ name: 'Big Room', category: 'Spaces', kind: 'audio' });
    expect(await server.patches()).toEqual([patch]);
    const p = await run(server, { type: 'applyPatch', trackId: b, patchId: patch.id });
    expect(track(p, b).strip.inserts.map(i => i.pluginId)).toEqual(['anode.reverb']);
    expect(track(p, b).strip.inserts[0]?.id).not.toBe(track(p, a).strip.inserts[0]?.id);
    expect(track(p, b).strip.sends[0]).toMatchObject({ bus: 2, levelDb: -10 });
    await server.deletePatch(patch.id);
    expect(await server.patches()).toEqual([]);
    await expect(server.deletePatch(patch.id)).rejects.toThrow();
  });

  it('keeps I/O labels, validated', async () => {
    const server = new FakeAnodeServer();
    expect(await server.ioLabels()).toEqual({ inputs: [], outputs: [], buses: [] });
    const saved = await server.saveIoLabels({
      inputs: [
        { number: 2, name: ' Guitar ' },
        { number: 1, name: 'Mic' }
      ],
      outputs: [{ number: 1, name: '' }],
      buses: [{ number: 64, name: 'Last' }]
    });
    expect(saved).toEqual({
      inputs: [
        { number: 1, name: 'Mic' },
        { number: 2, name: 'Guitar' }
      ],
      outputs: [],
      buses: [{ number: 64, name: 'Last' }]
    });
    await expect(
      server.saveIoLabels({ inputs: [], outputs: [{ number: 33, name: 'X' }], buses: [] })
    ).rejects.toThrow();
    await expect(server.saveIoLabels({ inputs: [], outputs: [], buses: [{ number: 0, name: 'X' }] })).rejects.toThrow();
  });

  it('opens plugin editors only for plugins that have one', async () => {
    const { server, ids } = await mixProject();
    const [a = ''] = ids;
    const p = await run(
      server,
      { type: 'addInsert', trackId: a, pluginId: 'com.anode.test-gain' },
      { type: 'addInsert', trackId: a, pluginId: 'anode.gain' }
    );
    const [own, builtin] = track(p, a).strip.inserts;
    await server.openPluginEditor({ trackId: a, insertId: own?.id ?? '', open: true });
    expect([...server.editors]).toEqual([`${a}/${own?.id}`]);
    await expect(
      server.openPluginEditor({ trackId: a, insertId: builtin?.id ?? '', open: true })
    ).rejects.toMatchObject({ status: 409 });
    await expect(server.openPluginEditor({ trackId: a, insertId: 'ins_none', open: true })).rejects.toMatchObject({
      status: 404
    });
    await expect(server.openPluginEditor({ trackId: 'trk_none', insertId: null, open: true })).rejects.toMatchObject({
      status: 404
    });
    await server.openPluginEditor({ trackId: a, insertId: own?.id ?? '', open: false });
    expect(server.editors.size).toBe(0);
  });

  it('reports a crashing plugin when playback starts, once', async () => {
    const { server, ids } = await mixProject();
    const [a = ''] = ids;
    const p = await run(server, { type: 'addInsert', trackId: a, pluginId: 'com.example.crashy' });
    const events = messages(server);
    await server.controlTransport({ command: 'play' });
    await server.controlTransport({ command: 'stop' });
    await server.controlTransport({ command: 'play' });
    await tick();
    const problems = events.filter(m => m.type === 'pluginProblem');
    expect(problems).toEqual([
      expect.objectContaining({
        trackId: a,
        insertId: track(p, a).strip.inserts[0]?.id,
        pluginId: 'com.example.crashy'
      })
    ]);
  });

  it('reports insert readings and aux levels in the meters', async () => {
    let time = 0;
    const server = new FakeAnodeServer({ now: () => time, demo: true });
    const events = messages(server);
    await server.controlTransport({ command: 'play', tick: 6 * Bar });
    time += 200;
    server.advance();
    const meters = events.filter(m => m.type === 'meters').at(-1);
    expect(meters?.type === 'meters' && meters.readings?.length).toBeGreaterThan(0);
    const aux = server.current?.tracks.find(t => t.kind === 'aux');
    const auxMeter = meters?.type === 'meters' ? meters.tracks.find(t => t.trackId === aux?.id) : undefined;
    expect(auxMeter?.peakLeft).toBeGreaterThan(0);
  });

  it('diffs automation lanes and groups', async () => {
    const { server, ids } = await mixProject();
    const [a = ''] = ids;
    const changed = async (command: EditCommand) => {
      const result = await server.applyCommands({ commands: [command] });
      return result.delta.ops.map(op => op.op);
    };
    expect(await changed({ type: 'setGroup', number: 2, properties: null })).toEqual(['groupUpserted']);
    expect(
      await changed({ type: 'setAutomation', trackId: a, target: 'volume', points: [{ tick: 0, value: -3, curve: 0 }] })
    ).toEqual(['automationLaneUpserted']);
    expect(await changed({ type: 'setAutomation', trackId: a, target: 'volume', points: [] })).toEqual([
      'automationLaneRemoved'
    ]);
    expect(await changed({ type: 'deleteGroup', number: 2 })).toEqual(['groupRemoved']);
  });

  it('opens the demo with a mix', async () => {
    const server = new FakeAnodeServer({ demo: true });
    const p = server.current as Project;
    expect(p.tracks.find(t => t.kind === 'aux')?.strip.inserts[0]?.pluginId).toBe('anode.reverb');
    expect(p.tracks.find(t => t.kind === 'vca')).toBeTruthy();
    expect(p.groups[0]?.name).toBe('Rhythm');
    expect(p.automation[0]?.points.some(pt => pt.curve !== 0)).toBe(true);
    expect((await server.patches()).map(x => x.name).sort()).toEqual(['Vocal Chain', 'Warm Keys']);
    expect((await server.ioLabels()).buses[0]?.name).toBe('Reverb');
  });
});

function bounceRequest(request: Partial<Parameters<FakeAnodeServer['bounce']>[0]> & { fileName: string }) {
  return {
    source: 'mix' as const,
    format: 'wav' as const,
    bitDepth: 24,
    dither: 'tpdf' as const,
    normalize: 'off' as const,
    loudnessLufs: -14,
    peakCeilingDb: -1,
    realtime: false,
    addToProject: false,
    ...request
  };
}
