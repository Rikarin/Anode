import { Injectable, InjectionToken, inject } from '@angular/core';
import {
  ApiError,
  type AudioFile,
  type Bookmarks,
  type BounceInPlaceRequest,
  type BounceRequest,
  type CommandBatch,
  type CommandResult,
  type DirectoryListing,
  type FileAnalysis,
  type IoLabels,
  type Job,
  type KeyCommandSet,
  type LiveMidiEvent,
  type MidiDevices,
  type PatchInfo,
  type PluginEditorRequest,
  type PluginInfo,
  type ProcessRequest,
  type Project,
  type ProjectAlternative,
  type ProjectBackup,
  type ProjectSummary,
  type SavePatchRequest,
  type Screenset,
  type SystemInfo,
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
}
