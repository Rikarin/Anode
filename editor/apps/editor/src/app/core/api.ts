import { Injectable, InjectionToken, inject } from '@angular/core';
import {
  type AddLoopRequest,
  type AddToLoopLibraryRequest,
  ApiError,
  type ArticulationSet,
  type AudioEditOperation,
  type AudioFile,
  type Bookmarks,
  type BounceInPlaceRequest,
  type BounceRequest,
  type CommandBatch,
  type CommandResult,
  type DirectoryListing,
  type FileAnalysis,
  type GrooveTemplate,
  type IoLabels,
  type Job,
  type KeyCommandSet,
  type LaunchRequest,
  type LiveMidiEvent,
  type LiveRecordRequest,
  type LoopInfo,
  type LoopKind,
  type LoopPage,
  type LoopScale,
  type LoopSearchRequest,
  type LoopSearchResult,
  type MidiDevices,
  type Pack,
  type PatchInfo,
  type PluginEditorRequest,
  type PluginInfo,
  type PreviewRangeRequest,
  type ProcessRequest,
  type Project,
  type ProjectAlternative,
  type ProjectBackup,
  type ProjectSummary,
  type SampleRange,
  type SavePatchRequest,
  type Screenset,
  type ScriptPreset,
  type StepInputRequest,
  type StepInputState,
  type StopRequest,
  type SystemInfo,
  type TransformSet,
  type TransportRequest,
  type TransportStatus,
  type UndoHistory,
  unwrap
} from '@anode/api-client';
import { ConnectionService } from './connection';

export interface CreateProject {
  name: string;
  /** The folder to create the project folder in; the Server's default projects folder when left out. */
  directory?: string | null;
  sampleRate?: number | null;
}

/**
 * A Loop Browser search (`GET /library/loops`, docs/07-phase-6.md): text in the name or tags, every one of `tags`, a
 * kind, a key (tonic 0…11), a scale (major or minor also match `both`), favourites only, and a page.
 */
export interface LoopSearch {
  text?: string | null;
  tags?: readonly string[] | null;
  kind?: LoopKind | null;
  key?: number | null;
  scale?: LoopScale | null;
  favouritesOnly?: boolean | null;
  offset?: number | null;
  limit?: number | null;
}

/**
 * The Server's REST API as the stores use it (engine/src/Anode.Server/openapi.json). {@link HttpAnodeApi} talks to a
 * real Server; the in-memory fake (core/fake/fake-server.ts) stands in for it in unit tests and the `?fake` dev mode.
 * Every method rejects with an `ApiError` when the Server refuses.
 */
