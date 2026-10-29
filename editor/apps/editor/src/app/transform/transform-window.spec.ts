import { TestBed } from '@angular/core/testing';
import type { TransformSet } from '@anode/api-client';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { applyTransform } from '../midi-edit/transform';
import { MidiLibraryStore } from '../state/midi-library.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';
import { TransformWindow } from './transform-window';

async function render() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Transform' }));
  await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: false }], 'Track');
  const trackId = project.trackIds()[0] ?? '';
  await project.execute([{ type: 'addMidiRegion', trackId, startTick: 3840, lengthTicks: 3840 }], 'Region');
  const regionId = [...project.midiRegions().keys()][0] ?? '';
  await project.execute(
    [
      {
        type: 'addNotes',
        regionId,
        notes: [60, 72, 73, 79].map((pitch, i) => ({
          pitch,
          velocity: 64,
          channel: 1,
          startTick: i * 480,
          lengthTicks: 480
        }))
      },
      { type: 'addControls', regionId, events: [{ kind: 'controlChange', number: 1, tick: 0, value: 20, channel: 1 }] }
    ],
    'Notes'
  );
  const selection = TestBed.inject(SelectionStore);
  selection.select([regionId]);
  TestBed.inject(UiStore).transformOpen.set(true);
  const fixture = TestBed.createComponent(TransformWindow);
  await settle();
  await fixture.whenStable();
  const element = fixture.nativeElement as HTMLElement;
  const window = fixture.componentInstance;
  const update = async () => {
    await settle();
    await fixture.whenStable();
  };
  const notes = () => project.midiRegions().get(regionId)?.notes ?? [];
  const byTestId = (id: string) => element.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  return { server, project, selection, regionId, element, window, update, notes, byTestId };
}

function factory(id: string): TransformSet {
  const set = TestBed.inject(MidiLibraryStore)
    .transformSets()
    .find(s => s.id === id);
  if (!set) {
    throw new Error(`No ${id}`);
  }
  return set;
}

