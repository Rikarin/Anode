import { TestBed } from '@angular/core/testing';
import type { EditCommand } from '@anode/api-client';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TransportStore } from '../state/transport.store';
import { PatternTemplates } from './pattern-edit';
import { StepSequencerStore } from './step-sequencer.store';

/** A project with an instrument track and an Electro Kit DMD (Kick, Snare, Clap, Closed Hat, Open Hat). */
async function open() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Beats' }));
  await server.installPack('/Users/anode/Downloads/Essentials.anodepack');
  await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: true }], 'Track');
  await project.execute([{ type: 'createDrumMachine', kitPatchId: 'kit_electro', name: 'Beat' }], 'DMD');
  const synth = project.trackList().find(t => t.kind === 'instrument' && !t.parentId)?.id ?? '';
  const dmd = project.trackList().find(t => t.drumMachine)?.id ?? '';
  const selection = TestBed.inject(SelectionStore);
  const store = TestBed.inject(StepSequencerStore);
  // Every batch the store sends.
  const sent: EditCommand[][] = [];
  const apply = server.applyCommands.bind(server);
  server.applyCommands = (batch, ...rest) => {
    sent.push(batch.commands);
    return apply(batch, ...rest);
  };
  return { server, project, selection, store, synth, dmd, sent };
}

async function withRegion(trackId: 'dmd' | 'synth' = 'dmd') {
  const setup = await open();
  setup.selection.selectTrack(trackId === 'dmd' ? setup.dmd : setup.synth);
  const id = await setup.store.newRegion();
  setup.sent.length = 0;
  return { ...setup, id: id ?? '' };
}

const onSteps = (store: StepSequencerStore, row: number) =>
  store.pattern()?.rows[row]?.steps.flatMap((s, i) => (s.on ? [i] : [])) ?? [];