export interface AnodeApi {
  system(): Promise<SystemInfo>;
  recentProjects(): Promise<ProjectSummary[]>;
  createProject(request: CreateProject): Promise<Project>;
  openProject(path: string): Promise<Project>;
  /** The open project, or null when none is open (404). */
  getProject(): Promise<Project | null>;
  saveProject(): Promise<ProjectSummary>;
  closeProject(): Promise<void>;
  /** One undo step. With `revision`, a stale one is refused (409) instead of applied. */
  applyCommands(batch: CommandBatch, revision?: number): Promise<CommandResult>;
  undo(): Promise<CommandResult>;
  redo(): Promise<CommandResult>;
  history(): Promise<UndoHistory>;
  /** Copies a WAV from this browser into the project (a browser on the network). */
  uploadAudioFile(file: File): Promise<AudioFile>;
  /** Copies a WAV the Server can read from its own disk into the project (the desktop app). */
  importAudioFile(path: string): Promise<AudioFile>;
  /** A `PeakFormat` body at the kept resolution nearest `framesPerPeak`. */
  peaks(fileId: string, framesPerPeak: number): Promise<ArrayBuffer>;
  transport(): Promise<TransportStatus>;
  controlTransport(request: TransportRequest): Promise<TransportStatus>;
  /** The plugins the engine can load, built-in first (empty without an engine). */
  plugins(): Promise<PluginInfo[]>;
  /** Rescans the CLAP folders; `pluginsChanged` follows on `/ws`. */
  rescanPlugins(): Promise<void>;
  /** The MIDI ports of the machine the engine runs on. */
  midiDevices(): Promise<MidiDevices>;
  /** Plays MIDI on a track now, as a keyboard would (the on-screen keyboard, musical typing). */
  sendLiveMidi(trackId: string, events: LiveMidiEvent[]): Promise<void>;
  /** Which track hears live MIDI when no track is record-armed (the selected one); null for none. */
  setMidiThru(trackId: string | null): Promise<void>;
  /** Flashback Capture: what the armed tracks heard since playback last started becomes a recording (a delta follows). */
  flashbackCapture(): Promise<void>;
  /** The open project's alternatives; one is current. */
  alternatives(): Promise<ProjectAlternative[]>;
  /** Saves the open project as a new alternative and makes it current. */
  createAlternative(name: string): Promise<ProjectAlternative>;
  /** Switches to another alternative; resolves with it as the open project. */
  openAlternative(id: string): Promise<Project>;
  deleteAlternative(id: string): Promise<void>;
  /** The current alternative's backups, newest first. */
  backups(): Promise<ProjectBackup[]>;
  /** Replaces the open project with a backup (like reopening it: no undo step). */
  revertToBackup(id: string): Promise<Project>;
  /** The screensets saved in the open project. */
  screensets(): Promise<Screenset[]>;
  /** Saves screenset 1…9 in the project (not an undo step; it marks the project unsaved). */
  saveScreenset(screenset: Screenset): Promise<Screenset>;
  /** Imports a Standard MIDI File from the Server's disk as instrument tracks, with its tempo and signatures. */
  importMidiFile(path: string): Promise<CommandResult>;
  /** The user's key bindings (empty: the editor's defaults). */
  keyCommands(): Promise<KeyCommandSet>;
  saveKeyCommands(set: KeyCommandSet): Promise<KeyCommandSet>;
  /** Folders pinned in the All Files browser. */
  bookmarks(): Promise<Bookmarks>;
  saveBookmarks(bookmarks: Bookmarks): Promise<Bookmarks>;
  /** A folder on the Server's disk (the user's home when `path` is left out). */
  listFiles(path?: string | null): Promise<DirectoryListing>;
  /** Plays an audio file through the engine's preview channel; null stops it. */
  previewFile(path: string | null): Promise<void>;
  /** Starts a bounce (export) job; its progress follows as `job` events. */
  bounce(request: BounceRequest): Promise<Job>;
  /** Starts freezing a track; when the job is done, a delta sets its `frozenFileId` (one undo step). */
  freezeTrack(trackId: string): Promise<Job>;
  job(id: string): Promise<Job>;
  cancelJob(id: string): Promise<Job>;
  /** The user's saved channel-strip patches. */
  patches(): Promise<PatchInfo[]>;
  /** Saves a track's instrument, inserts and sends as a patch. */
  savePatch(request: SavePatchRequest): Promise<PatchInfo>;
  deletePatch(id: string): Promise<void>;
  /** The user's names for inputs, output pairs and buses (Logic's I/O Labels). */
  ioLabels(): Promise<IoLabels>;
  saveIoLabels(labels: IoLabels): Promise<IoLabels>;
  /**
   * Opens or closes a plugin's own editor window on the machine the engine runs on (the desktop app); refused (409)
   * when the plugin has none or the engine runs headless.
   */
  openPluginEditor(request: PluginEditorRequest): Promise<void>;
  /**
   * A file's cached analysis (Phase 5): transients and beats (analysed now when missing), and its pitch curve and notes
   * once an `analyze` job has run.
   */
  fileAnalysis(fileId: string): Promise<FileAnalysis>;
  /** Starts pitch analysis of a file (Flex Pitch); an `analysis` event follows when it is cached. */
  analyzeFile(fileId: string): Promise<Job>;
  /** Bounce in place: renders tracks or regions to new files; the edit follows when the job is done (one undo step). */
  bounceInPlace(request: BounceInPlaceRequest): Promise<Job>;
  /** Selection-based processing: a chain of built-in effects over part of regions, as a job (one undo step). */
  processRegions(request: ProcessRequest): Promise<Job>;
  /** Searches the loop index (Phase 6: the installed packs' loops and the user's), sorted by name. */
  queryLoops(search: LoopSearch): Promise<LoopPage>;
  /** Plays a loop through the preview channel in sync with the project (tempo and key); `previewFile(null)` stops. */
  previewLoop(id: string): Promise<void>;
  /** Marks a loop a favourite, or not. */
  setLoopFavourite(id: string, favourite: boolean): Promise<LoopInfo>;
  /** "Add to Loop Library": saves a region as a user loop. */
  addToLoopLibrary(request: AddToLoopLibraryRequest): Promise<LoopInfo>;
  /** The installed content packs. */
  packs(): Promise<Pack[]>;
  /** Installs a `.anodepack` from the Server's disk (replacing an installed version of the same pack). */
  installPack(path: string): Promise<Pack>;
  removePack(id: string): Promise<void>;
  /** Adds a loop to the open project (a region, a new track's region, or a Live Loops cell): one undo step. */
  addLoop(request: AddLoopRequest): Promise<CommandResult>;
  /** Launches Live Loops cells or a scene, each at its quantise boundary; `cell` events follow. */
  launchCells(request: LaunchRequest): Promise<void>;
  /** Stops the cells playing on tracks (every track when left out) at the grid's quantise boundary. */
  stopCells(request: StopRequest): Promise<void>;
  /** Starts or stops recording into a cell, or a performance recording into the Tracks area. */
  recordLiveLoops(request: LiveRecordRequest): Promise<void>;
  // ---- Phase 7 (docs/08-phase-7.md)
  /** The groove templates saved in the user library. */
  libraryGrooves(): Promise<GrooveTemplate[]>;
  /** Saves a groove template in the user library (replacing the one with its id). */
  saveLibraryGroove(groove: GrooveTemplate): Promise<GrooveTemplate>;
  deleteLibraryGroove(id: string): Promise<void>;
  /** The MIDI Transform factory presets (`factory.…`) followed by the user's saved sets, by name. */
  transformSets(): Promise<TransformSet[]>;
  /** Saves a Transform set in the user library (a factory preset's id is refused: save a copy under a new id). */
  saveTransformSet(set: TransformSet): Promise<TransformSet>;
  deleteTransformSet(id: string): Promise<void>;
  /** The articulation sets saved in the user library. */
  libraryArticulationSets(): Promise<ArticulationSet[]>;
  saveLibraryArticulationSet(set: ArticulationSet): Promise<ArticulationSet>;
  deleteLibraryArticulationSet(id: string): Promise<void>;
  /** The Scripter's factory scripts (`factory.…`), in menu order. */
  scriptPresets(): Promise<ScriptPreset[]>;
  /** Runs an Audio File Editor operation as an `audioEdit` job; the file's new version follows as one undo step. */
  editFile(fileId: string, operation: AudioEditOperation): Promise<Job>;
  /** Loop search over a range of a file: the best loops, best first. */
  searchLoops(fileId: string, request: LoopSearchRequest): Promise<LoopSearchResult[]>;
  /** Samples of a file's current version: raw at bucket 1, else min and max per bucket. */
  fileSamples(fileId: string, start: number, frames: number, bucket?: number): Promise<SampleRange>;
  /** Plays a range of a file through the preview channel (`previewFile(null)` stops it). */
  previewFileRange(fileId: string, request: PreviewRangeRequest): Promise<void>;
  /** The step input state (not saved, not undoable). */
  stepInput(): Promise<StepInputState>;
  /** Changes the step input state (left-out properties stay); a `stepInput` event follows. */
  setStepInput(request: StepInputRequest): Promise<StepInputState>;
  /** A rest: advances the step position by one step. */
  stepRest(): Promise<StepInputState>;
}

