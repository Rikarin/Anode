import {
  type AddLoopRequest,
  type AddToLoopLibraryRequest,
  ApiError,
  type AudioFile,
  type AudioRegion,
  type Bookmarks,
  type BounceInPlaceRequest,
  type BounceRequest,
  type ChannelStrip,
  type CommandBatch,
  type CommandResult,
  type DeltaOp,
  type DirectoryListing,
  type EditCommand,
  type FileAnalysis,
  type Insert,
  type InsertReading,
  type IoLabel,
  type IoLabels,
  type Job,
  type KeyCommandSet,
  type LaunchRequest,
  type LiveMidiEvent,
  type LiveRecordRequest,
  type LoopInfo,
  type LoopPage,
  type MidiDevices,
  type MidiRegion,
  type Note,
  type Pack,
  type PatchInfo,
  type PatternRegion,
  type PluginEditorRequest,
  type PluginInfo,
  type ProcessRequest,
  type Project,
  type ProjectAlternative,
  type ProjectBackup,
  type ProjectDelta,
  type ProjectSummary,
  type SavePatchRequest,
  type Screenset,
  type ServerMessage,
  type StopRequest,
  type SystemInfo,
  type TakeFolder,
  type Track,
  type TrackMeter,
  type TransportRequest,
  type TransportStatus,
  type UndoHistory
} from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import { clampParameter, defaultParameters } from '../../instruments/parameters';
import { type CellChange, Launcher } from '../../live-loops/launcher';
import { isNoteOff, isNoteOn, NeutralPlayback, quantizeNotes } from '../../midi/midi-math';
import { TrackColors } from '../../state/track-colors';
import type { AnodeApi, CreateProject, LoopSearch } from '../api';
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
  meterOf,
  moveSection,
  packTakeFolder,
  replaceRange,
  shiftFolder,
  sortSections,
  tempoOf
} from './fake-arrange';
import { fakePeaks, hash, readWavHeader } from './fake-audio';
import { fakeFileExists, FakeHome, fileKind, listFakeFolder } from './fake-files';
import { adaptTempo, applyFlexCommand, fakeAnalysis, type FlexContext, timingOf } from './fake-flex';
import { type FakeLoop, FakeLoopLibrary, keyInterval } from './fake-loops';
import {
  applyMixCommand,
  applyStrip,
  forgetTrack,
  linkRecordArm,
  type MixContext,
  patchOf,
  type StoredPatch
} from './fake-mix';
import {
  applyMidiEffectCommand,
  applyPhase6Command,
  copyPatternRegions,
  deletePatternRegions,
  type FakeKit,
  FakeKits,
  isPatternRegionId,
  isPhase6Command,
  keepSavedValues,
  loadInsertFile,
  movePatternRegion,
  type Phase6Context,
  saveSmartControls,
  setCell
} from './fake-phase6';
import { CrashyPluginId, FakeCatalogue, FakeEffects, FakeMidiDevices } from './fake-plugins';
import { FakeMidiEffects, FakeT2Effects, FakeT2Instruments, RemixFxId, withMacros } from './fake-plugins-phase6';

/** Ticks per quarter note (the contract's time base). */
const Ppq = 960;
/** A new project's end marker: 32 bars of 4/4. */
const DefaultEndTick = 32 * 4 * Ppq;
/** Consecutive `setStrip` batches on one control within this long are one undo step (docs/02-phase-1.md). */
const CoalesceMs = 1_000;
/** How often the fake engine reports the transport and meters while it runs. */
const FrameMs = 33;
/** How often a job moves on in the dev mode (job events at most four times a second), and its steps to done. */
const JobStepMs = 250;
const JobSteps = 6;
/** The sample rates a bounce may convert to. */
const BounceRates = [44_100, 48_000, 88_200, 96_000, 176_400, 192_000];

