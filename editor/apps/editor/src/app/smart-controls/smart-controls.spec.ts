import { TestBed } from '@angular/core/testing';
import { AutomationStore } from '../automation/automation.store';
import { targetsOf } from '../automation/targets';
import type { FakeAnodeServer } from '../core/fake/fake-server';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { MixerStore } from '../state/mixer.store';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { mappingTargets } from './mapping-targets';
import { SmartControlsStore } from './smart-controls.store';
import { LinearGraph } from './smart-math';

/** A project with a synth track (the built-in synth, a compressor insert and an Arpeggiator MIDI FX slot). */
async function open(server: FakeAnodeServer, extras = true) {
  const project = TestBed.inject(ProjectStore);
  await server.createProject({ name: 'Smart' });
  await settle();
  await TestBed.inject(PluginsStore).refetchPlugins();
  await project.execute([{ type: 'addTrack', stereo: true, kind: 'instrument' }], 'New Track');
  const synth = project.trackIds()[0] ?? '';
  if (extras) {
    const mixer = TestBed.inject(MixerStore);
    await mixer.addInsert(synth, 'anode.compressor');
    await mixer.addInsert(synth, 'anode.arpeggiator', undefined, 'midiEffects');
  }
  return { project, synth, store: TestBed.inject(SmartControlsStore) };
}

