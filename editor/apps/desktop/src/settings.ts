import { app } from 'electron';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

export interface AudioSettings {
  /** Device uid, or null to run the engine on the null backend (no interface). */
  device: string | null;
  sampleRate: number;
  bufferFrames: number;
}

export type ServerTarget = { mode: 'local' } | { mode: 'remote'; baseUrl: string; token: string };

/**
 * Control from other devices: the local Server also listens on every network interface at `port`, serves the web
 * editor there and wants `token`, which is kept so a tablet that has it stays signed in. Off by default.
 */
export interface NetworkSettings {
  enabled: boolean;
  port: number;
  /** The access token, made the first time the setting is turned on; empty until then. */
  token: string;
}

export interface Settings {
  audio: AudioSettings;
  target: ServerTarget;
  network: NetworkSettings;
}

const Defaults: Settings = {
  audio: { device: null, sampleRate: 48_000, bufferFrames: 128 },
  target: { mode: 'local' },
  network: { enabled: false, port: 5190, token: '' }
};

const file = () => join(app.getPath('userData'), 'settings.json');

/** The desktop app's own settings, in userData/settings.json. Projects and the library belong to the Server. */
export function loadSettings(): Settings {
  try {
    const stored = JSON.parse(readFileSync(file(), 'utf8')) as Partial<Settings>;
    return {
      audio: { ...Defaults.audio, ...stored.audio },
      target: stored.target ?? Defaults.target,
      network: { ...Defaults.network, ...stored.network }
    };
  } catch {
    return structuredClone(Defaults);
  }
}

export function saveSettings(settings: Settings): void {
  writeFileSync(file(), JSON.stringify(settings, null, 2));
}
