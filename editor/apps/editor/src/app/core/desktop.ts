import type { Connection } from '@anode/api-client';

/** An audio interface as the engine host reports it (`anode-host --devices --json`). */
export interface AudioDevice {
  uid: string;
  name: string;
  inputChannels: number;
  outputChannels: number;
  sampleRate: number;
}

export interface AudioSettings {
  /** Output device uid, '' for the system's default output, or null to run without audio (the null backend). */
  device: string | null;
  /** Input device uid, '' for no inputs, or null for the output device's own (or the system's default input). */
  inputDevice: string | null;
  sampleRate: number;
  bufferFrames: number;
}

/**
 * How a switch of audio device ended. A failed one is not saved; `restored` says the engine runs on the previous
 * settings again, false only when those would not start either.
 */
export type AudioApplied = { applied: true } | { applied: false; error: string; restored: boolean };

/** Which Server the desktop app talks to: its own sidecars, or a remote one. */
export type ServerTarget = { mode: 'local' } | { mode: 'remote'; baseUrl: string; token: string };

/** Control from other devices on the network: the local Server on every interface, with the web editor and a token. */
export interface NetworkStatus {
  enabled: boolean;
  port: number;
  token: string;
  /** Addresses to open on another device, best first; empty when off. */
  urls: string[];
  /** The first URL with the token as a QR code: SVG path data, `size` modules square. */
  qr: { path: string; size: number; url: string } | null;
  /** Why the Server is not on the network although the setting is on (its port is taken). */
  error: string | null;
}

export interface SidecarStatus {
  name: 'host' | 'server';
  state: 'starting' | 'running' | 'restarting' | 'stopped' | 'failed';
  detail: string | null;
  /** The running process's id. */
  pid?: number | null;
}

/** What the app menu asks the editor to do (apps/desktop/src/main.ts sends it on `anode:command`). */
export type AppCommand =
  | 'newProject'
  | 'openProject'
  | 'saveProject'
  | 'closeProject'
  | 'importAudio'
  | 'importMidi'
  | 'saveAlternative'
  | 'missingFiles'
  | 'keyCommands'
  | 'bounce'
  | 'ioLabels'
  | 'controlSurfaces'
  | 'controllerAssignments'
  | 'syncSettings'
  | 'stemSplitter'
  | 'importMusicXml'
  | 'importAaf'
  | 'importMidiOptions'
  | 'exportMusicXml'
  | 'exportAaf'
  | 'exportMidi'
  | 'printScore'
  // Phase 10 (docs/11-phase-10.md): ADM BWF, the project's surround format, the Movie menu.
  | 'importAdm'
  | 'exportAdm'
  | 'surroundSettings'
  | 'movieSettings'
  | 'openMovie'
  | 'removeMovie'
  | 'importMovieAudio'
  | 'exportMovieAudio'
  | 'detectSceneCuts'
  | 'movieWindow'
  // Phase 11 (docs/12-phase-11.md): the master's Mastering Assistant.
  | 'masteringAssistant'
  // Phase 11: the MIDI Environment window.
  | 'environment';

/** Native file dialogs (apps/desktop/src/main.ts, `anode:dialog:*`); each resolves empty when cancelled. */
export interface DesktopDialogs {
  /** An Anode project folder to open. */
  openProject(): Promise<string | null>;
  /** A folder to create a new project in. */
  chooseFolder(): Promise<string | null>;
  /** WAV files to import. */
  importAudio(): Promise<string[]>;
  /** A Standard MIDI File to import. */
  importMidi?(): Promise<string | null>;
  /** A file for an instrument to load, of one of its `fileTypes` (`.sfz`, `.wav`). */
  instrumentFile?(fileTypes: readonly string[]): Promise<string | null>;
  /** A content pack (`.anodepack`) to install in the Sound Library. */
  contentPack?(): Promise<string | null>;
  /**
   * A MusicXML (`.musicxml`, `.mxl`, `.xml`) or AAF file to import (Phase 9); Phase 10: a movie (`.mov`, `.mp4`,
   * `.m4v`) or an ADM BWF (`.wav`).
   */
  importFile?(kind: 'musicxml' | 'aaf' | 'movie' | 'adm'): Promise<string | null>;
}

/** What the desktop app prints to PDF: the page size and the file name it suggests. */
export interface PrintToPdfRequest {
  title: string;
  pageWidthMm: number;
  pageHeightMm: number;
}

/**
 * What the Electron preload exposes as `window.anode` (apps/desktop/src/preload.ts). Absent when the editor runs in a
 * plain browser, where audio setup is not available.
 */
export interface AnodeDesktop {
  /** The path on disk of a file dropped on the window ('' for one with none, such as a dragged browser image). */
  pathForFile?(file: File): string;
  dialogs?: DesktopDialogs;
  /** Shows a file the engine wrote (a bounce) in Finder or the file manager; false when there is no such file here. */
  reveal?(path: string): Promise<boolean>;
  /**
   * Prints the window's print view (Phase 9's score pages, laid out for print) to a PDF the person names in a save
   * dialog; resolves its path, or null when cancelled.
   */
  printToPdf?(request: PrintToPdfRequest): Promise<string | null>;
  /** Called when an app menu item (File ▸ Save…) asks the editor to act. */
  onCommand?(listener: (command: AppCommand) => void): void;
  /** Node's `process.platform` of the desktop app ('darwin' on macOS). */
  platform: string;
  /** Called when the window enters or leaves full screen. */
  onFullScreen(listener: (fullScreen: boolean) => void): void;
  /** Called when the app menu's Settings… (⌘,) is chosen. */
  onOpenSettings?(listener: () => void): void;
  /** Asked before the app quits while it runs its own engine, whose Server holds the open project: false keeps it open. */
  onQuit?(mayQuit: () => boolean | Promise<boolean>): void;
  /** The Server to use; in local mode it resolves once the sidecars answer. */
  connection(): Promise<Connection>;
  target(): Promise<ServerTarget>;
  /** Switches Server and reloads the window. */
  setTarget(target: ServerTarget): Promise<void>;
  sidecars(): Promise<SidecarStatus[]>;
  /** Called whenever one of the local engine's processes changes state (restarting after a crash, say). */
  onSidecars?(listener: (statuses: SidecarStatus[]) => void): void;
  /** The last lines (up to 200) each local engine process printed. */
  logs?(): Promise<Record<'host' | 'server', string[]>>;
  network?: {
    status(): Promise<NetworkStatus>;
    /** Saves the setting, restarts the local Server with it and reloads the window; `renewToken` signs others out. */
    apply(network: { enabled: boolean; port: number; renewToken?: boolean }): Promise<NetworkStatus>;
  };
  audio: {
    devices(): Promise<{ backend: string; devices: AudioDevice[] }>;
    settings(): Promise<AudioSettings>;
    /** Restarts the engine host with these settings, and resolves once it runs on them or is back on the old ones. */
    apply(settings: AudioSettings): Promise<AudioApplied>;
  };
}

export type AudioBridge = AnodeDesktop['audio'];

declare global {
  interface Window {
    anode?: AnodeDesktop;
  }
}