/** A job the fake engine works through, a step at a time. */
interface FakeJob {
  job: Job;
  steps: number;
  done: number;
  bounce?: BounceRequest;
  trackId?: string;
  /** What finishing does (Phase 5's jobs): the files it wrote. */
  finish?: () => string[];
}

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

  /** Built-in instruments and effects first, then third-party plugins, as the host lists them. */
  private readonly catalogue: PluginInfo[] = structuredClone(
    [...FakeCatalogue, ...FakeT2Instruments, ...FakeEffects, ...FakeT2Effects, ...FakeMidiEffects]
      .map(withMacros)
      .sort((a, b) => Number(a.format !== 'builtin') - Number(b.format !== 'builtin'))
  );
  /** Drum Machine Designer kits (patches of kind `stack`), by id. */
  private readonly kits = new Map<string, FakeKit>(FakeKits.map(k => [k.info.id, structuredClone(k)]));
  /** The loop index: installed packs, user loops, favourites (Phase 6). */
  readonly loopLibrary = new FakeLoopLibrary();
  /** The loop the preview channel plays in sync, and how (tests). */
  previewSync: { loopId: string; tempoRatio: number; transpose: number; startOnBar: boolean } | null = null;
  /** Live Loops' launcher: cell states at quantise boundaries, reported as `cell` events. */
  private readonly launcher = new Launcher(() => {
    const project = this.project;
    const tempo = project ? tempoOf(project) : null;
    return {
      meter: project ? meterOf(project) : new MeterMap([]),
      quantize: project?.liveLoops.quantize ?? 'bar',
      // 1 ms at the tempo there.
      lateTicks: tick => (tempo ? (tempo.bpmAt(tick) * Ppq * 0.001) / 60 : 0)
    };
  });
  /** A performance recording (cells and Remix FX into the Tracks area): where cells played, and Remix FX moves. */
  private performance: {
    plays: { cellId: string; from: number; to: number | null }[];
    remix: { tick: number; insertId: string; parameterId: string; value: number }[];
  } | null = null;
  /** Recording into a cell: its slot, from where, and the notes held and played. */
  private cellRecording: {
    trackId: string;
    sceneId: string;
    from: number;
    held: Map<number, { start: number; velocity: number }>;
    notes: { pitch: number; velocity: number; startTick: number; lengthTicks: number }[];
  } | null = null;
  private readonly jobList = new Map<string, FakeJob>();
  /** File analyses (the package's `Analysis/<fileId>.json`), by file id. */
  private readonly analyses = new Map<string, FileAnalysis>();
  private jobTimer: ReturnType<typeof setInterval> | null = null;
  private readonly patchStore = new Map<string, StoredPatch>();
  private labels: IoLabels = { inputs: [], outputs: [], buses: [] };
  /** Plugin editor windows open, as `trackId/insertId` (`master/…`, `…/instrument`) (tests). */
  readonly editors = new Set<string>();
  /** Inserts the engine bypassed after their plugin crashed. */
  private readonly crashed = new Set<string>();
  /** Automation written this pass: the last tick per lane. */
  private readonly pass = new Map<string, number>();
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
          tempoEvents: [{ tick: 0, bpm: 120, curve: 'step', shape: 0 }],
          signatures: [{ tick: 0, numerator: 4, denominator: 4 }],
          keys: [{ tick: 0, tonic: 0, mode: 'major' }],
          recording: {
            cycleMode: 'takeFolder',
            replace: false,
            autopunch: false,
            punchInTick: 0,
            punchOutTick: 4 * 4 * Ppq
          },
          mixer: { pdc: 'all', lowLatency: false, lowLatencyLimitMs: 10 },
          smartTempo: { mode: 'keep', defaultFollow: 'off', analyzeImports: true },
          varispeed: { enabled: false, mode: 'speedOnly', speed: 1 }
        },
        master: strip(),
        tracks: [],
        regions: [],
        audioFiles: [],
        midiRegions: [],
        takeFolders: [],
        markers: [],
        arrangement: [],
        notes: '',
        groups: [],
        automation: [],
        patternRegions: [],
        liveLoops: { scenes: [], cells: [], quantize: 'bar', shown: false }
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
      let project = this.require();
      const saved = saveSmartControls(project);
      if (JSON.stringify(saved.tracks) !== JSON.stringify(project.tracks)) {
        // The controls' saved values become the edited ones (not an undo step).
        this.publish(project, saved);
        project = this.require();
      }
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
        after = this.applyCommand(keepSavedValues(after, command), command);
        this.notePerformance(command);
      }

      // Compare swaps saved and edited values without an undo step (its delta still reaches every client).
      if (batch.commands.every(c => c.type === 'compareSmartControls')) {
        return this.publish(before, after);
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
      if (this.cellRecording?.trackId === trackId && this.transportState !== 'stopped') {
        this.advance();
        this.captureCell(events);
      }
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
      this.previewSync = null;
    });
  }

  // ---- Phase 4: jobs, patches, I/O labels, plugin editors

  bounce(request: BounceRequest): Promise<Job> {
    return this.answer('Bounce', () => {
      const project = this.require();
      if (!request.fileName.trim() || /[\\/]/.test(request.fileName)) {
        throw new ApiError(422, 'A bounce needs a file name (without folders).');
      }
      if (![16, 24, 32].includes(request.bitDepth)) {
        throw new ApiError(422, 'The bit depth is 16, 24 or 32 (float).');
      }
      if (
        request.sampleRate !== null &&
        request.sampleRate !== undefined &&
        !BounceRates.includes(request.sampleRate)
      ) {
        throw new ApiError(422, `${request.sampleRate} Hz is not a sample rate Anode bounces to.`);
      }
      if (request.source === 'stems') {
        const ids = request.trackIds ?? [];
        if (ids.length === 0) {
          throw new ApiError(422, 'Choose the tracks to bounce as stems.');
        }
        for (const id of ids) {
          if (!project.tracks.some(t => t.id === id)) {
            throw new ApiError(422, `No track ${id}.`);
          }
        }
      }
      const [start, end] = this.bounceRange(project, request);
      if (start < 0 || end <= start) {
        throw new ApiError(422, 'The bounce range must end after it starts.');
      }
      if (request.loudnessLufs < -70 || request.loudnessLufs > 0) {
        throw new ApiError(422, 'The loudness target is −70 to 0 LUFS.');
      }
      if (request.peakCeilingDb < -20 || request.peakCeilingDb > 0) {
        throw new ApiError(422, 'The peak ceiling is −20 to 0 dB.');
      }
      return this.startJob('bounce', { bounce: structuredClone(request) }, request.realtime ? 2 : 1);
    });
  }

  freezeTrack(trackId: string): Promise<Job> {
    return this.answer('FreezeTrack', () => {
      const track = this.require().tracks.find(t => t.id === trackId);
      if (!track) {
        throw new ApiError(404, `No track ${trackId}.`);
      }
      if (track.kind === 'stack' || track.kind === 'aux' || track.kind === 'vca') {
        throw new ApiError(422, `${track.name} cannot be frozen: it has no source of its own.`);
      }
      if (track.frozenFileId) {
        throw new ApiError(422, `${track.name} is frozen already.`);
      }
      if ([...this.jobList.values()].some(j => j.trackId === trackId && isRunning(j.job))) {
        throw new ApiError(422, `${track.name} is being frozen.`);
      }
      return this.startJob('freeze', { trackId }, 1);
    });
  }

  job(id: string): Promise<Job> {
    return this.answer('GetJob', () => structuredClone(this.findJob(id).job));
  }

  cancelJob(id: string): Promise<Job> {
    return this.answer('CancelJob', () => {
      const entry = this.findJob(id);
      if (isRunning(entry.job)) {
        this.setJob(entry, { ...entry.job, state: 'cancelled', message: 'Cancelled.' });
      }
      return structuredClone(entry.job);
    });
  }

  patches(): Promise<PatchInfo[]> {
    return this.answer('ListPatches', () =>
      [...this.patchStore.values(), ...this.installedKits()]
        .map(p => ({ ...p.info }))
        .sort((a, b) => a.category.localeCompare(b.category) || a.name.localeCompare(b.name))
    );
  }

  savePatch(request: SavePatchRequest): Promise<PatchInfo> {
    return this.answer('SavePatch', () => {
      const track = this.require().tracks.find(t => t.id === request.trackId);
      if (!track) {
        throw new ApiError(404, `No track ${request.trackId}.`);
      }
      if (!request.name.trim()) {
        throw new ApiError(422, 'A patch needs a name.');
      }
      if (track.kind === 'stack' || track.kind === 'vca') {
        throw new ApiError(422, `${track.name} has no channel strip to save.`);
      }
      const info: PatchInfo = {
        id: `pat_${this.nextId++}`,
        name: request.name.trim(),
        category: request.category.trim() || 'User',
        kind: track.kind
      };
      this.patchStore.set(info.id, patchOf(track, info));
      return { ...info };
    });
  }

  deletePatch(id: string): Promise<void> {
    return this.answer('DeletePatch', () => {
      if (!this.patchStore.delete(id)) {
        throw new ApiError(404, `No patch ${id}.`);
      }
    });
  }

  ioLabels(): Promise<IoLabels> {
    return this.answer('GetIoLabels', () => structuredClone(this.labels));
  }

  saveIoLabels(labels: IoLabels): Promise<IoLabels> {
    return this.answer('SaveIoLabels', () => {
      const clean = (list: readonly IoLabel[], max: number, what: string): IoLabel[] => {
        const byNumber = new Map<number, string>();
        for (const label of list) {
          if (!Number.isInteger(label.number) || label.number < 1 || label.number > max) {
            throw new ApiError(422, `${what} are numbered 1 to ${max}.`);
          }
          const name = label.name.trim();
          if (name) {
            byNumber.set(label.number, name);
          }
        }
        return [...byNumber].sort(([a], [b]) => a - b).map(([number, name]) => ({ number, name }));
      };
      this.labels = {
        inputs: clean(labels.inputs, 64, 'Inputs'),
        outputs: clean(labels.outputs, 32, 'Output pairs'),
        buses: clean(labels.buses, 64, 'Buses')
      };
      return structuredClone(this.labels);
    });
  }

  openPluginEditor(request: PluginEditorRequest): Promise<void> {
    return this.answer('OpenPluginEditor', () => {
      const project = this.require();
      const track = request.trackId ? project.tracks.find(t => t.id === request.trackId) : null;
      if (request.trackId && !track) {
        throw new ApiError(404, `No track ${request.trackId}.`);
      }
      const strip = track ? track.strip : project.master;
      let pluginId: string | undefined;
      if (request.insertId) {
        pluginId = strip.inserts.find(i => i.id === request.insertId)?.pluginId;
        if (!pluginId) {
          throw new ApiError(404, `No insert ${request.insertId}.`);
        }
      } else {
        pluginId = track?.instrument?.pluginId;
        if (!pluginId) {
          throw new ApiError(404, 'No instrument on that track.');
        }
      }
      const plugin = this.catalogue.find(p => p.id === pluginId);
      if (!plugin?.hasEditor) {
        throw new ApiError(409, `${plugin?.name ?? pluginId} has no editor of its own.`);
      }
      const key = `${request.trackId ?? 'master'}/${request.insertId ?? 'instrument'}`;
      if (request.open) {
        this.editors.add(key);
      } else {
        this.editors.delete(key);
      }
    });
  }

  // ---- Phase 5: analysis, bounce in place, selection-based processing

  fileAnalysis(fileId: string): Promise<FileAnalysis> {
    return this.answer('GetFileAnalysis', () => {
      const file = this.require().audioFiles.find(f => f.id === fileId);
      if (!file) {
        throw new ApiError(404, `No file ${fileId}.`);
      }
      return structuredClone(this.analysisOf(file));
    });
  }

  analyzeFile(fileId: string): Promise<Job> {
    return this.answer('AnalyzeFile', () => {
      const file = this.require().audioFiles.find(f => f.id === fileId);
      if (!file) {
        throw new ApiError(404, `No file ${fileId}.`);
      }
      const finish = (): string[] => {
        this.analysisOf(file, true);
        return [];
      };
      return this.startJob('analyze', { finish }, 1);
    });
  }

  bounceInPlace(request: BounceInPlaceRequest): Promise<Job> {
    return this.answer('BounceInPlace', () => {
      const project = this.require();
      const trackIds = request.trackIds ?? [];
      const regionIds = request.regionIds ?? [];
      if (trackIds.length === 0 && regionIds.length === 0) {
        throw new ApiError(422, 'Choose the tracks or regions to bounce in place.');
      }
      if (project.settings.varispeed.enabled) {
        throw new ApiError(422, 'Turn Varispeed off to bounce in place.');
      }
      for (const id of trackIds) {
        const track = project.tracks.find(t => t.id === id);
        if (!track) {
          throw new ApiError(404, `No track ${id}.`);
        }
        if (track.kind === 'stack' || track.kind === 'vca') {
          throw new ApiError(422, `${track.name} has no audio of its own to bounce.`);
        }
        if (request.destination === 'replace' && track.kind !== 'audio') {
          throw new ApiError(422, 'Only audio tracks bounce in place replacing their regions.');
        }
      }
      for (const id of regionIds) {
        if (!project.regions.some(r => r.id === id) && !project.midiRegions.some(r => r.id === id)) {
          throw new ApiError(404, `No region ${id}.`);
        }
      }
      const copy = structuredClone(request);
      return this.startJob('bounceInPlace', { finish: () => this.finishBounceInPlace(copy) }, 1);
    });
  }

  processRegions(request: ProcessRequest): Promise<Job> {
    return this.answer('ProcessRegions', () => {
      const project = this.require();
      if (request.regionIds.length === 0) {
        throw new ApiError(422, 'Choose the regions to process.');
      }
      if (request.chain.length === 0) {
        throw new ApiError(422, 'The processing chain is empty.');
      }
      for (const effect of request.chain) {
        const plugin = this.catalogue.find(p => p.id === effect.pluginId);
        if (plugin?.format !== 'builtin' || plugin.isInstrument) {
          throw new ApiError(422, `${effect.pluginId} is not a built-in effect.`);
        }
      }
      for (const id of request.regionIds) {
        if (!project.regions.some(r => r.id === id)) {
          throw new ApiError(404, `No audio region ${id}.`);
        }
      }
      const { startTick, endTick } = request;
      if (startTick !== null && startTick !== undefined && endTick !== null && endTick !== undefined) {
        if (endTick <= startTick) {
          throw new ApiError(422, 'The range must end after it starts.');
        }
      }
      const copy = structuredClone(request);
      return this.startJob('process', { finish: () => this.finishProcess(copy) }, 1);
    });
  }

  /** The ranges a bounce in place renders, by source track: whole tracks, or the regions grouped by track. */
  // ---- Phase 6: loops, packs, Live Loops

  queryLoops(search: LoopSearch): Promise<LoopPage> {
    return this.answer('QueryLoops', () => this.loopLibrary.query(search));
  }

  previewLoop(id: string): Promise<void> {
    return this.answer('PreviewLoop', () => {
      const loop = this.loopLibrary.get(id).info;
      const project = this.project;
      const bpm = project ? tempoOf(project).bpmAt(this.positionTick) : 120;
      const tonic = project ? keyTonicAt(project, this.positionTick) : 0;
      this.previewing = loop.path;
      // In sync: at the project's tempo and key, from the next bar while playing.
      this.previewSync = {
        loopId: id,
        tempoRatio: loop.tempo ? bpm / loop.tempo : 1,
        transpose: keyInterval(loop.key, tonic),
        startOnBar: this.transportState !== 'stopped'
      };
    });
  }

  setLoopFavourite(id: string, favourite: boolean): Promise<LoopInfo> {
    return this.answer('SetLoopFavourite', () => this.loopLibrary.setFavourite(id, favourite));
  }

  addToLoopLibrary(request: AddToLoopLibraryRequest): Promise<LoopInfo> {
    return this.answer('AddToLoopLibrary', () => {
      const project = this.require();
      const name = request.name.trim();
      if (!name) {
        throw new ApiError(422, 'A loop needs a name.');
      }
      const audio = project.regions.find(r => r.id === request.regionId);
      const midi = project.midiRegions.find(r => r.id === request.regionId);
      const pattern = project.patternRegions.find(r => r.id === request.regionId);
      if (!audio && !midi && !pattern) {
        throw new ApiError(422, `No region ${request.regionId}.`);
      }
      const tempo = project.settings.tempo;
      const ticks = midi?.lengthTicks ?? pattern?.lengthTicks ?? (audio ? this.regionTicks(project, audio) : 0);
      const kind = audio ? 'audio' : midi ? 'midi' : 'pattern';
      const file = `${name}${kind === 'audio' ? '.wav' : kind === 'midi' ? '.mid' : '.anodeloop.json'}`;
      const loop: FakeLoop = {
        info: {
          id: `user:${this.nextId++}`,
          name,
          kind,
          packId: null,
          path: `/Users/anode/Library/Application Support/Anode/Library/Loops/User/${file}`,
          tempo: request.oneShot ? null : tempo,
          beats: request.oneShot ? null : Math.round((ticks / Ppq) * 100) / 100,
          key: request.key ?? null,
          scale: request.scale ?? 'neither',
          tags: [...request.tags],
          oneShot: request.oneShot,
          favourite: false
        },
        ...(midi
          ? {
              notes: midi.notes.map(n => ({
                pitch: n.pitch,
                startTick: n.startTick,
                lengthTicks: n.lengthTicks,
                velocity: n.velocity
              }))
            }
          : {}),
        ...(pattern ? { pattern: structuredClone(pattern.pattern) } : {})
      };
      return this.loopLibrary.add(loop);
    });
  }

  packs(): Promise<Pack[]> {
    return this.answer('ListPacks', () => this.loopLibrary.packs());
  }

  installPack(path: string): Promise<Pack> {
    return this.answer('InstallPack', () => this.loopLibrary.install(absolute(path)));
  }

  removePack(id: string): Promise<void> {
    return this.answer('RemovePack', () => this.loopLibrary.remove(id));
  }

  addLoop(request: AddLoopRequest): Promise<CommandResult> {
    return this.answer('AddLoop', () => {
      const before = this.require();
      return this.commit(before, this.withLoop(before, request), 'Add Loop');
    });
  }

  launchCells(request: LaunchRequest): Promise<void> {
    return this.answer('LaunchCells', () => {
      const project = this.require();
      const grid = project.liveLoops;
      let cells: typeof grid.cells;
      if (request.sceneId) {
        if (!grid.scenes.some(s => s.id === request.sceneId)) {
          throw new ApiError(422, `No scene ${request.sceneId}.`);
        }
        cells = grid.cells.filter(c => c.sceneId === request.sceneId);
      } else {
        cells = (request.cellIds ?? []).map(id => grid.cells.find(c => c.id === id) ?? this.refuse(`No cell ${id}.`));
        if (cells.length === 0) {
          throw new ApiError(422, 'Launch cells or a scene.');
        }
      }
      this.advance();
      if (this.transportState === 'stopped') {
        // A launch starts playback, as in Logic.
        this.start('playing');
      }
      const changes = request.sceneId
        ? this.launcher.launchScene(cells, this.positionTick)
        : this.launcher.launch(cells, this.positionTick);
      this.emitCells(changes);
    });
  }

  stopCells(request: StopRequest): Promise<void> {
    return this.answer('StopCells', () => {
      this.require();
      this.advance();
      this.emitCells(this.launcher.stop(request.trackIds ?? null, this.positionTick));
    });
  }

  recordLiveLoops(request: LiveRecordRequest): Promise<void> {
    return this.answer('RecordLiveLoops', () => {
      const project = this.require();
      this.advance();
      if (request.trackId || request.sceneId) {
        if (!request.on) {
          this.finishCellRecording();
          return;
        }
        const track = project.tracks.find(t => t.id === request.trackId) ?? this.refuse(`No track ${request.trackId}.`);
        if (!project.liveLoops.scenes.some(s => s.id === request.sceneId)) {
          throw new ApiError(422, `No scene ${request.sceneId}.`);
        }
        if (track.kind === 'audio') {
          throw new ApiError(422, 'The fake engine records MIDI into cells only.');
        }
        if (this.transportState === 'stopped') {
          this.start('playing');
        }
        const meter = meterOf(project);
        const bar = meter.barStart(Math.ceil(meter.barAt(this.positionTick) - 1e-9));
        this.cellRecording = {
          trackId: track.id,
          sceneId: request.sceneId ?? '',
          from: bar,
          held: new Map(),
          notes: []
        };
        return;
      }
      if (request.on) {
        if (this.transportState === 'stopped') {
          this.start('playing');
        }
        const now = this.positionTick;
        this.performance = {
          plays: [...this.launcher.active()]
            .filter(([, a]) => a.state === 'playing' || a.state === 'stopping')
            .map(([cellId]) => ({ cellId, from: now, to: null })),
          remix: []
        };
      } else if (this.performance) {
        this.finishPerformance();
      }
    });
  }

  /** The kits of the installed packs (every fake pack carries the same kits). */
  private installedKits(): FakeKit[] {
    return this.loopLibrary.packs().length ? [...this.kits.values()] : [];
  }

  /** Whether a performance or a cell is being recorded (tests). */
  get liveRecording(): 'performance' | 'cell' | null {
    return this.performance ? 'performance' : this.cellRecording ? 'cell' : null;
  }

  /** A launcher cell state's change as a `cell` event at its project sample; performance recording notes it. */
  private emitCells(changes: readonly CellChange[]): void {
    const project = this.project;
    if (!project) {
      return;
    }
    const tempo = tempoOf(project);
    for (const change of changes) {
      if (this.performance) {
        if (change.state === 'playing') {
          this.performance.plays.push({ cellId: change.cellId, from: change.tick, to: null });
        } else if (change.state === 'stopped') {
          const open = [...this.performance.plays].reverse().find(p => p.cellId === change.cellId && p.to === null);
          if (open) {
            open.to = change.tick;
          }
        }
      }
      this.emit({
        type: 'cell',
        cellId: change.cellId,
        state: change.state,
        sample: Math.round(tempo.secondsAt(change.tick) * project.sampleRate)
      });
    }
  }

  /** Remix FX moves during a performance recording become automation at its end. */
  private notePerformance(command: EditCommand): void {
    if (!this.performance || command.type !== 'setInsertParameter' || command.trackId) {
      return;
    }
    const insert = this.project?.master.inserts.find(i => i.id === command.insertId);
    if (insert?.pluginId === RemixFxId) {
      this.performance.remix.push({
        tick: Math.round(this.positionTick),
        insertId: insert.id,
        parameterId: command.parameterId,
        value: command.value
      });
    }
  }

  /**
   * The end of a performance recording: where each cell played becomes a region on its track (its content, looped
   * to the span) and Remix FX moves become master automation, as one undo step.
   */
  private finishPerformance(): void {
    const performance = this.performance;
    const project = this.project;
    this.performance = null;
    if (!performance || !project) {
      return;
    }
    let next = project;
    const end = Math.round(this.positionTick);
    for (const play of performance.plays) {
      const cell = project.liveLoops.cells.find(c => c.id === play.cellId);
      const to = play.to ?? end;
      const from = Math.round(play.from);
      if (!cell || to <= from) {
        continue;
      }
      next = this.withCellRegion(next, cell, from, Math.round(to) - from);
    }
    const byLane = new Map<string, { tick: number; value: number }[]>();
    for (const move of performance.remix) {
      const target = `insert:${move.insertId}:${move.parameterId}`;
      byLane.set(target, [...(byLane.get(target) ?? []), { tick: move.tick, value: move.value }]);
    }
    for (const [target, points] of byLane) {
      const existing = next.automation.find(l => (l.trackId ?? null) === null && l.target === target);
      const kept = (existing?.points ?? []).filter(p => !points.some(q => q.tick === p.tick));
      const lane = {
        id: existing?.id ?? `aut_${this.nextId++}`,
        trackId: null,
        target,
        points: [...kept, ...points.map(p => ({ ...p, curve: 0 }))].sort((a, b) => a.tick - b.tick)
      };
      next = {
        ...next,
        automation: existing ? next.automation.map(l => (l === existing ? lane : l)) : [...next.automation, lane]
      };
    }
    if (next !== project) {
      this.commit(project, next, 'Record Performance');
    }
  }

  /** A cell's content as a region on its track, `length` ticks from `from` (MIDI notes looped over the span). */
  private withCellRegion(project: Project, cell: Project['liveLoops']['cells'][number], from: number, length: number) {
    const { audio, midi, pattern } = cell.content;
    if (midi) {
      const loop = Math.max(1, cell.lengthTicks);
      const notes: Note[] = [];
      for (let start = 0; start < length; start += loop) {
        for (const note of midi.notes) {
          if (start + note.startTick < length) {
            notes.push({ ...note, id: this.nextId++, startTick: start + note.startTick });
          }
        }
      }
      const region: MidiRegion = {
        ...structuredClone(midi),
        id: `mid_${this.nextId++}`,
        trackId: cell.trackId,
        startTick: from,
        lengthTicks: length,
        notes: sortNotes(notes),
        controls: []
      };
      return { ...project, midiRegions: [...project.midiRegions, region] };
    }
    if (pattern) {
      const region = {
        ...structuredClone(pattern),
        id: `pat_${this.nextId++}`,
        trackId: cell.trackId,
        startTick: from,
        lengthTicks: length
      };
      return { ...project, patternRegions: [...project.patternRegions, region] };
    }
    if (audio) {
      const region: AudioRegion = {
        ...structuredClone(audio),
        id: `rgn_${this.nextId++}`,
        trackId: cell.trackId,
        startTick: from
      };
      return { ...project, regions: [...project.regions, region] };
    }
    return project;
  }

  /** Live MIDI played into the cell being recorded. */
  private captureCell(events: readonly LiveMidiEvent[]): void {
    const recording = this.cellRecording;
    if (!recording) {
      return;
    }
    const tick = Math.round(this.positionTick);
    for (const event of events) {
      if (isNoteOn(event.status, event.data2)) {
        recording.held.set(event.data1, { start: tick, velocity: event.data2 });
      } else if (isNoteOff(event.status, event.data2)) {
        const held = recording.held.get(event.data1);
        recording.held.delete(event.data1);
        if (held && held.start >= recording.from) {
          recording.notes.push({
            pitch: event.data1,
            velocity: held.velocity,
            startTick: held.start - recording.from,
            lengthTicks: Math.max(1, tick - held.start)
          });
        }
      }
    }
  }

  /** The end of recording into a cell: its notes become the cell's MIDI, a whole number of bars (one undo step). */
  private finishCellRecording(): void {
    const recording = this.cellRecording;
    const project = this.project;
    this.cellRecording = null;
    if (!recording || !project) {
      return;
    }
    const meter = meterOf(project);
    const bar = meter.barTicksAt(recording.from);
    const played = Math.max(
      Math.round(this.positionTick) - recording.from,
      ...recording.notes.map(n => n.startTick + n.lengthTicks)
    );
    const lengthTicks = Math.max(bar, Math.ceil(played / bar) * bar);
    const content: MidiRegion = {
      id: 'new',
      trackId: recording.trackId,
      name: project.tracks.find(t => t.id === recording.trackId)?.name ?? 'Recording',
      startTick: 0,
      lengthTicks,
      playback: { ...NeutralPlayback },
      notes: recording.notes.map(n => ({ ...n, id: 0, channel: 1 })),
      controls: [],
      muted: false
    };
    const after = setCell(
      project,
      {
        type: 'setCell',
        trackId: recording.trackId,
        sceneId: recording.sceneId,
        content: { audio: null, midi: content, pattern: null }
      },
      this.phase6Context()
    );
    this.commit(project, after, 'Record Cell');
  }

  /** A loop added to a project (`addLoop`): a region on a track (a new one when none is named) or a cell's content. */
  private withLoop(project: Project, request: AddLoopRequest): Project {
    const loop = this.loopLibrary.get(request.loopId);
    const info = loop.info;
    const tick = Math.max(0, Math.round(request.tick));
    const transpose = keyInterval(info.key, keyTonicAt(project, tick));
    let next = project;
    let trackId = request.trackId ?? null;
    if (!trackId) {
      const kind = info.kind === 'audio' ? 'audio' : 'instrument';
      next = this.applyCommand(next, { type: 'addTrack', kind, stereo: true, name: info.name });
      trackId = next.tracks.at(-1)?.id ?? '';
    }
    const track = next.tracks.find(t => t.id === trackId) ?? this.refuse(`No track ${trackId}.`);
    if ((info.kind === 'audio') !== (track.kind === 'audio')) {
      this.refuse(`${track.name} cannot hold ${info.kind === 'audio' ? 'an audio' : 'a MIDI'} loop.`);
    }
    const lengthTicks = Math.round((info.beats ?? 4) * Ppq);
    const name = info.name;
    let audio: AudioRegion | null = null;
    let midi: MidiRegion | null = null;
    let pattern: PatternRegion | null = null;
    if (info.kind === 'audio') {
      const seconds = info.tempo && info.beats ? (info.beats * 60) / info.tempo : 1;
      const frames = Math.round(seconds * project.sampleRate);
      const beat = info.tempo ? (60 * project.sampleRate) / info.tempo : 0;
      const beats = beat ? Array.from({ length: Math.round(info.beats ?? 0) }, (_, i) => Math.round(i * beat)) : [];
      const file: AudioFile = {
        id: `aud_${this.nextId++}`,
        name: `${name}.wav`,
        path: `Audio Files/${name}.wav`,
        sampleRate: project.sampleRate,
        channels: 2,
        bitDepth: 24,
        frames,
        tempo: info.tempo
          ? { bpm: info.tempo, variable: false, beats, downbeats: beats.filter((_, i) => i % 4 === 0) }
          : null
      };
      next = { ...next, audioFiles: [...next.audioFiles, file] };
      audio = {
        id: `rgn_${this.nextId++}`,
        trackId: track.id,
        fileId: file.id,
        name,
        startTick: tick,
        fileOffset: 0,
        length: frames,
        gainDb: 0,
        fadeIn: 0,
        fadeOut: 0,
        fadeInCurve: 0,
        fadeOutCurve: 0,
        muted: false,
        // Follows the tempo from the sidecar's (a one-shot is added unwarped); the key by transposing.
        follow: info.tempo && !info.oneShot ? 'on' : 'off',
        flexMarkers: [],
        transpose: info.oneShot ? 0 : transpose,
        fineTune: 0,
        pitchNotes: []
      };
    } else if (info.kind === 'midi') {
      midi = {
        id: `mid_${this.nextId++}`,
        trackId: track.id,
        name,
        startTick: tick,
        lengthTicks,
        playback: { ...NeutralPlayback },
        notes: sortNotes(
          (loop.notes ?? []).map(n => ({
            id: this.nextId++,
            pitch: Math.min(127, Math.max(0, n.pitch + transpose)),
            velocity: n.velocity,
            channel: 1,
            startTick: n.startTick,
            lengthTicks: n.lengthTicks
          }))
        ),
        controls: [],
        muted: false
      };
    } else {
      if (track.kind === 'midi') {
        this.refuse(`${track.name} cannot hold a pattern region.`);
      }
      pattern = {
        id: `pat_${this.nextId++}`,
        trackId: track.id,
        name,
        startTick: tick,
        lengthTicks,
        muted: false,
        pattern: structuredClone(loop.pattern ?? { rows: [], swing: 0, seed: 1, stepTicks: 240 })
      };
    }
    if (request.cellSceneId) {
      return setCell(
        next,
        {
          type: 'setCell',
          trackId: track.id,
          sceneId: request.cellSceneId,
          content: { audio, midi, pattern }
        },
        this.phase6Context()
      );
    }
    return {
      ...next,
      regions: audio ? [...next.regions, audio] : next.regions,
      midiRegions: midi ? [...next.midiRegions, midi] : next.midiRegions,
      patternRegions: pattern ? [...next.patternRegions, pattern] : next.patternRegions
    };
  }

  /** A region's length in ticks under the project's tempo. */
  private regionTicks(project: Project, region: AudioRegion): number {
    return audioEnd(project, region) - region.startTick;
  }

  private refuse(message: string): never {
    throw new ApiError(422, message);
  }

  private bounceSources(
    project: Project,
    request: BounceInPlaceRequest
  ): Map<string, { startTick: number; endTick: number; regionIds: string[] }> {
    const sources = new Map<string, { startTick: number; endTick: number; regionIds: string[] }>();
    const extend = (trackId: string, start: number, end: number, id: string) => {
      const source = sources.get(trackId) ?? { startTick: start, endTick: end, regionIds: [] };
      source.startTick = Math.min(source.startTick, start);
      source.endTick = Math.max(source.endTick, end);
      source.regionIds.push(id);
      sources.set(trackId, source);
    };
    const audio = (r: AudioRegion) => extend(r.trackId, r.startTick, timingOf(project, r).endTick, r.id);
    const midi = (r: MidiRegion) => extend(r.trackId, r.startTick, r.startTick + r.lengthTicks, r.id);
    for (const id of request.regionIds ?? []) {
      project.regions.filter(r => r.id === id).forEach(audio);
      project.midiRegions.filter(r => r.id === id).forEach(midi);
    }
    for (const id of request.trackIds ?? []) {
      project.regions.filter(r => r.trackId === id).forEach(audio);
      project.midiRegions.filter(r => r.trackId === id).forEach(midi);
      project.takeFolders
        .filter(f => f.trackId === id)
        .forEach(f => extend(id, f.startTick, f.startTick + f.lengthTicks, f.id));
      if (!sources.has(id)) {
        throw new Error(`${project.tracks.find(t => t.id === id)?.name ?? id} has nothing to bounce.`);
      }
    }
    return sources;
  }

  /** A finished bounce in place: its files, and the new track (or replaced regions) with the source handled. */
  private finishBounceInPlace(request: BounceInPlaceRequest): string[] {
    const before = this.require();
    let after = before;
    const paths: string[] = [];
    for (const [trackId, source] of this.bounceSources(before, request)) {
      const track = after.tracks.find(t => t.id === trackId);
      if (!track) {
        throw new Error('A source track was deleted during the bounce.');
      }
      const name = request.name?.trim() || `${track.name} Bounce`;
      const effects = !request.bypassEffects && track.strip.inserts.length > 0;
      const tail = request.includeTail && effects ? after.sampleRate : 0;
      const file: AudioFile = {
        id: `aud_${this.nextId++}`,
        name: `${name}.wav`,
        path: `Bounces/${name}.wav`,
        sampleRate: after.sampleRate,
        channels: 2,
        bitDepth: 24,
        frames: Math.max(1, framesBetween(after, source.startTick, source.endTick) + tail),
        tempo: null
      };
      paths.push(`${projectFolder(after)}/${file.path}`);
      after = { ...after, audioFiles: [...after.audioFiles, file] };
      const replace = request.destination === 'replace' && track.kind === 'audio';
      let target = trackId;
      if (replace) {
        after = this.applyCommand(after, { type: 'deleteRegions', regionIds: source.regionIds });
      } else {
        const index = after.tracks.findIndex(t => t.id === trackId) + 1;
        after = this.applyCommand(after, { type: 'addTrack', kind: 'audio', stereo: true, index, name });
        target = after.tracks[index]?.id ?? trackId;
      }
      after = this.applyCommand(after, {
        type: 'addRegion',
        trackId: target,
        fileId: file.id,
        startTick: source.startTick
      });
      after = renameLast(after, name);
      if (!replace && request.source === 'mute') {
        after = this.applyCommand(after, { type: 'setStrip', trackId, mute: true });
      } else if (!replace && request.source === 'delete') {
        after = this.applyCommand(after, { type: 'deleteRegions', regionIds: source.regionIds });
      }
    }
    this.commit(before, after, 'Bounce in Place');
    return paths;
  }

  /** A finished selection-based processing: a new file and region for the processed part of each region. */
  private finishProcess(request: ProcessRequest): string[] {
    const before = this.require();
    let after = before;
    const paths: string[] = [];
    for (const id of request.regionIds) {
      const region = after.regions.find(r => r.id === id);
      if (!region) {
        throw new Error('A region was deleted during processing.');
      }
      const timing = timingOf(after, region);
      const from = Math.round(Math.max(timing.startTick, request.startTick ?? timing.startTick));
      const to = Math.round(Math.min(timing.endTick, request.endTick ?? timing.endTick));
      if (to <= from) {
        continue;
      }
      const a = Math.round(timing.frameAt(from));
      const b = Math.round(timing.frameAt(to));
      const file: AudioFile = {
        id: `aud_${this.nextId++}`,
        name: `${region.name} Processed.wav`,
        path: `Audio Files/${region.name} Processed ${this.nextId}.wav`,
        sampleRate: after.sampleRate,
        channels: 2,
        bitDepth: 32,
        frames: b - a + (request.includeTail ? after.sampleRate : 0),
        tempo: null
      };
      paths.push(`${projectFolder(after)}/${file.path}`);
      after = { ...after, audioFiles: [...after.audioFiles, file] };
      // Split off the part, then replace it with a region of the processed file.
      const startsAt = (tick: number) =>
        after.regions.find(
          r => r.trackId === region.trackId && r.fileId === region.fileId && Math.abs(r.startTick - tick) <= 1
        );
      if (from > timing.startTick + 1) {
        after = this.applyCommand(after, { type: 'splitRegion', regionId: region.id, atTick: from });
      }
      const middle = startsAt(from);
      if (middle && to < timing.endTick - 1) {
        after = this.applyCommand(after, { type: 'splitRegion', regionId: middle.id, atTick: to });
      }
      const part = startsAt(from);
      if (part) {
        after = this.applyCommand(after, { type: 'deleteRegions', regionIds: [part.id] });
      }
      after = this.applyCommand(after, {
        type: 'addRegion',
        trackId: region.trackId,
        fileId: file.id,
        startTick: from,
        length: b - a
      });
      after = renameLast(after, `${region.name} Processed`);
    }
    this.commit(before, after, 'Selection-Based Processing');
    return paths;
  }

  /**
   * Moves every unfinished job on by up to `steps` steps (tests; the dev mode's timer calls it too), reporting each
   * change as a `job` event; a job finishes on its last step.
   */
  runJobs(steps = Infinity): void {
    for (let i = 0; i < steps; i++) {
      const running = [...this.jobList.values()].filter(j => isRunning(j.job));
      if (running.length === 0) {
        break;
      }
      for (const entry of running) {
        this.stepJob(entry);
      }
    }
    this.stopJobTimer();
  }

  private findJob(id: string): FakeJob {
    const entry = this.jobList.get(id);
    if (!entry) {
      throw new ApiError(404, `No job ${id}.`);
    }
    return entry;
  }

  private startJob(
    kind: Job['kind'],
    extra: { bounce?: BounceRequest; trackId?: string; finish?: () => string[] },
    slowness: number
  ): Job {
    const job: Job = { id: `job_${this.nextId++}`, kind, state: 'queued', progress: 0, message: null, files: [] };
    const entry: FakeJob = { job, steps: JobSteps * slowness, done: 0, ...extra };
    this.jobList.set(job.id, entry);
    // The answer comes first; the events follow.
    setTimeout(() => this.emit({ type: 'job', job: structuredClone(entry.job) }), 0);
    if (this.realtime && !this.jobTimer) {
      this.jobTimer = setInterval(() => this.runJobs(1), JobStepMs);
    }
    return structuredClone(job);
  }

  private stopJobTimer(): void {
    if (this.jobTimer && ![...this.jobList.values()].some(j => isRunning(j.job))) {
      clearInterval(this.jobTimer);
      this.jobTimer = null;
    }
  }

  private setJob(entry: FakeJob, job: Job): void {
    entry.job = job;
    this.emit({ type: 'job', job: structuredClone(job) });
  }

  private stepJob(entry: FakeJob): void {
    entry.done++;
    if (entry.done < entry.steps) {
      this.setJob(entry, { ...entry.job, state: 'running', progress: entry.done / entry.steps });
      return;
    }
    try {
      const files = entry.finish
        ? entry.finish()
        : entry.bounce
          ? this.finishBounce(entry.bounce)
          : this.finishFreeze(entry.trackId ?? '');
      this.setJob(entry, { ...entry.job, state: 'done', progress: 1, files });
    } catch (error) {
      this.setJob(entry, {
        ...entry.job,
        state: 'failed',
        message: error instanceof Error ? error.message : String(error)
      });
    }
  }

  /** The ticks a bounce covers: as asked, else the cycle when it is on, else the project start to its end. */
  private bounceRange(project: Project, request: BounceRequest): [number, number] {
    const { cycle, endTick } = project.settings;
    const fallback: [number, number] = cycle.enabled ? [cycle.startTick, cycle.endTick] : [0, endTick];
    return [request.startTick ?? fallback[0], request.endTick ?? fallback[1]];
  }

  /** The files a finished bounce wrote (absolute paths), added to the project when asked. */
  private finishBounce(request: BounceRequest): string[] {
    const project = this.require();
    const directory = (request.directory?.trim() || `${projectFolder(project)}/Bounces`).replace(/\/+$/, '');
    const extension = request.format === 'aiff' ? 'aif' : 'wav';
    const names =
      request.source === 'stems'
        ? (request.trackIds ?? []).map(id => {
            const track = project.tracks.find(t => t.id === id);
            if (!track) {
              throw new Error(`Track ${id} was deleted during the bounce.`);
            }
            return `${request.fileName} - ${track.name}`;
          })
        : [request.fileName];
    const files = names.map(name => `${directory}/${name}.${extension}`);
    if (request.addToProject) {
      const [start, end] = this.bounceRange(project, request);
      const sampleRate = request.sampleRate ?? project.sampleRate;
      const frames = Math.max(1, Math.round((framesBetween(project, start, end) * sampleRate) / project.sampleRate));
      const added: AudioFile[] = files.map(path => ({
        id: `aud_${this.nextId++}`,
        name: path.split('/').pop() ?? path,
        path,
        sampleRate,
        channels: 2,
        bitDepth: request.bitDepth,
        frames,
        tempo: null
      }));
      this.commit(project, { ...project, audioFiles: [...project.audioFiles, ...added] }, 'Add Bounce');
    }
    return files;
  }

  /** A finished freeze: the track's file in the project and the track playing it, one undo step. */
  private finishFreeze(trackId: string): string[] {
    const project = this.require();
    const track = project.tracks.find(t => t.id === trackId);
    if (!track) {
      throw new Error('The track was deleted during the freeze.');
    }
    const ends = [
      ...project.regions.filter(r => r.trackId === trackId).map(r => audioEnd(project, r)),
      ...project.midiRegions.filter(r => r.trackId === trackId).map(r => r.startTick + r.lengthTicks),
      ...project.takeFolders.filter(f => f.trackId === trackId).map(f => f.startTick + f.lengthTicks)
    ];
    const last = Math.max(Ppq, ...ends);
    const file: AudioFile = {
      id: `aud_${this.nextId++}`,
      name: `${track.name} Freeze.wav`,
      path: `Freeze Files/${track.name} Freeze.wav`,
      sampleRate: project.sampleRate,
      channels: 2,
      bitDepth: 32,
      frames: Math.round(framesBetween(project, 0, last) + 2 * project.sampleRate),
      tempo: null
    };
    this.commit(
      project,
      {
        ...project,
        audioFiles: [...project.audioFiles, file],
        tracks: project.tracks.map(t => (t.id === trackId ? { ...t, frozenFileId: file.id } : t))
      },
      'Freeze Track'
    );
    return [`${projectFolder(project)}/${file.path}`];
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
    // Varispeed: the project plays at its speed (the transport reports compiled samples).
    const speed = this.project.settings.varispeed.enabled ? this.project.settings.varispeed.speed : 1;
    let tick = map.tickAt(map.secondsAt(this.positionTick) + ((now - this.lastAdvance) / 1000) * speed);
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
    this.emitCells(this.launcher.advance(tick));
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
    this.pass.clear();
    this.crashPlugins();
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
    this.emitCells(this.launcher.reset(this.positionTick));
    if (this.performance) {
      this.finishPerformance();
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
        frames: total,
        tempo: null
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
    let project = this.applyCommand(before, {
      type: 'setTempoEvents',
      events: [{ tick: 0, bpm, curve: 'step', shape: 0 }]
    });
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
    const varispeed = project?.settings.varispeed;
    const speed = varispeed?.enabled ? varispeed.speed : 1;
    // Compiled samples: Varispeed scales every position by 1/speed.
    const seconds = (project ? tempoOf(project).secondsAt(this.positionTick) : (this.positionTick / Ppq) * 0.5) / speed;
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

  /**
   * Levels that follow the regions under the playhead, so the meters move with what plays: VCA gains folded in, aux
   * tracks fed by their bus, only what reaches the master in its meter, and readings of dynamics and metering inserts.
   */
  private meters(): { tracks: TrackMeter[]; master: TrackMeter; readings: InsertReading[] } {
    const project = this.project;
    if (!project) {
      return { tracks: [], master: { trackId: '', peakLeft: 0, peakRight: 0 }, readings: [] };
    }

    const soloed = project.tracks.some(t => t.strip.solo);
    const byId = new Map(project.tracks.map(t => [t.id, t]));
    const tick = this.positionTick;
    const levels = new Map<string, number>();
    /** A track's gain with its VCA folded in, and whether its VCA mutes it. */
    const gainOf = (track: Track): number => {
      const vca = track.strip.vcaId ? byId.get(track.strip.vcaId) : undefined;
      const vcaGain = vca ? (vca.strip.mute ? 0 : 10 ** (vca.strip.volumeDb / 20)) : 1;
      return 10 ** (track.strip.volumeDb / 20) * vcaGain;
    };
    const audible = (track: Track) =>
      !track.strip.mute && (!soloed || track.strip.solo || track.kind === 'aux' || track.kind === 'vca');
    // Sources first: what plays on each track under the playhead.
    for (const track of project.tracks) {
      if (track.kind === 'aux' || track.kind === 'vca' || track.kind === 'stack') {
        continue;
      }
      const playing =
        (track.frozenFileId !== null && track.frozenFileId !== undefined) ||
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
      const wobble = 0.55 + 0.35 * Math.abs(Math.sin(this.positionTick / 217 + hash(track.id)));
      const level =
        playing && audible(track)
          ? Math.min(1.2, wobble * gainOf(track))
          : track.recordArmed || track.inputMonitoring
            ? 0.05
            : 0;
      levels.set(track.id, level);
    }
    // Aux tracks hear what their bus receives: sends (pre-fader ones before the source's fader) and outputs.
    for (const aux of project.tracks.filter(t => t.kind === 'aux')) {
      let received = 0;
      for (const source of project.tracks) {
        const level = levels.get(source.id) ?? 0;
        if (source.id === aux.id || level <= 0) {
          continue;
        }
        if (source.strip.output === `bus:${aux.inputBus}`) {
          received = Math.max(received, level);
        }
        for (const send of source.strip.sends) {
          if (send.bus === aux.inputBus && send.levelDb > -144) {
            const tap = send.preFader ? level / Math.max(1e-6, gainOf(source)) : level;
            received = Math.max(received, tap * 10 ** (send.levelDb / 20));
          }
        }
      }
      levels.set(aux.id, audible(aux) ? Math.min(1.2, received * gainOf(aux)) : 0);
    }
    let masterLevel = 0;
    const tracks = project.tracks.map(track => {
      const level = track.kind === 'vca' ? 0 : (levels.get(track.id) ?? 0);
      if (track.strip.output === 'master' && track.kind !== 'vca') {
        masterLevel = Math.max(masterLevel, level);
      }
      const pan = track.strip.pan;
      return { trackId: track.id, peakLeft: level * Math.min(1, 1 - pan), peakRight: level * Math.min(1, 1 + pan) };
    });
    const master = masterLevel * 10 ** (project.master.volumeDb / 20) * (project.master.mute ? 0 : 1);
    const readings: InsertReading[] = [];
    const read = (trackId: string | null, inserts: readonly Insert[], level: number) => {
      const db = level > 0 ? 20 * Math.log10(level) : -144;
      for (const insert of inserts) {
        if (insert.bypassed) {
          continue;
        }
        const threshold = insert.parameters['threshold'] ?? -18;
        const ratio = insert.parameters['ratio'] ?? 4;
        switch (insert.pluginId) {
          case 'anode.compressor':
            readings.push({ trackId, insertId: insert.id, value: -Math.max(0, db - threshold) * (1 - 1 / ratio) });
            break;
          case 'anode.limiter':
            readings.push({
              trackId,
              insertId: insert.id,
              value: -Math.max(0, db + (insert.parameters['gain'] ?? 0) - (insert.parameters['ceiling'] ?? -1))
            });
            break;
          case 'anode.noise-gate':
            readings.push({ trackId, insertId: insert.id, value: db < threshold ? -60 : 0 });
            break;
          case 'anode.deesser':
            readings.push({ trackId, insertId: insert.id, value: -Math.min(6, Math.max(0, db - threshold) / 3) });
            break;
          case 'anode.loudness-meter':
            readings.push({ trackId, insertId: insert.id, value: level > 0 ? Math.max(-70, db - 9) : -70 });
            break;
        }
      }
    };
    for (const track of project.tracks) {
      read(track.id, track.strip.inserts, levels.get(track.id) ?? 0);
    }
    read(null, project.master.inserts, master);
    return { tracks, master: { trackId: '', peakLeft: master, peakRight: master * 0.97 }, readings };
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
          inputBus: 0,
          automationMode: 'read',
          frozenFileId: null,
          flexOn: false,
          flexMode: 'polyphonic',
          alternatives: [],
          alternativeId: null,
          drumMachine: null,
          smartControls: null,
          liveLoopsActive: false,
          // An external MIDI track sends nowhere until setMidiIo picks a port (as the Server does).
          midiOutput: null
        };
        let index = Math.min(count, Math.max(0, command.index ?? count));
        // A stack's members follow it (as the Model keeps them): among them, the new track goes after the last.
        const parent = project.tracks[index]?.parentId;
        if (parent) {
          index = Math.max(...project.tracks.map((t, i) => (t.id === parent || t.parentId === parent ? i : -1))) + 1;
        }
        const tracks = [...project.tracks];
        tracks.splice(index, 0, added);
        return { ...project, tracks };
      }
      case 'removeTrack': {
        const removed = track(command.trackId);
        if (removed.drumMachine) {
          // A Drum Machine Designer goes with its pads.
          let next = project;
          for (const pad of removed.drumMachine.pads) {
            if (next.tracks.some(t => t.id === pad.trackId)) {
              next = this.applyCommand(next, { type: 'removeTrack', trackId: pad.trackId });
            }
          }
          return this.applyCommand(
            { ...next, tracks: next.tracks.map(t => (t.id === removed.id ? { ...t, drumMachine: null } : t)) },
            command
          );
        }
        return forgetTrack(
          {
            ...project,
            patternRegions: project.patternRegions.filter(r => r.trackId !== command.trackId),
            liveLoops: {
              ...project.liveLoops,
              cells: project.liveLoops.cells.filter(c => c.trackId !== command.trackId)
            },
            // A removed stack's members stay, outside any stack.
            tracks: project.tracks
              .filter(t => t.id !== command.trackId)
              .map(t => (t.parentId === command.trackId ? { ...t, parentId: null } : t)),
            regions: project.regions.filter(r => r.trackId !== command.trackId),
            midiRegions: project.midiRegions.filter(r => r.trackId !== command.trackId),
            takeFolders: project.takeFolders.filter(f => f.trackId !== command.trackId)
          },
          command.trackId
        );
      }
      case 'moveTrack': {
        const moved = track(command.trackId);
        const tracks = project.tracks.filter(t => t.id !== moved.id);
        tracks.splice(Math.min(tracks.length, Math.max(0, command.index)), 0, moved);
        return { ...project, tracks: groupStacks(tracks) };
      }
      case 'setTrack': {
        const t = track(command.trackId);
        const next = withTrack({
          ...t,
          name: command.name ?? t.name,
          color: command.color ?? t.color,
          input: command.input ?? t.input,
          recordArmed: t.kind === 'stack' ? false : (command.recordArmed ?? t.recordArmed),
          inputMonitoring: command.inputMonitoring ?? t.inputMonitoring,
          collapsed: command.collapsed ?? t.collapsed,
          notes: command.notes ?? t.notes
        });
        const armed = command.recordArmed;
        return armed === null || armed === undefined || t.kind === 'stack' ? next : linkRecordArm(next, t.id, armed);
      }
      case 'setStrip':
        if (command.trackId !== null && command.trackId !== undefined) {
          track(command.trackId);
        }
        return applyStrip(project, command, this.mixContext());
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
          muted: false,
          // The default follow only for a file with a tempo (the Model's rule).
          follow: f.tempo ? project.settings.smartTempo.defaultFollow : 'off',
          flexMarkers: [],
          transpose: 0,
          fineTune: 0,
          pitchNotes: []
        };
        const smart = project.settings.smartTempo;
        const adapts =
          project.regions.length === 0 &&
          (smart.mode === 'adapt' ||
            (smart.mode === 'auto' && project.midiRegions.length === 0 && project.settings.tempoEvents.length === 1));
        if (adapts) {
          // The first audio region sets the tempo from its file's beats (Smart Tempo's adapt) and plays unwarped.
          const tempo = f.tempo ?? this.analysisOf(f).tempo;
          if (tempo) {
            const withTempo = f.tempo
              ? project
              : { ...project, audioFiles: project.audioFiles.map(x => (x.id === f.id ? { ...x, tempo } : x)) };
            const adapted = adaptTempo(withTempo, { ...added, follow: 'off' }, tempo);
            return { ...adapted, regions: [...adapted.regions, { ...added, follow: 'off' }] };
          }
        }
        return { ...project, regions: [...project.regions, added] };
      }
      case 'moveRegion': {
        if (isPatternRegionId(command.regionId)) {
          return movePatternRegion(project, command);
        }
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
        const timing = timingOf(project, r);
        if (command.atTick <= r.startTick || command.atTick >= timing.endTick) {
          throw new ApiError(422, 'A split must fall inside the region.');
        }
        const left = Math.round(timing.frameAt(command.atTick)) - r.fileOffset;
        const cut = r.fileOffset + left;
        const at = Math.round(command.atTick);
        const right: AudioRegion = {
          ...r,
          id: `rgn_${this.nextId++}`,
          startTick: at,
          fileOffset: cut,
          length: r.length - left,
          fadeIn: 0,
          fadeOut: Math.min(r.fadeOut, r.length - left),
          flexMarkers: r.flexMarkers
            .filter(m => m.fileFrame > cut)
            .map(m => ({ ...m, tick: m.tick - (at - r.startTick) }))
            .filter(m => m.tick > 0),
          pitchNotes: r.pitchNotes.filter(n => n.startFrame >= cut)
        };
        const first = {
          ...r,
          length: left,
          fadeIn: Math.min(r.fadeIn, left),
          fadeOut: 0,
          flexMarkers: r.flexMarkers.filter(m => m.fileFrame < cut && m.tick < at - r.startTick),
          pitchNotes: r.pitchNotes.filter(n => n.endFrame <= cut)
        };
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
        if (command.regionIds.some(isPatternRegionId)) {
          const without = deletePatternRegions(project, command.regionIds.filter(isPatternRegionId));
          const rest = command.regionIds.filter(r => !isPatternRegionId(r));
          return rest.length ? this.applyCommand(without, { ...command, regionIds: rest }) : without;
        }
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
          notes: '',
          inputBus: 0,
          automationMode: 'read',
          frozenFileId: null,
          flexOn: false,
          flexMode: 'polyphonic',
          alternatives: [],
          alternativeId: null,
          drumMachine: null,
          smartControls: null,
          liveLoopsActive: false
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
      case 'copyRegions': {
        const patterns = command.regionIds.filter(isPatternRegionId);
        const rest = command.regionIds.filter(r => !isPatternRegionId(r));
        const offset = Math.round(command.offsetTicks);
        const copied = patterns.length
          ? copyPatternRegions(project, patterns, offset, command.trackId, { id })
          : project;
        return rest.length ? copyRegions(copied, rest, offset, command.trackId, id) : copied;
      }
      case 'addInsert':
      case 'removeInsert':
      case 'moveInsert':
      case 'setInsert':
      case 'setInsertParameter':
      case 'addSend':
      case 'setSend':
      case 'removeSend':
      case 'setOutput':
      case 'addAux':
      case 'setGroup':
      case 'deleteGroup':
      case 'setTrackGroups':
      case 'addVca':
      case 'assignVca':
      case 'setAutomation':
      case 'setAutomationMode':
      case 'setMixerSettings':
      case 'applyPatch':
      case 'unfreezeTrack':
        if ('chain' in command && command.chain === 'midiEffects') {
          // MIDI FX slots (Phase 6): the insert commands on a strip's `midiEffects`.
          return applyMidiEffectCommand(project, command, this.phase6Context());
        }
        return applyMixCommand(project, command, this.mixContext()) ?? project;
      case 'loadInsertFile':
        // The convolution reverb's impulse response.
        return loadInsertFile(project, command, id => this.catalogue.find(p => p.id === id));
      default:
        if (isPhase6Command(command)) {
          // Patterns, the Drum Machine Designer, Smart Controls and Live Loops (Phase 6).
          return applyPhase6Command(project, command, this.phase6Context()) ?? project;
        }
        // Flex, Smart Tempo, Varispeed and track alternatives (Phase 5).
        return applyFlexCommand(project, command, this.flexContext()) ?? project;
    }
  }

  /** What the Phase 6 commands need: ids, note ids, the catalogue, the kits, mixing (automation recording). */
  private phase6Context(): Phase6Context {
    return {
      id: prefix => `${prefix}_${this.nextId++}`,
      noteId: () => this.nextId++,
      plugin: id => this.catalogue.find(p => p.id === id),
      kit: id => this.installedKits().find(k => k.info.id === id),
      mix: this.mixContext(),
      seed: () => (hash(`seed${this.nextId++}`) % 2_147_483_647) + 1
    };
  }

  /** What the Phase 5 commands need: ids, composition, and file analysis. */
  private flexContext(): FlexContext {
    return {
      id: prefix => `${prefix}_${this.nextId++}`,
      apply: (project, command) => this.applyCommand(project, command),
      analysis: file => this.analysisOf(file)
    };
  }

  /**
   * A file's cached analysis: transients and beats built now when missing (as the Server does synchronously), the
   * pitch curve and notes once an analyze job ran. Building or rebuilding announces it (`analysis`).
   */
  private analysisOf(file: AudioFile, withPitch = false): FileAnalysis {
    const cached = this.analyses.get(file.id);
    if (cached && (!withPitch || cached.pitch.length > 0)) {
      return cached;
    }
    const built = fakeAnalysis(file, withPitch);
    this.analyses.set(file.id, built);
    setTimeout(() => this.emit({ type: 'analysis', fileId: file.id }), 0);
    return built;
  }

  /** What the mixing commands need: ids, the catalogue, the patches, and the playhead while the transport runs. */
  private mixContext(): MixContext {
    return {
      id: prefix => `${prefix}_${this.nextId++}`,
      plugin: id => this.catalogue.find(p => p.id === id),
      patch: id => this.patchStore.get(id),
      playhead: this.transportState === 'stopped' ? null : this.positionTick,
      pass: this.pass
    };
  }

  /** Plugins that crash as they start to play: the engine bypasses them and says so (`pluginProblem`). */
  private crashPlugins(): void {
    const project = this.project;
    if (!project) {
      return;
    }
    const strips = [
      { trackId: null, strip: project.master },
      ...project.tracks.map(t => ({ trackId: t.id, strip: t.strip }))
    ];
    for (const { trackId, strip } of strips) {
      for (const insert of strip.inserts) {
        if (insert.pluginId === CrashyPluginId && !insert.bypassed && !this.crashed.has(insert.id)) {
          this.crashed.add(insert.id);
          setTimeout(
            () =>
              this.emit({
                type: 'pluginProblem',
                trackId,
                insertId: insert.id,
                pluginId: insert.pluginId,
                message: `${insert.name} crashed; it is bypassed until it is inserted again.`
              }),
            0
          );
        }
      }
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
    // Cells deleted while they play stop.
    this.emitCells(this.launcher.forget(new Set(project.liveLoops.cells.map(c => c.id)), this.positionTick));
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
      frames: header.frames,
      tempo: null
    };
    if (project.settings.smartTempo.analyzeImports) {
      file.tempo = this.analysisOf(file).tempo;
    }
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
    // Anode Essentials comes installed (the content builder's pack in the desktop app).
    this.loopLibrary.install(`${FakeHome}/Music/Anode Essentials.anodepack`);
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
        tempoEvents: [{ tick: 0, bpm: 112, curve: 'step', shape: 0 }],
        signatures: [{ tick: 0, numerator: 4, denominator: 4 }],
        keys: [{ tick: 0, tonic: 0, mode: 'major' }],
        recording: {
          cycleMode: 'takeFolder',
          replace: false,
          autopunch: false,
          punchInTick: 0,
          punchOutTick: 4 * 4 * Ppq
        },
        mixer: { pdc: 'all', lowLatency: false, lowLatencyLimitMs: 10 },
        smartTempo: { mode: 'keep', defaultFollow: 'off', analyzeImports: true },
        varispeed: { enabled: false, mode: 'speedOnly', speed: 1 }
      },
      master: strip(),
      tracks: [],
      regions: [],
      audioFiles: [],
      midiRegions: [],
      takeFolders: [],
      markers: [],
      arrangement: [],
      notes: '',
      groups: [],
      automation: [],
      patternRegions: [],
      liveLoops: { scenes: [], cells: [], quantize: 'bar', shown: false }
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
        frames: seconds * 48_000,
        tempo: null
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
    project = this.demoMix(project);
    this.disk.set(disk, structuredClone(project));
    this.recent = [{ id: project.id, name: project.name, path: disk, modified: new Date(this.now()).toISOString() }];
    this.project = project;
  }

  /**
   * The demo's mix (Phase 4's material): a Reverb aux on bus 1 fed by sends from the vocals and guitar, a compressor and
   * EQ on the vocals, a limiter on the master, a group and a VCA, a volume automation lane on the vocals with a curved
   * segment; then two patches and I/O labels.
   */
  private demoMix(base: Project): Project {
    let project = base;
    const named = (name: string) => project.tracks.find(t => t.name === name)?.id ?? '';
    const run = (command: EditCommand) => {
      project = this.applyCommand(project, command);
    };
    run({ type: 'addAux', bus: 1, name: 'Reverb' });
    const reverb = project.tracks.at(-1)?.id ?? '';
    run({ type: 'addInsert', trackId: reverb, pluginId: 'anode.reverb' });
    run({ type: 'addSend', trackId: named('Vocals'), bus: 1, levelDb: -12 });
    run({ type: 'addSend', trackId: named('Guitar'), bus: 1, levelDb: -18 });
    run({ type: 'addInsert', trackId: named('Vocals'), pluginId: 'anode.compressor' });
    run({ type: 'addInsert', trackId: named('Vocals'), pluginId: 'anode.channel-eq' });
    run({ type: 'addInsert', pluginId: 'anode.limiter' });
    run({ type: 'setGroup', number: 1, name: 'Rhythm', properties: null });
    run({ type: 'setTrackGroups', trackId: named('Drums'), groups: [1] });
    run({ type: 'setTrackGroups', trackId: named('Bass'), groups: [1] });
    run({ type: 'addVca', name: 'Band VCA' });
    const vca = project.tracks.at(-1)?.id ?? '';
    for (const name of ['Drums', 'Bass', 'Guitar']) {
      run({ type: 'assignVca', trackId: named(name), vcaId: vca });
    }
    const bar = 4 * Ppq;
    run({
      type: 'setAutomation',
      trackId: named('Vocals'),
      target: 'volume',
      points: [
        { tick: 4 * bar, value: -6, curve: 0 },
        { tick: 6 * bar, value: -2, curve: 0.6 },
        { tick: 8 * bar, value: -10, curve: 0 },
        { tick: 10 * bar, value: -10, curve: -0.5 },
        { tick: 12 * bar, value: 0, curve: 0 }
      ]
    });
    run({ type: 'setAutomationMode', trackId: named('Vocals'), mode: 'read' });

    const vocals = project.tracks.find(t => t.name === 'Vocals');
    const keys = project.tracks.find(t => t.name === 'Keys');
    if (vocals) {
      const info: PatchInfo = { id: `pat_${this.nextId++}`, name: 'Vocal Chain', category: 'Vocals', kind: 'audio' };
      this.patchStore.set(info.id, patchOf(vocals, info));
    }
    if (keys) {
      const info: PatchInfo = {
        id: `pat_${this.nextId++}`,
        name: 'Warm Keys',
        category: 'Keyboards',
        kind: 'instrument'
      };
      const chorus = this.catalogue.find(p => p.id === 'anode.chorus');
      const patch = patchOf(keys, info);
      if (chorus) {
        patch.inserts = [
          {
            id: `ins_${this.nextId++}`,
            pluginId: chorus.id,
            name: chorus.name,
            bypassed: false,
            parameters: defaultParameters(chorus),
            sidechain: null
          }
        ];
      }
      this.patchStore.set(info.id, patch);
    }
    this.labels = {
      inputs: [{ number: 1, name: 'Vocal Mic' }],
      outputs: [{ number: 1, name: 'Main' }],
      buses: [
        { number: 1, name: 'Reverb' },
        { number: 2, name: 'Delay' }
      ]
    };
    return project;
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
/** The last region of a project renamed (the one a command just added). */
/** The key's tonic at a tick (C without key events). */
function keyTonicAt(project: Project, tick: number): number {
  let tonic = 0;
  for (const key of [...project.settings.keys].sort((a, b) => a.tick - b.tick)) {
    if (key.tick <= tick) {
      tonic = key.tonic;
    }
  }
  return tonic;
}

function renameLast(project: Project, name: string): Project {
  return { ...project, regions: project.regions.map((r, i, all) => (i === all.length - 1 ? { ...r, name } : r)) };
}

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
  return {
    volumeDb: 0,
    pan: 0,
    mute: false,
    solo: false,
    inserts: [],
    sends: [],
    output: 'master',
    groups: [],
    vcaId: null,
    midiEffects: []
  };
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
  if (only?.type === 'setInsertParameter') {
    return `${only.trackId ?? 'master'}#${only.insertId}.${only.parameterId}`;
  }
  // A Smart Control's drag is one undo step, as a parameter's (docs/07-phase-6.md).
  if (only?.type === 'setSmartControl' && only.value !== null && only.value !== undefined) {
    return `${only.trackId}~${only.controlId}`;
  }
  if (only?.type === 'setSend') {
    const fields = (['levelDb', 'pan', 'preFader', 'bus'] as const).filter(
      f => only[f] !== undefined && only[f] !== null
    );
    return `${only.trackId}>${only.sendId}:${fields.join(',')}`;
  }
  if (only?.type !== 'setStrip') {
    return undefined;
  }

  const fields = (['volumeDb', 'pan', 'mute', 'solo'] as const).filter(f => only[f] !== undefined && only[f] !== null);
  return `${only.trackId ?? 'master'}:${fields.join(',')}`;
}

