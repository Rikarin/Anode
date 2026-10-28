import { TestBed } from '@angular/core/testing';
import { CrashyPluginId } from '../core/fake/fake-plugins';
import type { FakeAnodeServer } from '../core/fake/fake-server';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { Notify } from '../core/notify';
import { labelRows } from '../mixer/io-labels-dialog';
import { PluginWindows } from '../mixer/plugin-windows';
import { IoLabelsStore } from './io-labels.store';
import { JobsStore } from './jobs.store';
import { MeterBus } from './meters';
import { MixerStore } from './mixer.store';
import { PatchesStore, patchCategories, patchFits } from './patches.store';
import { PluginsStore } from './plugins.store';
import { ProjectStore } from './project.store';
import { TransportStore } from './transport.store';
import { UiStore } from './ui.store';

async function open(server: FakeAnodeServer) {
  const project = TestBed.inject(ProjectStore);
  await server.createProject({ name: 'Mix' });
  await settle();
  await TestBed.inject(PluginsStore).refetchPlugins();
  await project.execute(
    [
      { type: 'addTrack', stereo: false },
      { type: 'addTrack', stereo: true, kind: 'instrument' }
    ],
    'New Tracks'
  );
  const [a = '', b = ''] = project.trackIds();
  return { project, a, b, mixer: TestBed.inject(MixerStore) };
}

