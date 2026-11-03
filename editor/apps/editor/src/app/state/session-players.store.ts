import { computed, effect, inject, Injectable, signal, untracked } from '@angular/core';
import type {
  EditCommand,
  Note,
  SessionPlayerCatalog,
  SessionPlayerConvertTarget,
  SessionPlayerKind,
  SessionPlayerRegion,
  Track
} from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { Notify } from '../core/notify';
import { JobsStore } from './jobs.store';
import { ProjectStore } from './project.store';

/** The player a track plays (`SessionPlayerGenerator.PlayerOf`); null for other tracks. */
export function playerOf(kind: Track['kind'] | undefined): SessionPlayerKind | null {
  return kind === 'drummer'
    ? 'drummer'
    : kind === 'bassPlayer'
      ? 'bass'
      : kind === 'keyboardPlayer'
        ? 'keyboard'
        : null;
}

/** A `setSessionPlayerRegion` with its switch records left as they are (required-nullable in the client). */
export function sessionPlayerEdit(
  regionId: string,
  changes: Omit<
    Extract<EditCommand, { type: 'setSessionPlayerRegion' }>,
    'type' | 'regionId' | 'drums' | 'bass' | 'keys'
  > &
    Partial<Pick<Extract<EditCommand, { type: 'setSessionPlayerRegion' }>, 'drums' | 'bass' | 'keys'>>
): EditCommand {
  return { type: 'setSessionPlayerRegion', regionId, drums: null, bass: null, keys: null, ...changes };
}

/** What a region's generated notes depend on in the project, besides the region itself (a cache key). */
function inputsKey(region: SessionPlayerRegion): string {
  return JSON.stringify([region.startTick, region.lengthTicks, region.parameters, region.seed]);
}

/**
 * Session Players (Phase 11, ADR-0014): the style library, each region's generated notes (the Server's, refetched when
 * the region changes or the chord track, the sections or a rhythm source over it do) for the Tracks area and the Piano
 * Roll, and the Editor's actions: parameter edits, Regenerate and Convert.
 */
@Injectable({ providedIn: 'root' })
export class SessionPlayersStore {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly notify = inject(Notify);
  private readonly jobs = inject(JobsStore);

  /** The style library, once loaded (the Session Player Editor and New Tracks load it). */
  readonly catalog = signal<SessionPlayerCatalog | null>(null);
  /** Each region's generated notes (relative to its start), by region id. */
  readonly notes = signal<ReadonlyMap<string, readonly Note[]>>(new Map());
  /** What each region's notes were fetched for. */
  private readonly fetched = new Map<string, string>();
  private loadingCatalog: Promise<SessionPlayerCatalog | null> | null = null;

  /** The regions in start order. */
  readonly regions = computed(() =>
    [...this.project.sessionPlayerRegions().values()].sort((a, b) => a.startTick - b.startTick)
  );

  constructor() {
    // The notes follow the regions, the chord track, the sections and the MIDI the Drummers follow.
    effect(() => {
      const regions = this.project.sessionPlayerRegions();
      const context = JSON.stringify([
        this.project.chordTrack(),
        this.project.sections().map(s => [s.startTick, s.lengthTicks, s.name]),
        this.project.settings().signatures,
        this.project.settings().keys
      ]);
      const sources = new Set([...regions.values()].flatMap(r => r.parameters.drums?.rhythmSourceTrackId ?? []));
      const rhythm = sources.size
        ? JSON.stringify(
            [...this.project.midiRegions().values()].filter(r => sources.has(r.trackId)).map(r => [r.id, r.notes])
          )
        : '';
      untracked(() => this.refresh(regions, `${context}|${rhythm}`));
    });
  }

  /** Loads the style library once. */
  loadCatalog(): Promise<SessionPlayerCatalog | null> {
    this.loadingCatalog ??= this.notify
      .run(() => this.api.sessionPlayerCatalog(), 'Could not load the Session Player styles')
      .then(catalog => {
        this.catalog.set(catalog);
        if (!catalog) {
          this.loadingCatalog = null;
        }
        return catalog;
      });
    return this.loadingCatalog;
  }

  /** A player's styles. */
  styles(player: SessionPlayerKind | null) {
    return (this.catalog()?.styles ?? []).filter(s => s.player === player);
  }

  /** Changes a region's parameters (one undo step; an XY drag sends both axes). */
  set(regionId: string, changes: Parameters<typeof sessionPlayerEdit>[1], label: string): Promise<unknown> {
    return this.project.execute([sessionPlayerEdit(regionId, changes)], label);
  }

  /** Regenerate: a new seed, new notes from the same parameters. */
  async regenerate(regionIds: readonly string[]): Promise<void> {
    const [only, ...more] = regionIds;
    if (only !== undefined && more.length === 0) {
      // In line with the edits, its delta applied here: the next edit must not name a stale revision.
      await this.project.send(() => this.api.regenerateSessionPlayer(only, {}), 'Could not regenerate');
      return;
    }
    await this.project.execute([{ type: 'regenerateSessionPlayer', regionIds: [...regionIds] }], 'Regenerate');
  }

  /** Convert to MIDI (or, for the Drummer, to a pattern): a job that replaces the regions when done. */
  async convert(regionIds: readonly string[], target: SessionPlayerConvertTarget): Promise<void> {
    const first = regionIds[0];
    if (!first) {
      return;
    }
    const job = await this.notify.run(
      () => this.api.convertSessionPlayer(first, { target, regionIds: [...regionIds] }),
      target === 'pattern' ? 'Could not convert to a pattern region' : 'Could not convert to a MIDI region'
    );
    if (job) {
      this.jobs.update(job);
    }
  }

  private refresh(regions: ReadonlyMap<string, SessionPlayerRegion>, context: string): void {
    const current = this.notes();
    let next: Map<string, readonly Note[]> | null = null;
    for (const id of current.keys()) {
      if (!regions.has(id)) {
        next ??= new Map(current);
        next.delete(id);
        this.fetched.delete(id);
      }
    }
    if (next) {
      this.notes.set(next);
    }
    for (const region of regions.values()) {
      const key = `${inputsKey(region)}|${context}`;
      if (this.fetched.get(region.id) !== key) {
        this.fetched.set(region.id, key);
        void this.fetch(region.id, key);
      }
    }
  }

  private async fetch(regionId: string, key: string): Promise<void> {
    try {
      const { notes } = await this.api.sessionPlayerNotes(regionId);
      // A later change asked again: its answer wins.
      if (this.fetched.get(regionId) === key && this.project.sessionPlayerRegions().has(regionId)) {
        this.notes.update(map => new Map(map).set(regionId, notes));
      }
    } catch {
      // The region went (or the Server cannot generate it yet): it draws without notes.
      if (this.fetched.get(regionId) === key) {
        this.fetched.delete(regionId);
      }
    }
  }
}
