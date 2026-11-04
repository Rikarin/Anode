import { TestBed } from '@angular/core/testing';
import type { AutomationPoint, EditCommand } from '@anode/api-client';
import { AutomationStore } from '../automation/automation.store';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { Notify } from '../core/notify';
import { PluginsStore } from './plugins.store';
import { ProjectStore } from './project.store';
import { RegionClipboardStore } from './region-clipboard.store';
import { SelectionStore } from './selection.store';
import { TracksViewStore } from './tracks-view.store';
import { TransportStore } from './transport.store';

const Bar = 3840;
const p = (tick: number, value: number): AutomationPoint => ({ tick, value, curve: 0 });

/** A project of four tracks: two instrument tracks, an audio track, an instrument track. */
async function setUp() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Song' }));
  await settle();
  await TestBed.inject(PluginsStore).refetchPlugins();
  const file = await server.importAudioFile('/audio/take.wav');
  project.upsertFile(file);
  await project.execute(
    [
      { type: 'addTrack', stereo: false, kind: 'instrument' },
      { type: 'addTrack', stereo: false, kind: 'instrument' },
      { type: 'addTrack', stereo: false },
      { type: 'addTrack', stereo: false, kind: 'instrument' }
    ],
    'Tracks'
  );
  const [keys = '', pad = '', audio = '', lead = ''] = project.trackIds();
  const selection = TestBed.inject(SelectionStore);
  const run = (...commands: EditCommand[]) => project.execute(commands, 'Test');
  const midiId = (trackId: string, startTick: number) =>
    [...project.midiRegions().values()].find(r => r.trackId === trackId && r.startTick === startTick)?.id ?? '';
  return {
    server,
    project,
    selection,
    file,
    keys,
    pad,
    audio,
    lead,
    run,
    midiId,
    store: TestBed.inject(RegionClipboardStore),
    automation: TestBed.inject(AutomationStore),
    view: TestBed.inject(TracksViewStore),
    notify: TestBed.inject(Notify),
    at: (tick: number) => TestBed.inject(TransportStore).anchorTicks.set(tick)
  };
}