function isRunning(job: Job): boolean {
  return job.state === 'queued' || job.state === 'running';
}

/** A path with `~` as the fake user's home. */
function absolute(path: string): string {
  return path.startsWith('~/') ? `${FakeHome}${path.slice(1)}` : path;
}

/** A project's folder, absolute (its `Bounces` and `Freeze Files` are in it). */
function projectFolder(project: Project): string {
  return absolute(project.path).replace(/\/+$/, '');
}

/** Logic's arrangement marker colours, in the order new sections take them. */
const SectionColors = ['#f5a623', '#4a90e2', '#d0021b', '#7ed321', '#9013fe', '#50e3c2', '#f8e71c', '#bd10e0'];

const KindNames: Record<Track['kind'], string> = {
  audio: 'Audio',
  instrument: 'Inst',
  midi: 'MIDI',
  stack: 'Stack',
  aux: 'Aux',
  vca: 'VCA'
};

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
  diffList(
    before.automation,
    after.automation,
    same,
    id => ops.push({ op: 'automationLaneRemoved', id }),
    lane => ops.push({ op: 'automationLaneUpserted', lane })
  );
  diffList(
    before.patternRegions,
    after.patternRegions,
    same,
    id => ops.push({ op: 'patternRegionRemoved', id }),
    region => ops.push({ op: 'patternRegionUpserted', region })
  );
  if (!same(before.liveLoops, after.liveLoops)) {
    ops.push({ op: 'liveLoopsChanged', liveLoops: after.liveLoops });
  }
  const groupsAfter = new Map(after.groups.map(g => [g.number, g]));
  const groupsBefore = new Map(before.groups.map(g => [g.number, g]));
  for (const group of before.groups) {
    if (!groupsAfter.has(group.number)) {
      ops.push({ op: 'groupRemoved', number: group.number });
    }
  }
  for (const group of after.groups) {
    if (!same(groupsBefore.get(group.number), group)) {
      ops.push({ op: 'groupUpserted', group });
    }
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
