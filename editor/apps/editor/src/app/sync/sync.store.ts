import { computed, effect, inject, Injectable, signal, untracked } from '@angular/core';
import type {
  EditCommand,
  MidiSyncDestination,
  MtcFrameRate,
  SyncSettings,
  SyncSource,
  SyncState
} from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { ProjectStore } from '../state/project.store';

/** What `setSyncSettings` takes besides its type: any field left out stays as it is. */
export type SyncChange = Omit<Extract<EditCommand, { type: 'setSyncSettings' }>, 'type'>;

export const SyncSources: readonly { value: SyncSource; label: string; short: string }[] = [
  { value: 'internal', label: 'Internal', short: 'Int' },
  { value: 'midiClock', label: 'MIDI Clock', short: 'Clock' },
  { value: 'mtc', label: 'MIDI Time Code', short: 'MTC' },
  { value: 'link', label: 'Ableton Link', short: 'Link' }
];

export const FrameRates: readonly { value: MtcFrameRate; label: string; fps: number }[] = [
  { value: 'fps24', label: '24 fps', fps: 24 },
  { value: 'fps25', label: '25 fps', fps: 25 },
  { value: 'fps2997Drop', label: '29.97 fps drop', fps: 30 },
  { value: 'fps30', label: '30 fps', fps: 30 }
];

/** The start offset as timecode, `01:00:00:00` (whole seconds; frames are 00). */
export function formatOffset(seconds: number): string {
  const s = Math.max(0, Math.round(seconds));
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(Math.floor(s / 3600))}:${pad(Math.floor(s / 60) % 60)}:${pad(s % 60)}:00`;
}

/** Typed timecode (`1:00:00`, `01:00:00:00`, `3600`) as seconds of the day, or null for what does not read as one. */
export function parseOffset(text: string): number | null {
  const parts = text.trim().split(':');
  if (parts.length > 4 || parts.some(p => !/^\d+$/.test(p))) {
    return null;
  }
  const numbers = parts.map(Number);
  let seconds = numbers[0] ?? 0;
  if (numbers.length > 1) {
    // Hours, minutes, seconds (and frames, which the offset does not keep).
    const [h = 0, m = 0, s = 0] = numbers;
    if (m > 59 || s > 59) {
      return null;
    }
    seconds = h * 3600 + m * 60 + s;
  }
  return seconds <= 86_399 ? seconds : null;
}

/**
 * The project's synchronisation (docs/09-phase-8.md, Sync): the settings are the project's (`settings.sync`), changed
 * with `setSyncSettings` so they are undoable and saved with it; the live state (source, locked, external tempo, Link
 * peers) comes from `GET /project/sync` when a project opens or the connection comes back, then from `sync` events.
 */
@Injectable({ providedIn: 'root' })
export class SyncStore {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);

  readonly settings = computed<SyncSettings>(() => this.project.settings().sync);
  readonly state = signal<SyncState | null>(null);
  readonly source = computed(() => this.settings().source);
  readonly linkOn = computed(() => this.source() === 'link');
  /** Following an external clock or timecode. */
  readonly external = computed(() => this.source() === 'midiClock' || this.source() === 'mtc');
  readonly sourceInfo = computed(() => SyncSources.find(s => s.value === this.source()) ?? SyncSources[0]);
  /** Locked to the source (internal always is; MIDI Clock and MTC once they arrive; Link while it runs). */
  readonly locked = computed(() => {
    const state = this.state();
    return state?.source === this.source() ? state.locked : this.source() === 'internal';
  });
  readonly externalTempo = computed(() => (this.external() ? (this.state()?.externalTempo ?? null) : null));
  readonly linkPeers = computed(() => (this.linkOn() ? (this.state()?.linkPeers ?? 0) : 0));

  constructor() {
    const events = inject(EventStream);
    events.events$.subscribe(message => {
      if (message.type === 'sync') {
        this.state.set(message.state);
      }
    });
    events.connected$.subscribe(() => void this.refresh());
    effect(() => {
      const id = this.project.info()?.id;
      untracked(() => {
        if (id) {
          void this.refresh();
        } else {
          this.state.set(null);
        }
      });
    });
  }

  async refresh(): Promise<void> {
    if (!this.project.isOpen()) {
      return;
    }
    try {
      this.state.set(await this.api.syncState());
    } catch {
      // No sync state yet (an older Server): the indicator shows the settings alone.
      this.state.set(null);
    }
  }

  /** Changes the sync settings, one undo step. */
  apply(change: SyncChange, label = 'Synchronization'): Promise<unknown> {
    return this.project.execute([{ type: 'setSyncSettings', ...change }], label);
  }

  setSource(source: SyncSource): Promise<unknown> {
    const label = SyncSources.find(s => s.value === source)?.label ?? source;
    return this.apply({ source }, `Sync to ${label}`);
  }

  /** The control bar's Link button: Link on (Link becomes the source), or off (back to internal). */
  toggleLink(): Promise<unknown> {
    return this.linkOn() ? this.apply({ source: 'internal' }, 'Link Off') : this.apply({ source: 'link' }, 'Link On');
  }

  /** Adds a MIDI output that is sent clock and MTC. */
  addDestination(port: string): Promise<unknown> {
    const list = this.settings().destinations;
    if (list.some(d => d.port === port)) {
      return Promise.resolve(null);
    }
    return this.apply({ destinations: [...list, { port, clock: true, mtc: false, delayMs: 0 }] }, 'Add Sync Destination');
  }

  setDestination(port: string, change: Partial<Omit<MidiSyncDestination, 'port'>>): Promise<unknown> {
    const destinations = this.settings().destinations.map(d => (d.port === port ? { ...d, ...change } : d));
    return this.apply({ destinations }, 'Sync Destination');
  }

  removeDestination(port: string): Promise<unknown> {
    const destinations = this.settings().destinations.filter(d => d.port !== port);
    return this.apply({ destinations }, 'Remove Sync Destination');
  }
}