describe('TransformWindow', () => {
  it('lists the presets, loads Humanize with a fresh seed, and applies it seeded (one undo step)', async () => {
    const { project, window, update, notes, byTestId, element } = await render();
    expect(element.querySelector('[role="dialog"]')?.getAttribute('aria-label')).toBe('MIDI Transform');
    expect(byTestId('transform-target')?.textContent).toContain(': every event');
    expect(TestBed.inject(MidiLibraryStore).transformSets()[0]?.id).toBe('factory.humanize');

    window.load(factory('factory.humanize'));
    await update();
    expect(window.set().seed).toBeGreaterThan(0);
    expect(byTestId('transform-seed')).not.toBeNull();
    expect((byTestId('transform-operation-velocity') as HTMLSelectElement | null)?.value).toBe('plusMinusRandom');
    expect((byTestId('transform-operation-position-a') as HTMLInputElement | null)?.value).toBe('0 0 0 10');

    window.setSeed(1234);
    const before = notes();
    const expected = applyTransform(
      before.map(n => ({ ...n, startTick: n.startTick + 3840 })),
      [],
      { ...window.set(), seed: 1234 }
    ).notes.map(n => ({ ...n, startTick: n.startTick - 3840 }));
    await window.apply();
    await update();
    const byId = new Map(notes().map(n => [n.id, n]));
    for (const n of expected) {
      expect(byId.get(n.id)).toMatchObject({
        startTick: n.startTick,
        lengthTicks: n.lengthTicks,
        velocity: n.velocity
      });
    }
    expect(window.set().seed).not.toBe(1234);
    expect(byTestId('transform-status')?.textContent).toContain('Transformed 5 events.');
    await project.undo();
    expect(notes()).toEqual(before);
  });

  it('applies the golden path’s custom Select and Operate from the grid, and selects what it matched', async () => {
    const { window, update, notes, byTestId, selection } = await render();
    const change = async (testId: string, value: string) => {
      const control = byTestId(testId) as HTMLInputElement | HTMLSelectElement | null;
      if (!control) {
        throw new Error(`No ${testId}`);
      }
      control.value = value;
      control.dispatchEvent(new Event('change'));
      await update();
    };
    await change('transform-condition-status', 'equal');
    expect((byTestId('transform-condition-status-a') as HTMLSelectElement | null)?.value).toBe('144');
    await change('transform-condition-pitch', 'greaterEqual');
    await change('transform-condition-pitch-a', '73');
    await change('transform-operation-velocity', 'fix');
    await change('transform-operation-velocity-a', '100');
    await change('transform-operation-length', 'fix');
    await change('transform-operation-length-a', '0 0 1 0');
    expect(window.set().mode).toBe('selectAndOperate');

    (byTestId('transform-apply') as HTMLButtonElement | null)?.click();
    await update();
    expect(notes().map(n => [n.pitch, n.velocity, n.lengthTicks])).toEqual([
      [60, 64, 480],
      [72, 64, 480],
      [73, 100, 240],
      [79, 100, 240]
    ]);
    const matched = notes()
      .filter(n => n.pitch >= 73)
      .map(n => n.id);
    expect([...selection.notes()]).toEqual(matched);
  });

  it('marks a value it cannot read and refuses to apply until it is fixed', async () => {
    const { window, update, byTestId } = await render();
    window.edit({ row: 'condition', field: 'position', part: 'op', value: 'inside' });
    window.edit({ row: 'condition', field: 'position', part: 'b', value: 'soon' });
    await update();
    expect(byTestId('transform-condition-position-b')?.getAttribute('aria-invalid')).toBe('true');
    expect((byTestId('transform-condition-position-b') as HTMLInputElement | null)?.value).toBe('soon');
    await window.apply();
    await update();
    expect(byTestId('transform-status')?.textContent).toContain('Fix the values marked invalid first.');
  });

  it('runs Select Only itself, changing the selection and not the project', async () => {
    const { project, window, update, notes, selection } = await render();
    const revision = project.revision();
    window.edit({ row: 'condition', field: 'pitch', part: 'op', value: 'lessEqual' });
    window.edit({ row: 'condition', field: 'pitch', part: 'a', value: '72' });
    window.edit({ row: 'operation', field: 'velocity', part: 'op', value: 'fix' });
    window.setMode('selectOnly');
    await window.apply();
    await update();
    expect(project.revision()).toBe(revision);
    expect([...selection.notes()]).toEqual(
      notes()
        .filter(n => n.pitch <= 72)
        .map(n => n.id)
    );
    // The CC 1 event's first data byte (1) is a pitch ≤ 72, as in Logic.
    expect(selection.controls().size).toBe(1);
  });

  it('operates on the selected events only, and fits Reverse Pitch to them', async () => {
    const { window, update, notes, selection } = await render();
    const [first, , , last] = notes();
    selection.selectNotes([first?.id ?? 0, last?.id ?? 0]);
    window.load(factory('factory.reversePitch'));
    expect(window.set().operations[0]).toMatchObject({ op: 'flip', a: (60 + 79) / 2 });
    window.setMode('operateOnly');
    await window.apply();
    await update();
    expect(notes().map(n => n.pitch)).toEqual([79, 72, 73, 60]);
    expect(selection.notes().size).toBe(2);
  });

  it('saves a set to the library (a factory preset as a new one), replaces and deletes it', async () => {
    const { server, window, update, byTestId } = await render();
    window.load(factory('factory.fixedVelocity'));
    await update();
    (byTestId('transform-save') as HTMLButtonElement | null)?.click();
    await update();
    const name = byTestId('transform-save-name') as HTMLInputElement | null;
    expect(name?.value).toBe('Fixed Velocity Copy');
    expect(byTestId('transform-save-replace')).toBeNull();
    await window.save(false);
    await update();
    const saved = (await server.transformSets()).find(s => s.name === 'Fixed Velocity Copy');
    expect(saved?.id).toMatch(/^trs_/);
    expect(window.set().id).toBe(saved?.id);
    expect(byTestId('transform-delete')).not.toBeNull();

    window.edit({ row: 'operation', field: 'velocity', part: 'a', value: '90' });
    (byTestId('transform-save') as HTMLButtonElement | null)?.click();
    await update();
    await window.save(true);
    const replaced = (await server.transformSets()).filter(s => s.name === 'Fixed Velocity Copy');
    expect(replaced).toHaveLength(1);
    expect(replaced[0]?.operations[0]?.a).toBe(90);

    (byTestId('transform-delete') as HTMLButtonElement | null)?.click();
    await update();
    expect((await server.transformSets()).some(s => s.id === saved?.id)).toBe(false);
    expect(window.set().id).toBe('');
  });

  it('shows the map editor for Use Map and sends the drawn map', async () => {
    const { window, update, byTestId, notes } = await render();
    window.edit({ row: 'operation', field: 'pitch', part: 'op', value: 'useMap' });
    await update();
    const map = byTestId('transform-map');
    expect(map?.getAttribute('role')).toBe('slider');
    map?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', shiftKey: true }));
    await update();
    expect(window.set().map?.[60]).toBe(68);
    await window.apply();
    await update();
    expect(notes().map(n => n.pitch)).toEqual([68, 72, 73, 79]);
    (byTestId('transform-map-reset') as HTMLButtonElement | null)?.click();
    await update();
    expect(window.set().map?.[60]).toBe(60);
  });
});
