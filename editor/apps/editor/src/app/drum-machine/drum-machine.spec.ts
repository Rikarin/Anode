import { TestBed } from '@angular/core/testing';
import type { Track } from '@anode/api-client';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { PatchesStore } from '../state/patches.store';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';
import {
  auditionEvents,
  drumMachineOf,
  FirstPage,
  kitPatches,
  LastPage,
  padGrid,
  padLabel,
  pageLabel,
  pageOf
} from './drum-machine';
import { DrumMachineEditor } from './drum-machine-editor';
import { AuditionMs, DrumMachineStore } from './drum-machine.store';

const Pack = '/Users/anode/Downloads/TestPack.anodepack';

async function open() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Beats' }));
  await server.installPack(Pack);
  await TestBed.inject(PatchesStore).refetch();
  await TestBed.inject(PluginsStore).refetchPlugins();
  const store = TestBed.inject(DrumMachineStore);
  return { server, project, store };
}

async function electro() {
  const opened = await open();
  const kit = opened.store.kits().find(k => k.name === 'Electro Kit');
  await opened.store.create(kit ?? null);
  const dmd = opened.project.trackList().find(t => t.drumMachine) as Track;
  return { ...opened, dmd };
}

describe('Drum Machine Designer', () => {
  describe('the pad grid', () => {
    it('lays a page out 4 × 4 from its first note, lowest at the bottom left', () => {
      const pads = [
        { note: 36, name: 'Kick', trackId: 'k' },
        { note: 51, name: 'Ride', trackId: 'r' },
        { note: 52, name: 'Next page', trackId: 'n' }
      ];
      const grid = padGrid(pads, 0);
      expect(grid.map(row => row.map(c => c.note))).toEqual([
        [48, 49, 50, 51],
        [44, 45, 46, 47],
        [40, 41, 42, 43],
        [36, 37, 38, 39]
      ]);
      expect(grid[3]?.[0]?.pad?.name).toBe('Kick');
      expect(grid[0]?.[3]?.pad?.name).toBe('Ride');
      expect(padGrid(pads, 1)[3]?.[0]?.pad?.name).toBe('Next page');
      expect(pageOf(36)).toBe(0);
      expect(pageOf(35)).toBe(-1);
      expect(pageLabel(0)).toBe('C1–D♯2');
    });

    it('leaves cells outside the MIDI range empty at the first and last pages', () => {
      const low = padGrid([], FirstPage).flat();
      expect(low.filter(c => c.note === null).length).toBe(12);
      const high = padGrid([], LastPage).flat();
      expect(Math.max(...high.map(c => c.note ?? 0))).toBe(127);
      expect(high.some(c => c.note === null)).toBe(true);
    });

    it('names pads by name and note, empty ones by note', () => {
      expect(padLabel({ name: 'Snare' }, 38)).toBe('Snare (D1)');
      expect(padLabel(null, 37)).toBe('Empty pad C♯1');
    });

    it('finds the DMD of a DMD track or of one of its pads', () => {
      const dmd = { id: 'd', drumMachine: { pads: [] }, parentId: null } as unknown as Track;
      const pad = { id: 'p', drumMachine: null, parentId: 'd' } as unknown as Track;
      const other = { id: 'o', drumMachine: null, parentId: null } as unknown as Track;
      const tracks = new Map([dmd, pad, other].map(t => [t.id, t]));
      expect(drumMachineOf(dmd, tracks)).toBe(dmd);
      expect(drumMachineOf(pad, tracks)).toBe(dmd);
      expect(drumMachineOf(other, tracks)).toBeNull();
      expect(drumMachineOf(null, tracks)).toBeNull();
    });

    it('lists kits (stack patches) by name, and auditions with note on and off', () => {
      expect(
        kitPatches([
          { id: 'b', name: 'B Kit', category: 'Drum Kits', kind: 'stack' },
          { id: 'p', name: 'Piano', category: 'Keys', kind: 'instrument' },
          { id: 'a', name: 'A Kit', category: 'Drum Kits', kind: 'stack' }
        ]).map(k => k.id)
      ).toEqual(['a', 'b']);
      expect(auditionEvents(38)).toEqual({
        on: { status: 0x90, data1: 38, data2: 100 },
        off: { status: 0x80, data1: 38, data2: 0 }
      });
    });
  });

  describe('DrumMachineStore', () => {
    it('makes a DMD from a kit, selects it and shows its editor', async () => {
      const { project, store, dmd } = await electro();
      expect(dmd.name).toBe('Electro Kit');
      expect(TestBed.inject(SelectionStore).track()).toBe(dmd.id);
      expect(TestBed.inject(UiStore).editorView()).toBe('drumMachine');
      expect(store.current()?.id).toBe(dmd.id);
      expect(project.trackList().filter(t => t.parentId === dmd.id).length).toBe(5);
      expect(store.selectedNote(dmd.id)).toBe(36);
    });

    it('converts an instrument track in place', async () => {
      const { project, store } = await open();
      await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: true }], 'Track');
      const synth = project.trackList()[0] as Track;
      await store.create(store.kits()[0] ?? null, synth.id);
      expect(project.track(synth.id)?.drumMachine?.pads.length).toBeGreaterThan(0);
      expect(project.track(synth.id)?.kind).toBe('stack');
    });

    it('selects the pad of a selected pad track, else the picked one', async () => {
      const { project, store, dmd } = await electro();
      const snare = project.trackList().find(t => t.parentId === dmd.id && t.name === 'Snare') as Track;
      TestBed.inject(SelectionStore).track.set(snare.id);
      expect(store.current()?.id).toBe(dmd.id);
      expect(store.selectedNote(dmd.id)).toBe(38);
      store.selectPad(dmd.id, 42);
      expect(store.selectedNote(dmd.id)).toBe(42);
    });

    it('adds, renames, re-instruments, moves and removes pads, one undo step each', async () => {
      const { project, store, dmd } = await electro();
      const pads = () => project.track(dmd.id)?.drumMachine?.pads ?? [];
      await store.addPad(dmd.id, 37);
      expect(pads().find(p => p.note === 37)?.name).toBe('Pad 37');
      expect(store.selectedNote(dmd.id)).toBe(37);
      const added = pads().find(p => p.note === 37);
      expect(project.track(added?.trackId ?? '')?.instrument?.pluginId).toBe('anode.drum-synth');

      await store.rename(dmd.id, 37, '  Rim ');
      expect(pads().find(p => p.note === 37)?.name).toBe('Rim');
      expect(await store.rename(dmd.id, 37, 'Rim')).toBeNull();

      await store.setInstrument(dmd.id, 37, 'anode.quick-sampler');
      expect(project.track(added?.trackId ?? '')?.instrument?.pluginId).toBe('anode.quick-sampler');

      await store.moveTo(dmd.id, 37, 40);
      expect(pads().some(p => p.note === 37)).toBe(false);
      expect(pads().find(p => p.note === 40)?.name).toBe('Rim');
      expect(store.selectedNote(dmd.id)).toBe(40);

      await store.remove(dmd.id, 40);
      expect(pads().some(p => p.note === 40)).toBe(false);
      expect(project.track(added?.trackId ?? '')).toBeUndefined();
    });

    it('auditions a pad: note on to the DMD, off a moment later', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout'] });
      try {
        const { server, store, dmd } = await electro();
        await store.audition(dmd.id, 38);
        expect(server.liveMidi.at(-1)).toEqual({ trackId: dmd.id, events: [{ status: 0x90, data1: 38, data2: 100 }] });
        await vi.advanceTimersByTimeAsync(AuditionMs + 10);
        expect(server.liveMidi.at(-1)?.events).toEqual([{ status: 0x80, data1: 38, data2: 0 }]);
      } finally {
        vi.useRealTimers();
      }
    });
  });

  describe('the editor', () => {
    it('shows the grid; a pad selects and auditions, an empty pad adds one; the pad editor edits its instrument', async () => {
      const { server, project, dmd } = await electro();
      const fixture = TestBed.createComponent(DrumMachineEditor);
      await fixture.whenStable();
      const element = fixture.nativeElement as HTMLElement;
      const pad = (note: number) =>
        element.querySelector<HTMLButtonElement>(`[data-testid="drum-pad"][data-note="${note}"]`);

      expect(element.querySelector('[role="grid"]')?.getAttribute('aria-label')).toBe('Pads');
      expect(element.querySelectorAll('[data-testid="drum-pad"]').length).toBe(16);
      expect(pad(38)?.getAttribute('aria-label')).toBe('Snare (D1)');
      expect(pad(37)?.getAttribute('aria-label')).toBe('Empty pad C♯1');

      pad(38)?.click();
      await settle();
      await fixture.whenStable();
      expect(server.liveMidi.at(-1)?.events[0]).toMatchObject({ status: 0x90, data1: 38 });
      expect((element.querySelector('[data-testid="pad-name"]') as HTMLInputElement).value).toBe('Snare');
      expect(element.querySelector('an-knob[data-parameter="decay"] [role="slider"]')).not.toBeNull();

      const instrument = element.querySelector('[data-testid="pad-instrument"]') as HTMLSelectElement;
      expect(instrument.value).toBe('anode.drum-synth');
      instrument.value = 'anode.quick-sampler';
      instrument.dispatchEvent(new Event('change'));
      await settle();
      const snare = project.track(dmd.id)?.drumMachine?.pads.find(p => p.note === 38);
      expect(project.track(snare?.trackId ?? '')?.instrument?.pluginId).toBe('anode.quick-sampler');

      pad(37)?.click();
      await settle();
      await fixture.whenStable();
      expect(project.track(dmd.id)?.drumMachine?.pads.some(p => p.note === 37)).toBe(true);
      expect(pad(37)?.getAttribute('aria-label')).toBe('Pad 37 (C♯1)');
    });
  });
});