describe('MixerStore', () => {
  let server: FakeAnodeServer;
  beforeEach(() => {
    server = setUpFakeServer();
  });

  it('adds, moves, bypasses, replaces and removes inserts', async () => {
    const { project, a, mixer } = await open(server);
    await mixer.addInsert(a, 'anode.channel-eq');
    await mixer.addInsert(a, 'anode.compressor');
    const names = () => project.track(a)?.strip.inserts.map(i => i.pluginId);
    expect(names()).toEqual(['anode.channel-eq', 'anode.compressor']);

    const [eq, comp] = project.track(a)?.strip.inserts ?? [];
    await mixer.moveInsert(a, eq?.id ?? '', 1);
    expect(names()).toEqual(['anode.compressor', 'anode.channel-eq']);
    if (comp) {
      await mixer.toggleBypass(a, comp);
    }
    expect(project.track(a)?.strip.inserts[0]?.bypassed).toBe(true);

    const first = project.track(a)?.strip.inserts[0];
    if (first) {
      await mixer.replaceInsert(a, first, 'anode.limiter', 0);
    }
    expect(names()).toEqual(['anode.limiter', 'anode.channel-eq']);

    // The master has inserts too.
    await mixer.addInsert(null, 'anode.limiter');
    expect(project.master().inserts.map(i => i.pluginId)).toEqual(['anode.limiter']);
  });

  it('shows a dragged parameter at once and sends it coalesced, one undo step per drag', async () => {
    const { project, a, mixer } = await open(server);
    await mixer.addInsert(a, 'anode.compressor');
    const insert = project.track(a)?.strip.inserts[0];
    expect(insert).toBeDefined();
    if (!insert) {
      return;
    }
    const sent = mixer.setParameter(a, insert.id, 'threshold', -30);
    expect(mixer.parameterValue(a, insert, 'threshold')).toBe(-30);
    void mixer.setParameter(a, insert.id, 'threshold', -32);
    await sent;
    await settle();
    expect(project.track(a)?.strip.inserts[0]?.parameters['threshold']).toBe(-32);
    const history = await server.history();
    expect(history.undo.filter(step => step.label === 'Threshold')).toHaveLength(1);
  });

  it('adds sends, sets their level, pre/post and bus, and routes to a new aux in one step', async () => {
    const { project, a, mixer } = await open(server);
    await mixer.addSendToNewAux(a, 1);
    const aux = project.trackList().find(t => t.kind === 'aux');
    expect(aux?.inputBus).toBe(1);
    const send = project.track(a)?.strip.sends[0];
    expect(send).toMatchObject({ bus: 1, levelDb: 0, preFader: false });
    if (!send) {
      return;
    }
    await mixer.setSendLevel(a, send.id, -12);
    await settle();
    expect(project.track(a)?.strip.sends[0]?.levelDb).toBe(-12);
    await mixer.togglePreFader(a, send);
    expect(project.track(a)?.strip.sends[0]?.preFader).toBe(true);
    await mixer.setSendBus(a, send, 4);
    expect(project.track(a)?.strip.sends[0]?.bus).toBe(4);
    await mixer.removeSend(a, send);
    expect(project.track(a)?.strip.sends).toEqual([]);
  });

  it('refuses an output that would loop (the Server says why), and routes the rest', async () => {
    const { project, a, mixer } = await open(server);
    await mixer.addAux(1);
    const aux = project.trackList().find(t => t.kind === 'aux');
    await mixer.setOutput(a, 'bus:1');
    expect(project.track(a)?.strip.output).toBe('bus:1');
    await mixer.setOutput(aux?.id ?? '', 'bus:1');
    expect(project.track(aux?.id ?? '')?.strip.output).toBe('master');
    expect(TestBed.inject(Notify).last()).toMatch(/loop/i);
  });

  it('makes groups with Logic’s defaults and assigns VCA strips', async () => {
    const { project, a, b, mixer } = await open(server);
    const number = await mixer.newGroup(a);
    expect(number).toBe(1);
    expect(project.groups().get(1)).toMatchObject({ name: 'Group 1', enabled: true });
    expect(project.track(a)?.strip.groups).toEqual([1]);
    await mixer.setTrackGroups(b, [1]);
    expect(mixer.groupMembers(1).map(t => t.id)).toEqual([a, b]);
    const group = project.groups().get(1);
    if (group) {
      await mixer.setGroup(group, { name: 'Band', properties: { ...group.properties, pan: true } });
    }
    expect(project.groups().get(1)).toMatchObject({ name: 'Band', properties: { pan: true } });
    expect(mixer.nextGroupNumber()).toBe(2);

    const result = await mixer.addVca('Drums VCA');
    const vca = project.trackList().find(t => t.kind === 'vca');
    expect(result).not.toBeNull();
    await mixer.assignVca(a, vca?.id ?? null);
    expect(project.track(a)?.strip.vcaId).toBe(vca?.id);

    await mixer.deleteGroup(1);
    expect(project.groups().size).toBe(0);
    expect(project.track(a)?.strip.groups).toEqual([]);
  });

  it('shows the automation while playing in read, and the held value while a control moves', async () => {
    const { project, a, mixer } = await open(server);
    await project.execute(
      [
        {
          type: 'setAutomation',
          trackId: a,
          target: 'volume',
          points: [
            { tick: 0, value: -20, curve: 0 },
            { tick: 960 * 100, value: -20, curve: 0 }
          ]
        }
      ],
      'Automation'
    );
    expect(mixer.value(a, 'volumeDb')).toBe(0);
    TestBed.inject(TransportStore).state.set('playing');
    expect(mixer.value(a, 'volumeDb')).toBe(-20);
    void mixer.set(a, 'volumeDb', -3);
    expect(mixer.value(a, 'volumeDb')).toBe(-3);
    TestBed.inject(TransportStore).state.set('stopped');
  });

  it('sets the mixer settings', async () => {
    const { project, mixer } = await open(server);
    await mixer.setMixerSettings({ pdc: 'tracks', lowLatency: true });
    expect(project.settings().mixer).toMatchObject({ pdc: 'tracks', lowLatency: true });
  });
});

describe('JobsStore', () => {
  let server: FakeAnodeServer;
  beforeEach(() => {
    server = setUpFakeServer();
  });

  it('follows a bounce to done and says where the file went', async () => {
    await open(server);
    const jobs = TestBed.inject(JobsStore);
    const job = await jobs.bounce({
      fileName: 'Mix',
      source: 'mix',
      format: 'wav',
      bitDepth: 24,
      dither: 'tpdf',
      normalize: 'loudness',
      loudnessLufs: -14,
      peakCeilingDb: -1,
      realtime: false,
      addToProject: false
    });
    expect(job?.state).toMatch(/queued|running/);
    expect(jobs.active()).toHaveLength(1);
    server.runJobs();
    await settle();
    expect(jobs.bounceJob()?.state).toBe('done');
    expect(jobs.bounceJob()?.files[0]).toMatch(/Mix\.wav$/);
    expect(TestBed.inject(Notify).last()).toBe('Bounced Mix.wav');
    expect(jobs.active()).toHaveLength(0);
  });

  it('freezes a track through a job, then unfreezes it', async () => {
    const { project, b } = await open(server);
    const jobs = TestBed.inject(JobsStore);
    await jobs.freeze(b);
    expect(jobs.freezeOf(b)?.kind).toBe('freeze');
    server.runJobs();
    await settle();
    expect(project.track(b)?.frozenFileId).toBeTruthy();
    await jobs.unfreeze(b);
    expect(project.track(b)?.frozenFileId).toBeNull();
  });

  it('cancels a job', async () => {
    await open(server);
    const jobs = TestBed.inject(JobsStore);
    const job = await jobs.bounce({
      fileName: 'Long',
      source: 'mix',
      format: 'aiff',
      bitDepth: 16,
      dither: 'shaped',
      normalize: 'off',
      loudnessLufs: -14,
      peakCeilingDb: -1,
      realtime: true,
      addToProject: false
    });
    await jobs.cancel(job?.id ?? '');
    expect(jobs.jobs().get(job?.id ?? '')?.state).toBe('cancelled');
  });
});

