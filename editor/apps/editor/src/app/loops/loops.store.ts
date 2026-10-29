import { Injectable, computed, inject, signal } from '@angular/core';
import type { CommandResult, LoopInfo, LoopKind, LoopScale, Pack, Track } from '@anode/api-client';
import { ANODE_API, type LoopSearch } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { PatchesStore } from '../state/patches.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TransportStore } from '../state/transport.store';

/** The drag type of a loop dragged out of the Loop Browser (its id). */
export const LoopDragType = 'application/x-anode-loop';

/** Loops per page: the first page, then one more each time the list scrolls to its end. */
export const LoopPageSize = 100;

/** How long the search field waits after a key before it searches. */
export const LoopSearchDebounceMs = 250;

/** Logic's keyword buttons: instruments, then genres and descriptors; several combine (AND) as the query's tags. */
export const LoopKeywords: readonly string[] = [
  'Drums',
  'Percussion',
  'Bass',
  'Synth',
  'Keys',
  'Piano',
  'Guitar',
  'Strings',
  'Vocals',
  'Electronic',
  'Hip Hop',
  'Ambient',
  'Pattern',
  'One-shot',
  'Grooving',
  'Melodic'
];

/** The Loop Browser's filters (Logic's header): keywords, kind, key, scale, favourites, and the search text. */
export interface LoopFilters {
  text: string;
  tags: ReadonlySet<string>;
  kind: LoopKind | null;
  /** A tonic, 0…11; null: any key. */
  key: number | null;
  scale: LoopScale | null;
  favouritesOnly: boolean;
}

export const NoLoopFilters: LoopFilters = {
  text: '',
  tags: new Set(),
  kind: null,
  key: null,
  scale: null,
  favouritesOnly: false
};

/** The columns the results sort by (Logic's: Name, Beats, Tempo, Key). */
export type LoopColumn = 'name' | 'beats' | 'tempo' | 'key';

export interface LoopSort {
  column: LoopColumn;
  descending: boolean;
}

/** A page's query from the filters: only what narrows the search is sent; the keywords in Logic's order. */
export function loopSearch(filters: LoopFilters, offset = 0, limit = LoopPageSize): LoopSearch {
  const text = filters.text.trim();
  const tags = LoopKeywords.filter(k => filters.tags.has(k));
  return {
    ...(text ? { text } : {}),
    ...(tags.length ? { tags } : {}),
    ...(filters.kind ? { kind: filters.kind } : {}),
    ...(filters.key !== null ? { key: filters.key } : {}),
    ...(filters.scale ? { scale: filters.scale } : {}),
    ...(filters.favouritesOnly ? { favouritesOnly: true } : {}),
    offset,
    limit
  };
}

/** Loops sorted by a column; loops without a value (a one-shot's tempo) sort last either way, then by name. */
export function sortLoops(loops: readonly LoopInfo[], sort: LoopSort): LoopInfo[] {
  const value = (loop: LoopInfo): number | string | null =>
    sort.column === 'name' ? loop.name.toLowerCase() : (loop[sort.column] ?? null);
  return [...loops].sort((a, b) => {
    const va = value(a);
    const vb = value(b);
    if (va === null || vb === null) {
      return va === vb ? a.name.localeCompare(b.name) : va === null ? 1 : -1;
    }
    const order = va < vb ? -1 : va > vb ? 1 : 0;
    return (sort.descending ? -order : order) || a.name.localeCompare(b.name);
  });
}

/** Whether a track can take a loop: audio loops go on audio tracks; MIDI and pattern loops on instrument, MIDI or DMD. */
export function loopFits(loop: Pick<LoopInfo, 'kind'>, track: Pick<Track, 'kind' | 'drumMachine'>): boolean {
  return loop.kind === 'audio'
    ? track.kind === 'audio'
    : track.kind === 'instrument' || track.kind === 'midi' || !!track.drumMachine;
}

/**
 * The Loop Browser's state (docs/07-phase-6.md): the filters and the search they make (`/library/loops`, debounced
 * while typing, paged), the results, favourites, the one loop previewing (in sync with the project, which the Server
 * does), adding a loop to the project, and the Sound Library's installed packs (install and remove). Packs and results
 * are refetched on every (re)connection and after a pack changes; the Library's kits come with packs, so its patches
 * are too.
 */
