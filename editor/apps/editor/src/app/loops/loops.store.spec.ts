import { TestBed } from '@angular/core/testing';
import type { LoopInfo } from '@anode/api-client';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { Notify } from '../core/notify';
import { PatchesStore } from '../state/patches.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { loopFits, loopSearch, LoopSearchDebounceMs, LoopsStore, NoLoopFilters, sortLoops } from './loops.store';

const Pack = '/Users/anode/Downloads/TestPack.anodepack';

function loop(name: string, change: Partial<LoopInfo> = {}): LoopInfo {
  return {
    id: name,
    name,
    kind: 'audio',
    path: `/${name}.wav`,
    tempo: 120,
    beats: 8,
    key: 0,
    scale: 'major',
    tags: [],
    oneShot: false,
    favourite: false,
    ...change
  };
}

async function open() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Loops' }));
  const store = TestBed.inject(LoopsStore);
  return { server, project, store };
}

describe('Loop Browser', () => {
  describe('loopSearch', () => {
    it('sends only what narrows the search, keywords in the buttons’ order', () => {
      expect(loopSearch(NoLoopFilters)).toEqual({ offset: 0, limit: 100 });
      expect(
        loopSearch(
          {
            text: '  bass ',
            tags: new Set(['Electronic', 'Drums']),
            kind: 'midi',
            key: 0,
            scale: 'minor',
            favouritesOnly: true
          },
          100
        )
      ).toEqual({
        text: 'bass',
        tags: ['Drums', 'Electronic'],
        kind: 'midi',
        key: 0,
        scale: 'minor',
        favouritesOnly: true,
        offset: 100,
        limit: 100
      });
    });
  });

  it('sorts by a column, loops without a value last', () => {
    const loops = [loop('B', { tempo: 90 }), loop('A', { tempo: null }), loop('C', { tempo: 120 })];
    expect(sortLoops(loops, { column: 'name', descending: false }).map(l => l.name)).toEqual(['A', 'B', 'C']);
    expect(sortLoops(loops, { column: 'tempo', descending: false }).map(l => l.name)).toEqual(['B', 'C', 'A']);
    expect(sortLoops(loops, { column: 'tempo', descending: true }).map(l => l.name)).toEqual(['C', 'B', 'A']);
  });

  it('fits audio loops to audio tracks, MIDI and pattern loops to instrument, MIDI and DMD tracks', () => {
    expect(loopFits({ kind: 'audio' }, { kind: 'audio', drumMachine: null })).toBe(true);
    expect(loopFits({ kind: 'audio' }, { kind: 'instrument', drumMachine: null })).toBe(false);
    expect(loopFits({ kind: 'midi' }, { kind: 'midi', drumMachine: null })).toBe(true);
    expect(loopFits({ kind: 'pattern' }, { kind: 'stack', drumMachine: { pads: [] } })).toBe(true);
    expect(loopFits({ kind: 'pattern' }, { kind: 'aux', drumMachine: null })).toBe(false);
  });

  it('installs a pack (packs, results and the Library’s kits follow) and removes it', async () => {
    const { server, store } = await open();
    await store.start();
    expect(store.packs()).toEqual([]);
    expect(store.loops()).toEqual([]);

    const pack = await store.install(` ${Pack} `);
    expect(pack?.name).toBe('Test Pack');
    expect(store.installing()).toBe(false);
    expect(store.packs().map(p => p.name)).toEqual(['Test Pack']);
    expect(store.total()).toBe(7);
    expect(
      TestBed.inject(PatchesStore)
        .patches()
        .filter(p => p.kind === 'stack').length
    ).toBe(2);

    expect(await store.install('/Users/anode/Downloads/notes.txt')).toBeNull();
    expect(TestBed.inject(Notify).last()).toMatch(/not an Anode content pack/);

    expect(await store.remove(store.packs()[0] ?? (pack as never))).toBe(true);
    expect(store.packs()).toEqual([]);
    expect(store.total()).toBe(0);
    expect(server.requests).toContain('RemovePack');
  });

  it('narrows by keywords (all of them), kind, key, scale and favourites; Reset clears them', async () => {
    const { store } = await open();
    await store.install(Pack);
    store.toggleTag('Drums');
    await settle();
    expect(store.loops().map(l => l.name)).toEqual(['Beat Pattern', 'Drum Loop 100', 'Hat Groove']);
    store.toggleTag('Percussion');
    await settle();
    expect(store.loops().map(l => l.name)).toEqual(['Hat Groove']);
    store.reset();
    store.setKind('midi');
    await settle();
    expect(store.loops().map(l => l.name)).toEqual(['Bass Line', 'Synth Chords']);
    store.setKey(0);
    store.setScale('minor');
    await settle();
    expect(store.loops().map(l => l.name)).toEqual(['Bass Line']);
    expect(store.filtering()).toBe(true);

    store.reset();
    await settle();
    expect(store.total()).toBe(7);
    const bass = store.loops().find(l => l.name === 'Bass Line') as LoopInfo;
    await store.toggleFavourite(bass);
    expect(store.loops().find(l => l.name === 'Bass Line')?.favourite).toBe(true);
    store.toggleFavouritesOnly();
    await settle();
    expect(store.loops().map(l => l.name)).toEqual(['Bass Line']);
    // Unfavouriting under "favourites only" drops it from the list.
    await store.toggleFavourite(store.loops()[0] as LoopInfo);
    expect(store.loops()).toEqual([]);
    expect(store.total()).toBe(0);
  });

  it('searches the text once typing pauses, and keeps only the latest search’s answer', async () => {
    vi.useFakeTimers();
    try {
      const { server, store } = await open();
      await store.install(Pack);
      const before = server.requests.filter(r => r === 'QueryLoops').length;
      store.setText('b');
      store.setText('ba');
      store.setText('bass');
      expect(server.requests.filter(r => r === 'QueryLoops').length).toBe(before);
      await vi.advanceTimersByTimeAsync(LoopSearchDebounceMs + 10);
      expect(server.requests.filter(r => r === 'QueryLoops').length).toBe(before + 1);
      expect(store.loops().map(l => l.name)).toEqual(['Bass Line']);
    } finally {
      vi.useRealTimers();
    }
  });

  it('previews one loop at a time; a second click stops it', async () => {
    const { server, store } = await open();
    await store.install(Pack);
    const [first, second] = store.loops();
    await store.togglePreview(first as LoopInfo);
    expect(store.previewing()).toBe(first?.id);
    expect(server.previewSync?.loopId).toBe(first?.id);
    await store.togglePreview(second as LoopInfo);
    expect(store.previewing()).toBe(second?.id);
    await store.togglePreview(second as LoopInfo);
    expect(store.previewing()).toBeNull();
    expect(server.previewing).toBeNull();
  });

  it('adds a loop: on the selected track at the playhead’s bar when it fits, else on a new track', async () => {
    const { project, store } = await open();
    await store.install(Pack);
    const bass = store.loops().find(l => l.name === 'Bass Line') as LoopInfo;
    const drums = store.loops().find(l => l.name === 'Drum Loop 100') as LoopInfo;

    // No track selected: a new track, at bar 1.
    await store.add(drums);
    const audioTrack = project.trackList()[0];
    expect(audioTrack?.kind).toBe('audio');
    expect(project.regionList().map(r => [r.trackId, r.startTick])).toEqual([[audioTrack?.id, 0]]);

    // The selected audio track does not suit a MIDI loop: a new instrument track.
    TestBed.inject(SelectionStore).track.set(audioTrack?.id ?? null);
    await store.add(bass);
    expect(project.trackList().length).toBe(2);
    const synth = project.trackList()[1];
    expect(project.midiRegionList().map(r => r.trackId)).toEqual([synth?.id]);

    // Dropped on a lane: that track, there.
    await store.addAt(drums.id, drums.name, audioTrack?.id ?? null, 3840);
    expect(project.regionList().map(r => r.startTick)).toEqual([0, 3840]);
  });
});
