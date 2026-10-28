import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import {
  ApiError,
  type AudioFile,
  type AudioRegion,
  type ChannelStrip,
  type CommandResult,
  type DeltaOp,
  type EditCommand,
  type MidiRegion,
  type Project,
  type ProjectDelta,
  type ProjectSettings,
  type Track
} from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';

/** Ticks per quarter note: the project's musical time base (docs/01-implementation-plan.md §5.1). */
export const Ppq = 960;

/** How long a delta that arrived ahead of a missing one waits for it before the store refetches the project. */
export const GapGraceMs = 300;

/** The open project's identity: what the title bar and the chooser show. */
export interface ProjectInfo {
  id: string;
  name: string;
  path: string;
  sampleRate: number;
}

export const DefaultSettings: ProjectSettings = {
  tempo: 120,
  signature: { numerator: 4, denominator: 4 },
  cycle: { enabled: false, startTick: 0, endTick: 4 * 4 * Ppq },
  metronome: { enabled: false, countIn: false, levelDb: -6 },
  endTick: 32 * 4 * Ppq,
  mergeMidiRecording: false
};

const SilentStrip: ChannelStrip = { volumeDb: 0, pan: 0, mute: false, solo: false };

/** Audio frames per tick at a tempo and sample rate (one tempo per project in Phase 1). */
export function framesPerTick(sampleRate: number, tempo: number): number {
  return (sampleRate * 60) / (tempo * Ppq);
}

/** A region's length in ticks. */
export function regionTicks(region: Pick<AudioRegion, 'length'>, perTick: number): number {
  return region.length / perTick;
}

/** What `apply` did with a delta. */
export type DeltaOutcome = 'applied' | 'stale' | 'waiting';

/**
 * The open project as normalised signal maps (tracks, regions and files by id, the track order), kept current by the
 * deltas of every edit, undo, redo and finished recording (docs/adr/0004-state-ownership.md). Deltas apply strictly in
 * revision order: one already applied is ignored (our own edit's answer and its `/ws` echo carry the same delta), one
 * ahead of a missing revision waits briefly for it, and a gap that stays open, a `resync` or a reconnection refetch the
 * whole project.
 *
 * Edits go through {@link execute}: one `CommandBatch` (one undo step, with a label) at a time, with `If-Match`, so a
 * batch never applies to a project it was not made for. A refused one (409, 422) is reported and changes nothing here.
 */
@Injectable({ providedIn: 'root' })
export class ProjectStore {
  private readonly api = inject(ANODE_API);
  private readonly events = inject(EventStream);
  private readonly notify = inject(Notify);

  readonly info = signal<ProjectInfo | null>(null);
  readonly revision = signal(0);
  readonly dirty = signal(false);
  readonly settings = signal<ProjectSettings>(DefaultSettings);
  readonly master = signal<ChannelStrip>(SilentStrip);
  readonly trackIds = signal<readonly string[]>([]);
  readonly tracks = signal<ReadonlyMap<string, Track>>(new Map());
  readonly regions = signal<ReadonlyMap<string, AudioRegion>>(new Map());
  /** MIDI regions (`mid_…`), with their notes and controller events. */
  readonly midiRegions = signal<ReadonlyMap<string, MidiRegion>>(new Map());
  readonly files = signal<ReadonlyMap<string, AudioFile>>(new Map());
  /** A project is being opened, created or refetched. */
  readonly loading = signal(false);

  readonly isOpen = computed(() => this.info() !== null);
  readonly trackList = computed(() => {
    const tracks = this.tracks();
    return this.trackIds()
      .map(id => tracks.get(id))
      .filter((track): track is Track => track !== undefined);
  });
  readonly regionList = computed(() => [...this.regions().values()]);
  readonly midiRegionList = computed(() => [...this.midiRegions().values()]);
  readonly framesPerTick = computed(() => framesPerTick(this.info()?.sampleRate ?? 48_000, this.settings().tempo));

  /** Deltas that arrived ahead of a missing revision, by revision. */
  private readonly ahead = new Map<number, ProjectDelta>();
  private gapTimer: ReturnType<typeof setTimeout> | null = null;
  /** The batch, undo or redo in flight: the next waits for it, so each carries the revision it was made against. */
  private queue: Promise<unknown> = Promise.resolve();
  private refetching: Promise<void> | null = null;