describe('RegionClipboardStore', () => {
  it('pastes a copy at the playhead on the selected track, with its automation, in one undo step', async () => {
    const { project, server, selection, store, keys, pad, run, midiId, at } = await setUp();
    await run(
      { type: 'addMidiRegion', trackId: keys, startTick: Bar, lengthTicks: Bar },
      { type: 'setAutomation', trackId: keys, target: 'volume', points: [p(0, -12), p(Bar + 960, -6), p(4 * Bar, 0)] }
    );
    const id = midiId(keys, Bar);
    await run({
      type: 'addNotes',
      regionId: id,
      notes: [{ pitch: 60, velocity: 100, channel: 1, startTick: 0, lengthTicks: 480 }]
    });

    selection.select([id]);
    expect(store.canPaste()).toBe(false);
    expect(store.copy()).toBe(true);
    expect(project.midiRegions().size).toBe(1); // copying changes nothing
    selection.selectTrack(pad);
    at(8 * Bar);
    const undoBefore = server.current?.revision;
    await store.paste();

    const pasted = midiId(pad, 8 * Bar);
    expect(project.midiRegion(pasted)?.notes.map(n => n.pitch)).toEqual([60]);
    expect(project.midiRegion(pasted)?.notes[0]?.id).not.toBe(project.midiRegion(id)?.notes[0]?.id);
    expect([...selection.regions()]).toEqual([pasted]);
    expect(project.automationLane(pad, 'volume')?.points.map(q => q.tick)).toEqual([8 * Bar, 8 * Bar + 960, 9 * Bar]);
    expect(server.current?.revision).toBe((undoBefore ?? 0) + 1);

    await project.undo();
    expect(project.midiRegions().size).toBe(1);
    expect(project.automationLane(pad, 'volume')).toBeUndefined();
  });

  it('cuts regions of several tracks with their automation, and pastes them across tracks', async () => {
    const { project, selection, store, keys, pad, audio, lead, run, midiId, at } = await setUp();
    await run(
      { type: 'addMidiRegion', trackId: keys, startTick: Bar, lengthTicks: Bar },
      { type: 'addMidiRegion', trackId: pad, startTick: 2 * Bar, lengthTicks: Bar },
      { type: 'setAutomation', trackId: keys, target: 'volume', points: [p(0, -12), p(Bar + 960, -6), p(4 * Bar, 0)] }
    );
    const ids = [midiId(keys, Bar), midiId(pad, 2 * Bar)];
    selection.select(ids);
    store.cut();
    await settle();
    expect(project.midiRegions().size).toBe(0);
    expect(selection.regions().size).toBe(0);
    const lane = project.automationLane(keys, 'volume')?.points ?? [];
    expect(lane.map(q => q.tick)).toEqual([0, Bar, 2 * Bar, 4 * Bar]);

    await project.undo();
    expect(project.midiRegions().size).toBe(2);
    expect(project.automationLane(keys, 'volume')?.points).toHaveLength(3);
    await project.redo();

    // The audio track under the pad cannot hold MIDI: the second track moves on down to the next that can.
    selection.selectTrack(pad);
    at(5 * Bar);
    await store.paste();
    expect(midiId(pad, 5 * Bar)).not.toBe('');
    expect(midiId(lead, 6 * Bar)).not.toBe('');
    expect([...project.midiRegions().values()].filter(r => r.trackId === audio)).toEqual([]);
    expect(selection.regions().size).toBe(2);
  });

  it('refuses a paste on a track that cannot hold the region, and keeps the clipboard', async () => {
    const { project, selection, store, keys, audio, run, midiId, notify } = await setUp();
    await run({ type: 'addMidiRegion', trackId: keys, startTick: 0, lengthTicks: Bar });
    selection.select([midiId(keys, 0)]);
    store.copy();
    selection.selectTrack(audio);
    await store.paste();
    expect(project.midiRegions().size).toBe(1);
    expect(notify.last()).toContain('cannot hold');
    expect(store.canPaste()).toBe(true);
  });

  it('copies audio regions and take folders whole', async () => {
    const { project, selection, store, audio, file, run, at } = await setUp();
    await run({ type: 'addRegion', trackId: audio, fileId: file.id, startTick: 0, length: 48_000 });
    await run({ type: 'addRegion', trackId: audio, fileId: file.id, startTick: 0, length: 48_000 });
    await run({ type: 'packTakeFolder', regionIds: [...project.regions().keys()] });
    const folder = [...project.takeFolders().values()][0];
    await run({ type: 'addRegion', trackId: audio, fileId: file.id, startTick: 4 * Bar, length: 48_000 });
    selection.select([folder?.id ?? '', ...project.regions().keys()]);
    selection.selectTrack(audio);
    store.copy();
    at(8 * Bar);
    await store.paste();
    const folders = [...project.takeFolders().values()];
    const copy = folders.find(f => f.id !== folder?.id);
    expect(copy?.startTick).toBe(8 * Bar);
    expect(copy?.comp[0]?.startTick).toBe(8 * Bar);
    expect(copy?.takes.map(t => t.id)).not.toEqual(folder?.takes.map(t => t.id));
    expect([...project.regions().values()].map(r => r.startTick).sort((a, b) => a - b)).toEqual([4 * Bar, 12 * Bar]);
  });

  it('leaves out automation of a target the other track does not have', async () => {
    const { project, selection, store, keys, pad, run, midiId, notify, at } = await setUp();
    await run({ type: 'addInsert', trackId: keys, pluginId: 'anode.compressor' });
    const insert = project.track(keys)?.strip.inserts[0]?.id ?? '';
    const parameter = TestBed.inject(PluginsStore).plugin('anode.compressor')?.parameters[0];
    const target = `insert:${insert}:${parameter?.id ?? ''}`;
    await run(
      { type: 'addMidiRegion', trackId: keys, startTick: 0, lengthTicks: Bar },
      { type: 'setAutomation', trackId: keys, target, points: [p(0, parameter?.min ?? 0), p(960, parameter?.max ?? 1)] }
    );
    selection.select([midiId(keys, 0)]);
    store.copy();
    selection.selectTrack(pad);
    at(4 * Bar);
    await store.paste();
    expect(midiId(pad, 4 * Bar)).not.toBe('');
    expect(project.automationList().filter(l => l.trackId === pad)).toEqual([]);
    expect(notify.last()).toContain('automation lane');

    // On its own track it goes along.
    selection.selectTrack(keys);
    await store.paste();
    expect(project.automationLane(keys, target)?.points.at(-1)?.tick).toBeGreaterThanOrEqual(4 * Bar);
  });

  it('copies a selected range of a lane, and cuts it, without regions', async () => {
    const { project, selection, store, automation, keys, pad, run, at } = await setUp();
    await run({ type: 'setAutomation', trackId: keys, target: 'pan', points: [p(0, -1), p(Bar, 0), p(2 * Bar, 1)] });
    automation.selectRange(keys, 'pan', Bar / 2, Bar + Bar / 2, [1]);
    expect(store.copy()).toBe(true);
    selection.selectTrack(pad);
    at(4 * Bar);
    await store.paste();
    expect(project.automationLane(pad, 'pan')?.points.map(q => q.tick)).toEqual([4 * Bar, 4 * Bar + Bar / 2, 5 * Bar]);

    automation.selectPoints(keys, 'pan', [1]);
    store.cut();
    await settle();
    expect(project.automationLane(keys, 'pan')?.points.map(q => q.tick)).toEqual([0, 2 * Bar]);
    expect(store.canPaste()).toBe(true);
  });

  it('copies what a marquee holds, and cutting it trims the regions across its edges', async () => {
    const { project, selection, store, view, keys, pad, run, midiId, at } = await setUp();
    await run({ type: 'addMidiRegion', trackId: keys, startTick: 0, lengthTicks: 4 * Bar });
    await run({
      type: 'addNotes',
      regionId: midiId(keys, 0),
      notes: [
        { pitch: 60, velocity: 100, channel: 1, startTick: 0, lengthTicks: 480 },
        { pitch: 62, velocity: 100, channel: 1, startTick: Bar + 960, lengthTicks: 480 }
      ]
    });
    view.marquee.set({ startTick: Bar, endTick: 2 * Bar, laneFrom: 0, laneTo: 0 });
    store.cut();
    await settle();
    expect(view.marquee()).toBeNull();
    expect(
      [...project.midiRegions().values()]
        .map(r => [r.startTick, r.lengthTicks])
        .sort((a, b) => (a[0] ?? 0) - (b[0] ?? 0))
    ).toEqual([
      [0, Bar],
      [2 * Bar, 2 * Bar]
    ]);

    selection.selectTrack(pad);
    at(8 * Bar);
    await store.paste();
    const pasted = project.midiRegion(midiId(pad, 8 * Bar));
    expect(pasted?.lengthTicks).toBe(Bar);
    expect(pasted?.notes.map(n => [n.pitch, n.startTick])).toEqual([[62, 960]]);
  });

  it('holds nothing for another project', async () => {
    const { server, project, selection, store, keys, run, midiId } = await setUp();
    await run({ type: 'addMidiRegion', trackId: keys, startTick: 0, lengthTicks: Bar });
    selection.select([midiId(keys, 0)]);
    store.copy();
    expect(store.canPaste()).toBe(true);
    project.load(await server.createProject({ name: 'Other' }));
    expect(store.canPaste()).toBe(false);
  });

  it('does nothing with nothing selected', async () => {
    const { store } = await setUp();
    expect(store.copy()).toBe(false);
    store.cut();
    await store.paste();
    expect(store.canPaste()).toBe(false);
  });
});
