import {
  ApiError,
  type AudioFile,
  type AudioRegion,
  type ChannelStrip,
  type CommandBatch,
  type CommandResult,
  type DeltaOp,
  type EditCommand,
  type Project,
  type ProjectDelta,
  type ProjectSummary,
  type ServerMessage,
  type SystemInfo,
  type Track,
  type TrackMeter,
  type TransportRequest,
  type TransportStatus,
  type UndoHistory
} from '@anode/api-client';
import { TrackColors } from '../../state/track-colors';
import type { AnodeApi, CreateProject } from '../api';
import { fakePeaks, hash, readWavHeader } from './fake-audio';

/** Ticks per quarter note (the contract's time base). */
const Ppq = 960;
/** A new project's end marker: 32 bars of 4/4. */
const DefaultEndTick = 32 * 4 * Ppq;
/** Consecutive `setStrip` batches on one control within this long are one undo step (docs/02-phase-1.md). */
const CoalesceMs = 1_000;
/** How often the fake engine reports the transport and meters while it runs. */
const FrameMs = 33;

interface UndoStep {
  label: string;
  revision: number;
  before: Project;
  after: Project;
  /** The control a `setStrip` batch set, and when, for coalescing. */
  strip?: { key: string; at: number };
}

export interface FakeServerOptions {
  /** Milliseconds, for undo coalescing and the transport (Date.now by default). */
  now?: () => number;
  /** Whether the fake engine runs its own timer while playing (dev mode); tests call {@link FakeAnodeServer.advance}. */
  realtime?: boolean;
  /** A project to start with open (fake dev mode, stories). */
  demo?: boolean;
}

/**
 * An in-memory Anode Server: the REST API of `AnodeApi` and the `/ws` messages, with the Phase 1 command semantics
 * (engine/src/Anode.Contracts/Commands.cs), one undo stack, deltas computed by diffing snapshots, a fake engine that
 * plays, records and meters, and synthetic peaks. It stands in for the real Server in unit tests and in the editor's
 * `?fake` mode until workstream B lands, so it follows the contract, not an implementation.
 */
export class FakeAnodeServer implements AnodeApi {
  private readonly listeners = new Set<(message: ServerMessage) => void>();
  private readonly now: () => number;
  private readonly disk = new Map<string, Project>();
  private recent: ProjectSummary[] = [];
  private project: Project | null = null;
  private undoStack: UndoStep[] = [];
  private redoStack: UndoStep[] = [];
  private nextId = 1;

  private transportState: TransportStatus['state'] = 'stopped';
  private positionTick = 0;
  private lastAdvance = 0;
  private recordFrom = 0;
  private timer: ReturnType<typeof setInterval> | null = null;
  private readonly realtime: boolean;

  /** Requests seen, newest last (tests). */
  readonly requests: string[] = [];

  constructor(options: FakeServerOptions = {}) {
    this.now = options.now ?? (() => Date.now());
    this.realtime = options.realtime ?? false;
    if (options.demo) {
      this.openDemo();
    }
  }

  /** Receives every `/ws` message from now on; returns the unsubscribe. */
  subscribe(listener: (message: ServerMessage) => void): () => void {
    this.listeners.add(listener);
    listener({ type: 'connected', version: '0.1.0-fake' });
    return () => this.listeners.delete(listener);
  }

  /** The open project as the Server has it (tests). */
  get current(): Project | null {
    return this.project;
  }

  // ---- AnodeApi

  system(): Promise<SystemInfo> {
    return this.answer('GetSystem', () => ({
      version: '0.1.0-fake',
      engine: { state: 'inprocess', backend: 'fake', hostVersion: '0.1.0-fake', message: null }
    }));
  }

  recentProjects(): Promise<ProjectSummary[]> {
    return this.answer('ListRecentProjects', () => this.recent.map(summary => ({ ...summary })));
  }

  createProject(request: CreateProject): Promise<Project> {
    return this.answer('CreateProject', () => {
      const name = request.name.trim();
      if (!name) {
        throw new ApiError(422, 'A project needs a name.');
      }

      const directory = (request.directory ?? '~/Music/Anode').replace(/\/+$/, '');
      const path = `${directory}/${name}.anode`;
      const project: Project = {
        id: `prj_${this.nextId++}`,
        name,
        path,
        revision: 0,
        dirty: false,
        sampleRate: request.sampleRate ?? 48_000,
        settings: {
          tempo: 120,
          signature: { numerator: 4, denominator: 4 },
          cycle: { enabled: false, startTick: 0, endTick: 4 * 4 * Ppq },
          metronome: { enabled: false, countIn: false, levelDb: -6 },
          endTick: DefaultEndTick
        },
        master: strip(),
        tracks: [],
        regions: [],
        audioFiles: []
      };
      this.disk.set(path, structuredClone(project));
      this.open(project);
      return structuredClone(project);
    });
  }

