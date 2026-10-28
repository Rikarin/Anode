import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import {
  ApiError,
  type ArrangementMarker,
  type AudioFile,
  type AudioRegion,
  type AutomationLane,
  type ChannelStrip,
  type CommandResult,
  type DeltaOp,
  type EditCommand,
  type Marker,
  type MidiRegion,
  type MixerGroup,
  type Project,
  type ProjectDelta,
  type ProjectSettings,
  type TakeFolder,
  type Track
} from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { TempoMap } from '../music/tempo-map';

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
  mergeMidiRecording: false,
  tempoEvents: [{ tick: 0, bpm: 120, curve: 'step', shape: 0 }],
  signatures: [{ tick: 0, numerator: 4, denominator: 4 }],
  keys: [{ tick: 0, tonic: 0, mode: 'major' }],
  recording: { cycleMode: 'takeFolder', replace: false, autopunch: false, punchInTick: 0, punchOutTick: 4 * 4 * Ppq },
  mixer: { pdc: 'all', lowLatency: false, lowLatencyLimitMs: 10 },
  smartTempo: { mode: 'keep', defaultFollow: 'off', analyzeImports: true },
  varispeed: { enabled: false, mode: 'speedOnly', speed: 1 }
};

const SilentStrip: ChannelStrip = {
  volumeDb: 0,
  pan: 0,
  mute: false,
  solo: false,
  inserts: [],
  sends: [],
  output: 'master',
  groups: [],
  vcaId: null
};

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
  /** Take folders (`tkf_…`) by id: takes of cycle recordings, or packed regions, and their comps. */
  readonly takeFolders = signal<ReadonlyMap<string, TakeFolder>>(new Map());
  /** Markers (the marker track and the Marker List), in time order. */
  readonly markers = signal<readonly Marker[]>([]);
  /** Arrangement sections, in time order. */
  readonly sections = signal<readonly ArrangementMarker[]>([]);
  /** The project Note Pad. */
  readonly notes = signal('');
  /** Mixer groups 1…32 by number. */
  readonly groups = signal<ReadonlyMap<number, MixerGroup>>(new Map());
  /** Track automation lanes by id (a lane's `trackId` null is the master's). */
  readonly automation = signal<ReadonlyMap<string, AutomationLane>>(new Map());
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
  readonly takeFolderList = computed(() => [...this.takeFolders().values()]);
  /** Mixer groups in number order. */
  readonly groupList = computed(() => [...this.groups().values()].sort((a, b) => a.number - b.number));
  readonly automationList = computed(() => [...this.automation().values()]);
  /** The tempo track as time (steps and ramps). */
  readonly tempoMap = computed(() => new TempoMap(this.settings().tempoEvents, this.settings().tempo));
  /** The signature track as bars and beats. */
  readonly meter = computed(() => {
    const signatures = this.settings().signatures;
    return new MeterMap(signatures.length ? signatures : [{ tick: 0, ...this.settings().signature }]);
  });

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
    this.takeFolders.set(new Map(project.takeFolders.map(f => [f.id, f])));
    this.markers.set(byTick(project.markers));
    this.sections.set(byStart(project.arrangement));
    this.notes.set(project.notes);
    this.groups.set(new Map(project.groups.map(g => [g.number, g])));
    this.automation.set(new Map(project.automation.map(l => [l.id, l])));
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
    this.takeFolders.set(new Map());
    this.markers.set([]);
    this.sections.set([]);
    this.notes.set('');
    this.groups.set(new Map());
    this.automation.set(new Map());
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

  /**
   * Runs another request that answers with an edit's result (a MIDI file import), in line with the edits, applying
   * its delta here; null when refused (reported).
   */
  send(request: () => Promise<CommandResult>, failure: string): Promise<CommandResult | null> {
    return this.serial(request, failure);
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

  /** The end of a region in ticks, under the tempo track. */
  regionEnd(region: Pick<AudioRegion, 'startTick' | 'length'>): number {
    const map = this.tempoMap();
    return map.constant
      ? region.startTick + regionTicks(region, this.framesPerTick())
      : map.tickAfterFrames(region.startTick, region.length, this.info()?.sampleRate ?? 48_000);
  }

  /** Audio frames per tick over a region (its average under a changing tempo). */
  regionRate(region: Pick<AudioRegion, 'startTick' | 'length'>): number {
    const ticks = this.regionEnd(region) - region.startTick;
    return ticks > 0 ? region.length / ticks : this.framesPerTick();
  }

  takeFolder(id: string): TakeFolder | undefined {
    return this.takeFolders().get(id);
  }

  /** Marks the project unsaved without an edit (a saved screenset). */
  markDirty(): void {
    if (this.info()) {
      this.dirty.set(true);
    }
  }

  midiRegion(id: string): MidiRegion | undefined {
    return this.midiRegions().get(id);
  }

  /** A track's (or, for null, the master's) automation lane of `target`, if it has one. */
  automationLane(trackId: string | null, target: string): AutomationLane | undefined {
    for (const lane of this.automation().values()) {
      if ((lane.trackId ?? null) === trackId && lane.target === target) {
        return lane;
      }
    }
    return undefined;
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
    const takeFolders = new Map(this.takeFolders());
    const markers = new Map(this.markers().map(m => [m.id, m]));
    const sections = new Map(this.sections().map(s => [s.id, s]));
    const groups = new Map(this.groups());
    const automation = new Map(this.automation());
    let trackIds = [...this.trackIds()];
    let missingFile = false;
    for (const op of delta.ops) {
      this.applyOp(
        op,
        { tracks, regions, midiRegions, files, takeFolders, markers, sections, groups, automation },
        ids => (trackIds = ids(trackIds))
      );
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
    if (delta.ops.some(op => op.op.startsWith('takeFolder') || op.op === 'trackRemoved')) {
      this.takeFolders.set(takeFolders);
    }
    if (delta.ops.some(op => op.op.startsWith('marker'))) {
      this.markers.set(byTick([...markers.values()]));
    }
    if (delta.ops.some(op => op.op.startsWith('section'))) {
      this.sections.set(byStart([...sections.values()]));
    }
    if (delta.ops.some(op => op.op.startsWith('group'))) {
      this.groups.set(groups);
    }
    if (delta.ops.some(op => op.op.startsWith('automationLane') || op.op === 'trackRemoved')) {
      this.automation.set(automation);
    }
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
      takeFolders: Map<string, TakeFolder>;
      markers: Map<string, Marker>;
      sections: Map<string, ArrangementMarker>;
      groups: Map<number, MixerGroup>;
      automation: Map<string, AutomationLane>;
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
        for (const [id, folder] of maps.takeFolders) {
          if (folder.trackId === op.id) {
            maps.takeFolders.delete(id);
          }
        }
        for (const [id, lane] of maps.automation) {
          if (lane.trackId === op.id) {
            maps.automation.delete(id);
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
      case 'takeFolderUpserted':
        maps.takeFolders.set(op.folder.id, op.folder);
        break;
      case 'takeFolderRemoved':
        maps.takeFolders.delete(op.id);
        break;
      case 'markerUpserted':
        maps.markers.set(op.marker.id, op.marker);
        break;
      case 'markerRemoved':
        maps.markers.delete(op.id);
        break;
      case 'sectionUpserted':
        maps.sections.set(op.section.id, op.section);
        break;
      case 'sectionRemoved':
        maps.sections.delete(op.id);
        break;
      case 'notesChanged':
        this.notes.set(op.text);
        break;
      case 'automationLaneUpserted':
        maps.automation.set(op.lane.id, op.lane);
        break;
      case 'automationLaneRemoved':
        maps.automation.delete(op.id);
        break;
      case 'groupUpserted':
        maps.groups.set(op.group.number, op.group);
        break;
      case 'groupRemoved':
        maps.groups.delete(op.number);
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

function byTick(markers: readonly Marker[]): Marker[] {
  return [...markers].sort((a, b) => a.tick - b.tick);
}

function byStart(sections: readonly ArrangementMarker[]): ArrangementMarker[] {
  return [...sections].sort((a, b) => a.startTick - b.startTick);
}