@Injectable({ providedIn: 'root' })
export class LoopsStore {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly transport = inject(TransportStore);
  private readonly patches = inject(PatchesStore);

  readonly filters = signal<LoopFilters>(NoLoopFilters);
  readonly sort = signal<LoopSort>({ column: 'name', descending: false });
  private readonly loaded = signal<readonly LoopInfo[]>([]);
  readonly total = signal(0);
  readonly loading = signal(false);
  /** The loop playing through the preview channel, or null. */
  readonly previewing = signal<string | null>(null);
  readonly packs = signal<readonly Pack[]>([]);
  readonly installing = signal(false);

  /** The results as the columns sort them. */
  readonly loops = computed(() => sortLoops(this.loaded(), this.sort()));
  readonly hasMore = computed(() => this.loaded().length < this.total());
  readonly filtering = computed(() => {
    const f = this.filters();
    return (
      f.tags.size > 0 || f.kind !== null || f.key !== null || f.scale !== null || f.favouritesOnly || !!f.text.trim()
    );
  });

  private started = false;
  /** Bumped by every new search, so an older search's late answer is dropped. */
  private generation = 0;
  private textTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    inject(EventStream).connected$.subscribe(() => {
      if (this.started) {
        void this.refresh();
      }
    });
  }

  /** Searches and lists the packs the first time the browser (or the Sound Library) shows. */
  async start(): Promise<void> {
    if (this.started) {
      return;
    }
    this.started = true;
    await this.refresh();
  }

  /** The packs and the first page again. */
  async refresh(): Promise<void> {
    await Promise.all([this.refetchPacks(), this.search()]);
  }

  // ---- filters

  /** The search text; the search runs once typing pauses. */
  setText(text: string): void {
    this.filters.update(f => ({ ...f, text }));
    if (this.textTimer !== null) {
      clearTimeout(this.textTimer);
    }
    this.textTimer = setTimeout(() => {
      this.textTimer = null;
      void this.search();
    }, LoopSearchDebounceMs);
  }

  toggleTag(tag: string): void {
    this.change(f => {
      const tags = new Set(f.tags);
      if (!tags.delete(tag)) {
        tags.add(tag);
      }
      return { ...f, tags };
    });
  }

  setKind(kind: LoopKind | null): void {
    this.change(f => ({ ...f, kind }));
  }

  setKey(key: number | null): void {
    this.change(f => ({ ...f, key }));
  }

  setScale(scale: LoopScale | null): void {
    this.change(f => ({ ...f, scale }));
  }

  toggleFavouritesOnly(): void {
    this.change(f => ({ ...f, favouritesOnly: !f.favouritesOnly }));
  }

  /** Logic's Reset: the keywords and the other filters off (the search text stays). */
  reset(): void {
    this.change(f => ({ ...NoLoopFilters, text: f.text }));
  }

  /** Sorts by a column; the sorted column again reverses it. */
  sortBy(column: LoopColumn): void {
    this.sort.update(s => ({ column, descending: s.column === column ? !s.descending : false }));
  }

  private change(update: (filters: LoopFilters) => LoopFilters): void {
    this.filters.update(update);
    void this.search();
  }

  // ---- results

  /** The first page of the current search. */
  async search(): Promise<void> {
    const generation = ++this.generation;
    this.loading.set(true);
    try {
      const page = await this.api.queryLoops(loopSearch(this.filters()));
      if (generation === this.generation) {
        this.loaded.set(page.items);
        this.total.set(page.total);
      }
    } catch (error) {
      if (generation === this.generation) {
        this.loaded.set([]);
        this.total.set(0);
        this.notify.failure(error, 'Could not search the loops');
      }
    } finally {
      if (generation === this.generation) {
        this.loading.set(false);
      }
    }
  }

  /** The next page, appended (when the list scrolls to its end). */
  async more(): Promise<void> {
    if (this.loading() || !this.hasMore()) {
      return;
    }
    const generation = this.generation;
    this.loading.set(true);
    try {
      const page = await this.api.queryLoops(loopSearch(this.filters(), this.loaded().length));
      if (generation === this.generation) {
        const known = new Set(this.loaded().map(l => l.id));
        this.loaded.update(list => [...list, ...page.items.filter(l => !known.has(l.id))]);
        this.total.set(page.total);
      }
    } catch (error) {
      this.notify.failure(error, 'Could not list more loops');
    } finally {
      if (generation === this.generation) {
        this.loading.set(false);
      }
    }
  }

  // ---- favourites and preview

  async toggleFavourite(loop: LoopInfo): Promise<void> {
    const info = await this.notify.run(
      () => this.api.setLoopFavourite(loop.id, !loop.favourite),
      `Could not change ${loop.name}`
    );
    if (!info) {
      return;
    }
    if (this.filters().favouritesOnly && !info.favourite) {
      this.loaded.update(list => list.filter(l => l.id !== info.id));
      this.total.update(total => Math.max(0, total - 1));
    } else {
      this.loaded.update(list => list.map(l => (l.id === info.id ? info : l)));
    }
  }

  /** Previews a loop, or stops it when it is the one previewing (one at a time, as in Logic). */
  async togglePreview(loop: LoopInfo): Promise<void> {
    if (this.previewing() === loop.id) {
      await this.stopPreview();
      return;
    }
    this.previewing.set(loop.id);
    try {
      await this.api.previewLoop(loop.id);
    } catch (error) {
      if (this.previewing() === loop.id) {
        this.previewing.set(null);
      }
      this.notify.failure(error, `Could not preview ${loop.name}`);
    }
  }

  async stopPreview(): Promise<void> {
    if (this.previewing() === null) {
      return;
    }
    this.previewing.set(null);
    await this.notify.run(() => this.api.previewFile(null), 'Could not stop the preview');
  }

  // ---- adding to the project

  /**
   * Adds a loop (one undo step): on `trackId` at `tick`; or, without a track, on the selected track at the playhead's
   * bar when the loop suits it, else on a new track at bar 1 (Logic's double-click).
   */
  add(loop: LoopInfo, at?: { trackId: string | null; tick: number }): Promise<CommandResult | null> {
    const target = at ?? this.defaultTarget(loop);
    return this.addAt(loop.id, loop.name, target.trackId, target.tick);
  }

  /** Adds a loop by id (a drop on the Tracks area): on `trackId`, or a new track when null. The Server checks the kind. */
  addAt(loopId: string, name: string, trackId: string | null, tick: number): Promise<CommandResult | null> {
    return this.project.send(
      () => this.api.addLoop({ loopId, trackId, tick: Math.max(0, Math.round(tick)) }),
      `Could not add ${name || 'the loop'}`
    );
  }

  private defaultTarget(loop: LoopInfo): { trackId: string | null; tick: number } {
    const id = this.selection.track();
    const track = id ? this.project.track(id) : undefined;
    if (!track || !loopFits(loop, track)) {
      return { trackId: null, tick: 0 };
    }
    const meter = this.project.meter();
    const position = Math.max(0, this.transport.positionAt(performance.now()));
    return { trackId: track.id, tick: meter.barStart(Math.floor(meter.barAt(position) + 1e-9)) };
  }

  // ---- packs (the Sound Library)

  async refetchPacks(): Promise<void> {
    try {
      this.packs.set(await this.api.packs());
    } catch (error) {
      this.notify.failure(error, 'Could not list the content packs');
    }
  }

  /** Installs a `.anodepack` from the Server's disk; resolves with it, or null when refused (reported). */
  async install(path: string): Promise<Pack | null> {
    const trimmed = path.trim();
    if (!trimmed || this.installing()) {
      return null;
    }
    this.installing.set(true);
    try {
      const pack = await this.notify.run(() => this.api.installPack(trimmed), 'Could not install the pack');
      if (pack) {
        this.notify.success(`Installed ${pack.name}`);
        await this.packsChanged();
      }
      return pack;
    } finally {
      this.installing.set(false);
    }
  }

  async remove(pack: Pack): Promise<boolean> {
    const done = await this.notify.run(
      () => this.api.removePack(pack.id).then(() => true),
      `Could not remove ${pack.name}`
    );
    if (done) {
      // A pack's loop ids are `<packId>:<loop>`: its loop previewing goes with it.
      if (this.previewing()?.startsWith(`${pack.id}:`)) {
        await this.stopPreview();
      }
      await this.packsChanged();
    }
    return done === true;
  }

  /** A pack came or went: its loops and kits with it. */
  private async packsChanged(): Promise<void> {
    await Promise.all([this.refetchPacks(), this.search(), this.patches.refetch()]);
  }
}
