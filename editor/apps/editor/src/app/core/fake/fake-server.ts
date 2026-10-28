import {
  ApiError,
  type AudioFile,
  type AudioRegion,
  type Bookmarks,
  type ChannelStrip,
  type CommandBatch,
  type CommandResult,
  type DeltaOp,
  type DirectoryListing,
  type EditCommand,
  type KeyCommandSet,
  type LiveMidiEvent,
  type MidiDevices,
  type MidiRegion,
  type Note,
  type PluginInfo,
  type Project,
  type ProjectAlternative,
  type ProjectBackup,
  type ProjectDelta,
  type ProjectSummary,
  type Screenset,
  type ServerMessage,
  type SystemInfo,
  type TakeFolder,
  type Track,
  type TrackMeter,
  type TransportRequest,
  type TransportStatus,
  type UndoHistory
} from '@anode/api-client';
import { clampParameter, defaultParameters } from '../../instruments/parameters';
import { isNoteOff, isNoteOn, NeutralPlayback, quantizeNotes } from '../../midi/midi-math';
import { TrackColors } from '../../state/track-colors';
import type { AnodeApi, CreateProject } from '../api';
import {
  audioEnd,
  checkComp,
  checkSections,
  copyRegions,
  deleteSections,
  flattenTakeFolder,
  framesBetween,
  groupStacks,
  joinRegions,
  moveSection,
  packTakeFolder,
  replaceRange,
  shiftFolder,
  sortSections,
  tempoOf
} from './fake-arrange';
import { fakePeaks, hash, readWavHeader } from './fake-audio';
import { fakeFileExists, FakeHome, fileKind, listFakeFolder } from './fake-files';
import { FakeCatalogue, FakeMidiDevices } from './fake-plugins';

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
  /** The control a `setStrip` or `setParameter` batch set, and when, for coalescing. */
  strip?: { key: string; at: number };
}

/** A note held on a record-armed track while recording, from live MIDI. */
interface HeldNote {
  pitch: number;
  velocity: number;
  channel: number;
  startTick: number;
}

