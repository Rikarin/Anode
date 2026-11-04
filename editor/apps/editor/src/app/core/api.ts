import { inject, Injectable, InjectionToken } from '@angular/core';
import {
  type AafExportRequest,
  type AafImportRequest,
  type AddLoopRequest,
  type AddSurfaceRequest,
  type AddToLoopLibraryRequest,
  type AdmExportRequest,
  type AdmImportRequest,
  ApiError,
  type ArticulationSet,
  type AudioEditOperation,
  type AudioFile,
  type Bookmarks,
  type BounceInPlaceRequest,
  type BounceRequest,
  type ChordIdRequest,
  type ChordTrack,
  type CommandBatch,
  type CommandResult,
  type ControllerAssignments,
  type ControlSurface,
  type DirectoryListing,
  type DiscoveryInfo,
  type EnvironmentInputRequest,
  type FileAnalysis,
  type GrooveTemplate,
  type IoLabels,
  type Job,
  type KeyCommandSet,
  type LaunchRequest,
  type LearnRequest,
  type LearnState,
  type ListenInState,
  type LiveMidiEvent,
  type LiveRecordRequest,
  type LoopInfo,
  type LoopKind,
  type LoopPage,
  type LoopScale,
  type LoopSearchRequest,
  type LoopSearchResult,
  type MasteringAnalysisRequest,
  type MasteringSettings,
  type MasteringState,
  type MidiDevices,
  type MidiEnvironment,
  type MidiExportRequest,
  type MidiImportRequest,
  type MonitoringSettings,
  type Movie,
  type MovieAudioExportRequest,
  type MovieAudioImportRequest,
  type MovieImportRequest,
  type MovieThumbnails,
  type MusicXmlExportRequest,
  type MusicXmlImportRequest,
  type OfflineReport,
  type OfflineSearch,
  type OfflineSearchRequest,
  type OscSettings,
  type OscStatus,
  type Pack,
  type PatchInfo,
  type PluginEditorRequest,
  type PluginInfo,
  type PreviewRangeRequest,
  type ProcessRequest,
  type Project,
  type ImportProjectTracksRequest,
  type ProjectAlternative,
  type ProjectContents,
  type SampleRateChangeMode,
  type ProjectBackup,
  type ProjectSummary,
  type RecoverableProject,
  type RegenerateRequest,
  type RelinkRequest,
  type RemotePads,
  type SampleRange,
  type SavePatchRequest,
  type SceneDetectRequest,
  type ScoreDocument,
  type ScoreViewMode,
  type Screenset,
  type ScriptPreset,
  type SessionPlayerCatalog,
  type SessionPlayerConvertRequest,
  type SessionPlayerNotes,
  type StaffStyle,
  type StemSplitRequest,
  type StepInputRequest,
  type StepInputState,
  type StopRequest,
  type SurfaceStatus,
  type SyncState,
  type SystemInfo,
  type ThumbnailsRequest,
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
 * Which score to render (`GET /project/score`, docs/10-phase-9.md): a score set (left out: every MIDI track), or one
 * track's part, in the linear (default) or page view.
 */
export interface ScoreQuery {
  set?: string | null;
  part?: string | null;
  view?: ScoreViewMode | null;
}

/** A MIDI file import's options (`MidiImportRequest` without its path). */
export type MidiImportOptions = Partial<Omit<MidiImportRequest, 'path'>>;

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
  /** What a project on the Server's disk holds, read without opening it. */
  projectContents(path: string): Promise<ProjectContents>;
  /** Queues importing chosen tracks of another project, one undo step. */
  importProjectTracks(request: ImportProjectTracksRequest): Promise<Job>;
  /** Queues changing the project's sample rate; it reopens at the new rate when done. */
  changeSampleRate(sampleRate: number, mode: SampleRateChangeMode): Promise<Job>;
  /** Queues writing the whole package at a new place and going on there. */
  saveProjectAs(path: string): Promise<Job>;
  /** Queues writing the whole package at a new place; the open project stays. */
  saveProjectCopy(path: string): Promise<Job>;
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
  /** Reads the disk now: what is offline. */
  offline(): Promise<OfflineReport>;
  /** Looks through a folder of the Server's disk for WAVs named as the offline files, judging each by what it holds. */
  searchOffline(request: OfflineSearchRequest): Promise<OfflineSearch>;
  /** Points files at WAVs on the Server's disk, one undo step; 422, changing nothing, when one does not match. */
  relinkFiles(request: RelinkRequest): Promise<CommandResult>;
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
  /** Recent projects with autosaved edits their last save lacks (offered after a crash). */
  recoverableProjects(): Promise<RecoverableProject[]>;
  /** Opens a project with its autosaved edits, which show as unsaved. */
  recoverProject(path: string): Promise<Project>;
  /** Stops offering a project's autosaved edits; they stay available as its newest backup. */
  discardRecovery(path: string): Promise<void>;
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
  // ---- Phase 8 (docs/09-phase-8.md)
  /** Every controller assignment, by zone and mode (user settings). */
  controllerAssignments(): Promise<ControllerAssignments>;
  /** Replaces every controller assignment; the host's matcher table follows. */
  setControllerAssignments(assignments: ControllerAssignments): Promise<ControllerAssignments>;
  learn(): Promise<LearnState>;
  /** Turns Learn on, or changes its target while on; `controlLearn` events follow as controls move. */
  startLearn(request: LearnRequest): Promise<LearnState>;
  stopLearn(): Promise<LearnState>;
  /** The control surfaces, in order (user settings). */
  surfaces(): Promise<ControlSurface[]>;
  /** Each surface's live state, in order; `surfaces` events report changes. */
  surfaceStatus(): Promise<SurfaceStatus[]>;
  /** Adds a control surface after the others; its ports become control ports. */
  addSurface(request: AddSurfaceRequest): Promise<ControlSurface>;
  removeSurface(id: string): Promise<void>;
  /** Puts the surfaces in this order (every id once). */
  reorderSurfaces(ids: string[]): Promise<ControlSurface[]>;
  /** The OSC server: settings, whether it listens, this machine's addresses, subscribers. */
  osc(): Promise<OscStatus>;
  setOsc(settings: OscSettings): Promise<OscStatus>;
  /** Tells the Server the selected track (selected-track assignments, surfaces and OSC follow it). */
  setSelection(trackId: string | null): Promise<void>;
  /** The live sync state; the settings are `settings.sync`, changed with `setSyncSettings`. */
  syncState(): Promise<SyncState>;
  /** Splits an audio region into stems as a `stemSplit` job; the tracks follow as one undo step. */
  splitStems(regionId: string, request: StemSplitRequest): Promise<Job>;
  // ---- Phase 9 (docs/10-phase-9.md)
  /** The rendered score and its MEI; refetched after deltas that change notes, meter, key, tempo or score content. */
  score(query: ScoreQuery): Promise<ScoreDocument>;
  /** The factory staff styles; a project's own are in `score.staffStyles`. */
  staffStyles(): Promise<StaffStyle[]>;
  /** Exports a score set or part as MusicXML: a `musicXmlExport` job. */
  exportMusicXml(request: MusicXmlExportRequest): Promise<Job>;
  /** Exports audio tracks as AAF: an `aafExport` job. */
  exportAaf(request: AafExportRequest): Promise<Job>;
  /** Exports a Standard MIDI File: a `midiExport` job. */
  exportMidi(request: MidiExportRequest): Promise<Job>;
  /** Imports a MusicXML file from the Server's disk: a `musicXmlImport` job, one undo step when done. */
  importMusicXml(request: MusicXmlImportRequest): Promise<Job>;
  /** Imports an uploaded MusicXML file (a browser on the network). */
  uploadMusicXml(file: File): Promise<Job>;
  /** Imports an AAF file from the Server's disk: an `aafImport` job. */
  importAaf(request: AafImportRequest): Promise<Job>;
  /** Imports an uploaded AAF file (embedded media only). */
  uploadAaf(file: File): Promise<Job>;
  /** Imports a Standard MIDI File from the Server's disk with options: a `midiImport` job (`importMidiFile` imports at once). */
  importMidi(request: MidiImportRequest): Promise<Job>;
  /** Imports an uploaded Standard MIDI File with options. */
  uploadMidi(file: File, options?: MidiImportOptions): Promise<Job>;
  // ---- Phase 10 (docs/11-phase-10.md)
  /** Imports a movie from the Server's disk (copied in or referenced): a `movieImport` job; `setMovie` when done. */
  importMovie(request: MovieImportRequest): Promise<Job>;
  /** The project's movie (as `Project.movie`); 404 without one. */
  movie(): Promise<Movie>;
  /** Remove Movie (`removeMovie`, one undo step). */
  removeMovie(): Promise<CommandResult>;
  /** Renders movie frames as JPEG thumbnails into the movie's cache: a `thumbnails` job. */
  renderMovieThumbnails(request: ThumbnailsRequest): Promise<Job>;
  /** The frames cached as thumbnails at `height` (default 64); each is `GET /project/movie/frames/{frame}`. */
  movieThumbnails(height?: number): Promise<MovieThumbnails>;
  /** Detect Scene Cuts: a `sceneDetect` job that adds scene markers. */
  detectMovieScenes(request: SceneDetectRequest): Promise<Job>;
  /** Import Audio from Movie: a `movieAudioImport` job, a new audio track when done. */
  importMovieAudio(request: MovieAudioImportRequest): Promise<Job>;
  /** Export Audio to Movie: a `movieAudioExport` job writing a new movie. */
  exportMovieAudio(request: MovieAudioExportRequest): Promise<Job>;
  /** Exports the mix as ADM BWF (bed and objects): an `admExport` job. */
  exportAdm(request: AdmExportRequest): Promise<Job>;
  /** Imports an ADM BWF from the Server's disk as bed and object tracks: an `admImport` job. */
  importAdm(request: AdmImportRequest): Promise<Job>;
  /** Monitoring: binaural, down-mix, speaker map (user settings). */
  monitoring(): Promise<MonitoringSettings>;
  setMonitoring(settings: MonitoringSettings): Promise<MonitoringSettings>;
  /** This Server as remotes find it (what the `_anode._tcp` advertisement carries). */
  discovery(): Promise<DiscoveryInfo>;
  /** The remote's key-command pad sets (user settings). */
  remotePads(): Promise<RemotePads>;
  setRemotePads(pads: RemotePads): Promise<RemotePads>;
  /** Asks the desktop editor to run a key command the remote cannot (broadcast as the `keyCommand` event). */
  runRemoteKeyCommand(commandId: string): Promise<void>;
  /** Listen-in's state; the stream itself is the WebSocket at `listenInUrl` (`@anode/api-client`). */
  listenInState(): Promise<ListenInState>;
  // ---- Phase 11 (docs/12-phase-11.md)
  /** The chord track (as `Project.chordTrack`). */
  chordTrack(): Promise<ChordTrack>;
  /** Chord ID over MIDI and audio regions: a `chordId` job writing the chord track and the regions' keys. */
  detectChords(request: ChordIdRequest): Promise<Job>;
  /** The Session Players' style library: its version and styles with their presets. */
  sessionPlayerCatalog(): Promise<SessionPlayerCatalog>;
  /** A Session Player region's generated notes (relative to its start), read-only in the piano roll. */
  sessionPlayerNotes(regionId: string): Promise<SessionPlayerNotes>;
  /** Regenerate: a new seed for the region (`regenerateSessionPlayer`, one undo step). */
  regenerateSessionPlayer(regionId: string, request: RegenerateRequest): Promise<CommandResult>;
  /** Convert to MIDI or to a pattern: a `sessionPlayerConvert` job. */
  convertSessionPlayer(regionId: string, request: SessionPlayerConvertRequest): Promise<Job>;
  /** The master's Mastering Assistant: insert, settings and analysis; 404 without one. */
  mastering(): Promise<MasteringState>;
  /** Replaces the assistant's settings (`setMastering`, one undo step). */
  setMastering(settings: MasteringSettings): Promise<CommandResult>;
  /** Analyse: a `masteringAnalysis` job over an offline render of the master before the assistant. */
  analyseMastering(request: MasteringAnalysisRequest): Promise<Job>;
  /** The Environment (as `Project.environment`). */
  environment(): Promise<MidiEnvironment>;
  /** Live MIDI into an on-screen Environment object (a keyboard plays it, a fader moves). */
  environmentInput(objectId: string, request: EnvironmentInputRequest): Promise<void>;
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

  async recoverableProjects(): Promise<RecoverableProject[]> {
    return unwrap(await this.client.GET('/api/v1/projects/recoverable'));
  }

  async recoverProject(path: string): Promise<Project> {
    return unwrap(await this.client.POST('/api/v1/projects/recover', { body: { path } }));
  }

  async discardRecovery(path: string): Promise<void> {
    unwrap(await this.client.POST('/api/v1/projects/recoverable/discard', { body: { path } }));
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

  async saveProjectAs(path: string): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/save-as', { body: { path } }));
  }

  async saveProjectCopy(path: string): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/save-copy', { body: { path } }));
  }

  async changeSampleRate(sampleRate: number, mode: SampleRateChangeMode): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/sample-rate', { body: { sampleRate, mode } }));
  }

  async projectContents(path: string): Promise<ProjectContents> {
    return unwrap(await this.client.POST('/api/v1/projects/contents', { body: { path } }));
  }

  async importProjectTracks(request: ImportProjectTracksRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/import-tracks', { body: request }));
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

  async offline(): Promise<OfflineReport> {
    return unwrap(await this.client.GET('/api/v1/project/offline'));
  }

  async searchOffline(request: OfflineSearchRequest): Promise<OfflineSearch> {
    return unwrap(await this.client.POST('/api/v1/project/offline/search', { body: request }));
  }

  async relinkFiles(request: RelinkRequest): Promise<CommandResult> {
    return unwrap(await this.client.POST('/api/v1/project/offline/relink', { body: request }));
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

  async controllerAssignments(): Promise<ControllerAssignments> {
    return unwrap(await this.client.GET('/api/v1/control/assignments'));
  }

  async setControllerAssignments(assignments: ControllerAssignments): Promise<ControllerAssignments> {
    return unwrap(await this.client.PUT('/api/v1/control/assignments', { body: assignments }));
  }

  async learn(): Promise<LearnState> {
    return unwrap(await this.client.GET('/api/v1/control/learn'));
  }

  async startLearn(request: LearnRequest): Promise<LearnState> {
    return unwrap(await this.client.POST('/api/v1/control/learn/start', { body: request }));
  }

  async stopLearn(): Promise<LearnState> {
    return unwrap(await this.client.POST('/api/v1/control/learn/stop'));
  }

  async surfaces(): Promise<ControlSurface[]> {
    return unwrap(await this.client.GET('/api/v1/control/surfaces'));
  }

  async surfaceStatus(): Promise<SurfaceStatus[]> {
    return unwrap(await this.client.GET('/api/v1/control/surfaces/status'));
  }

  async addSurface(request: AddSurfaceRequest): Promise<ControlSurface> {
    return unwrap(await this.client.POST('/api/v1/control/surfaces', { body: request }));
  }

  async removeSurface(id: string): Promise<void> {
    unwrap(await this.client.DELETE('/api/v1/control/surfaces/{id}', { params: { path: { id } } }));
  }

  async reorderSurfaces(ids: string[]): Promise<ControlSurface[]> {
    return unwrap(await this.client.PUT('/api/v1/control/surfaces/order', { body: { ids } }));
  }

  async osc(): Promise<OscStatus> {
    return unwrap(await this.client.GET('/api/v1/control/osc'));
  }

  async setOsc(settings: OscSettings): Promise<OscStatus> {
    return unwrap(await this.client.PUT('/api/v1/control/osc', { body: settings }));
  }

  async setSelection(trackId: string | null): Promise<void> {
    unwrap(await this.client.PUT('/api/v1/project/selection', { body: { trackId } }));
  }

  async syncState(): Promise<SyncState> {
    return unwrap(await this.client.GET('/api/v1/project/sync'));
  }

  async splitStems(regionId: string, request: StemSplitRequest): Promise<Job> {
    return unwrap(
      await this.client.POST('/api/v1/project/regions/{regionId}/split-stems', {
        params: { path: { regionId } },
        body: request
      })
    );
  }

  async score(query: ScoreQuery): Promise<ScoreDocument> {
    const params: { set?: string; part?: string; view?: string } = {};
    if (query.set) {
      params.set = query.set;
    }
    if (query.part) {
      params.part = query.part;
    }
    if (query.view) {
      params.view = query.view;
    }
    return unwrap(await this.client.GET('/api/v1/project/score', { params: { query: params } }));
  }

  async staffStyles(): Promise<StaffStyle[]> {
    return unwrap(await this.client.GET('/api/v1/library/staff-styles'));
  }

  async exportMusicXml(request: MusicXmlExportRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/export/musicxml', { body: request }));
  }

  async exportAaf(request: AafExportRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/export/aaf', { body: request }));
  }

  async exportMidi(request: MidiExportRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/export/midi', { body: request }));
  }

  async importMusicXml(request: MusicXmlImportRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/import/musicxml', { body: request }));
  }

  async uploadMusicXml(file: File): Promise<Job> {
    return unwrap(
      await this.client.POST('/api/v1/project/import/musicxml/upload', {
        // The generated type says string (IFormFile); the body is multipart with the File itself.
        body: { file: file as unknown as string },
        bodySerializer: body => upload(body.file, file.name)
      })
    );
  }

  async importAaf(request: AafImportRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/import/aaf', { body: request }));
  }

  async uploadAaf(file: File): Promise<Job> {
    return unwrap(
      await this.client.POST('/api/v1/project/import/aaf/upload', {
        body: { file: file as unknown as string },
        bodySerializer: body => upload(body.file, file.name)
      })
    );
  }

  async importMidi(request: MidiImportRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/import/midi', { body: request }));
  }

  async uploadMidi(file: File, options: MidiImportOptions = {}): Promise<Job> {
    return unwrap(
      await this.client.POST('/api/v1/project/import/midi/upload', {
        params: { query: options },
        body: { file: file as unknown as string },
        bodySerializer: body => upload(body.file, file.name)
      })
    );
  }

  async importMovie(request: MovieImportRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/movie', { body: request }));
  }

  async movie(): Promise<Movie> {
    return unwrap(await this.client.GET('/api/v1/project/movie'));
  }

  async removeMovie(): Promise<CommandResult> {
    return unwrap(await this.client.DELETE('/api/v1/project/movie'));
  }

  async renderMovieThumbnails(request: ThumbnailsRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/movie/thumbnails', { body: request }));
  }

  async movieThumbnails(height?: number): Promise<MovieThumbnails> {
    return unwrap(
      await this.client.GET('/api/v1/project/movie/thumbnails', {
        params: { query: height === undefined ? {} : { height } }
      })
    );
  }

  async detectMovieScenes(request: SceneDetectRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/movie/scenes', { body: request }));
  }

  async importMovieAudio(request: MovieAudioImportRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/movie/audio/import', { body: request }));
  }

  async exportMovieAudio(request: MovieAudioExportRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/movie/audio/export', { body: request }));
  }

  async exportAdm(request: AdmExportRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/export/adm', { body: request }));
  }

  async importAdm(request: AdmImportRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/import/adm', { body: request }));
  }

  async monitoring(): Promise<MonitoringSettings> {
    return unwrap(await this.client.GET('/api/v1/settings/monitoring'));
  }

  async setMonitoring(settings: MonitoringSettings): Promise<MonitoringSettings> {
    return unwrap(await this.client.PUT('/api/v1/settings/monitoring', { body: settings }));
  }

  async discovery(): Promise<DiscoveryInfo> {
    return unwrap(await this.client.GET('/api/v1/system/discovery'));
  }

  async remotePads(): Promise<RemotePads> {
    return unwrap(await this.client.GET('/api/v1/remote/pads'));
  }

  async setRemotePads(pads: RemotePads): Promise<RemotePads> {
    return unwrap(await this.client.PUT('/api/v1/remote/pads', { body: pads }));
  }

  async runRemoteKeyCommand(commandId: string): Promise<void> {
    unwrap(await this.client.POST('/api/v1/remote/pads/run', { body: { commandId } }));
  }

  async listenInState(): Promise<ListenInState> {
    return unwrap(await this.client.GET('/api/v1/remote/listen/state'));
  }

  async chordTrack(): Promise<ChordTrack> {
    return unwrap(await this.client.GET('/api/v1/project/chords'));
  }

  async detectChords(request: ChordIdRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/chords/detect', { body: request }));
  }

  async sessionPlayerCatalog(): Promise<SessionPlayerCatalog> {
    return unwrap(await this.client.GET('/api/v1/library/session-players'));
  }

  async sessionPlayerNotes(regionId: string): Promise<SessionPlayerNotes> {
    return unwrap(
      await this.client.GET('/api/v1/project/regions/{regionId}/session-player', { params: { path: { regionId } } })
    );
  }

  async regenerateSessionPlayer(regionId: string, request: RegenerateRequest): Promise<CommandResult> {
    return unwrap(
      await this.client.POST('/api/v1/project/regions/{regionId}/session-player/regenerate', {
        params: { path: { regionId } },
        body: request
      })
    );
  }

  async convertSessionPlayer(regionId: string, request: SessionPlayerConvertRequest): Promise<Job> {
    return unwrap(
      await this.client.POST('/api/v1/project/regions/{regionId}/session-player/convert', {
        params: { path: { regionId } },
        body: request
      })
    );
  }

  async mastering(): Promise<MasteringState> {
    return unwrap(await this.client.GET('/api/v1/project/mastering'));
  }

  async setMastering(settings: MasteringSettings): Promise<CommandResult> {
    return unwrap(await this.client.PUT('/api/v1/project/mastering', { body: settings }));
  }

  async analyseMastering(request: MasteringAnalysisRequest): Promise<Job> {
    return unwrap(await this.client.POST('/api/v1/project/mastering/analyse', { body: request }));
  }

  async environment(): Promise<MidiEnvironment> {
    return unwrap(await this.client.GET('/api/v1/project/environment'));
  }

  async environmentInput(objectId: string, request: EnvironmentInputRequest): Promise<void> {
    unwrap(
      await this.client.POST('/api/v1/project/environment/objects/{objectId}/input', {
        params: { path: { objectId } },
        body: request
      })
    );
  }
}

/** A multipart body with one file (the generated types say string for IFormFile). */
function upload(file: string, name: string): FormData {
  const form = new FormData();
  form.append('file', file as unknown as Blob, name);
  return form;
}
