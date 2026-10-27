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
  /** Device uid, or null to run without audio (the null backend). */
  device: string | null;
  sampleRate: number;
  bufferFrames: number;
}

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

/**
 * What the Electron preload exposes as `window.anode` (apps/desktop/src/preload.ts). Absent when the editor runs in a
 * plain browser, where audio setup is not available.
 */
export interface AnodeDesktop {
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
    /** Restarts the engine host with these settings. */
    apply(settings: AudioSettings): Promise<void>;
  };
}

declare global {
  interface Window {
    anode?: AnodeDesktop;
  }
}