  constructor() {
    this.events.connected$.subscribe(() => void this.refetch());
    this.events.events$.subscribe(message => {
      switch (message.type) {
        case 'projectChanged':
          this.apply(message.delta);
          break;
        case 'projectOpened':
          if (message.project.id !== this.info()?.id || message.revision !== this.revision()) {
            void this.refetch();
          }
          break;
        case 'projectClosed':
          this.clear();
          break;
        case 'projectSaved':
          if (message.project.id === this.info()?.id && message.revision === this.revision()) {
            this.dirty.set(false);
          }
          this.info.update(info => (info?.id === message.project.id ? { ...info, path: message.project.path } : info));
          break;
        default:
          break;
      }
    });
    inject(DestroyRef).onDestroy(() => this.clearGap());
  }

  /** Replaces everything with a snapshot from the Server. */
  load(project: Project): void {
    this.clearGap();
    this.ahead.clear();
    this.info.set({ id: project.id, name: project.name, path: project.path, sampleRate: project.sampleRate });
    this.revision.set(project.revision);
    this.dirty.set(project.dirty);
    this.settings.set(project.settings);
    this.master.set(project.master);
    this.trackIds.set(project.tracks.map(t => t.id));
    this.tracks.set(new Map(project.tracks.map(t => [t.id, t])));
    this.regions.set(new Map(project.regions.map(r => [r.id, r])));
    this.midiRegions.set(new Map(project.midiRegions.map(r => [r.id, r])));
    this.files.set(new Map(project.audioFiles.map(f => [f.id, f])));
  }

  /** No project open. */
  clear(): void {
    this.clearGap();
    this.ahead.clear();
    this.info.set(null);
    this.revision.set(0);
    this.dirty.set(false);
    this.settings.set(DefaultSettings);
    this.master.set(SilentStrip);
    this.trackIds.set([]);
    this.tracks.set(new Map());
    this.regions.set(new Map());
    this.midiRegions.set(new Map());
    this.files.set(new Map());
  }

  /** Fetches the open project (or learns that none is), replacing what the store has. Concurrent calls share one. */
  refetch(): Promise<void> {
    this.refetching ??= (async () => {
      try {
        const project = await this.api.getProject();
        if (project) {
          this.load(project);
        } else {
          this.clear();
        }
      } catch (error) {
        this.notify.failure(error, 'Could not load the project');
      } finally {
        this.refetching = null;
      }
    })();
    return this.refetching;
  }

  /**
   * Applies a delta in revision order: the next revision now (then any that were waiting behind it), an old one not at
   * all, and one ahead of a gap after the gap fills, or a refetch when it does not within {@link GapGraceMs}.
   */
  apply(delta: ProjectDelta): DeltaOutcome {
    if (!this.info()) {
      return 'stale';
    }

    const next = this.revision() + 1;
    if (delta.revision < next) {
      return 'stale';
    }

    if (delta.revision > next) {
      this.ahead.set(delta.revision, delta);
      this.gapTimer ??= setTimeout(() => {
        this.gapTimer = null;
        void this.refetch();
      }, GapGraceMs);
      return 'waiting';
    }

    this.applyOps(delta);
    for (let waiting = this.ahead.get(this.revision() + 1); waiting; waiting = this.ahead.get(this.revision() + 1)) {
      this.ahead.delete(waiting.revision);
      this.applyOps(waiting);
    }

    for (const revision of this.ahead.keys()) {
      if (revision <= this.revision()) {
        this.ahead.delete(revision);
      }
    }

    if (this.ahead.size === 0) {
      this.clearGap();
    }

    return 'applied';
  }

  /**
   * Sends edits as one undo step named `label` ("Move Region"), after any in flight, against the revision the store is
   * at. Resolves with the result once applied here, or null when the Server refused it (reported to the person; on a
   * stale revision the store refetches).
   */
  execute(commands: EditCommand[], label: string): Promise<CommandResult | null> {
    if (commands.length === 0) {
      return Promise.resolve(null);
    }

    return this.serial(
      () => this.api.applyCommands({ commands, label }, this.revision()),
      `Could not ${label.toLowerCase()}`
    );
  }

  undo(): Promise<CommandResult | null> {
    return this.serial(() => this.api.undo(), 'Could not undo');
  }