describe('patches, I/O labels and plugin windows', () => {
  let server: FakeAnodeServer;
  beforeEach(() => {
    server = setUpFakeServer();
  });

  it('saves a track’s strip as a patch and applies it to another', async () => {
    const { project, a, mixer } = await open(server);
    await mixer.addInsert(a, 'anode.compressor');
    await project.execute([{ type: 'addTrack', stereo: false }], 'New Track');
    const c = project.trackIds().at(-1) ?? '';
    const patches = TestBed.inject(PatchesStore);
    const patch = await patches.save(a, 'Vox', 'Vocals');
    expect(patch).toMatchObject({ name: 'Vox', category: 'Vocals', kind: 'audio' });
    if (patch) {
      await patches.apply(c, patch);
      await patches.remove(patch);
    }
    expect(project.track(c)?.strip.inserts.map(i => i.pluginId)).toEqual(['anode.compressor']);
    expect(patches.patches()).toEqual([]);
  });

  it('groups patches by category and fits them to track kinds', () => {
    const p = (name: string, category: string, kind: 'audio' | 'instrument') => ({ id: name, name, category, kind });
    expect(
      patchCategories([p('b', 'Vocals', 'audio'), p('a', 'Vocals', 'audio'), p('c', '', 'audio')]).map(c => [
        c.name,
        c.patches.map(x => x.name)
      ])
    ).toEqual([
      ['Uncategorised', ['c']],
      ['Vocals', ['a', 'b']]
    ]);
    expect(patchFits(p('x', 'Keys', 'instrument'), 'instrument')).toBe(true);
    expect(patchFits(p('x', 'Vox', 'audio'), 'aux')).toBe(true);
    expect(patchFits(p('x', 'Vox', 'audio'), 'instrument')).toBe(false);
  });

  it('saves I/O labels and lists the rows of each table', async () => {
    await open(server);
    const labels = TestBed.inject(IoLabelsStore);
    expect(await labels.save({ inputs: [], outputs: [], buses: [{ number: 3, name: ' Drums ' }] })).toBe(true);
    expect(labels.labels().buses).toEqual([{ number: 3, name: 'Drums' }]);
    const rows = labelRows('buses', labels.labels());
    expect(rows).toHaveLength(64);
    expect(rows[2]).toEqual({ number: 3, channel: 'Bus 3', name: 'Drums' });
    expect(labelRows('outputs', labels.labels())[1]?.channel).toBe('Output 3-4');
  });

  it('opens the generic editor for a plugin without its own, and reports a crashed plugin', async () => {
    const { project, a, mixer } = await open(server);
    const windows = TestBed.inject(PluginWindows);
    await mixer.addInsert(a, 'anode.compressor');
    const insert = project.track(a)?.strip.inserts[0];
    if (insert) {
      await windows.open(a, insert);
    }
    expect(TestBed.inject(UiStore).pluginWindows()).toEqual([{ trackId: a, insertId: insert?.id }]);

    await mixer.addInsert(a, CrashyPluginId);
    TestBed.inject(TransportStore).play();
    await settle();
    const crashy = project.track(a)?.strip.inserts[1];
    expect(windows.hasProblem(a, crashy?.id ?? '')).toBe(true);
    expect(TestBed.inject(Notify).last()).toMatch(/bypassed/);
  });

  it('keeps the readings of the latest meters frame', () => {
    const bus = TestBed.inject(MeterBus);
    bus.applyReadings([{ trackId: 't', insertId: 'i', value: -4 }]);
    expect(bus.reading('t', 'i')).toBe(-4);
    bus.applyReadings([]);
    expect(bus.reading('t', 'i')).toBeNull();
  });
});