export const ANODE_API = new InjectionToken<AnodeApi>('AnodeApi', {
  providedIn: 'root',
  factory: () => inject(HttpAnodeApi)
});

/** An `If-Match` value for a revision (the Server's ETag is the quoted revision). */
export function ifMatch(revision: number): string {
  return `"${revision}"`;
}

/** {@link AnodeApi} over HTTP, through the generated client of the connected Server. */
@Injectable({ providedIn: 'root' })
export class HttpAnodeApi implements AnodeApi {
  private readonly connection = inject(ConnectionService);

  private get client() {
    return this.connection.client;
  }

  async system(): Promise<SystemInfo> {
    return unwrap(await this.client.GET('/api/v1/system'));
  }

  async recentProjects(): Promise<ProjectSummary[]> {
    return unwrap(await this.client.GET('/api/v1/projects/recent'));
  }

  async createProject(request: CreateProject): Promise<Project> {
    return unwrap(
      await this.client.POST('/api/v1/projects', {
        body: { name: request.name, directory: request.directory ?? null, sampleRate: request.sampleRate ?? null }
      })
    );
  }

  async openProject(path: string): Promise<Project> {
    return unwrap(await this.client.POST('/api/v1/projects/open', { body: { path } }));
  }

  async getProject(): Promise<Project | null> {
    try {
      return unwrap(await this.client.GET('/api/v1/project'));
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) {
        return null;
      }

      throw error;
    }
  }

  async saveProject(): Promise<ProjectSummary> {
    return unwrap(await this.client.POST('/api/v1/project/save'));
  }

  async closeProject(): Promise<void> {
    await this.client.POST('/api/v1/project/close');
  }

  async applyCommands(batch: CommandBatch, revision?: number): Promise<CommandResult> {
    return unwrap(
      await this.client.POST('/api/v1/project/commands', {
        body: batch,
        ...(revision === undefined ? {} : { headers: { 'If-Match': ifMatch(revision) } })
      })
    );
  }

  async undo(): Promise<CommandResult> {
    return unwrap(await this.client.POST('/api/v1/project/undo'));
  }

  async redo(): Promise<CommandResult> {
    return unwrap(await this.client.POST('/api/v1/project/redo'));
  }

  async history(): Promise<UndoHistory> {
    return unwrap(await this.client.GET('/api/v1/project/history'));
  }

  async uploadAudioFile(file: File): Promise<AudioFile> {
    return unwrap(
      await this.client.POST('/api/v1/project/files', {
        // The generated type says string (IFormFile); the body is multipart with the File itself.
        body: { file: file as unknown as string },
        bodySerializer: body => {
          const form = new FormData();
          form.append('file', body.file as unknown as Blob, file.name);
          return form;
        }
      })
    );
  }

  async importAudioFile(path: string): Promise<AudioFile> {
    return unwrap(await this.client.POST('/api/v1/project/files/import', { body: { path } }));
  }

  async peaks(fileId: string, framesPerPeak: number): Promise<ArrayBuffer> {
    const result = await this.client.GET('/api/v1/project/files/{fileId}/peaks', {
      params: { path: { fileId }, query: { framesPerPeak } },
      parseAs: 'arrayBuffer'
    });
    return unwrap(result);
  }

  async transport(): Promise<TransportStatus> {
    return unwrap(await this.client.GET('/api/v1/transport'));
  }

  async controlTransport(request: TransportRequest): Promise<TransportStatus> {
    return unwrap(await this.client.POST('/api/v1/transport', { body: request }));
  }

  async plugins(): Promise<PluginInfo[]> {
    return unwrap(await this.client.GET('/api/v1/plugins'));
  }

  async rescanPlugins(): Promise<void> {
    await this.client.POST('/api/v1/plugins/rescan');
  }

  async midiDevices(): Promise<MidiDevices> {
    return unwrap(await this.client.GET('/api/v1/midi/devices'));
  }

  async sendLiveMidi(trackId: string, events: LiveMidiEvent[]): Promise<void> {
    await this.client.POST('/api/v1/midi/live', { body: { trackId, events } });
  }

  async setMidiThru(trackId: string | null): Promise<void> {
    await this.client.POST('/api/v1/midi/thru', { body: { trackId } });
  }

  async flashbackCapture(): Promise<void> {
    await this.client.POST('/api/v1/transport/capture');
  }

  async alternatives(): Promise<ProjectAlternative[]> {
    return unwrap(await this.client.GET('/api/v1/project/alternatives'));
  }

  async createAlternative(name: string): Promise<ProjectAlternative> {
    return unwrap(await this.client.POST('/api/v1/project/alternatives', { body: { name } }));
  }

  async openAlternative(id: string): Promise<Project> {
    return unwrap(await this.client.POST('/api/v1/project/alternatives/{id}/open', { params: { path: { id } } }));
  }

  async deleteAlternative(id: string): Promise<void> {
    await this.client.DELETE('/api/v1/project/alternatives/{id}', { params: { path: { id } } });
  }

  async backups(): Promise<ProjectBackup[]> {
    return unwrap(await this.client.GET('/api/v1/project/backups'));
  }

  async revertToBackup(id: string): Promise<Project> {
    return unwrap(await this.client.POST('/api/v1/project/backups/{id}/revert', { params: { path: { id } } }));
  }

  async screensets(): Promise<Screenset[]> {
    return unwrap(await this.client.GET('/api/v1/project/screensets'));
  }

  async saveScreenset(screenset: Screenset): Promise<Screenset> {
    return unwrap(
      await this.client.PUT('/api/v1/project/screensets/{number}', {
        params: { path: { number: screenset.number } },
        body: screenset
      })
    );
  }

  async importMidiFile(path: string): Promise<CommandResult> {
    return unwrap(await this.client.POST('/api/v1/project/files/import-midi', { body: { path } }));
  }

  async keyCommands(): Promise<KeyCommandSet> {
    return unwrap(await this.client.GET('/api/v1/settings/key-commands'));
  }

  async saveKeyCommands(set: KeyCommandSet): Promise<KeyCommandSet> {
    return unwrap(await this.client.PUT('/api/v1/settings/key-commands', { body: set }));
  }

  async bookmarks(): Promise<Bookmarks> {
    return unwrap(await this.client.GET('/api/v1/settings/bookmarks'));
  }

  async saveBookmarks(bookmarks: Bookmarks): Promise<Bookmarks> {
    return unwrap(await this.client.PUT('/api/v1/settings/bookmarks', { body: bookmarks }));
  }

  async listFiles(path?: string | null): Promise<DirectoryListing> {
    return unwrap(await this.client.GET('/api/v1/files', { params: { query: path ? { path } : {} } }));
  }

  async previewFile(path: string | null): Promise<void> {
    await this.client.POST('/api/v1/files/preview', { body: { path } });
  }

  async bounce(request: BounceRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/bounce', { body: request }));
  }

  async freezeTrack(trackId: string): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/tracks/{trackId}/freeze', { params: { path: { trackId } } }));
  }

  async job(id: string): Promise<Job> {
    return unwrap(await this.client.GET('/api/v1/jobs/{id}', { params: { path: { id } } }));
  }

  async cancelJob(id: string): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/jobs/{id}/cancel', { params: { path: { id } } }));
  }

  async patches(): Promise<PatchInfo[]> {
    return unwrap(await this.client.GET('/api/v1/library/patches'));
  }

  async savePatch(request: SavePatchRequest): Promise<PatchInfo> {
    return unwrap(await this.client.POST('/api/v1/library/patches', { body: request }));
  }

  async deletePatch(id: string): Promise<void> {
    unwrap(await this.client.DELETE('/api/v1/library/patches/{id}', { params: { path: { id } } }));
  }

  async ioLabels(): Promise<IoLabels> {
    return unwrap(await this.client.GET('/api/v1/settings/io-labels'));
  }

  async saveIoLabels(labels: IoLabels): Promise<IoLabels> {
    return unwrap(await this.client.PUT('/api/v1/settings/io-labels', { body: labels }));
  }

  async openPluginEditor(request: PluginEditorRequest): Promise<void> {
    unwrap(await this.client.POST('/api/v1/plugins/editor', { body: request }));
  }

  async fileAnalysis(fileId: string): Promise<FileAnalysis> {
    return unwrap(await this.client.GET('/api/v1/project/files/{fileId}/analysis', { params: { path: { fileId } } }));
  }

  async analyzeFile(fileId: string): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/files/{fileId}/analyze', { params: { path: { fileId } } }));
  }

  async bounceInPlace(request: BounceInPlaceRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/bounce-in-place', { body: request }));
  }

  async processRegions(request: ProcessRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/process', { body: request }));
  }

  async queryLoops(search: LoopSearch): Promise<LoopPage> {
    const query: Record<string, string | number | boolean | string[]> = {};
    for (const [key, value] of Object.entries(search)) {
      if (value !== null && value !== undefined && value !== '' && !(Array.isArray(value) && value.length === 0)) {
        query[key] = Array.isArray(value) ? [...(value as string[])] : (value as string | number | boolean);
      }
    }
    return unwrap(await this.client.GET('/api/v1/library/loops', { params: { query } }));
  }

  async previewLoop(id: string): Promise<void> {
    unwrap(await this.client.POST('/api/v1/library/loops/{id}/preview', { params: { path: { id } } }));
  }

  async setLoopFavourite(id: string, favourite: boolean): Promise<LoopInfo> {
    return unwrap(
      await this.client.PUT('/api/v1/library/loops/{id}/favourite', { params: { path: { id } }, body: { favourite } })
    );
  }

  async addToLoopLibrary(request: AddToLoopLibraryRequest): Promise<LoopInfo> {
    return unwrap(await this.client.POST('/api/v1/library/loops', { body: request }));
  }

  async packs(): Promise<Pack[]> {
    return unwrap(await this.client.GET('/api/v1/library/packs'));
  }

  async installPack(path: string): Promise<Pack> {
    return unwrap(await this.client.POST('/api/v1/library/packs', { body: { path } }));
  }

  async removePack(id: string): Promise<void> {
    unwrap(await this.client.DELETE('/api/v1/library/packs/{id}', { params: { path: { id } } }));
  }

  async addLoop(request: AddLoopRequest): Promise<CommandResult> {
    return unwrap(await this.client.POST('/api/v1/project/loops', { body: request }));
  }

  async launchCells(request: LaunchRequest): Promise<void> {
    unwrap(await this.client.POST('/api/v1/project/live-loops/launch', { body: request }));
  }

  async stopCells(request: StopRequest): Promise<void> {
    unwrap(await this.client.POST('/api/v1/project/live-loops/stop', { body: request }));
  }

  async recordLiveLoops(request: LiveRecordRequest): Promise<void> {
    unwrap(await this.client.POST('/api/v1/project/live-loops/record', { body: request }));
  }

  async libraryGrooves(): Promise<GrooveTemplate[]> {
    return unwrap(await this.client.GET('/api/v1/library/grooves'));
  }

  async saveLibraryGroove(groove: GrooveTemplate): Promise<GrooveTemplate> {
    return unwrap(await this.client.POST('/api/v1/library/grooves', { body: groove }));
  }

  async deleteLibraryGroove(id: string): Promise<void> {
    unwrap(await this.client.DELETE('/api/v1/library/grooves/{id}', { params: { path: { id } } }));
  }

  async transformSets(): Promise<TransformSet[]> {
    return unwrap(await this.client.GET('/api/v1/library/transform-sets'));
  }

  async saveTransformSet(set: TransformSet): Promise<TransformSet> {
    return unwrap(await this.client.POST('/api/v1/library/transform-sets', { body: set }));
  }

  async deleteTransformSet(id: string): Promise<void> {
    unwrap(await this.client.DELETE('/api/v1/library/transform-sets/{id}', { params: { path: { id } } }));
  }

  async libraryArticulationSets(): Promise<ArticulationSet[]> {
    return unwrap(await this.client.GET('/api/v1/library/articulation-sets'));
  }

  async saveLibraryArticulationSet(set: ArticulationSet): Promise<ArticulationSet> {
    return unwrap(await this.client.POST('/api/v1/library/articulation-sets', { body: set }));
  }

  async deleteLibraryArticulationSet(id: string): Promise<void> {
    unwrap(await this.client.DELETE('/api/v1/library/articulation-sets/{id}', { params: { path: { id } } }));
  }

  async scriptPresets(): Promise<ScriptPreset[]> {
    return unwrap(await this.client.GET('/api/v1/library/scripts'));
  }

  async editFile(fileId: string, operation: AudioEditOperation): Promise<Job> {
    return unwrap(
      await this.client.POST('/api/v1/project/files/{fileId}/edit', { params: { path: { fileId } }, body: operation })
    );
  }

  async searchLoops(fileId: string, request: LoopSearchRequest): Promise<LoopSearchResult[]> {
    return unwrap(
      await this.client.POST('/api/v1/project/files/{fileId}/loop-search', {
        params: { path: { fileId } },
        body: request
      })
    );
  }

  async fileSamples(fileId: string, start: number, frames: number, bucket = 1): Promise<SampleRange> {
    return unwrap(
      await this.client.GET('/api/v1/project/files/{fileId}/samples', {
        params: { path: { fileId }, query: { start, frames, bucket } }
      })
    );
  }

  async previewFileRange(fileId: string, request: PreviewRangeRequest): Promise<void> {
    unwrap(
      await this.client.POST('/api/v1/project/files/{fileId}/preview', { params: { path: { fileId } }, body: request })
    );
  }

  async stepInput(): Promise<StepInputState> {
    return unwrap(await this.client.GET('/api/v1/project/step-input'));
  }

  async setStepInput(request: StepInputRequest): Promise<StepInputState> {
    return unwrap(await this.client.PUT('/api/v1/project/step-input', { body: request }));
  }

  async stepRest(): Promise<StepInputState> {
    return unwrap(await this.client.POST('/api/v1/project/step-input/rest'));
  }
}
