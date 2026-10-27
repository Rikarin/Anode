import { Injectable, InjectionToken, inject } from '@angular/core';
import {
  ApiError,
  type AudioFile,
  type CommandBatch,
  type CommandResult,
  type Project,
  type ProjectSummary,
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
}
