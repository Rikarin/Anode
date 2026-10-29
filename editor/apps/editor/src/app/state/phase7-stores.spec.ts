import { TestBed } from '@angular/core/testing';
import { EventStream } from '../core/events';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { MidiLibraryStore } from './midi-library.store';
import { ProjectStore } from './project.store';
import { ScriptLogStore } from './script-log.store';
import { SelectionStore } from './selection.store';
import { StepInputStore } from './step-input.store';

async function openProject() {
  const server = setUpFakeServer();
  const store = TestBed.inject(ProjectStore);
  store.load(await server.createProject({ name: 'Phase 7' }));
  await store.execute([{ type: 'addTrack', kind: 'instrument', stereo: false }], 'Track');
  const trackId = store.trackIds()[0] ?? '';
  await store.execute([{ type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: 3840 }], 'Region');
  const regionId = [...store.midiRegions().keys()][0] ?? '';
  return { server, store, trackId, regionId };
}

describe('Phase 7 stores', () => {
  it('keeps grooves, articulation sets and lane sets from deltas and snapshots', async () => {
    const { store, regionId, trackId } = await openProject();
    await store.execute(
      [
        {
          type: 'addNotes',
          regionId,
          notes: [{ pitch: 36, velocity: 100, channel: 1, startTick: 0, lengthTicks: 120 }]
        },
        { type: 'makeGroove', regionId, name: 'Beat' },
        {
          type: 'setArticulationSet',
          set: { id: 'ars_1', name: 'Strings', articulations: [{ id: 1, name: 'Legato' }], switches: [], outputs: [] }
        },
        { type: 'assignArticulationSet', trackId, setId: 'ars_1' },
        { type: 'setLaneSet', set: { id: 'lns_1', name: 'Mine', lanes: [] } }
      ],
      'Phase 7'
    );
    expect(store.grooveList().map(g => g.name)).toEqual(['Beat']);
    expect(store.articulationSets().get('ars_1')?.name).toBe('Strings');
    expect(store.track(trackId)?.articulationSetId).toBe('ars_1');
    expect(store.laneSetList().map(l => l.id)).toEqual(['lns_1']);
    await store.undo();
    expect(store.grooves().size).toBe(0);
    expect(store.articulationSets().size).toBe(0);
    expect(store.laneSets().size).toBe(0);
    await store.redo();
    await store.refetch();
    expect(store.grooves().size).toBe(1);
    expect(store.laneSets().size).toBe(1);
  });

  it('follows the step input state and enters steps, rests and ties', async () => {
    const { store, trackId } = await openProject();
    TestBed.inject(EventStream).start();
    const steps = TestBed.inject(StepInputStore);
    await steps.toggle(true, { trackId, positionTick: 0 });
    expect(steps.state()).toMatchObject({ on: true, trackId, positionTick: 0 });
    await steps.set({ noteValueTicks: 480, dotted: true });
    expect(steps.stepTicks()).toBe(720);
    await steps.step([60, 64]);
    await settle();
    expect(steps.state().positionTick).toBe(720);
    await steps.rest();
    expect(steps.state().positionTick).toBe(1440);
    await steps.step([67]);
    await steps.tie();
    await settle();
    expect(steps.state().positionTick).toBe(2880);
    const region = [...store.midiRegions().values()].find(r => r.id === steps.state().regionId);
    expect(region?.notes.map(n => [n.pitch, n.startTick, n.lengthTicks])).toEqual([
      [60, 0, 720],
      [64, 0, 720],
      [67, 1440, 1440]
    ]);
  });

  it('collects a Scripter slot’s console and parameters', async () => {
    const { server, trackId } = await openProject();
    TestBed.inject(EventStream).start();
    const log = TestBed.inject(ScriptLogStore);
    const store = TestBed.inject(ProjectStore);
    await store.execute([{ type: 'addInsert', pluginId: 'anode.scripter', trackId, chain: 'midiEffects' }], 'Scripter');
    const slotId = store.track(trackId)?.strip.midiEffects[0]?.id ?? '';
    await store.execute(
      [
        {
          type: 'setScript',
          trackId,
          slotId,
          source: "var PluginParameters = [{ name: 'Gate', type: 'checkbox' }];\nTrace('one'); Trace('two');"
        }
      ],
      'Script'
    );
    await settle();
    expect(log.linesOf(trackId, slotId).map(l => l.text)).toEqual(['one', 'two']);
    expect(
      log
        .parameters()
        .get(`${trackId}/${slotId}`)
        ?.map(p => p.name)
    ).toEqual(['Gate']);
    await store.execute([{ type: 'setScript', trackId, slotId, source: 'while (true) {}' }], 'Script');
    await settle();
    expect(log.linesOf(trackId, slotId).at(-1)?.kind).toBe('problem');
    log.clear(trackId, slotId);
    expect(log.linesOf(trackId, slotId)).toEqual([]);
    expect(server.requests).toContain('ApplyCommands');
  });

  it('loads and saves the library, factory Transform presets first', async () => {
    await openProject();
    const library = TestBed.inject(MidiLibraryStore);
    await library.loadTransformSets();
    expect(library.transformSets()[0]?.id).toBe('factory.humanize');
    const preset = library.transformSets()[0];
    expect(preset).toBeDefined();
    if (preset) {
      expect(await library.saveTransformSet({ ...preset, id: 'trs_1', name: 'My Humanize' })).toBe(true);
      expect(library.transformSets().some(s => s.id === 'trs_1')).toBe(true);
      expect(await library.saveTransformSet(preset)).toBe(false);
    }
    expect(
      await library.saveGroove({
        id: 'grv_9',
        name: 'G',
        lengthTicks: 240,
        stepTicks: 240,
        points: [{ tick: 10, velocityScale: 1 }]
      })
    ).toBe(true);
    expect(library.grooves().map(g => g.id)).toEqual(['grv_9']);
    expect(await library.deleteGroove('grv_9')).toBe(true);
    expect(library.grooves()).toEqual([]);
  });

  it('drops controller events that leave the project from the selection', async () => {
    const { store, regionId } = await openProject();
    await store.execute(
      [
        { type: 'addControls', regionId, events: [{ kind: 'controlChange', channel: 1, number: 1, value: 3, tick: 0 }] }
      ],
      'CC'
    );
    const selection = TestBed.inject(SelectionStore);
    const id = store.midiRegion(regionId)?.controls[0]?.id ?? 0;
    selection.selectControls([id]);
    selection.toggleControl(id + 1000);
    expect(selection.controls().size).toBe(2);
    await store.execute([{ type: 'deleteControls', regionId, eventIds: [id] }], 'Delete');
    TestBed.tick();
    expect(selection.controls().size).toBe(0);
  });
});