describe('StepSequencerStore', () => {
  it('makes a one-bar pattern region at the playhead’s bar on the selected DMD, selects it and shows its pad rows', async () => {
    const { store, selection, project, dmd } = await open();
    expect(store.targetTrack()).toBeNull();
    selection.selectTrack(dmd);
    TestBed.inject(TransportStore).locate(4000);
    const id = await store.newRegion();
    expect(project.patternRegion(id ?? '')).toMatchObject({ trackId: dmd, startTick: 3840, lengthTicks: 3840 });
    expect(selection.regions()).toEqual(new Set([id]));
    expect(store.region()?.id).toBe(id);
    expect(store.pattern()?.rows.map(r => r.name)).toEqual(['Kick', 'Snare', 'Clap', 'Closed Hat', 'Open Hat']);
  });

  it('sends each edit as one setPattern with the whole pattern, building on edits not yet answered', async () => {
    const { store, sent, project, id } = await withRegion();
    const kick = store.pattern()?.rows[0]?.id ?? '';
    // Three quick edits: shown at once, each building on the last.
    void store.toggleStep(kick, 0);
    void store.toggleStep(kick, 4);
    void store.setStep(kick, 4, 'velocity', 80);
    expect(onSteps(store, 0)).toEqual([0, 4]);
    await settle();
    await settle();
    expect(sent.every(batch => batch.length === 1 && batch[0]?.type === 'setPattern')).toBe(true);
    expect(sent.length).toBeLessThanOrEqual(3);
    const stored = project.patternRegion(id)?.pattern.rows[0]?.steps;
    expect(stored?.[0]?.on).toBe(true);
    expect(stored?.[4]).toMatchObject({ on: true, velocity: 80 });
  });

  it('shows a drag without sending it, then sends it once when it ends', async () => {
    const { store, sent } = await withRegion();
    const kick = store.pattern()?.rows[0]?.id ?? '';
    await store.setStep(kick, 2, 'velocity', 90, false);
    await store.setStep(kick, 2, 'velocity', 70, false);
    expect(store.pattern()?.rows[0]?.steps[2]).toMatchObject({ on: true, velocity: 70 });
    expect(sent).toHaveLength(0);
    await store.setStep(kick, 2, 'velocity', 60);
    await settle();
    expect(sent).toHaveLength(1);
    expect(store.pattern()?.rows[0]?.steps[2]?.velocity).toBe(60);
    // Swing too.
    await store.setSwing(0.3, false);
    expect(store.pattern()?.swing).toBe(0.3);
    await store.setSwing(0.4);
    await settle();
    expect(sent).toHaveLength(2);
    expect(store.region()?.pattern.swing).toBe(0.4);
  });

  it('changes a row’s length, rate, play mode, mute and note, adds and removes rows', async () => {
    const { store } = await withRegion();
    const hat = store.pattern()?.rows[3]?.id ?? '';
    const snare = store.pattern()?.rows[1]?.id ?? '';
    await store.setRowLength(hat, 7);
    await store.setRowRate(hat, 160);
    await store.setRowPlayMode(snare, 'pingPong');
    await store.toggleRowMute(snare);
    await store.setRowNote(snare, 40);
    await settle();
    const rows = store.region()?.pattern.rows;
    expect(rows?.[3]).toMatchObject({ length: 7, stepTicks: 160 });
    expect(rows?.[3]?.steps).toHaveLength(7);
    expect(rows?.[1]).toMatchObject({ playMode: 'pingPong', muted: true, note: 40 });
    await store.removeRow(hat);
    await store.addRow('pad', 42);
    await settle();
    expect(store.region()?.pattern.rows.at(-1)).toMatchObject({
      kind: 'pad',
      note: 42,
      name: 'Closed Hat',
      length: 16
    });
  });

  it('applies templates and randomises from a seed (the selected row only when asked)', async () => {
    const { store } = await withRegion();
    const backbeat = PatternTemplates.find(t => t.id === 'backbeat');
    if (!backbeat) {
      throw new Error('no template');
    }
    await store.applyTemplate(backbeat);
    await settle();
    expect(onSteps(store, 0)).toEqual([0, 8]);
    expect(onSteps(store, 1)).toEqual([4, 12]);
    const before = structuredClone(store.pattern());
    store.selectedRow.set(store.pattern()?.rows[2]?.id ?? null);
    await store.randomise('velocity', true);
    await settle();
    const after = store.pattern();
    expect(after?.rows[0]).toEqual(before?.rows[0]);
    expect(after?.rows[2]).not.toEqual(before?.rows[2]);
  });

  it('step-records live MIDI on the track into the next step of the note’s row', async () => {
    const { server, store, dmd } = await withRegion();
    store.toggleStepRecord();
    await server.sendLiveMidi(dmd, [{ status: 0x90, data1: 36, data2: 100 }]);
    await settle();
    await server.sendLiveMidi(dmd, [{ status: 0x90, data1: 38, data2: 90 }]);
    await settle();
    await server.sendLiveMidi(dmd, [{ status: 0x80, data1: 38, data2: 0 }]);
    await settle();
    expect(onSteps(store, 0)).toEqual([0]);
    expect(onSteps(store, 1)).toEqual([1]);
    expect(store.stepCursor()).toBe(2);
    await settle();
    expect(store.region()?.pattern.rows[1]?.steps[1]).toMatchObject({ on: true, velocity: 90 });
  });

  it('live-records a note into the step under the playhead while playing', async () => {
    const { store, dmd } = await withRegion();
    const transport = TestBed.inject(TransportStore);
    store.toggleLiveRecord();
    transport.state.set('playing');
    transport.anchorTicks.set(960);
    vi.spyOn(transport, 'positionAt').mockReturnValue(960);
    store.noteIn(dmd, 42, 100);
    await settle();
    expect(onSteps(store, 3)).toEqual([4]);
  });

  it('reseeds, and converts to a MIDI region it then selects', async () => {
    const { store, sent, project, selection, id } = await withRegion();
    const seed = store.pattern()?.seed;
    await store.reseed();
    expect(sent.at(-1)).toEqual([{ type: 'reseedPattern', regionId: id }]);
    expect(store.pattern()?.seed).not.toBe(seed);
    await store.convertToMidi();
    expect(project.patternRegion(id)).toBeUndefined();
    const [midi] = [...selection.regions()];
    expect(project.midiRegions().has(midi ?? '')).toBe(true);
  });
});
