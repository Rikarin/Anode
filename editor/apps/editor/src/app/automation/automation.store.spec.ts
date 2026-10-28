import { TestBed } from '@angular/core/testing';
import type { FakeAnodeServer } from '../core/fake/fake-server';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { automationRowId } from '../state/lanes';
import { LanesStore } from '../state/lanes.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TransportStore } from '../state/transport.store';
import { AutomationStore } from './automation.store';

async function open(server: FakeAnodeServer) {
  const project = TestBed.inject(ProjectStore);
  await server.createProject({ name: 'Mix' });
  await settle();
  await project.execute(
    [
      { type: 'addTrack', stereo: false },
      { type: 'addTrack', stereo: false }
    ],
    'New Tracks'
  );
  const [a = '', b = ''] = project.trackIds();
  return { project, a, b, automation: TestBed.inject(AutomationStore) };
}

describe('AutomationStore', () => {
  let server: FakeAnodeServer;
  beforeEach(() => {
    server = setUpFakeServer();
  });

  it('adds sub-lane rows under each track while automation shows', async () => {
    const { a, b, automation } = await open(server);
    const lanes = TestBed.inject(LanesStore);
    expect(lanes.rows().map(r => r.id)).toEqual([a, b]);
    automation.toggleShown();
    expect(lanes.rows().map(r => r.id)).toEqual([a, automationRowId(a, 'volume'), b, automationRowId(b, 'volume')]);
    automation.addLane(a);
    expect(automation.targetsShown(a)).toEqual(['volume', 'pan']);
    automation.setLaneTarget(a, 'pan', 'mute');
    expect(automation.targetsShown(a)).toEqual(['volume', 'mute']);
    automation.removeLane(a, 'volume');
    expect(
      lanes
        .rows()
        .filter(r => r.kind === 'automation' && r.track.id === a)
        .map(r => r.id)
    ).toEqual([automationRowId(a, 'mute')]);
    automation.toggleShown();
    expect(lanes.count()).toBe(2);
  });

  it('commits a lane as one undo step and shows the targets that have lanes', async () => {
    const { project, a, automation } = await open(server);
    await automation.commit(a, 'pan', [
      { tick: 0, value: -1, curve: 0 },
      { tick: 960, value: 1, curve: 0.5 }
    ]);
    expect(project.automationLane(a, 'pan')?.points).toHaveLength(2);
    expect(automation.targetsShown(a)).toEqual(['volume', 'pan']);
    expect(automation.valueAt(a, 'pan', 0)).toBe(-1);
    expect(automation.valueAt(a, 'pan', 5_000)).toBe(1);
    expect((await server.history()).undo[0]?.label).toBe('Automation: Pan');
    await automation.commit(a, 'pan', []);
    expect(project.automationLane(a, 'pan')).toBeUndefined();
  });

  it('deletes the selected points, or the selected range keeping its edges', async () => {
    const { project, a, automation } = await open(server);
    const points = [
      { tick: 0, value: 0, curve: 0 },
      { tick: 960, value: -12, curve: 0 },
      { tick: 1920, value: 0, curve: 0 }
    ];
    await automation.commit(a, 'volume', points);
    expect(automation.deleteSelection()).toBe(false);
    automation.selectPoints(a, 'volume', [1]);
    expect(automation.deleteSelection()).toBe(true);
    await settle();
    expect(project.automationLane(a, 'volume')?.points.map(p => p.tick)).toEqual([0, 1920]);

    await automation.commit(a, 'volume', points);
    automation.selectRange(a, 'volume', 1440, 480, [1]);
    expect(automation.selection()?.range).toEqual({ startTick: 480, endTick: 1440 });
    automation.deleteSelection();
    await settle();
    expect(project.automationLane(a, 'volume')?.points.map(p => p.tick)).toEqual([0, 480, 1440, 1920]);
    expect(automation.selection()).toBeNull();
  });

  it('ends an automation selection when regions are selected', async () => {
    const { project, a, automation } = await open(server);
    await project.execute([{ type: 'addTrack', stereo: true, kind: 'instrument' }], 'Track');
    const inst = project.trackIds().at(-1) ?? '';
    await project.execute([{ type: 'addMidiRegion', trackId: inst, startTick: 0, lengthTicks: 960 }], 'Region');
    TestBed.tick();
    automation.selectPoints(a, 'volume', [0]);
    TestBed.tick();
    TestBed.inject(SelectionStore).select([...project.midiRegions().keys()]);
    TestBed.tick();
    expect(automation.selection()).toBeNull();
  });

  it('sets the automation mode', async () => {
    const { project, a, automation } = await open(server);
    await automation.setMode(a, 'touch');
    expect(project.track(a)?.automationMode).toBe('touch');
    expect(automation.mode(a)).toBe('touch');
    expect(automation.mode(null)).toBe('read');
  });

  it('gives a lane’s value at the playhead only while playing, and not in off', async () => {
    const { a, automation } = await open(server);
    await automation.commit(a, 'volume', [
      { tick: 0, value: -20, curve: 0 },
      { tick: 1920, value: 0, curve: 0 }
    ]);
    expect(automation.playbackValue(a, 'volume')).toBeUndefined();
    const transport = TestBed.inject(TransportStore);
    transport.applyStatus({ state: 'playing', positionTick: 960, positionSamples: 0, sampleRate: 48_000 });
    TestBed.tick();
    expect(automation.playheadTick()).toBeGreaterThanOrEqual(960);
    expect(automation.playbackValue(a, 'volume')).toBeCloseTo(-10, 0);
    expect(automation.playbackValue(a, 'pan')).toBeUndefined();
    await automation.setMode(a, 'off');
    expect(automation.playbackValue(a, 'volume')).toBeUndefined();
    transport.applyStatus({ state: 'stopped', positionTick: 0, positionSamples: 0, sampleRate: 48_000 });
    TestBed.tick();
    expect(automation.playbackValue(a, 'volume')).toBeUndefined();
  });

  it('lists a track’s targets and describes unknown ones', async () => {
    const { a, automation } = await open(server);
    expect(automation.targets(a).map(t => t.target)).toEqual(['volume', 'pan', 'mute']);
    expect(automation.targets(null).map(t => t.target)).toEqual(['volume', 'pan', 'mute']);
    expect(automation.target(a, 'insert:gone:x').name).toBe('insert:gone:x');
  });
});
