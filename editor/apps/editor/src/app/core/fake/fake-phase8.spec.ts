import { ApiError, type ControllerAssignments, type ServerMessage } from '@anode/api-client';
import { inputOf } from './fake-phase8';
import { FakeAnodeServer } from './fake-server';

async function status(run: () => Promise<unknown>): Promise<number> {
  try {
    await run();
    return 200;
  } catch (error) {
    return error instanceof ApiError ? error.status : 500;
  }
}

/** Lets the fake's deferred events (setTimeout 0) arrive. */
const tick = () => new Promise(resolve => setTimeout(resolve, 0));

async function setUp() {
  const server = new FakeAnodeServer();
  const events: ServerMessage[] = [];
  server.subscribe(message => events.push(message));
  await server.createProject({ name: 'Control' });
  await server.applyCommands({
    commands: [
      { type: 'addTrack', stereo: true },
      { type: 'addTrack', stereo: true }
    ]
  });
  const [first, second] = server.current?.tracks.map(t => t.id) ?? [];
  return { server, events, first: first ?? '', second: second ?? '' };
}

const OneZone: ControllerAssignments = {
  zones: [
    {
      id: 'czn_a',
      name: 'Zone',
      activeModeId: 'cmd_a',
      modes: [
        {
          id: 'cmd_a',
          name: 'Mode',
          assignments: [
            {
              id: 'cas_a',
              name: 'Volume',
              input: { port: null, kind: 'controlChange', channel: null, number: 7 },
              target: { kind: 'parameter', trackId: null, address: 'volume' },
              mode: 'absolute',
              min: 0,
              max: 1,
              pickup: false
            }
          ]
        }
      ]
    }
  ]
};