  openProject(path: string): Promise<Project> {
    return this.answer('OpenProject', () => {
      const saved = this.disk.get(path.replace(/\/+$/, ''));
      if (!saved) {
        throw new ApiError(404, `No Anode project at ${path}.`);
      }

      const project = { ...structuredClone(saved), revision: 0, dirty: false };
      this.open(project);
      return structuredClone(project);
    });
  }

  getProject(): Promise<Project | null> {
    return this.answer('GetProject', () => (this.project ? structuredClone(this.project) : null));
  }

  saveProject(): Promise<ProjectSummary> {
    return this.answer('SaveProject', () => {
      const project = this.require();
      this.project = { ...project, dirty: false };
      this.disk.set(project.path, structuredClone(this.project));
      const summary = this.remember(this.project);
      this.emit({ type: 'projectSaved', project: summary, revision: project.revision });
      return summary;
    });
  }

  closeProject(): Promise<void> {
    return this.answer('CloseProject', () => {
      this.require();
      this.stopEngine();
      this.project = null;
      this.undoStack = [];
      this.redoStack = [];
      this.emit({ type: 'projectClosed' });
    });
  }

  applyCommands(batch: CommandBatch, revision?: number): Promise<CommandResult> {
    return this.answer('ApplyCommands', () => {
      const before = this.require();
      if (revision !== undefined && revision !== before.revision) {
        throw new ApiError(409, `The project is at revision ${before.revision}, not ${revision}.`);
      }

      let after = before;
      for (const command of batch.commands) {
        after = this.applyCommand(after, command);
      }

      return this.commit(before, after, batch.label || 'Edit', stripKey(batch.commands));
    });
  }

  undo(): Promise<CommandResult> {
    return this.answer('Undo', () => {
      const project = this.require();
      const step = this.undoStack.pop();
      if (!step) {
        throw new ApiError(422, 'There is nothing to undo.');
      }

      this.redoStack.push(step);
      return this.publish(project, step.before);
    });
  }

  redo(): Promise<CommandResult> {
    return this.answer('Redo', () => {
      const project = this.require();
      const step = this.redoStack.pop();
      if (!step) {
        throw new ApiError(422, 'There is nothing to redo.');
      }

      this.undoStack.push(step);
      return this.publish(project, step.after);
    });
  }

  history(): Promise<UndoHistory> {
    return this.answer('GetHistory', () => {
      this.require();
      return {
        undo: [...this.undoStack].reverse().map(step => ({ label: step.label, revision: step.revision })),
        redo: [...this.redoStack].reverse().map(step => ({ label: step.label, revision: step.revision }))
      };
    });
  }

  async uploadAudioFile(file: File): Promise<AudioFile> {
    const header = readWavHeader(await file.slice(0, 64 * 1024).arrayBuffer());
    return this.answer('UploadAudioFile', () => this.addFile(file.name, header));
  }

  importAudioFile(path: string): Promise<AudioFile> {
    return this.answer('ImportAudioFile', () => {
      if (!/\.wave?$/i.test(path)) {
        throw new ApiError(422, `${path} is not a WAV file.`);
      }

      const project = this.require();
      const seconds = 4 + (hash(path) % 9);
      return this.addFile(path.split(/[\\/]/).pop() ?? path, {
        sampleRate: project.sampleRate,
        channels: hash(path) % 2 ? 2 : 1,
        bitDepth: 24,
        frames: seconds * project.sampleRate
      });
    });
  }

  peaks(fileId: string, framesPerPeak: number): Promise<ArrayBuffer> {
    return this.answer('GetPeaks', () => {
      const file = this.require().audioFiles.find(f => f.id === fileId);
      if (!file) {
        throw new ApiError(404, `No file ${fileId}.`);
      }

      return fakePeaks(hash(file.id + file.name), file.frames, file.channels, framesPerPeak);
    });
  }

  transport(): Promise<TransportStatus> {
    return this.answer('GetTransport', () => this.status());
  }