  redo(): Promise<CommandResult | null> {
    return this.serial(() => this.api.redo(), 'Could not redo');
  }

  /** Adds a file the Server imported (an import is not an edit: no delta announces it). */
  upsertFile(file: AudioFile): void {
    this.files.update(files => new Map(files).set(file.id, file));
  }

  track(id: string): Track | undefined {
    return this.tracks().get(id);
  }

  /** The end of a region in ticks. */
  regionEnd(region: AudioRegion): number {
    return region.startTick + regionTicks(region, this.framesPerTick());
  }

  midiRegion(id: string): MidiRegion | undefined {
    return this.midiRegions().get(id);
  }

  private serial(send: () => Promise<CommandResult>, failure: string): Promise<CommandResult | null> {
    const run = this.queue.then(async () => {
      try {
        const result = await send();
        this.apply(result.delta);
        return result;
      } catch (error) {
        if (error instanceof ApiError && error.status === 409) {
          this.notify.error('The project changed elsewhere; your edit was not applied.');
          await this.refetch();
        } else {
          this.notify.failure(error, failure);
        }
        return null;
      }
    });
    this.queue = run;
    return run;
  }

  private applyOps(delta: ProjectDelta): void {
    const tracks = new Map(this.tracks());
    const regions = new Map(this.regions());
    const midiRegions = new Map(this.midiRegions());
    const files = new Map(this.files());
    let trackIds = [...this.trackIds()];
    let missingFile = false;
    for (const op of delta.ops) {
      this.applyOp(op, { tracks, regions, midiRegions, files }, ids => (trackIds = ids(trackIds)));
      if (op.op === 'regionUpserted' && !files.has(op.region.fileId)) {
        missingFile = true;
      }
    }

    this.tracks.set(tracks);
    this.regions.set(regions);
    if (
      delta.ops.some(op => op.op === 'midiRegionUpserted' || op.op === 'midiRegionRemoved' || op.op === 'trackRemoved')
    ) {
      this.midiRegions.set(midiRegions);
    }
    this.files.set(files);
    this.trackIds.set(trackIds);
    this.revision.set(delta.revision);
    this.dirty.set(true);
    if (missingFile) {
      // Another client imported a file and placed it: only a refetch tells this one about the file.
      void this.refetch();
    }
  }

  private applyOp(
    op: DeltaOp,
    maps: {
      tracks: Map<string, Track>;
      regions: Map<string, AudioRegion>;
      midiRegions: Map<string, MidiRegion>;
      files: Map<string, AudioFile>;
    },
    order: (update: (ids: string[]) => string[]) => void
  ): void {
    switch (op.op) {
      case 'trackUpserted':
        maps.tracks.set(op.track.id, op.track);
        order(ids => {
          const without = ids.filter(id => id !== op.track.id);
          without.splice(Math.min(without.length, Math.max(0, op.index)), 0, op.track.id);
          return without;
        });
        break;
      case 'trackRemoved':
        maps.tracks.delete(op.id);
        order(ids => ids.filter(id => id !== op.id));
        for (const [id, region] of maps.regions) {
          if (region.trackId === op.id) {
            maps.regions.delete(id);
          }
        }
        for (const [id, region] of maps.midiRegions) {
          if (region.trackId === op.id) {
            maps.midiRegions.delete(id);
          }
        }
        break;
      case 'regionUpserted':
        maps.regions.set(op.region.id, op.region);
        break;
      case 'regionRemoved':
        maps.regions.delete(op.id);
        break;
      case 'audioFileUpserted':
        maps.files.set(op.file.id, op.file);
        break;
      case 'settingsChanged':
        this.settings.set(op.settings);
        break;
      case 'masterChanged':
        this.master.set(op.strip);
        break;
      case 'projectRenamed':
        this.info.update(info => (info ? { ...info, name: op.name } : info));
        break;
      case 'midiRegionUpserted':
        maps.midiRegions.set(op.region.id, op.region);
        break;
      case 'midiRegionRemoved':
        maps.midiRegions.delete(op.id);
        break;
    }
  }

  private clearGap(): void {
    if (this.gapTimer) {
      clearTimeout(this.gapTimer);
      this.gapTimer = null;
    }
  }
}