describe('FakeAnodeServer, Phase 8 (docs/09-phase-8.md)', () => {
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

  it('reads raw messages as inputs', () => {
    expect(inputOf('anode:in', 0xb0, 21)).toEqual({ port: 'anode:in', kind: 'controlChange', channel: 1, number: 21 });
    expect(inputOf('anode:in', 0x99, 36)).toEqual({ port: 'anode:in', kind: 'note', channel: 10, number: 36 });
    expect(inputOf('p', 0xe3, 0)).toEqual({ port: 'p', kind: 'pitchBend', channel: 4, number: 0 });
    expect(inputOf('p', 0xf8, 0)).toBeNull();
  });

  it('keeps assignments and refuses bad ones', async () => {
    const { server } = await setUp();
    expect(await server.controllerAssignments()).toEqual({ zones: [] });
    expect(await server.setControllerAssignments(OneZone)).toEqual(OneZone);
    expect(await server.controllerAssignments()).toEqual(OneZone);
    const zone = OneZone.zones[0];
    if (!zone) {
      throw new Error('no zone');
    }
    const bad = (change: (copy: ControllerAssignments) => void) => {
      const copy = structuredClone(OneZone);
      change(copy);
      return status(() => server.setControllerAssignments(copy));
    };
    expect(await bad(c => (c.zones[0] = { ...zone, activeModeId: 'cmd_x' }))).toBe(422);
    expect(await bad(c => c.zones.push(structuredClone(zone)))).toBe(422); // duplicate ids
    expect(
      await bad(c => {
        const a = c.zones[0]?.modes[0]?.assignments[0];
        if (a) {
          a.input.channel = 17;
        }
      })
    ).toBe(422);
    expect(
      await bad(c => {
        const a = c.zones[0]?.modes[0]?.assignments[0];
        if (a) {
          a.max = 2;
        }
      })
    ).toBe(422);
    // Unchanged after the refusals.
    expect(await server.controllerAssignments()).toEqual(OneZone);
  });

  it('learns: the last message is offered, and with a target it becomes an assignment', async () => {
    const { server, events, second } = await setUp();
    await server.startLearn({ target: null });
    server.controlMidi('anode:in', 0xb0, 21, 64);
    const offered = events.filter(e => e.type === 'controlLearn');
    expect(offered).toEqual([
      {
        type: 'controlLearn',
        message: {
          port: 'anode:in',
          status: 0xb0,
          data1: 21,
          data2: 64,
          input: { port: 'anode:in', kind: 'controlChange', channel: 1, number: 21 }
        },
        assignmentId: null
      }
    ]);
    expect((await server.learn()).last?.data1).toBe(21);

    const target = { kind: 'parameter' as const, trackId: second, address: 'volume' };
    expect((await server.startLearn({ target })).target).toEqual(target);
    server.controlMidi('anode:in', 0xb0, 21, 70);
    const made = events.filter(e => e.type === 'controlLearn').at(-1);
    const id = made?.type === 'controlLearn' ? made.assignmentId : null;
    expect(id).toBeTruthy();
    const assignments = await server.controllerAssignments();
    expect(assignments.zones).toHaveLength(1);
    const mode = assignments.zones[0]?.modes[0];
    expect(mode?.assignments).toEqual([
      expect.objectContaining({
        id,
        input: { port: 'anode:in', kind: 'controlChange', channel: 1, number: 21 },
        target,
        mode: 'absolute'
      })
    ]);

    // The same control again replaces its assignment; a key toggles a switch.
    server.controlMidi('anode:in', 0xb0, 21, 10);
    await server.startLearn({ target: { kind: 'trackSwitch', trackId: null, switch: 'mute' } });
    server.controlMidi('anode:in', 0x90, 36, 100);
    const after = (await server.controllerAssignments()).zones[0]?.modes[0]?.assignments ?? [];
    expect(after.map(a => [a.input.kind, a.mode])).toEqual([
      ['controlChange', 'absolute'],
      ['note', 'toggle']
    ]);
    expect((await server.stopLearn()).on).toBe(false);
  });

  it('runs assignments: absolute volume on a fixed track, a toggle on the selected one', async () => {
    const { server, first, second } = await setUp();
    await server.startLearn({ target: { kind: 'parameter', trackId: second, address: 'volume' } });
    server.controlMidi('anode:in', 0xb0, 21, 0);
    await server.startLearn({ target: { kind: 'trackSwitch', trackId: null, switch: 'mute' } });
    server.controlMidi('anode:in', 0x90, 36, 100);
    await server.stopLearn();
    await server.setSelection(first);

    server.controlMidi('anode:in', 0xb0, 21, 127);
    await tick();
    expect(server.current?.tracks.find(t => t.id === second)?.strip.volumeDb).toBeCloseTo(6, 0);
    server.controlMidi('anode:in', 0x90, 36, 100);
    server.controlMidi('anode:in', 0x80, 36, 0);
    await tick();
    expect(server.current?.tracks.find(t => t.id === first)?.strip.mute).toBe(true);
    expect(server.current?.tracks.find(t => t.id === second)?.strip.mute).toBe(false);
    // A surface's port is its own: assignments do not see it.
    await server.addSurface({ kind: 'mackieControl', inputPort: 'anode:in', outputPort: 'anode:out' });
    server.controlMidi('anode:in', 0x90, 36, 100);
    await tick();
    expect(server.current?.tracks.find(t => t.id === first)?.strip.mute).toBe(true);
  });

  it('adds, orders and removes control surfaces with their status', async () => {
    const { server, events } = await setUp();
    const main = await server.addSurface({ kind: 'mackieControl', inputPort: 'anode:in', outputPort: 'anode:out' });
    expect(main).toEqual(expect.objectContaining({ kind: 'mackieControl', order: 0 }));
    expect(await status(() => server.addSurface({ kind: 'hui', inputPort: 'anode:in', outputPort: 'out:iac-1' }))).toBe(
      422
    );
    expect(await status(() => server.addSurface({ kind: 'hui', inputPort: 'in:nope', outputPort: 'out:iac-1' }))).toBe(
      422
    );
    const xt = await server.addSurface({ kind: 'mackieControlXt', inputPort: 'in:iac-1', outputPort: 'out:iac-1' });
    expect((await server.surfaceStatus()).map(s => s.connected)).toEqual([true, true]);
    expect((await server.reorderSurfaces([xt.id, main.id])).map(s => [s.id, s.order])).toEqual([
      [xt.id, 0],
      [main.id, 1]
    ]);
    expect(await status(() => server.reorderSurfaces([xt.id]))).toBe(422);
    server.controlState.setSurfaceStatus(main.id, { connected: false, bankOffset: 8 });
    await tick();
    const last = events.filter(e => e.type === 'surfaces').at(-1);
    expect(last?.type === 'surfaces' ? last.surfaces : []).toEqual([
      { id: xt.id, connected: true, bankOffset: 0 },
      { id: main.id, connected: false, bankOffset: 8 }
    ]);
    await server.removeSurface(xt.id);
    expect((await server.surfaces()).map(s => [s.id, s.order])).toEqual([[main.id, 0]]);
    expect(await status(() => server.removeSurface(xt.id))).toBe(404);
  });

  it('sets OSC and lists its addresses and subscribers', async () => {
    const { server } = await setUp();
    expect(await server.osc()).toEqual({
      settings: { enabled: false, port: 8000, clients: [] },
      listening: false,
      addresses: [],
      subscribed: []
    });
    const on = await server.setOsc({ enabled: true, port: 9000, clients: [{ host: '192.168.1.7', port: 9001 }] });
    expect(on.listening).toBe(true);
    expect(on.addresses.length).toBeGreaterThan(0);
    server.controlState.oscSubscribe({ host: '192.168.1.8', port: 9002 });
    expect((await server.osc()).subscribed).toEqual([{ host: '192.168.1.8', port: 9002 }]);
    expect(await status(() => server.setOsc({ enabled: true, port: 80, clients: [] }))).toBe(422);
    expect(await status(() => server.setOsc({ enabled: true, port: 9000, clients: [{ host: '', port: 1 }] }))).toBe(
      422
    );
  });

  it('takes the selection', async () => {
    const { server, second } = await setUp();
    await server.setSelection(second);
    expect(server.controlState.selection).toBe(second);
    await server.setSelection(null);
    expect(server.controlState.selection).toBeNull();
    expect(await status(() => server.setSelection('trk_nope'))).toBe(404);
  });

  it('changes the sync settings as an undoable command, and reports the sync state', async () => {
    const { server, events } = await setUp();
    expect(await server.syncState()).toEqual({ source: 'internal', locked: true, externalTempo: null, linkPeers: 0 });
    await server.applyCommands({
      commands: [
        {
          type: 'setSyncSettings',
          source: 'midiClock',
          destinations: [{ port: 'anode:out', clock: true, mtc: true, delayMs: -3 }],
          frameRate: 'fps30',
          startOffsetSeconds: 3600,
          clockInPort: 'anode:in'
        }
      ]
    });
    expect(server.current?.settings.sync).toEqual({
      source: 'midiClock',
      destinations: [{ port: 'anode:out', clock: true, mtc: true, delayMs: -3 }],
      frameRate: 'fps30',
      startOffsetSeconds: 3600,
      linkStartStop: false,
      clockInPort: 'anode:in'
    });
    await tick();
    expect(events.filter(e => e.type === 'sync').at(-1)).toEqual({
      type: 'sync',
      state: { source: 'midiClock', locked: false, externalTempo: null, linkPeers: 0 }
    });
    server.setExternalSync(true, 100.2);
    expect(await server.syncState()).toEqual({
      source: 'midiClock',
      locked: true,
      externalTempo: 100.2,
      linkPeers: 0
    });
    // An empty port is "any".
    await server.applyCommands({ commands: [{ type: 'setSyncSettings', clockInPort: '' }] });
    expect(server.current?.settings.sync.clockInPort).toBeNull();
    for (const bad of [
      { type: 'setSyncSettings' as const, startOffsetSeconds: 90_000 },
      { type: 'setSyncSettings' as const, destinations: [{ port: 'out:nope', clock: true, mtc: false, delayMs: 0 }] },
      { type: 'setSyncSettings' as const, destinations: [{ port: 'anode:out', clock: true, mtc: false, delayMs: 60 }] }
    ]) {
      expect(await status(() => server.applyCommands({ commands: [bad] }))).toBe(422);
    }
    await server.applyCommands({ commands: [{ type: 'setSyncSettings', source: 'link' }] });
    server.setLinkPeers(2);
    expect((await server.syncState()).linkPeers).toBe(2);
    await server.undo();
    expect(server.current?.settings.sync.source).toBe('midiClock');
  });

  it('splits an audio region into stems as a job: a model download first, then the tracks below', async () => {
    const { server, events, first } = await setUp();
    /** The open project, read afresh (TypeScript would narrow the getter once). */
    const current = () => server.current;
    await server.applyCommands({ commands: [{ type: 'setOutput', trackId: first, output: 'bus:3' }] });
    const file = await server.importAudioFile('/Users/me/Music/Mix.wav');
    await server.applyCommands({ commands: [{ type: 'addRegion', trackId: first, fileId: file.id, startTick: 3840 }] });
    const region = server.current?.regions[0];
    if (!region) {
      throw new Error('no region');
    }
    expect(await status(() => server.splitStems('rgn_nope', { keepSource: false }))).toBe(404);
    expect(await status(() => server.splitStems(region.id, { stems: [], keepSource: false }))).toBe(422);
    const job = await server.splitStems(region.id, { keepSource: false });
    expect(job.kind).toBe('stemSplit');
    server.runJobs(1);
    const first1 = events.filter(e => e.type === 'job').at(-1);
    expect(first1?.type === 'job' ? first1.job.message : null).toMatch(/Downloading the four-stem model/);
    server.runJobs();
    const done = events.filter(e => e.type === 'job').at(-1);
    expect(done?.type === 'job' ? [done.job.state, done.job.files.length] : null).toEqual(['done', 4]);
    const tracks = current()?.tracks ?? [];
    expect(tracks.map(t => t.name).slice(1, 5)).toEqual([
      `${region.name} – Vocals`,
      `${region.name} – Drums`,
      `${region.name} – Bass`,
      `${region.name} – Other`
    ]);
    expect(current()?.audioFiles.slice(-4).map(f => f.path)).toEqual([
      `Audio Files/${region.name} Vocals.wav`,
      `Audio Files/${region.name} Drums.wav`,
      `Audio Files/${region.name} Bass.wav`,
      `Audio Files/${region.name} Other.wav`
    ]);
    expect(tracks.slice(1, 5).every(t => t.strip.output === 'bus:3')).toBe(true);
    const stems = current()?.regions.filter(r => r.id !== region.id) ?? [];
    expect(stems.map(r => r.startTick)).toEqual([3840, 3840, 3840, 3840]);
    expect(current()?.regions.find(r => r.id === region.id)?.muted).toBe(true);
    // One undo step takes it all back.
    await server.undo();
    expect(current()?.tracks).toHaveLength(2);
    expect(current()?.regions.find(r => r.id === region.id)?.muted).toBe(false);

    // The model is there now; six stems need the other one; keep source leaves the region playing.
    const six = await server.splitStems(region.id, {
      stems: ['vocals', 'drums', 'bass', 'guitar', 'piano', 'other'],
      keepSource: true
    });
    server.runJobs(1);
    const downloading = events.filter(e => e.type === 'job' && e.job.id === six.id).at(-1);
    expect(downloading?.type === 'job' ? downloading.job.message : null).toMatch(/six-stem/);
    server.runJobs();
    expect(current()?.tracks).toHaveLength(8);
    expect(current()?.regions.find(r => r.id === region.id)?.muted).toBe(false);
    const again = await server.splitStems(region.id, { stems: ['drums'], keepSource: true });
    server.runJobs(1);
    const separating = events.filter(e => e.type === 'job' && e.job.id === again.id).at(-1);
    expect(separating?.type === 'job' ? separating.job.message : null).toBe('Separating stems…');
  });
});