  controlTransport(request: TransportRequest): Promise<TransportStatus> {
    return this.answer('ControlTransport', () => {
      this.advance();
      const project = this.require();
      switch (request.command) {
        case 'play':
          this.start('playing', request.tick);
          break;
        case 'record':
          if (!project.tracks.some(track => track.recordArmed)) {
            throw new ApiError(422, 'Record-enable a track first.');
          }

          this.start('recording', request.tick);
          this.recordFrom = this.positionTick;
          break;
        case 'stop':
          this.stopEngine();
          break;
        case 'locate':
          this.positionTick = Math.max(0, request.tick ?? 0);
          this.emitTransport();
          break;
      }

      return this.status();
    });
  }

  // ---- the fake engine

  /** Moves the transport on to now and reports it (and meters); the dev mode's timer calls it every frame. */
  advance(): void {
    const now = this.now();
    if (this.transportState === 'stopped' || !this.project) {
      this.lastAdvance = now;
      return;
    }

    const { tempo, cycle } = this.project.settings;
    let tick = this.positionTick + ((now - this.lastAdvance) / 1000) * (tempo / 60) * Ppq;
    if (cycle.enabled && this.transportState === 'playing' && cycle.endTick > cycle.startTick) {
      if (this.positionTick < cycle.endTick && tick >= cycle.endTick) {
        tick = cycle.startTick + ((tick - cycle.startTick) % (cycle.endTick - cycle.startTick));
      }
    }

    this.positionTick = tick;
    this.lastAdvance = now;
    this.emitTransport();
    this.emit({ type: 'meters', ...this.meters() });
  }

  private start(state: 'playing' | 'recording', tick?: number | null): void {
    if (tick !== undefined && tick !== null) {
      this.positionTick = Math.max(0, tick);
    }

    this.transportState = state;
    this.lastAdvance = this.now();
    this.emitTransport();
    if (this.realtime && !this.timer) {
      this.timer = setInterval(() => this.advance(), FrameMs);
    }
  }

  private stopEngine(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }

    const wasRecording = this.transportState === 'recording';
    if (this.transportState !== 'stopped') {
      this.transportState = 'stopped';
      if (wasRecording) {
        this.finishRecording();
      }

      this.emitTransport();
      this.emit({
        type: 'meters',
        tracks: (this.project?.tracks ?? []).map(t => ({ trackId: t.id, peakLeft: 0, peakRight: 0 })),
        master: { trackId: '', peakLeft: 0, peakRight: 0 }
      });
    }
  }

  /** A finished take per armed track, applied as one undoable "Record" batch (docs/02-phase-1.md). */
  private finishRecording(): void {
    const before = this.project;
    if (!before || this.positionTick <= this.recordFrom) {
      return;
    }

    const frames = Math.round(
      ((this.positionTick - this.recordFrom) / Ppq) * (60 / before.settings.tempo) * before.sampleRate
    );
    let after = before;
    for (const [index, track] of before.tracks.entries()) {
      if (!track.recordArmed) {
        continue;
      }

      const file: AudioFile = {
        id: `aud_${this.nextId++}`,
        name: `${index + 1}-${new Date(this.now()).toISOString().replace(/[-:.]/g, '')}.wav`,
        path: `Audio Files/${index + 1}-take.wav`,
        sampleRate: before.sampleRate,
        channels: track.input.stereo ? 2 : 1,
        bitDepth: 24,
        frames
      };
      after = { ...after, audioFiles: [...after.audioFiles, file] };
      after = this.applyCommand(after, {
        type: 'addRegion',
        trackId: track.id,
        fileId: file.id,
        startTick: Math.round(this.recordFrom)
      });
    }

    this.commit(before, after, 'Record');
  }

  private status(): TransportStatus {
    const project = this.project;
    const sampleRate = project?.sampleRate ?? 48_000;
    const tempo = project?.settings.tempo ?? 120;
    return {
      state: this.transportState,
      positionTick: Math.round(this.positionTick),
      positionSamples: Math.round((this.positionTick / Ppq) * (60 / tempo) * sampleRate),
      sampleRate
    };
  }

  private emitTransport(): void {
    this.emit({ type: 'transport', transport: this.status() });
  }

  /** Levels that follow the regions under the playhead, so the meters move with what plays. */
  private meters(): { tracks: TrackMeter[]; master: TrackMeter } {
    const project = this.project;
    if (!project) {
      return { tracks: [], master: { trackId: '', peakLeft: 0, peakRight: 0 } };
    }

    const framesPerTick = (project.sampleRate * 60) / (project.settings.tempo * Ppq);
    const soloed = project.tracks.some(t => t.strip.solo);
    let masterLevel = 0;
    const tracks = project.tracks.map(track => {
      const playing = project.regions.some(
        r =>
          r.trackId === track.id &&
          r.startTick <= this.positionTick &&
          this.positionTick < r.startTick + r.length / framesPerTick
      );
      const audible = !track.strip.mute && (!soloed || track.strip.solo);
      const wobble = 0.55 + 0.35 * Math.abs(Math.sin(this.positionTick / 217 + hash(track.id)));
      const gain = 10 ** (track.strip.volumeDb / 20);
      const level =
        playing && audible ? Math.min(1.2, wobble * gain) : track.recordArmed || track.inputMonitoring ? 0.05 : 0;
      masterLevel = Math.max(masterLevel, level);
      const pan = track.strip.pan;
      return { trackId: track.id, peakLeft: level * Math.min(1, 1 - pan), peakRight: level * Math.min(1, 1 + pan) };
    });
    const master = masterLevel * 10 ** (project.master.volumeDb / 20) * (project.master.mute ? 0 : 1);
    return { tracks, master: { trackId: '', peakLeft: master, peakRight: master * 0.97 } };
  }

  // ---- commands

  /** Applies one command to a snapshot, or throws 422 for one that does not apply. */
  applyCommand(project: Project, command: EditCommand): Project {
    const framesPerTick = (project.sampleRate * 60) / (project.settings.tempo * Ppq);
    const track = (id: string | null | undefined): Track => {
      const found = project.tracks.find(t => t.id === id);
      if (!found) {
        throw new ApiError(422, `No track ${id ?? '(none)'}.`);
      }
      return found;
    };
    const region = (id: string): AudioRegion => {
      const found = project.regions.find(r => r.id === id);
      if (!found) {
        throw new ApiError(422, `No region ${id}.`);
      }
      return found;
    };
    const file = (id: string): AudioFile => {
      const found = project.audioFiles.find(f => f.id === id);
      if (!found) {
        throw new ApiError(422, `No audio file ${id}.`);
      }
      return found;
    };
    const withRegion = (next: AudioRegion): Project => ({
      ...project,
      regions: project.regions.map(r => (r.id === next.id ? next : r))
    });
    const withTrack = (next: Track): Project => ({
      ...project,
      tracks: project.tracks.map(t => (t.id === next.id ? next : t))
    });
    const nonNegative = (value: number, what: string) => {
      if (!Number.isFinite(value) || value < 0) {
        throw new ApiError(422, `${what} must not be negative.`);
      }
    };

    switch (command.type) {
      case 'addTrack': {
        const number = this.nextId++;
        const count = project.tracks.length;
        const added: Track = {
          id: `trk_${number}`,
          name: command.name || `Audio ${count + 1}`,
          kind: 'audio',
          color: TrackColors[count % TrackColors.length] ?? '#0090ff',
          input: { channel: command.stereo ? 2 * count + 1 : count + 1, stereo: command.stereo },
          recordArmed: false,
          inputMonitoring: false,
          strip: strip()
        };
        const index = Math.min(count, Math.max(0, command.index ?? count));
        const tracks = [...project.tracks];
        tracks.splice(index, 0, added);
        return { ...project, tracks };
      }
      case 'removeTrack':
        track(command.trackId);
        return {
          ...project,
          tracks: project.tracks.filter(t => t.id !== command.trackId),
          regions: project.regions.filter(r => r.trackId !== command.trackId)
        };
      case 'moveTrack': {
        const moved = track(command.trackId);
        const tracks = project.tracks.filter(t => t.id !== moved.id);
        tracks.splice(Math.min(tracks.length, Math.max(0, command.index)), 0, moved);
        return { ...project, tracks };
      }
      case 'setTrack': {
        const t = track(command.trackId);
        return withTrack({
          ...t,
          name: command.name ?? t.name,
          color: command.color ?? t.color,
          input: command.input ?? t.input,
          recordArmed: command.recordArmed ?? t.recordArmed,
          inputMonitoring: command.inputMonitoring ?? t.inputMonitoring
        });
      }
      case 'setStrip': {
        const patch = (s: ChannelStrip): ChannelStrip => ({
          volumeDb: Math.min(6, Math.max(-144, command.volumeDb ?? s.volumeDb)),
          pan: Math.min(1, Math.max(-1, command.pan ?? s.pan)),
          mute: command.mute ?? s.mute,
          solo: command.solo ?? s.solo
        });
        if (command.trackId === null || command.trackId === undefined) {
          return { ...project, master: patch(project.master) };
        }
        const t = track(command.trackId);
        return withTrack({ ...t, strip: patch(t.strip) });
      }
      case 'addRegion': {
        track(command.trackId);
        const f = file(command.fileId);
        const fileOffset = command.fileOffset ?? 0;
        const length = command.length ?? f.frames - fileOffset;
        nonNegative(command.startTick, 'A region start');
        if (fileOffset < 0 || length <= 0 || fileOffset + length > f.frames) {
          throw new ApiError(422, 'The region must lie within its file.');
        }
        const added: AudioRegion = {
          id: `rgn_${this.nextId++}`,
          trackId: command.trackId,
          fileId: f.id,
          name: f.name.replace(/\.[^.]+$/, ''),
          startTick: Math.round(command.startTick),
          fileOffset,
          length,
          gainDb: 0,
          fadeIn: 0,
          fadeOut: 0
        };
        return { ...project, regions: [...project.regions, added] };
      }
      case 'moveRegion': {
        const r = region(command.regionId);
        nonNegative(command.startTick, 'A region start');
        const trackId = command.trackId ?? r.trackId;
        track(trackId);
        return withRegion({ ...r, startTick: Math.round(command.startTick), trackId });
      }
      case 'resizeRegion': {
        const r = region(command.regionId);
        const f = file(r.fileId);
        nonNegative(command.startTick, 'A region start');
        if (command.fileOffset < 0 || command.length <= 0 || command.fileOffset + command.length > f.frames) {
          throw new ApiError(422, 'The region must lie within its file.');
        }
        return withRegion({
          ...r,
          startTick: Math.round(command.startTick),
          fileOffset: command.fileOffset,
          length: command.length,
          fadeIn: Math.min(r.fadeIn, command.length),
          fadeOut: Math.min(r.fadeOut, Math.max(0, command.length - Math.min(r.fadeIn, command.length)))
        });
      }
      case 'splitRegion': {
        const r = region(command.regionId);
        const endTick = r.startTick + r.length / framesPerTick;
        if (command.atTick <= r.startTick || command.atTick >= endTick) {
          throw new ApiError(422, 'A split must fall inside the region.');
        }
        const left = Math.round((command.atTick - r.startTick) * framesPerTick);
        const right: AudioRegion = {
          ...r,
          id: `rgn_${this.nextId++}`,
          startTick: Math.round(command.atTick),
          fileOffset: r.fileOffset + left,
          length: r.length - left,
          fadeIn: 0,
          fadeOut: Math.min(r.fadeOut, r.length - left)
        };
        const first = { ...r, length: left, fadeIn: Math.min(r.fadeIn, left), fadeOut: 0 };
        return { ...withRegion(first), regions: [...withRegion(first).regions, right] };
      }
      case 'setRegion': {
        const r = region(command.regionId);
        const fadeIn = Math.max(0, Math.min(r.length, command.fadeIn ?? r.fadeIn));
        const fadeOut = Math.max(0, Math.min(r.length - fadeIn, command.fadeOut ?? r.fadeOut));
        return withRegion({ ...r, name: command.name ?? r.name, gainDb: command.gainDb ?? r.gainDb, fadeIn, fadeOut });
      }
      case 'deleteRegions':
        command.regionIds.forEach(region);
        return { ...project, regions: project.regions.filter(r => !command.regionIds.includes(r.id)) };
      case 'setTempo':
        if (!(command.bpm >= 20 && command.bpm <= 999)) {
          throw new ApiError(422, 'The tempo must be 20 to 999 bpm.');
        }
        return { ...project, settings: { ...project.settings, tempo: command.bpm } };
      case 'setSignature':
        if (command.numerator < 1 || command.numerator > 32 || ![1, 2, 4, 8, 16, 32].includes(command.denominator)) {
          throw new ApiError(
            422,
            `${command.numerator}/${command.denominator} is not a time signature Anode supports.`
          );
        }
        return {
          ...project,
          settings: {
            ...project.settings,
            signature: { numerator: command.numerator, denominator: command.denominator }
          }
        };
      case 'setCycle': {
        const cycle = project.settings.cycle;
        const next = {
          enabled: command.enabled ?? cycle.enabled,
          startTick: command.startTick ?? cycle.startTick,
          endTick: command.endTick ?? cycle.endTick
        };
        if (next.startTick < 0 || next.endTick <= next.startTick) {
          throw new ApiError(422, 'The cycle must end after it starts.');
        }
        return { ...project, settings: { ...project.settings, cycle: next } };
      }
      case 'setMetronome': {
        const m = project.settings.metronome;
        return {
          ...project,
          settings: {
            ...project.settings,
            metronome: {
              enabled: command.enabled ?? m.enabled,
              countIn: command.countIn ?? m.countIn,
              levelDb: command.levelDb ?? m.levelDb
            }
          }
        };
      }
      case 'renameProject':
        if (!command.name.trim()) {
          throw new ApiError(422, 'A project needs a name.');
        }
        return { ...project, name: command.name.trim() };
    }
  }

  // ---- internals

  private commit(before: Project, after: Project, label: string, strip?: string): CommandResult {
    const now = this.now();
    const last = this.undoStack.at(-1);
    const coalesce =
      strip !== undefined &&
      last?.strip?.key === strip &&
      now - last.strip.at < CoalesceMs &&
      this.redoStack.length === 0;
    const result = this.publish(before, after);
    if (coalesce) {
      this.undoStack[this.undoStack.length - 1] = {
        ...last,
        after: this.project ?? after,
        revision: result.revision,
        strip: { key: strip, at: now }
      };
    } else {
      this.undoStack.push({
        label,
        revision: result.revision,
        before,
        after: this.project ?? after,
        ...(strip === undefined ? {} : { strip: { key: strip, at: now } })
      });
    }

    this.redoStack = [];
    return result;
  }

  /** Makes `next` the open project at the next revision and tells every client what changed. */
  private publish(previous: Project, next: Project): CommandResult {
    const revision = previous.revision + 1;
    const project = { ...next, revision, dirty: true };
    this.project = project;
    const delta: ProjectDelta = { revision, ops: diff(previous, project) };
    this.emit({ type: 'projectChanged', delta });
    return { revision, delta: structuredClone(delta) };
  }

  private addFile(name: string, header: ReturnType<typeof readWavHeader>): AudioFile {
    const project = this.require();
    if (!header) {
      throw new ApiError(422, `${name} is not a WAV file Anode can read.`);
    }

    if (header.sampleRate !== project.sampleRate) {
      throw new ApiError(
        422,
        `${name} is at ${header.sampleRate} Hz; this project is at ${project.sampleRate} Hz (no sample-rate conversion yet).`
      );
    }

    const file: AudioFile = {
      id: `aud_${this.nextId++}`,
      name,
      path: `Audio Files/${name}`,
      sampleRate: header.sampleRate,
      channels: header.channels,
      bitDepth: header.bitDepth,
      frames: header.frames
    };
    // An import is not an edit: no revision, no undo step. The file reaches other clients with the region that uses it.
    this.project = { ...project, audioFiles: [...project.audioFiles, file] };
    return { ...file };
  }

  private open(project: Project): void {
    this.stopEngine();
    this.positionTick = 0;
    this.project = project;
    this.undoStack = [];
    this.redoStack = [];
    const summary = this.remember(project);
    this.emit({ type: 'projectOpened', project: summary, revision: project.revision });
  }

  private remember(project: Project): ProjectSummary {
    const summary: ProjectSummary = {
      id: project.id,
      name: project.name,
      path: project.path,
      modified: new Date(this.now()).toISOString()
    };
    this.recent = [summary, ...this.recent.filter(p => p.path !== project.path)].slice(0, 10);
    return summary;
  }

  private require(): Project {
    if (!this.project) {
      throw new ApiError(404, 'No project is open.');
    }
    return this.project;
  }

  private emit(message: ServerMessage): void {
    for (const listener of this.listeners) {
      listener(structuredClone(message));
    }
  }

  /** Runs a request as the Server would: asynchronously, with a copy of the answer. */
  private answer<T>(operation: string, run: () => T): Promise<T> {
    this.requests.push(operation);
    try {
      return Promise.resolve(run());
    } catch (error) {
      return Promise.reject(error instanceof Error ? error : new Error(String(error)));
    }
  }

  /** A small project with three tracks of audio, for the fake dev mode and stories. */
  private openDemo(): void {
    const disk = '~/Music/Anode/Demo.anode';
    const base: Project = {
      id: 'prj_demo',
      name: 'Demo',
      path: disk,
      revision: 0,
      dirty: false,
      sampleRate: 48_000,
      settings: {
        tempo: 112,
        signature: { numerator: 4, denominator: 4 },
        cycle: { enabled: false, startTick: 4 * 4 * Ppq, endTick: 8 * 4 * Ppq },
        metronome: { enabled: false, countIn: true, levelDb: -6 },
        endTick: DefaultEndTick
      },
      master: strip(),
      tracks: [],
      regions: [],
      audioFiles: []
    };
    let project = base;
    for (const name of ['Drums', 'Bass', 'Guitar', 'Vocals']) {
      project = this.applyCommand(project, { type: 'addTrack', name, stereo: name === 'Drums' });
    }
    const files: [string, number, number][] = [
      ['Drums.wav', 2, 16],
      ['Bass DI.wav', 1, 12],
      ['Guitar L.wav', 1, 8],
      ['Lead vocal.wav', 1, 10]
    ];
    files.forEach(([name, channels, seconds], i) => {
      const file: AudioFile = {
        id: `aud_${this.nextId++}`,
        name,
        path: `Audio Files/${name}`,
        sampleRate: 48_000,
        channels,
        bitDepth: 24,
        frames: seconds * 48_000
      };
      project = { ...project, audioFiles: [...project.audioFiles, file] };
      const track = project.tracks[i];
      if (track) {
        project = this.applyCommand(project, {
          type: 'addRegion',
          trackId: track.id,
          fileId: file.id,
          startTick: i === 0 ? 0 : (i + 1) * 4 * Ppq
        });
      }
    });
    this.disk.set(disk, structuredClone(project));
    this.recent = [{ id: project.id, name: project.name, path: disk, modified: new Date(this.now()).toISOString() }];
    this.project = project;
  }
}