/** A note a recording pass captured, in project ticks. */
type TakeNote = HeldNote & { lengthTicks: number };

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

  private readonly catalogue: PluginInfo[] = structuredClone([...FakeCatalogue]);
  private devices: MidiDevices = structuredClone(FakeMidiDevices);
  /** The track that hears live MIDI when none is armed (the selected one). */
  private thru: string | null = null;
  /** Live notes held and captured on armed tracks during the recording pass, by track id. */
  private readonly held = new Map<string, Map<number, HeldNote>>();
  private readonly captured = new Map<string, TakeNote[]>();
  /** Live MIDI sent, newest last (tests). */
  readonly liveMidi: { trackId: string; events: LiveMidiEvent[] }[] = [];

  /** Requests seen, newest last (tests). */
  readonly requests: string[] = [];

  /** Cycle wraps of the transport since it last started. */
  private wraps = 0;
  /** The last playback (not recording): where it started and got to, and its cycle wraps; Flashback Capture's source. */
  private played: { from: number; to: number; wraps: number } | null = null;
  /** Alternatives per project path; the current one's project is the open project (or what is on disk). */
  private readonly alternativeSets = new Map<string, { alternative: ProjectAlternative; project: Project | null }[]>();
  private readonly backupSets = new Map<string, { backup: ProjectBackup; project: Project }[]>();
  private readonly screensetSets = new Map<string, Screenset[]>();
  private keyCommandSet: KeyCommandSet = { bindings: {} };
  private bookmarkSet: Bookmarks = { paths: [`${FakeHome}/Music/Loops`, `${FakeHome}/Music/MIDI`] };
  /** The file the preview channel plays (tests). */
  previewing: string | null = null;

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
          endTick: DefaultEndTick,
          mergeMidiRecording: false,
          tempoEvents: [{ tick: 0, bpm: 120, curve: 'step' }],
          signatures: [{ tick: 0, numerator: 4, denominator: 4 }],
          keys: [{ tick: 0, tonic: 0, mode: 'major' }],
          recording: {
            cycleMode: 'takeFolder',
            replace: false,
            autopunch: false,
            punchInTick: 0,
            punchOutTick: 4 * 4 * Ppq
          }
        },
        master: strip(),
        tracks: [],
        regions: [],
        audioFiles: [],
        midiRegions: [],
        takeFolders: [],
        markers: [],
        arrangement: [],
        notes: ''
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
      // Every save first keeps the previous one as a backup (the newest ten).
      const previous = this.disk.get(project.path);
      if (previous) {
        const backups = this.backupSets.get(project.path) ?? [];
        backups.unshift({
          backup: { id: `bak_${this.nextId++}`, saved: new Date(this.now()).toISOString() },
          project: structuredClone(previous)
        });
        this.backupSets.set(project.path, backups.slice(0, 10));
      }
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

      return this.commit(before, after, batch.label || 'Edit', coalesceKey(batch.commands));
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
    const header = readWavHeader(await readBytes(file.slice(0, 64 * 1024)));
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
          this.held.clear();
          this.captured.clear();
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

  plugins(): Promise<PluginInfo[]> {
    return this.answer('ListPlugins', () => structuredClone(this.catalogue));
  }

  rescanPlugins(): Promise<void> {
    return this.answer('RescanPlugins', () => {
      // The scan finishes later; the catalogue is the same, but clients refetch it.
      setTimeout(() => this.emit({ type: 'pluginsChanged' }), 0);
    });
  }

  midiDevices(): Promise<MidiDevices> {
    return this.answer('ListMidiDevices', () => structuredClone(this.devices));
  }

  sendLiveMidi(trackId: string, events: LiveMidiEvent[]): Promise<void> {
    return this.answer('SendLiveMidi', () => {
      const track = this.require().tracks.find(t => t.id === trackId);
      if (!track) {
        throw new ApiError(404, `No track ${trackId}.`);
      }
      if (track.kind === 'audio') {
        throw new ApiError(422, `${track.name} is an audio track: it does not play MIDI.`);
      }
      for (const event of events) {
        if (event.status < 0x80 || event.status > 0xef || event.data1 < 0 || event.data1 > 127) {
          throw new ApiError(422, 'Live MIDI takes channel messages (status 0x80…0xEF, data 0…127).');
        }
      }

      this.liveMidi.push({ trackId, events: structuredClone(events) });
      if (this.transportState === 'recording' && track.recordArmed) {
        this.advance();
        this.capture(trackId, events);
      }
      this.emit({
        type: 'midiActivity',
        events: events.map(e => ({ trackId, device: null, status: e.status, data1: e.data1, data2: e.data2 }))
      });
    });
  }

  setMidiThru(trackId: string | null): Promise<void> {
    return this.answer('SetMidiThru', () => {
      if (trackId !== null && !this.project?.tracks.some(t => t.id === trackId)) {
        throw new ApiError(404, `No track ${trackId}.`);
      }
      this.thru = trackId;
    });
  }

  flashbackCapture(): Promise<void> {
    return this.answer('FlashbackCapture', () => {
      const project = this.require();
      this.advance();
      const played =
        this.transportState === 'playing'
          ? { ...(this.played ?? { from: 0, wraps: 0 }), to: this.positionTick }
          : this.played;
      if (!played || played.to <= played.from + 1) {
        throw new ApiError(422, 'Nothing was played to capture: play first, then capture.');
      }
      if (!project.tracks.some(t => t.recordArmed)) {
        throw new ApiError(422, 'Record-enable the tracks to capture first.');
      }
      this.commit(
        project,
        this.recordedTakes(project, played.from, played.to, played.wraps, true),
        'Flashback Capture'
      );
    });
  }

  alternatives(): Promise<ProjectAlternative[]> {
    return this.answer('ListAlternatives', () => this.alternativeSet().map(a => ({ ...a.alternative })));
  }

  createAlternative(name: string): Promise<ProjectAlternative> {
    return this.answer('CreateAlternative', () => {
      const project = this.require();
      if (!name.trim()) {
        throw new ApiError(422, 'An alternative needs a name.');
      }
      const set = this.alternativeSet();
      const modified = new Date(this.now()).toISOString();
      for (const entry of set) {
        if (entry.alternative.current) {
          entry.project = structuredClone(project);
          entry.alternative = { ...entry.alternative, current: false };
        }
      }
      const alternative: ProjectAlternative = {
        id: `alt_${this.nextId++}`,
        name: name.trim(),
        modified,
        current: true
      };
      set.push({ alternative, project: null });
      return { ...alternative };
    });
  }

  openAlternative(id: string): Promise<Project> {
    return this.answer('OpenAlternative', () => {
      const project = this.require();
      const set = this.alternativeSet();
      const target = set.find(a => a.alternative.id === id);
      if (!target) {
        throw new ApiError(404, `No alternative ${id}.`);
      }
      if (target.alternative.current) {
        return structuredClone(project);
      }
      const current = set.find(a => a.alternative.current);
      if (current) {
        current.project = structuredClone(project);
        current.alternative = { ...current.alternative, current: false };
      }
      target.alternative = { ...target.alternative, current: true };
      const next = { ...structuredClone(target.project ?? project), revision: 0, dirty: false };
      target.project = null;
      this.open(next);
      return structuredClone(next);
    });
  }

  deleteAlternative(id: string): Promise<void> {
    return this.answer('DeleteAlternative', () => {
      const set = this.alternativeSet();
      const index = set.findIndex(a => a.alternative.id === id);
      if (index < 0) {
        throw new ApiError(404, `No alternative ${id}.`);
      }
      if (set[index]?.alternative.current) {
        throw new ApiError(422, 'The current alternative cannot be deleted.');
      }
      set.splice(index, 1);
    });
  }

  backups(): Promise<ProjectBackup[]> {
    return this.answer('ListBackups', () =>
      (this.backupSets.get(this.require().path) ?? []).map(b => ({ ...b.backup }))
    );
  }

  revertToBackup(id: string): Promise<Project> {
    return this.answer('RevertToBackup', () => {
      const project = this.require();
      const backup = (this.backupSets.get(project.path) ?? []).find(b => b.backup.id === id);
      if (!backup) {
        throw new ApiError(404, `No backup ${id}.`);
      }
      const next = { ...structuredClone(backup.project), revision: project.revision + 1, dirty: true };
      this.open(next);
      return structuredClone(next);
    });
  }

  screensets(): Promise<Screenset[]> {
    return this.answer('ListScreensets', () => structuredClone(this.screensetSets.get(this.require().path) ?? []));
  }

  saveScreenset(screenset: Screenset): Promise<Screenset> {
    return this.answer('SaveScreenset', () => {
      const project = this.require();
      if (!Number.isInteger(screenset.number) || screenset.number < 1 || screenset.number > 9) {
        throw new ApiError(422, 'Screensets are numbered 1 to 9.');
      }
      const list = (this.screensetSets.get(project.path) ?? []).filter(s => s.number !== screenset.number);
      this.screensetSets.set(
        project.path,
        [...list, structuredClone(screenset)].sort((a, b) => a.number - b.number)
      );
      this.project = { ...project, dirty: true };
      return structuredClone(screenset);
    });
  }

  importMidiFile(path: string): Promise<CommandResult> {
    return this.answer('ImportMidiFile', () => {
      const before = this.require();
      if (fileKind(path) !== 'midi') {
        throw new ApiError(422, `${path} is not a Standard MIDI File.`);
      }
      if (path.startsWith(FakeHome) && !fakeFileExists(path)) {
        throw new ApiError(422, `No file ${path}.`);
      }
      return this.commit(before, this.midiFileTracks(before, path), 'Import MIDI File');
    });
  }

  keyCommands(): Promise<KeyCommandSet> {
    return this.answer('GetKeyCommands', () => structuredClone(this.keyCommandSet));
  }

  saveKeyCommands(set: KeyCommandSet): Promise<KeyCommandSet> {
    return this.answer('SaveKeyCommands', () => {
      for (const list of Object.values(set.bindings)) {
        if (list.some(binding => !binding.trim())) {
          throw new ApiError(422, 'A key binding cannot be empty.');
        }
      }
      this.keyCommandSet = structuredClone(set);
      return structuredClone(set);
    });
  }

  bookmarks(): Promise<Bookmarks> {
    return this.answer('GetBookmarks', () => structuredClone(this.bookmarkSet));
  }

  saveBookmarks(bookmarks: Bookmarks): Promise<Bookmarks> {
    return this.answer('SaveBookmarks', () => {
      this.bookmarkSet = { paths: [...new Set(bookmarks.paths)] };
      return structuredClone(this.bookmarkSet);
    });
  }

  listFiles(path?: string | null): Promise<DirectoryListing> {
    return this.answer('ListFiles', () =>
      listFakeFolder(path, this.project?.sampleRate ?? 48_000, new Date(this.now()).toISOString())
    );
  }

  previewFile(path: string | null): Promise<void> {
    return this.answer('PreviewFile', () => {
      if (path !== null && !/\.wave?$/i.test(path)) {
        throw new ApiError(422, `The preview plays WAV files; ${path} is not one.`);
      }
      this.previewing = path;
    });
  }

  /** The track live MIDI goes to when none is armed (tests). */
  get thruTrack(): string | null {
    return this.thru;
  }

  /** Plugs in (or pulls out) MIDI devices, as hotplug would (tests and stories). */
  setMidiDevices(devices: MidiDevices): void {
    this.devices = structuredClone(devices);
    this.emit({ type: 'midiDevicesChanged' });
  }

  // ---- the fake engine

  /** Moves the transport on to now and reports it (and meters); the dev mode's timer calls it every frame. */
  advance(): void {
    const now = this.now();
    if (this.transportState === 'stopped' || !this.project) {
      this.lastAdvance = now;
      return;
    }

    const { cycle } = this.project.settings;
    const map = tempoOf(this.project);
    let tick = map.tickAt(map.secondsAt(this.positionTick) + (now - this.lastAdvance) / 1000);
    // The engine cycles while playing and while recording (cycle recording: one pass per wrap).
    if (cycle.enabled && cycle.endTick > cycle.startTick) {
      if (this.positionTick < cycle.endTick && tick >= cycle.endTick) {
        const length = cycle.endTick - cycle.startTick;
        this.wraps += Math.floor((tick - cycle.startTick) / length);
        tick = cycle.startTick + ((tick - cycle.startTick) % length);
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
    this.wraps = 0;
    if (state === 'playing') {
      this.played = { from: this.positionTick, to: this.positionTick, wraps: 0 };
    }
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
    if (this.transportState === 'playing' && this.played) {
      this.played = { ...this.played, to: this.positionTick, wraps: this.wraps };
    }
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

  /**
   * A finished recording, applied as one undoable "Record" batch (docs/02-phase-1.md, docs/04-phase-3.md): per armed
   * audio track a file and, per cycle pass, a take in a take folder or a region; per armed MIDI track its notes.
   */
  private finishRecording(): void {
    const before = this.project;
    if (!before || (this.positionTick <= this.recordFrom && this.wraps === 0)) {
      return;
    }

    let after = this.recordedTakes(before, this.recordFrom, this.positionTick, this.wraps, false);
    for (const track of before.tracks) {
      if (track.recordArmed && track.kind !== 'audio' && track.kind !== 'stack') {
        after = this.addMidiTake(after, track, this.takeNotes(track.id));
      }
    }

    this.held.clear();
    this.captured.clear();
    this.commit(before, after, 'Record');
  }

  /**
   * What the armed audio tracks recorded from `from` to `to` with `wraps` cycle wraps: one file per track for the whole
   * recording; each pass a take of one take folder (`cycleMode = takeFolder`, with a comp of the last pass) or a region.
   * A recording keeps only the punch range under autopunch, and replace mode clears what it covers first. Flashback
   * Capture makes the same from what was played.
   */
  private recordedTakes(before: Project, from: number, to: number, wraps: number, flashback: boolean): Project {
    const { cycle, recording } = before.settings;
    const passes: [number, number][] =
      wraps === 0
        ? [[from, to]]
        : [
            [from, cycle.endTick],
            ...Array.from({ length: wraps - 1 }, (): [number, number] => [cycle.startTick, cycle.endTick]),
            [cycle.startTick, to]
          ];
    const frames = passes.map(([a, b]) => Math.max(0, framesBetween(before, a, b)));
    const total = frames.reduce((sum, f) => sum + f, 0);
    if (total <= 0) {
      return before;
    }
    let after = before;
    for (const [index, track] of before.tracks.entries()) {
      if (!track.recordArmed || track.kind !== 'audio') {
        continue;
      }
      const file: AudioFile = {
        id: `aud_${this.nextId++}`,
        name: `${track.name}#${String(index + 1).padStart(2, '0')}${flashback ? ' (Flashback)' : ''}.wav`,
        path: `Audio Files/${index + 1}-take-${this.nextId}.wav`,
        sampleRate: before.sampleRate,
        channels: track.input.stereo ? 2 : 1,
        bitDepth: 24,
        frames: total
      };
      after = { ...after, audioFiles: [...after.audioFiles, file] };
      // Each pass: where it is in the song and in the file, cut to the punch range under autopunch.
      let offset = 0;
      const spans = passes.flatMap(([a, b], i) => {
        const fileStart = offset;
        offset += frames[i] ?? 0;
        const punch = recording.autopunch && !flashback;
        const start = punch ? Math.max(a, recording.punchInTick) : a;
        const end = punch ? Math.min(b, recording.punchOutTick) : b;
        if (end - start < 1) {
          return [];
        }
        return [
          {
            start,
            end,
            fileOffset: fileStart + framesBetween(before, a, start),
            length: framesBetween(before, start, end)
          }
        ];
      });
      if (spans.length === 0) {
        continue;
      }
      const lo = Math.min(...spans.map(p => p.start));
      const hi = Math.max(...spans.map(p => p.end));
      if (recording.replace && !flashback) {
        after = replaceRange(after, track.id, lo, hi, prefix => `${prefix}_${this.nextId++}`);
      }
      if (spans.length > 1 && recording.cycleMode === 'takeFolder') {
        const takes = spans.map((p, i) => ({
          id: `tak_${this.nextId++}`,
          name: `Take ${i + 1}`,
          fileId: file.id,
          startTick: Math.round(p.start),
          fileOffset: p.fileOffset,
          length: Math.max(1, p.length)
        }));
        const last = takes.at(-1) as (typeof takes)[number];
        const lastSpan = spans.at(-1) as (typeof spans)[number];
        const folder: TakeFolder = {
          id: `tkf_${this.nextId++}`,
          trackId: track.id,
          name: `${track.name}: Take Folder`,
          startTick: Math.round(lo),
          lengthTicks: Math.round(hi - lo),
          takes,
          comp: [{ startTick: last.startTick, endTick: Math.round(lastSpan.end), takeId: last.id }],
          crossfade: Math.round(before.sampleRate / 100),
          muted: false
        };
        after = { ...after, takeFolders: [...after.takeFolders, folder] };
      } else {
        for (const p of spans) {
          after = this.applyCommand(after, {
            type: 'addRegion',
            trackId: track.id,
            fileId: file.id,
            startTick: Math.round(p.start),
            fileOffset: p.fileOffset,
            length: Math.max(1, Math.min(p.length, total - p.fileOffset))
          });
        }
      }
    }
    return after;
  }

  /** A Standard MIDI File as the fake reads one: two instrument tracks of four bars, and the file's tempo. */
  private midiFileTracks(before: Project, path: string): Project {
    const name = (path.split('/').pop() ?? path).replace(/\.midi?$/i, '');
    const seed = hash(path);
    const bpm = 84 + (seed % 48);
    let project = this.applyCommand(before, { type: 'setTempoEvents', events: [{ tick: 0, bpm, curve: 'step' }] });
    project = this.applyCommand(project, {
      type: 'setSignatures',
      events: [{ tick: 0, numerator: 4, denominator: 4 }]
    });
    const bar = 4 * Ppq;
    const parts: { name: string; notes: { pitch: number; startTick: number; lengthTicks: number }[] }[] = [
      {
        name: `${name}: Keys`,
        notes: [0, 1, 2, 3].flatMap(i =>
          [0, 4, 7].map(k => ({ pitch: 60 + ((seed + i * 5) % 7) + k, startTick: i * bar, lengthTicks: bar - Ppq / 4 }))
        )
      },
      {
        name: `${name}: Bass`,
        notes: Array.from({ length: 16 }, (_, i) => ({
          pitch: 36 + ((seed + Math.floor(i / 4) * 5) % 7),
          startTick: i * Ppq,
          lengthTicks: Ppq / 2
        }))
      }
    ];
    for (const part of parts) {
      project = this.applyCommand(project, { type: 'addTrack', name: part.name, stereo: true, kind: 'instrument' });
      const track = project.tracks.at(-1) as Track;
      project = this.applyCommand(project, {
        type: 'addMidiRegion',
        trackId: track.id,
        startTick: 0,
        lengthTicks: 4 * bar,
        name: part.name
      });
      const region = project.midiRegions.at(-1) as MidiRegion;
      project = this.applyCommand(project, {
        type: 'addNotes',
        regionId: region.id,
        notes: part.notes.map(n => ({ ...n, velocity: 96, channel: 1 }))
      });
    }
    return project;
  }

  private alternativeSet(): { alternative: ProjectAlternative; project: Project | null }[] {
    const project = this.require();
    let set = this.alternativeSets.get(project.path);
    if (!set) {
      set = [
        {
          alternative: {
            id: `alt_${this.nextId++}`,
            name: project.name,
            modified: new Date(this.now()).toISOString(),
            current: true
          },
          project: null
        }
      ];
      this.alternativeSets.set(project.path, set);
    }
    return set;
  }

  /** Live note-ons and note-offs on an armed track while recording, kept for its take. */
  private capture(trackId: string, events: LiveMidiEvent[]): void {
    const held = this.held.get(trackId) ?? new Map<number, HeldNote>();
    this.held.set(trackId, held);
    const tick = Math.round(this.positionTick);
    for (const event of events) {
      if (isNoteOn(event.status, event.data2)) {
        held.set(event.data1, {
          pitch: event.data1,
          velocity: event.data2,
          channel: (event.status & 0x0f) + 1,
          startTick: tick
        });
      } else if (isNoteOff(event.status, event.data2)) {
        const note = held.get(event.data1);
        if (note) {
          held.delete(event.data1);
          this.captured.set(trackId, [
            ...(this.captured.get(trackId) ?? []),
            { ...note, lengthTicks: Math.max(1, tick - note.startTick) }
          ]);
        }
      }
    }
  }

  /**
   * What an armed MIDI track recorded this pass: the live notes it received (held ones end with the pass), or else a
   * few notes as a device would have played them (a rising arpeggio, one per beat).
   */
  private takeNotes(trackId: string): TakeNote[] {
    const end = Math.round(this.positionTick);
    const notes = [...(this.captured.get(trackId) ?? [])];
    for (const note of this.held.get(trackId)?.values() ?? []) {
      notes.push({ ...note, lengthTicks: Math.max(1, end - note.startTick) });
    }
    if (notes.length > 0) {
      return notes;
    }

    const arpeggio = [60, 64, 67, 72];
    const first = Math.ceil(this.recordFrom / Ppq) * Ppq;
    for (let tick = first, i = 0; tick < end; tick += Ppq, i++) {
      notes.push({
        pitch: arpeggio[i % arpeggio.length] ?? 60,
        velocity: 80 + ((i * 13) % 40),
        channel: 1,
        startTick: tick,
        lengthTicks: Math.min(Ppq / 2, end - tick)
      });
    }
    return notes;
  }

  /**
   * A MIDI take as the contract applies it: a new region spanning the pass rounded out to bars, or, with
   * `mergeMidiRecording` on, its notes added to the region under the start of the pass.
   */
  private addMidiTake(project: Project, track: Track, notes: TakeNote[]): Project {
    if (notes.length === 0) {
      return project;
    }

    const bar = barTicks(project);
    const from = Math.round(this.recordFrom);
    const merge = project.settings.mergeMidiRecording
      ? project.midiRegions.find(
          r => r.trackId === track.id && r.startTick <= from && from < r.startTick + r.lengthTicks
        )
      : undefined;
    if (merge) {
      return this.applyCommand(project, {
        type: 'addNotes',
        regionId: merge.id,
        notes: notes.map(n => ({ ...n, startTick: n.startTick - merge.startTick }))
      });
    }

    const start = Math.floor(from / bar) * bar;
    const end = Math.max(start + bar, Math.ceil(Math.round(this.positionTick) / bar) * bar);
    const added = this.applyCommand(project, {
      type: 'addMidiRegion',
      trackId: track.id,
      startTick: start,
      lengthTicks: end - start,
      name: track.name
    });
    const region = added.midiRegions.at(-1);
    return region
      ? this.applyCommand(added, {
          type: 'addNotes',
          regionId: region.id,
          notes: notes.map(n => ({ ...n, startTick: n.startTick - start }))
        })
      : added;
  }

  private status(): TransportStatus {
    const project = this.project;
    const sampleRate = project?.sampleRate ?? 48_000;
    const seconds = project ? tempoOf(project).secondsAt(this.positionTick) : (this.positionTick / Ppq) * 0.5;
    return {
      state: this.transportState,
      positionTick: Math.round(this.positionTick),
      positionSamples: Math.round(seconds * sampleRate),
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

    const soloed = project.tracks.some(t => t.strip.solo);
    let masterLevel = 0;
    const tracks = project.tracks.map(track => {
      const tick = this.positionTick;
      const playing =
        project.regions.some(
          r => r.trackId === track.id && !r.muted && r.startTick <= tick && tick < audioEnd(project, r)
        ) ||
        project.takeFolders.some(
          f => f.trackId === track.id && !f.muted && f.comp.some(c => c.startTick <= tick && tick < c.endTick)
        ) ||
        project.midiRegions.some(
          r =>
            r.trackId === track.id &&
            r.startTick <= tick &&
            tick < r.startTick + r.lengthTicks &&
            r.notes.some(n => r.startTick + n.startTick <= tick && tick < r.startTick + n.startTick + n.lengthTicks)
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
    const midiRegion = (id: string): MidiRegion => {
      const found = project.midiRegions.find(r => r.id === id);
      if (!found) {
        throw new ApiError(422, `No MIDI region ${id}.`);
      }
      return found;
    };
    const withMidiRegion = (next: MidiRegion): Project => ({
      ...project,
      midiRegions: project.midiRegions.map(r => (r.id === next.id ? next : r))
    });
    const midiTrack = (id: string): Track => {
      const t = track(id);
      if (t.kind === 'audio') {
        throw new ApiError(422, `${t.name} is an audio track: it holds no MIDI.`);
      }
      return t;
    };
    const instrumentTrack = (id: string): Track & { instrument: NonNullable<Track['instrument']> } => {
      const t = track(id);
      if (t.kind !== 'instrument' || !t.instrument) {
        throw new ApiError(422, `${t.name} is not an instrument track.`);
      }
      return t as Track & { instrument: NonNullable<Track['instrument']> };
    };
    const plugin = (id: string): PluginInfo => {
      const found = this.catalogue.find(p => p.id === id);
      if (!found?.isInstrument) {
        throw new ApiError(422, `No instrument ${id} in the plugin catalogue.`);
      }
      return found;
    };
    const id = (prefix: string) => `${prefix}_${this.nextId++}`;
    const folder = (folderId: string): TakeFolder => {
      const found = project.takeFolders.find(f => f.id === folderId);
      if (!found) {
        throw new ApiError(422, `No take folder ${folderId}.`);
      }
      return found;
    };
    const withFolder = (next: TakeFolder): Project => ({
      ...project,
      takeFolders: project.takeFolders.map(f => (f.id === next.id ? next : f))
    });
    const nonNegative = (value: number, what: string) => {
      if (!Number.isFinite(value) || value < 0) {
        throw new ApiError(422, `${what} must not be negative.`);
      }
    };

    switch (command.type) {
      case 'addTrack': {
        const count = project.tracks.length;
        const kind = command.kind ?? 'audio';
        const instrument = kind === 'instrument' ? plugin(command.pluginId ?? 'anode.synth') : null;
        const sameKind = project.tracks.filter(t => t.kind === kind).length;
        const added: Track = {
          id: `trk_${this.nextId++}`,
          name: command.name || `${KindNames[kind]} ${sameKind + 1}`,
          kind,
          color: TrackColors[count % TrackColors.length] ?? '#0090ff',
          input: { channel: command.stereo ? 2 * count + 1 : count + 1, stereo: command.stereo },
          recordArmed: false,
          inputMonitoring: false,
          strip: strip(),
          instrument: instrument
            ? { pluginId: instrument.id, name: instrument.name, parameters: defaultParameters(instrument), file: null }
            : null,
          midiInput: { device: null, channel: 0 },
          parentId: null,
          collapsed: false,
          notes: '',
          // An external MIDI track sends nowhere until setMidiIo picks a port (as the Server does).
          midiOutput: null
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
          // A removed stack's members stay, outside any stack.
          tracks: project.tracks
            .filter(t => t.id !== command.trackId)
            .map(t => (t.parentId === command.trackId ? { ...t, parentId: null } : t)),
          regions: project.regions.filter(r => r.trackId !== command.trackId),
          midiRegions: project.midiRegions.filter(r => r.trackId !== command.trackId),
          takeFolders: project.takeFolders.filter(f => f.trackId !== command.trackId)
        };
      case 'moveTrack': {
        const moved = track(command.trackId);
        const tracks = project.tracks.filter(t => t.id !== moved.id);
        tracks.splice(Math.min(tracks.length, Math.max(0, command.index)), 0, moved);
        return { ...project, tracks: groupStacks(tracks) };
      }
      case 'setTrack': {
        const t = track(command.trackId);
        return withTrack({
          ...t,
          name: command.name ?? t.name,
          color: command.color ?? t.color,
          input: command.input ?? t.input,
          recordArmed: t.kind === 'stack' ? false : (command.recordArmed ?? t.recordArmed),
          inputMonitoring: command.inputMonitoring ?? t.inputMonitoring,
          collapsed: command.collapsed ?? t.collapsed,
          notes: command.notes ?? t.notes
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
          fadeOut: 0,
          fadeInCurve: 0,
          fadeOutCurve: 0,
          muted: false
        };
        return { ...project, regions: [...project.regions, added] };
      }
      case 'moveRegion': {
        nonNegative(command.startTick, 'A region start');
        if (command.regionId.startsWith('tkf_')) {
          const f = folder(command.regionId);
          const trackId = command.trackId ?? f.trackId;
          if (track(trackId).kind !== 'audio') {
            throw new ApiError(422, 'A take folder goes on an audio track.');
          }
          return withFolder({ ...shiftFolder(f, Math.round(command.startTick) - f.startTick), trackId });
        }
        if (isMidiRegionId(command.regionId)) {
          const r = midiRegion(command.regionId);
          const trackId = command.trackId ?? r.trackId;
          midiTrack(trackId);
          return withMidiRegion({ ...r, startTick: Math.round(command.startTick), trackId });
        }
        const r = region(command.regionId);
        const trackId = command.trackId ?? r.trackId;
        if (track(trackId).kind !== 'audio') {
          throw new ApiError(422, 'An audio region goes on an audio track.');
        }
        return withRegion({ ...r, startTick: Math.round(command.startTick), trackId });
      }
      case 'resizeRegion': {
        if (isMidiRegionId(command.regionId)) {
          throw new ApiError(422, 'resizeRegion trims audio regions; a MIDI region takes resizeMidiRegion.');
        }
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
        if (isMidiRegionId(command.regionId)) {
          return this.splitMidiRegion(project, midiRegion(command.regionId), command.atTick);
        }
        if (command.regionId.startsWith('tkf_')) {
          throw new ApiError(422, 'A take folder does not split: flatten it first.');
        }
        const r = region(command.regionId);
        const endTick = audioEnd(project, r);
        if (command.atTick <= r.startTick || command.atTick >= endTick) {
          throw new ApiError(422, 'A split must fall inside the region.');
        }
        const left = framesBetween(project, r.startTick, command.atTick);
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
        const curve = (value: number | null | undefined, current: number) => {
          const c = value ?? current;
          if (!(c >= -1 && c <= 1)) {
            throw new ApiError(422, 'A fade curve is -1 to 1.');
          }
          return c;
        };
        return withRegion({
          ...r,
          name: command.name ?? r.name,
          gainDb: Math.min(30, Math.max(-96, command.gainDb ?? r.gainDb)),
          fadeIn,
          fadeOut,
          fadeInCurve: curve(command.fadeInCurve, r.fadeInCurve),
          fadeOutCurve: curve(command.fadeOutCurve, r.fadeOutCurve),
          muted: command.muted ?? r.muted
        });
      }
      case 'deleteRegions':
        command.regionIds.forEach(regionId =>
          regionId.startsWith('tkf_')
            ? folder(regionId)
            : isMidiRegionId(regionId)
              ? midiRegion(regionId)
              : region(regionId)
        );
        return {
          ...project,
          regions: project.regions.filter(r => !command.regionIds.includes(r.id)),
          midiRegions: project.midiRegions.filter(r => !command.regionIds.includes(r.id)),
          takeFolders: project.takeFolders.filter(f => !command.regionIds.includes(f.id))
        };
      case 'setTempo':
        if (!(command.bpm >= 20 && command.bpm <= 999)) {
          throw new ApiError(422, 'The tempo must be 20 to 999 bpm.');
        }
        return {
          ...project,
          settings: {
            ...project.settings,
            tempo: command.bpm,
            tempoEvents: project.settings.tempoEvents.map((e, i) => (i === 0 ? { ...e, bpm: command.bpm } : e))
          }
        };
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
            signature: { numerator: command.numerator, denominator: command.denominator },
            signatures: project.settings.signatures.map((e, i) =>
              i === 0 ? { ...e, numerator: command.numerator, denominator: command.denominator } : e
            )
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
      case 'setInstrument': {
        const t = instrumentTrack(command.trackId);
        const p = plugin(command.pluginId);
        return withTrack({
          ...t,
          instrument: { pluginId: p.id, name: p.name, parameters: defaultParameters(p), file: null }
        });
      }
      case 'setParameter': {
        const t = instrumentTrack(command.trackId);
        const info = this.catalogue
          .find(p => p.id === t.instrument.pluginId)
          ?.parameters.find(p => p.id === command.parameterId);
        if (!info || !Number.isFinite(command.value)) {
          throw new ApiError(422, `${t.instrument.name} has no parameter ${command.parameterId}.`);
        }
        return withTrack({
          ...t,
          instrument: {
            ...t.instrument,
            parameters: { ...t.instrument.parameters, [info.id]: clampParameter(info, command.value) }
          }
        });
      }
      case 'loadInstrumentFile': {
        const t = instrumentTrack(command.trackId);
        const types = this.catalogue.find(p => p.id === t.instrument.pluginId)?.fileTypes ?? [];
        const path = command.path.trim();
        if (!types.some(type => path.toLowerCase().endsWith(type.toLowerCase()))) {
          throw new ApiError(
            422,
            types.length
              ? `${t.instrument.name} loads ${types.join(', ')} files, not ${path}.`
              : `${t.instrument.name} loads no files.`
          );
        }
        const inside = path.startsWith(`${project.path}/`) ? path.slice(project.path.length + 1) : path;
        return withTrack({ ...t, instrument: { ...t.instrument, file: inside } });
      }
      case 'setMidiIo': {
        const t = midiTrack(command.trackId);
        const input = command.input ?? t.midiInput;
        if (!Number.isInteger(input.channel) || input.channel < 0 || input.channel > 16) {
          throw new ApiError(422, 'A MIDI input channel is 0 (all) or 1…16.');
        }
        const output = command.output ?? t.midiOutput;
        if (command.output && t.kind !== 'midi') {
          throw new ApiError(422, 'Only an external MIDI track sends to a MIDI output.');
        }
        if (output && (!Number.isInteger(output.channel) || output.channel < 1 || output.channel > 16)) {
          throw new ApiError(422, 'A MIDI output channel is 1…16.');
        }
        return withTrack({ ...t, midiInput: input, midiOutput: output });
      }
      case 'addMidiRegion': {
        const t = midiTrack(command.trackId);
        nonNegative(command.startTick, 'A region start');
        if (!(command.lengthTicks > 0)) {
          throw new ApiError(422, 'A MIDI region needs a length.');
        }
        const added: MidiRegion = {
          id: `mid_${this.nextId++}`,
          trackId: t.id,
          name: command.name || t.name,
          startTick: Math.round(command.startTick),
          lengthTicks: Math.round(command.lengthTicks),
          playback: { ...NeutralPlayback },
          notes: [],
          controls: [],
          muted: false
        };
        return { ...project, midiRegions: [...project.midiRegions, added] };
      }
      case 'resizeMidiRegion': {
        const r = midiRegion(command.regionId);
        nonNegative(command.startTick, 'A region start');
        if (!(command.lengthTicks > 0)) {
          throw new ApiError(422, 'A MIDI region needs a length.');
        }
        // Its notes stay where they are in time.
        const shift = Math.round(command.startTick) - r.startTick;
        return withMidiRegion({
          ...r,
          startTick: Math.round(command.startTick),
          lengthTicks: Math.round(command.lengthTicks),
          notes: r.notes.map(n => ({ ...n, startTick: n.startTick - shift })),
          controls: r.controls.map(c => ({ ...c, tick: c.tick - shift }))
        });
      }
      case 'setMidiRegion': {
        const r = midiRegion(command.regionId);
        const p = r.playback;
        const playback = {
          transpose: command.transpose ?? p.transpose,
          velocity: command.velocity ?? p.velocity,
          quantizeTicks: command.quantizeTicks ?? p.quantizeTicks,
          quantizeStrength: command.quantizeStrength ?? p.quantizeStrength,
          swing: command.swing ?? p.swing
        };
        if (Math.abs(playback.transpose) > 96 || Math.abs(playback.velocity) > 127) {
          throw new ApiError(422, 'Transpose is −96…96 semitones and velocity −127…127.');
        }
        if (playback.quantizeTicks < 0 || !inUnit(playback.quantizeStrength) || !inUnit(playback.swing)) {
          throw new ApiError(422, 'Quantise needs a grid of 0 or more ticks, and strength and swing of 0…1.');
        }
        return withMidiRegion({ ...r, name: command.name ?? r.name, playback, muted: command.muted ?? r.muted });
      }
      case 'addNotes': {
        const r = midiRegion(command.regionId);
        const added: Note[] = command.notes.map(n => {
          checkNote(n);
          return {
            id: this.nextId++,
            ...n,
            startTick: Math.round(n.startTick),
            lengthTicks: Math.round(n.lengthTicks)
          };
        });
        return withMidiRegion({ ...r, notes: sortNotes([...r.notes, ...added]) });
      }
      case 'editNotes': {
        const r = midiRegion(command.regionId);
        const changes = new Map(command.changes.map(c => [c.id, c]));
        for (const id of changes.keys()) {
          if (!r.notes.some(n => n.id === id)) {
            throw new ApiError(422, `${r.name} has no note ${id}.`);
          }
        }
        const notes = r.notes.map(n => {
          const c = changes.get(n.id);
          if (!c) {
            return n;
          }
          const next: Note = {
            id: n.id,
            pitch: c.pitch ?? n.pitch,
            velocity: c.velocity ?? n.velocity,
            channel: c.channel ?? n.channel,
            startTick: Math.round(c.startTick ?? n.startTick),
            lengthTicks: Math.round(c.lengthTicks ?? n.lengthTicks)
          };
          checkNote(next, true);
          return next;
        });
        return withMidiRegion({ ...r, notes: sortNotes(notes) });
      }
      case 'deleteNotes': {
        const r = midiRegion(command.regionId);
        const ids = new Set(command.noteIds);
        return withMidiRegion({ ...r, notes: r.notes.filter(n => !ids.has(n.id)) });
      }
      case 'quantizeNotes': {
        const r = midiRegion(command.regionId);
        if (!(command.gridTicks > 0) || !inUnit(command.strength) || !inUnit(command.swing)) {
          throw new ApiError(422, 'Quantise needs a grid of more than 0 ticks, and strength and swing of 0…1.');
        }
        const ids = command.noteIds ? new Set(command.noteIds) : undefined;
        return withMidiRegion({
          ...r,
          notes: sortNotes(quantizeNotes(r, command.gridTicks, command.strength, command.swing, ids))
        });
      }
      case 'addControls': {
        const r = midiRegion(command.regionId);
        const added = command.events.map(e => {
          const range = e.kind === 'pitchBend' ? [-8192, 8191] : [0, 127];
          if (e.value < (range[0] ?? 0) || e.value > (range[1] ?? 127) || e.channel < 1 || e.channel > 16) {
            throw new ApiError(422, `A ${e.kind} event is out of range.`);
          }
          return { id: this.nextId++, ...e, tick: Math.round(e.tick) };
        });
        return withMidiRegion({ ...r, controls: [...r.controls, ...added].sort((a, b) => a.tick - b.tick) });
      }
      case 'deleteControls': {
        const r = midiRegion(command.regionId);
        const ids = new Set(command.eventIds);
        return withMidiRegion({ ...r, controls: r.controls.filter(c => !ids.has(c.id)) });
      }
      case 'setMidiRecording':
        return { ...project, settings: { ...project.settings, mergeMidiRecording: command.merge } };
      case 'setTempoEvents': {
        const events = command.events.map(e => ({ ...e, tick: Math.round(e.tick) }));
        if (events[0]?.tick !== 0) {
          throw new ApiError(422, 'The tempo track starts at tick 0.');
        }
        events.forEach((e, i) => {
          if (!(e.bpm >= 20 && e.bpm <= 999)) {
            throw new ApiError(422, 'Tempos are 20 to 999 bpm.');
          }
          if (i > 0 && e.tick <= (events[i - 1]?.tick ?? 0)) {
            throw new ApiError(422, 'Tempo events are in order, one per tick.');
          }
        });
        return {
          ...project,
          settings: { ...project.settings, tempoEvents: events, tempo: events[0].bpm }
        };
      }
      case 'setSignatures': {
        const events = command.events.map(e => ({ ...e, tick: Math.round(e.tick) }));
        const first = events[0];
        if (first?.tick !== 0) {
          throw new ApiError(422, 'The signature track starts at tick 0.');
        }
        for (const [i, e] of events.entries()) {
          if (e.numerator < 1 || e.numerator > 32 || ![1, 2, 4, 8, 16, 32].includes(e.denominator)) {
            throw new ApiError(422, `${e.numerator}/${e.denominator} is not a time signature Anode supports.`);
          }
          const previous = events[i - 1];
          if (previous) {
            const bar = ((Ppq * 4) / previous.denominator) * previous.numerator;
            if (e.tick <= previous.tick || (e.tick - previous.tick) % bar !== 0) {
              throw new ApiError(422, 'A signature change goes on a bar line of the signature before it.');
            }
          }
        }
        return {
          ...project,
          settings: {
            ...project.settings,
            signatures: events,
            signature: { numerator: first.numerator, denominator: first.denominator }
          }
        };
      }
      case 'setKeys': {
        const events = command.events.map(e => ({ ...e, tick: Math.round(e.tick) }));
        if (events[0]?.tick !== 0 || events.some(e => e.tonic < 0 || e.tonic > 11 || !Number.isInteger(e.tonic))) {
          throw new ApiError(422, 'The key track starts at tick 0, with tonics 0 (C) to 11 (B).');
        }
        return { ...project, settings: { ...project.settings, keys: events.sort((a, b) => a.tick - b.tick) } };
      }
      case 'addMarker': {
        nonNegative(command.tick, 'A marker');
        const added = {
          id: `mrk_${this.nextId++}`,
          name: command.name || `Marker ${project.markers.length + 1}`,
          tick: Math.round(command.tick),
          lengthTicks: Math.max(0, Math.round(command.lengthTicks)),
          color: command.color ?? null,
          text: ''
        };
        return { ...project, markers: [...project.markers, added].sort((a, b) => a.tick - b.tick) };
      }
      case 'setMarker': {
        const m = project.markers.find(x => x.id === command.markerId);
        if (!m) {
          throw new ApiError(422, `No marker ${command.markerId}.`);
        }
        const next = {
          ...m,
          name: command.name ?? m.name,
          tick: Math.round(command.tick ?? m.tick),
          lengthTicks: Math.round(command.lengthTicks ?? m.lengthTicks),
          color: command.color ?? m.color ?? null,
          text: command.text ?? m.text
        };
        nonNegative(next.tick, 'A marker');
        nonNegative(next.lengthTicks, 'A marker length');
        return {
          ...project,
          markers: project.markers.map(x => (x.id === m.id ? next : x)).sort((a, b) => a.tick - b.tick)
        };
      }
      case 'deleteMarkers':
        command.markerIds.forEach(id => {
          if (!project.markers.some(m => m.id === id)) {
            throw new ApiError(422, `No marker ${id}.`);
          }
        });
        return { ...project, markers: project.markers.filter(m => !command.markerIds.includes(m.id)) };
      case 'addSection': {
        const added = {
          id: `sec_${this.nextId++}`,
          name: command.name || 'Section',
          startTick: Math.round(command.startTick),
          lengthTicks: Math.round(command.lengthTicks),
          color: command.color ?? SectionColors[project.arrangement.length % SectionColors.length] ?? '#f5a623'
        };
        const arrangement = sortSections([...project.arrangement, added]);
        checkSections(arrangement);
        return { ...project, arrangement };
      }
      case 'setSection': {
        const section = project.arrangement.find(x => x.id === command.sectionId);
        if (!section) {
          throw new ApiError(422, `No section ${command.sectionId}.`);
        }
        const arrangement = project.arrangement.map(x =>
          x.id === section.id
            ? {
                ...x,
                name: command.name ?? x.name,
                lengthTicks: Math.round(command.lengthTicks ?? x.lengthTicks),
                color: command.color ?? x.color
              }
            : x
        );
        checkSections(arrangement);
        return { ...project, arrangement };
      }
      case 'deleteSections':
        return deleteSections(project, command.sectionIds, command.removeContent);
      case 'moveSection':
        return moveSection(project, command.sectionId, Math.round(command.toTick), command.copy, id);
      case 'setComp': {
        const f = folder(command.folderId);
        return withFolder({ ...f, comp: checkComp(f, command.segments) });
      }
      case 'selectTake': {
        const f = folder(command.folderId);
        if (!f.takes.some(t => t.id === command.takeId)) {
          throw new ApiError(422, `${f.name} has no take ${command.takeId}.`);
        }
        return withFolder({
          ...f,
          comp: [{ startTick: f.startTick, endTick: f.startTick + f.lengthTicks, takeId: command.takeId }]
        });
      }
      case 'setTakeFolder': {
        const f = folder(command.folderId);
        if (command.takeName !== undefined && command.takeName !== null && !command.takeId) {
          throw new ApiError(422, 'Renaming a take needs its takeId.');
        }
        if (command.takeId && !f.takes.some(t => t.id === command.takeId)) {
          throw new ApiError(422, `${f.name} has no take ${command.takeId}.`);
        }
        return withFolder({
          ...f,
          name: command.name ?? f.name,
          crossfade: Math.max(0, Math.round(command.crossfade ?? f.crossfade)),
          muted: command.muted ?? f.muted,
          takes: f.takes.map(t => (t.id === command.takeId && command.takeName ? { ...t, name: command.takeName } : t))
        });
      }
      case 'deleteTake': {
        const f = folder(command.folderId);
        if (!f.takes.some(t => t.id === command.takeId)) {
          throw new ApiError(422, `${f.name} has no take ${command.takeId}.`);
        }
        if (f.takes.length === 1) {
          throw new ApiError(422, "A take folder's last take cannot be deleted.");
        }
        return withFolder({
          ...f,
          takes: f.takes.filter(t => t.id !== command.takeId),
          comp: f.comp.filter(s => s.takeId !== command.takeId)
        });
      }
      case 'flattenTakeFolder':
        return flattenTakeFolder(project, command.folderId, id);
      case 'packTakeFolder':
        return packTakeFolder(project, command.regionIds, command.name, id);
      case 'setRecording': {
        const r = project.settings.recording;
        const next = {
          cycleMode: command.cycleMode ?? r.cycleMode,
          replace: command.replace ?? r.replace,
          autopunch: command.autopunch ?? r.autopunch,
          punchInTick: Math.round(command.punchInTick ?? r.punchInTick),
          punchOutTick: Math.round(command.punchOutTick ?? r.punchOutTick)
        };
        if (next.punchInTick < 0 || next.punchOutTick <= next.punchInTick) {
          throw new ApiError(422, 'The punch range must end after it starts.');
        }
        return { ...project, settings: { ...project.settings, recording: next } };
      }
      case 'createStack': {
        const members = command.trackIds.map(t => track(t));
        if (members.length === 0) {
          throw new ApiError(422, 'A stack needs tracks.');
        }
        if (members.some(m => m.kind === 'stack')) {
          throw new ApiError(422, 'Stacks do not nest.');
        }
        const first = Math.min(...members.map(m => project.tracks.indexOf(m)));
        const stack: Track = {
          id: `trk_${this.nextId++}`,
          name: command.name || 'Track Stack',
          kind: 'stack',
          color: members[0]?.color ?? '#8e8e93',
          input: { channel: 0, stereo: true },
          recordArmed: false,
          inputMonitoring: false,
          strip: strip(),
          instrument: null,
          midiInput: { device: null, channel: 0 },
          midiOutput: null,
          parentId: null,
          collapsed: false,
          notes: ''
        };
        const ids = new Set(members.map(m => m.id));
        const rest = project.tracks.filter(t => !ids.has(t.id));
        const inserted = rest.slice(0, first).length;
        const tracks = [
          ...rest.slice(0, inserted),
          stack,
          ...members.map(m => ({ ...m, parentId: stack.id })),
          ...rest.slice(inserted)
        ];
        // Stacks the members left behind empty are removed with them.
        const emptied = new Set(
          project.tracks.filter(t => t.kind === 'stack' && !tracks.some(m => m.parentId === t.id)).map(t => t.id)
        );
        return { ...project, tracks: groupStacks(tracks.filter(t => !emptied.has(t.id))) };
      }
      case 'removeStack': {
        const stack = track(command.stackId);
        if (stack.kind !== 'stack') {
          throw new ApiError(422, `${stack.name} is not a track stack.`);
        }
        return {
          ...project,
          tracks: project.tracks
            .filter(t => t.id !== stack.id)
            .map(t => (t.parentId === stack.id ? { ...t, parentId: null } : t))
        };
      }
      case 'setTrackParent': {
        const t = track(command.trackId);
        if (t.kind === 'stack') {
          throw new ApiError(422, 'Stacks do not nest.');
        }
        if (command.parentId) {
          const parent = track(command.parentId);
          if (parent.kind !== 'stack') {
            throw new ApiError(422, `${parent.name} is not a track stack.`);
          }
        }
        const moved = { ...t, parentId: command.parentId ?? null };
        const others = project.tracks.filter(x => x.id !== t.id);
        const anchor = command.parentId ?? t.parentId;
        // Into a stack: after its last member; out of one: right after the stack's members.
        let index = others.findIndex(x => x.id === anchor);
        while (index >= 0 && others[index + 1]?.parentId === anchor) {
          index++;
        }
        const tracks = [...others];
        tracks.splice(index < 0 ? tracks.length : index + 1, 0, moved);
        return { ...project, tracks: groupStacks(tracks) };
      }
      case 'setProjectNotes':
        return { ...project, notes: command.text };
      case 'joinRegions':
        return joinRegions(project, command.regionIds);
      case 'copyRegions':
        return copyRegions(project, command.regionIds, Math.round(command.offsetTicks), command.trackId, id);
    }
  }

  /**
   * Splits a MIDI region at `atTick`: notes and controls that start before it stay in the first part (a note that
   * crosses the split is cut there when it plays), the rest move to the second, relative to its start.
   */
  private splitMidiRegion(project: Project, r: MidiRegion, atTick: number): Project {
    const at = Math.round(atTick);
    if (at <= r.startTick || at >= r.startTick + r.lengthTicks) {
      throw new ApiError(422, 'A split must fall inside the region.');
    }
    const offset = at - r.startTick;
    const first: MidiRegion = {
      ...r,
      lengthTicks: offset,
      notes: r.notes.filter(n => n.startTick < offset),
      controls: r.controls.filter(c => c.tick < offset)
    };
    const second: MidiRegion = {
      ...r,
      id: `mid_${this.nextId++}`,
      startTick: at,
      lengthTicks: r.lengthTicks - offset,
      notes: r.notes.filter(n => n.startTick >= offset).map(n => ({ ...n, startTick: n.startTick - offset })),
      controls: r.controls.filter(c => c.tick >= offset).map(c => ({ ...c, tick: c.tick - offset }))
    };
    return {
      ...project,
      midiRegions: [...project.midiRegions.map(m => (m.id === r.id ? first : m)), second]
    };
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
        endTick: DefaultEndTick,
        mergeMidiRecording: false,
        tempoEvents: [{ tick: 0, bpm: 112, curve: 'step' }],
        signatures: [{ tick: 0, numerator: 4, denominator: 4 }],
        keys: [{ tick: 0, tonic: 0, mode: 'major' }],
        recording: {
          cycleMode: 'takeFolder',
          replace: false,
          autopunch: false,
          punchInTick: 0,
          punchOutTick: 4 * 4 * Ppq
        }
      },
      master: strip(),
      tracks: [],
      regions: [],
      audioFiles: [],
      midiRegions: [],
      takeFolders: [],
      markers: [],
      arrangement: [],
      notes: ''
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
    project = this.demoMidi(project);
    project = this.demoArrangement(project);
    this.disk.set(disk, structuredClone(project));
    this.recent = [{ id: project.id, name: project.name, path: disk, modified: new Date(this.now()).toISOString() }];
    this.project = project;
  }

  /** The demo's arrangement, markers, a vocal take folder of two takes, and a note: Phase 3's material. */
  private demoArrangement(base: Project): Project {
    const bar = 4 * Ppq;
    let project = base;
    for (const [name, start, bars] of [
      ['Intro', 0, 4],
      ['Verse', 4, 8],
      ['Chorus', 12, 8]
    ] as const) {
      project = this.applyCommand(project, {
        type: 'addSection',
        name,
        startTick: start * bar,
        lengthTicks: bars * bar
      });
    }
    project = this.applyCommand(project, { type: 'addMarker', name: 'Vocals in', tick: 4 * bar, lengthTicks: 0 });
    project = this.applyCommand(project, {
      type: 'addMarker',
      name: 'Drop',
      tick: 12 * bar,
      lengthTicks: 0,
      color: '#d0021b'
    });
    const vocals = project.regions.find(r => r.name === 'Lead vocal');
    if (vocals) {
      project = this.applyCommand(project, {
        type: 'addRegion',
        trackId: vocals.trackId,
        fileId: vocals.fileId,
        startTick: vocals.startTick,
        fileOffset: 48_000,
        length: vocals.length - 48_000
      });
      const second = project.regions.at(-1);
      if (second) {
        project = this.applyCommand(project, {
          type: 'packTakeFolder',
          regionIds: [vocals.id, second.id],
          name: 'Lead vocal'
        });
      }
    }
    return { ...project, notes: 'Demo session.\nTry Quick Swipe comping on the Lead vocal takes.' };
  }

  /** The demo's instrument tracks: a synth with chords and a melody, and a sampler with a drum pattern. */
  private demoMidi(base: Project): Project {
    let project = this.applyCommand(base, { type: 'addTrack', name: 'Keys', stereo: true, kind: 'instrument' });
    project = this.applyCommand(project, {
      type: 'addTrack',
      name: 'Kit',
      stereo: true,
      kind: 'instrument',
      pluginId: 'anode.sampler'
    });
    const [keys, kit] = project.tracks.slice(-2);
    if (!keys || !kit) {
      return project;
    }

    const bar = 4 * Ppq;
    const chords = [
      [57, 60, 64],
      [53, 57, 60],
      [48, 52, 55],
      [55, 59, 62]
    ];
    project = this.applyCommand(project, {
      type: 'addMidiRegion',
      trackId: keys.id,
      startTick: 0,
      lengthTicks: 4 * bar
    });
    const chordRegion = project.midiRegions.at(-1)?.id ?? '';
    project = this.applyCommand(project, {
      type: 'addNotes',
      regionId: chordRegion,
      notes: chords.flatMap((chord, i) =>
        chord.map((pitch, k) => ({
          pitch,
          velocity: 70 + 12 * k + 4 * i,
          channel: 1,
          startTick: i * bar + 25 * k,
          lengthTicks: bar - Ppq / 4
        }))
      )
    });
    project = this.applyCommand(project, {
      type: 'addMidiRegion',
      trackId: keys.id,
      startTick: 4 * bar,
      lengthTicks: 4 * bar,
      name: 'Keys melody'
    });
    const melody = project.midiRegions.at(-1)?.id ?? '';
    const line = [69, 72, 71, 67, 69, 64, 65, 67, 69, 72, 74, 76, 74, 72, 71, 69];
    project = this.applyCommand(project, {
      type: 'addNotes',
      regionId: melody,
      notes: line.map((pitch, i) => ({
        pitch,
        velocity: 40 + ((i * 29) % 87),
        channel: 1,
        startTick: i * Ppq + (i % 3 === 1 ? 37 : 0),
        lengthTicks: i % 4 === 3 ? (Ppq * 9) / 10 : Ppq / 2
      }))
    });
    project = this.applyCommand(project, {
      type: 'addMidiRegion',
      trackId: kit.id,
      startTick: 0,
      lengthTicks: 4 * bar
    });
    const beat = project.midiRegions.at(-1)?.id ?? '';
    const hits: { pitch: number; velocity: number; startTick: number }[] = [];
    for (let sixteenth = 0; sixteenth < 64; sixteenth++) {
      const tick = (sixteenth * Ppq) / 4;
      if (sixteenth % 8 === 0 || sixteenth % 16 === 10) {
        hits.push({ pitch: 36, velocity: 110, startTick: tick });
      }
      if (sixteenth % 8 === 4) {
        hits.push({ pitch: 38, velocity: 100, startTick: tick });
      }
      hits.push({ pitch: 42, velocity: sixteenth % 2 ? 55 : 85, startTick: tick });
    }
    project = this.applyCommand(project, {
      type: 'addNotes',
      regionId: beat,
      notes: hits.map(hit => ({ ...hit, channel: 10, lengthTicks: Ppq / 8 }))
    });
    return this.applyCommand(project, { type: 'loadInstrumentFile', trackId: kit.id, path: 'Samples/808 Kit.sfz' });
  }
}

/** A blob's bytes (FileReader where `Blob.arrayBuffer` is missing, as in jsdom). */
function readBytes(blob: Blob): Promise<ArrayBuffer> {
  if (typeof blob.arrayBuffer === 'function') {
    return blob.arrayBuffer();
  }

  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as ArrayBuffer);
    reader.onerror = () => reject(reader.error ?? new Error('Could not read the file.'));
    reader.readAsArrayBuffer(blob);
  });
}

function strip(): ChannelStrip {
  return { volumeDb: 0, pan: 0, mute: false, solo: false };
}

/**
 * The control a batch of exactly one `setStrip` or `setParameter` sets ("trk_1:volumeDb", "trk_1#filter.cutoff"), for
 * undo coalescing.
 */
function coalesceKey(commands: readonly EditCommand[]): string | undefined {
  const [only] = commands;
  if (commands.length !== 1) {
    return undefined;
  }
  if (only?.type === 'setParameter') {
    return `${only.trackId}#${only.parameterId}`;
  }
  if (only?.type !== 'setStrip') {
    return undefined;
  }

  const fields = (['volumeDb', 'pan', 'mute', 'solo'] as const).filter(f => only[f] !== undefined && only[f] !== null);
  return `${only.trackId ?? 'master'}:${fields.join(',')}`;
}

/** Logic's arrangement marker colours, in the order new sections take them. */
const SectionColors = ['#f5a623', '#4a90e2', '#d0021b', '#7ed321', '#9013fe', '#50e3c2', '#f8e71c', '#bd10e0'];

const KindNames: Record<Track['kind'], string> = { audio: 'Audio', instrument: 'Inst', midi: 'MIDI', stack: 'Stack' };

/** MIDI region ids are `mid_…` (docs/03-phase-2.md). */
function isMidiRegionId(id: string): boolean {
  return id.startsWith('mid_');
}

function inUnit(value: number): boolean {
  return Number.isFinite(value) && value >= 0 && value <= 1;
}

/** Refuses a note out of the contract's ranges. Positions may be negative (a trimmed start keeps notes in time). */
function checkNote(note: Omit<Note, 'id'>, existing = false): void {
  if (!Number.isInteger(note.pitch) || note.pitch < 0 || note.pitch > 127) {
    throw new ApiError(422, 'A note pitch is 0…127.');
  }
  if (!Number.isInteger(note.velocity) || note.velocity < 1 || note.velocity > 127) {
    throw new ApiError(422, 'A note velocity is 1…127.');
  }
  if (!Number.isInteger(note.channel) || note.channel < 1 || note.channel > 16) {
    throw new ApiError(422, 'A note channel is 1…16.');
  }
  if (!(note.lengthTicks > 0) || (!existing && !Number.isFinite(note.startTick))) {
    throw new ApiError(422, 'A note needs a start and a length.');
  }
}

function sortNotes(notes: Note[]): Note[] {
  return notes.sort((a, b) => a.startTick - b.startTick || a.pitch - b.pitch || a.id - b.id);
}

/** Ticks per bar of the project's signature. */
function barTicks(project: Project): number {
  const { numerator, denominator } = project.settings.signature;
  return ((Ppq * 4) / denominator) * numerator;
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
  const afterMidi = new Set(after.midiRegions.map(r => r.id));
  for (const region of before.midiRegions) {
    if (!afterMidi.has(region.id)) {
      ops.push({ op: 'midiRegionRemoved', id: region.id });
    }
  }
  const beforeMidi = new Map(before.midiRegions.map(r => [r.id, r]));
  for (const region of after.midiRegions) {
    if (!same(beforeMidi.get(region.id), region)) {
      ops.push({ op: 'midiRegionUpserted', region });
    }
  }
  diffList(
    before.takeFolders,
    after.takeFolders,
    same,
    id => ops.push({ op: 'takeFolderRemoved', id }),
    folder => ops.push({ op: 'takeFolderUpserted', folder })
  );
  diffList(
    before.markers,
    after.markers,
    same,
    id => ops.push({ op: 'markerRemoved', id }),
    marker => ops.push({ op: 'markerUpserted', marker })
  );
  diffList(
    before.arrangement,
    after.arrangement,
    same,
    id => ops.push({ op: 'sectionRemoved', id }),
    section => ops.push({ op: 'sectionUpserted', section })
  );
  if (before.notes !== after.notes) {
    ops.push({ op: 'notesChanged', text: after.notes });
  }
  return ops;
}

/** Removals, then upserts, of one entity list. */
function diffList<T extends { id: string }>(
  before: readonly T[],
  after: readonly T[],
  same: (a: unknown, b: unknown) => boolean,
  removed: (id: string) => void,
  upserted: (item: T) => void
): void {
  const kept = new Set(after.map(x => x.id));
  for (const item of before) {
    if (!kept.has(item.id)) {
      removed(item.id);
    }
  }
  const previous = new Map(before.map(x => [x.id, x]));
  for (const item of after) {
    if (!same(previous.get(item.id), item)) {
      upserted(item);
    }
  }
}
