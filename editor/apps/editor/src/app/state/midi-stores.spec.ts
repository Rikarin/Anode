import { TestBed } from '@angular/core/testing';
import type { FakeAnodeServer } from '../core/fake/fake-server';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { InstrumentStore } from './instrument.store';
import { ActivityHoldMs, LiveMidiStore } from './live-midi.store';
import { PianoRollStore } from './piano-roll.store';
import { PluginsStore } from './plugins.store';
import { ProjectStore } from './project.store';
import { SelectionStore } from './selection.store';

/** A project with an audio track and an instrument track holding two MIDI regions; the store has it. */
async function open(server: FakeAnodeServer) {
  const project = TestBed.inject(ProjectStore);
  await server.createProject({ name: 'Song' });
  await settle();
  await project.execute(
    [
      { type: 'addTrack', stereo: false },
      { type: 'addTrack', stereo: true, kind: 'instrument' }
    ],
    'New Tracks'
  );
  const [audio = '', inst = ''] = project.trackIds();
  await project.execute(
    [
      { type: 'addMidiRegion', trackId: inst, startTick: 0, lengthTicks: 3840 },
      { type: 'addMidiRegion', trackId: inst, startTick: 7680, lengthTicks: 3840 }
    ],
    'Regions'
  );
  await settle();
  return { project, audio, inst, regions: [...project.midiRegions().keys()] };
}

describe('MIDI stores', () => {
  let server: FakeAnodeServer;
  beforeEach(() => {
    server = setUpFakeServer();
  });

  it('applies MIDI region deltas, and drops a removed track’s regions', async () => {
    const { project, inst, regions } = await open(server);
    expect(regions).toHaveLength(2);
    const [first = ''] = regions;
    await project.execute(
      [
        {
          type: 'addNotes',
          regionId: first,
          notes: [{ pitch: 60, velocity: 90, channel: 1, startTick: 0, lengthTicks: 480 }]
        }
      ],
      'Create Note'
    );
    expect(project.midiRegion(first)?.notes).toHaveLength(1);
    await project.execute([{ type: 'deleteRegions', regionIds: [first] }], 'Delete Region');
    expect(project.midiRegions().size).toBe(1);
    await project.undo();
    expect(project.midiRegions().size).toBe(2);
    await project.execute([{ type: 'removeTrack', trackId: inst }], 'Delete Track');
    expect(project.midiRegions().size).toBe(0);
    await project.refetch();
    expect(project.midiRegions().size).toBe(0);
  });

  it('shows the selected MIDI regions of one track in the Piano Roll, or all of the selected track’s', async () => {
    const { inst, audio, regions } = await open(server);
    const selection = TestBed.inject(SelectionStore);
    const roll = TestBed.inject(PianoRollStore);
    expect(roll.regions()).toEqual([]);
    selection.track.set(inst);
    expect(roll.regions().map(r => r.id)).toEqual(regions);
    selection.select([regions[1] ?? '']);
    expect(roll.regions().map(r => r.id)).toEqual([regions[1]]);
    selection.select([]);
    selection.track.set(audio);
    expect(roll.regions()).toEqual([]);
    expect(roll.track()).toBeNull();
  });

  it('keeps note selection to notes that exist', async () => {
    const { project, regions } = await open(server);
    const selection = TestBed.inject(SelectionStore);
    const [first = ''] = regions;
    await project.execute(
      [
        {
          type: 'addNotes',
          regionId: first,
          notes: [{ pitch: 60, velocity: 90, channel: 1, startTick: 0, lengthTicks: 480 }]
        }
      ],
      'Create Note'
    );
    const id = project.midiRegion(first)?.notes[0]?.id ?? -1;
    selection.selectNotes([id, 999_999]);
    TestBed.tick();
    expect([...selection.notes()]).toEqual([id]);
  });

  it('plays live MIDI on the selected instrument track, makes it the thru track, and lights what it hears', async () => {
    const { inst, audio } = await open(server);
    const live = TestBed.inject(LiveMidiStore);
    const selection = TestBed.inject(SelectionStore);
    selection.track.set(audio);
    TestBed.tick();
    expect(live.target()).toBeNull();
    live.noteOn(60);
    expect(server.liveMidi).toHaveLength(0);

    selection.track.set(inst);
    TestBed.tick();
    await Promise.resolve();
    expect(server.thruTrack).toBe(inst);
    expect(live.typeDown('KeyE')).toBe(true); // E♭ of octave 5: 63
    await Promise.resolve();
    expect(server.liveMidi.at(-1)).toEqual({ trackId: inst, events: [{ status: 0x90, data1: 63, data2: 100 }] });
    expect(live.sounding().has(63)).toBe(true);
    expect(live.isActive(inst)).toBe(true);
    live.typeUp('KeyE');
    await Promise.resolve();
    expect(server.liveMidi.at(-1)?.events).toEqual([{ status: 0x80, data1: 63, data2: 0 }]);
    expect(live.sounding().size).toBe(0);
    await new Promise(resolve => setTimeout(resolve, ActivityHoldMs + 20));
    expect(live.isActive(inst)).toBe(false);

    expect(live.typeDown('KeyX')).toBe(true);
    expect(live.octave()).toBe(6);
    expect(live.typeDown('KeyQ')).toBe(false);
  });

  it('sends held notes off on the track they started on when the selection moves', async () => {
    const { inst, audio } = await open(server);
    const live = TestBed.inject(LiveMidiStore);
    const selection = TestBed.inject(SelectionStore);
    selection.track.set(inst);
    TestBed.tick();
    live.noteOn(72, 80);
    selection.track.set(audio);
    TestBed.tick();
    await settle();
    expect(server.liveMidi.at(-1)).toEqual({ trackId: inst, events: [{ status: 0x80, data1: 72, data2: 0 }] });
    expect(server.thruTrack).toBeNull();
  });

  it('fetches the catalogue and devices, and refetches when the Server says they changed', async () => {
    const plugins = TestBed.inject(PluginsStore);
    await settle();
    expect(plugins.instruments().map(p => p.id)).toContain('anode.sampler');
    expect(plugins.devices().inputs.length).toBeGreaterThan(0);
    server.setMidiDevices({ inputs: [{ id: 'in:new', name: 'New keyboard' }], outputs: [] });
    await settle();
    expect(plugins.devices().inputs).toEqual([{ id: 'in:new', name: 'New keyboard' }]);
    await plugins.rescan();
    expect(plugins.scanning()).toBe(true);
    await settle();
    expect(plugins.scanning()).toBe(false);
  });

  it('shows a parameter drag at once and sends one setParameter at a time', async () => {
    const { project, inst } = await open(server);
    const instruments = TestBed.inject(InstrumentStore);
    TestBed.inject(PluginsStore);
    await settle();
    expect(instruments.value(inst, 'filter.cutoff')).toBe(2400);
    const sent = server.requests.length;
    void instruments.set(inst, 'filter.cutoff', 1000);
    void instruments.set(inst, 'filter.cutoff', 800);
    void instruments.set(inst, 'filter.cutoff', 500);
    expect(instruments.value(inst, 'filter.cutoff')).toBe(500);
    await settle();
    expect(server.requests.slice(sent).filter(r => r === 'ApplyCommands')).toHaveLength(2);
    expect(project.track(inst)?.instrument?.parameters['filter.cutoff']).toBe(500);
    expect(instruments.value(inst, 'filter.cutoff')).toBe(500);
  });
});