describe('Smart Controls store', () => {
  let server: FakeAnodeServer;

  beforeEach(() => {
    server = setUpFakeServer();
  });

  it('shows null controls as 8 unmapped knobs, and automatic mapping names and maps them', async () => {
    const { project, synth, store } = await open(server);
    expect(project.track(synth)?.smartControls).toBeNull();
    expect(store.controls(synth).controls.filter(c => c.kind === 'knob')).toHaveLength(8);
    expect(store.canCompare(synth)).toBe(false);
    // Its first MIDI FX slot is an Arpeggiator: the button shows it on before the controls are set up.
    expect(store.controls(synth).arpeggiator).toBe(true);
    await store.autoMap(synth);
    const knobs = store.controls(synth).controls.filter(c => c.kind === 'knob');
    expect(knobs.every(k => k.name.length > 0 && k.mappings.length === 1)).toBe(true);
    expect(knobs[0]?.mappings[0]?.target).toBe('instrument:filter.cutoff');
    // One mapping: the knob reads in its parameter's unit.
    expect(store.format(synth, 'knob1', 1)).toMatch(/kHz|Hz/);
    expect(store.format(synth, 'switch1', 1)).toBe('On');
  });

  it('shows a dragged control at once and sends it coalesced, one undo step per drag', async () => {
    const { synth, store } = await open(server, false);
    const before = server.requests.length;
    const sent = store.set(synth, 'knob1', 0.4);
    expect(store.value(synth, 'knob1')).toBe(0.4);
    void store.set(synth, 'knob1', 0.5);
    void store.set(synth, 'knob1', 0.6);
    await sent;
    await settle();
    // The first value, then only the latest of those set while it was in flight.
    expect(server.requests.slice(before).filter(r => r === 'ApplyCommands')).toHaveLength(2);
    expect(store.control(synth, 'knob1')?.value).toBe(0.6);
    store.release(synth, 'knob1');
    const history = await server.history();
    expect(history.undo.filter(step => step.label === 'Knob 1')).toHaveLength(1);
  });

  it('lists mapping targets: the instrument, each insert, each MIDI FX slot, then Main', async () => {
    const { project, synth, store } = await open(server);
    const groups = store.targets(synth).map(g => g.group);
    expect(groups[0]).toBe('Instrument: Anode Synth');
    expect(groups).toContain('Compressor');
    expect(groups).toContain('MIDI FX: Arpeggiator');
    expect(groups[groups.length - 1]).toBe('Main');
    const main = store
      .targets(synth)
      .find(g => g.group === 'Main')
      ?.targets.map(t => t.target);
    expect(main).toEqual(['volume', 'pan']);
    // Twice the MIDI FX slot (when the automation list has it too) is still once.
    const track = project.track(synth);
    if (track) {
      const plugin = (id: string) => TestBed.inject(PluginsStore).plugin(id);
      const all = mappingTargets(track, plugin).flatMap(g => g.targets.map(t => t.target));
      expect(new Set(all).size).toBe(all.length);
    }
  });

  it('adds a mapping at its full range with the linear graph, edits, retargets and removes it', async () => {
    const { project, synth, store } = await open(server);
    await store.addMapping(synth, 'knob1', 'instrument:filter.cutoff');
    await store.addMapping(synth, 'knob1', 'volume');
    const mappings = () => project.track(synth)?.smartControls?.controls[0]?.mappings ?? [];
    expect(mappings()).toHaveLength(2);
    expect(mappings()[0]).toMatchObject({ min: 20, max: 20_000, invert: false, graph: LinearGraph });
    await store.updateMapping(synth, 'knob1', 1, {
      invert: true,
      graph: [
        { x: 0, y: 0 },
        { x: 0.5, y: 0.8 },
        { x: 1, y: 1 }
      ]
    });
    expect(mappings()[1]).toMatchObject({ target: 'volume', invert: true });
    expect(mappings()[1]?.graph).toHaveLength(3);
    // A graph the Server would refuse is not sent.
    const sent = server.requests.length;
    await store.updateMapping(synth, 'knob1', 1, { graph: [{ x: 0.2, y: 0 }] });
    expect(server.requests.length).toBe(sent);
    await store.updateMapping(synth, 'knob1', 1, { min: -12, max: 0 });
    expect(mappings()[1]).toMatchObject({ min: -12, max: 0 });
    // A new target starts at its own range.
    await store.updateMapping(synth, 'knob1', 1, { target: 'pan' });
    expect(mappings()[1]).toMatchObject({ target: 'pan', min: -1, max: 1, invert: true });
    await store.removeMapping(synth, 'knob1', 0);
    expect(mappings().map(m => m.target)).toEqual(['pan']);
    // Two mappings: the knob reads in percent.
    await store.addMapping(synth, 'knob1', 'volume');
    expect(store.format(synth, 'knob1', 0.25)).toBe('25 %');
  });

  it('renames a control, switches the layout, toggles the arpeggiator and compares without an undo step', async () => {
    const { project, synth, store } = await open(server, false);
    await store.rename(synth, 'knob2', '  Brightness ');
    expect(store.control(synth, 'knob2')?.name).toBe('Brightness');
    await store.setLayout(synth, 'knobs12');
    expect(store.controls(synth).controls.filter(c => c.kind === 'knob')).toHaveLength(12);
    expect(store.controls(synth).arpeggiator).toBe(false);
    await store.toggleArpeggiator(synth);
    expect(store.controls(synth).arpeggiator).toBe(true);
    expect(project.track(synth)?.strip.midiEffects[0]?.pluginId).toBe('anode.arpeggiator');

    await store.set(synth, 'knob1', 0.7);
    await settle();
    expect(store.canCompare(synth)).toBe(true);
    const steps = (await server.history()).undo.length;
    await store.toggleCompare(synth);
    expect(store.controls(synth).comparing).toBe(true);
    expect(store.value(synth, 'knob1')).toBe(0);
    expect((await server.history()).undo.length).toBe(steps);
    await store.toggleCompare(synth);
    expect(store.value(synth, 'knob1')).toBe(0.7);
  });

  it('gives set-up controls automation lanes, labelled by their names', async () => {
    const { project, synth, store } = await open(server, false);
    const automation = TestBed.inject(AutomationStore);
    const plugin = (id: string) => TestBed.inject(PluginsStore).plugin(id);
    const track = project.track(synth);
    expect(track && targetsOf(track, plugin).some(t => t.group === 'Smart Controls')).toBe(false);
    await store.autoMap(synth);
    const knob = automation.target(synth, 'smartControl:knob1');
    expect(knob).toMatchObject({ group: 'Smart Controls', min: 0, max: 1, name: store.control(synth, 'knob1')?.name });
    expect(knob.format(0.5)).toBe('50 %');
    expect(automation.target(synth, 'smartControl:switch1')).toMatchObject({ stepped: true });
    expect(automation.targets(synth).filter(t => t.group === 'Smart Controls')).toHaveLength(12);
    await automation.commit(synth, 'smartControl:knob1', [
      { tick: 0, value: 0.25, curve: 0 },
      { tick: 3840, value: 0.75, curve: 0 }
    ]);
    expect(project.automationLane(synth, 'smartControl:knob1')?.points).toHaveLength(2);
  });
});