function strip(): ChannelStrip {
  return { volumeDb: 0, pan: 0, mute: false, solo: false };
}

/** The control a batch of exactly one `setStrip` sets ("trk_1:volumeDb"), for undo coalescing. */
function stripKey(commands: readonly EditCommand[]): string | undefined {
  const [only] = commands;
  if (commands.length !== 1 || only?.type !== 'setStrip') {
    return undefined;
  }

  const fields = (['volumeDb', 'pan', 'mute', 'solo'] as const).filter(f => only[f] !== undefined && only[f] !== null);
  return `${only.trackId ?? 'master'}:${fields.join(',')}`;
}

/** The delta from one snapshot to the next: every entity that differs, upserted or removed. */
export function diff(before: Project, after: Project): DeltaOp[] {
  const ops: DeltaOp[] = [];
  const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);
  if (before.name !== after.name) {
    ops.push({ op: 'projectRenamed', name: after.name });
  }
  if (!same(before.settings, after.settings)) {
    ops.push({ op: 'settingsChanged', settings: after.settings });
  }
  if (!same(before.master, after.master)) {
    ops.push({ op: 'masterChanged', strip: after.master });
  }
  const beforeFiles = new Map(before.audioFiles.map(f => [f.id, f]));
  for (const file of after.audioFiles) {
    if (!same(beforeFiles.get(file.id), file)) {
      ops.push({ op: 'audioFileUpserted', file });
    }
  }
  const afterTracks = new Set(after.tracks.map(t => t.id));
  for (const track of before.tracks) {
    if (!afterTracks.has(track.id)) {
      ops.push({ op: 'trackRemoved', id: track.id });
    }
  }
  after.tracks.forEach((track, index) => {
    const previousIndex = before.tracks.findIndex(t => t.id === track.id);
    if (previousIndex !== index || !same(before.tracks[previousIndex], track)) {
      ops.push({ op: 'trackUpserted', track, index });
    }
  });
  const afterRegions = new Set(after.regions.map(r => r.id));
  for (const region of before.regions) {
    if (!afterRegions.has(region.id)) {
      ops.push({ op: 'regionRemoved', id: region.id });
    }
  }
  const beforeRegions = new Map(before.regions.map(r => [r.id, r]));
  for (const region of after.regions) {
    if (!same(beforeRegions.get(region.id), region)) {
      ops.push({ op: 'regionUpserted', region });
    }
  }
  return ops;
}
