import { Injectable, computed, inject, signal } from '@angular/core';
import type { MidiDevices, PluginInfo } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';

const NoDevices: MidiDevices = { inputs: [], outputs: [] };

/**
 * What the engine offers: its plugin catalogue (`/plugins`, built-in first) and the machine's MIDI ports
 * (`/midi/devices`). Fetched on every (re)connection and again when the Server says either changed (`pluginsChanged`
 * after a rescan, `midiDevicesChanged` on hotplug).
 */
@Injectable({ providedIn: 'root' })
export class PluginsStore {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);

  readonly plugins = signal<readonly PluginInfo[]>([]);
  readonly devices = signal<MidiDevices>(NoDevices);
  readonly scanning = signal(false);
  readonly instruments = computed(() => this.plugins().filter(plugin => plugin.isInstrument));
  private readonly byId = computed(() => new Map(this.plugins().map(plugin => [plugin.id, plugin])));

  constructor() {
    const events = inject(EventStream);
    events.connected$.subscribe(() => {
      void this.refetchPlugins();
      void this.refetchDevices();
    });
    events.events$.subscribe(message => {
      if (message.type === 'pluginsChanged') {
        this.scanning.set(false);
        void this.refetchPlugins();
      } else if (message.type === 'midiDevicesChanged') {
        void this.refetchDevices();
      }
    });
    if (events.status() === 'open') {
      // Created after the connection came up: fetch now rather than wait for the next one.
      void this.refetchPlugins();
      void this.refetchDevices();
    }
  }

  plugin(id: string | null | undefined): PluginInfo | undefined {
    return id ? this.byId().get(id) : undefined;
  }

  async refetchPlugins(): Promise<void> {
    try {
      this.plugins.set(await this.api.plugins());
    } catch (error) {
      this.notify.failure(error, 'Could not list the plugins');
    }
  }

  async refetchDevices(): Promise<void> {
    try {
      this.devices.set(await this.api.midiDevices());
    } catch (error) {
      this.notify.failure(error, 'Could not list the MIDI devices');
    }
  }

  /** Rescans the CLAP folders; the catalogue is refetched when the Server says the scan is in. */
  async rescan(): Promise<void> {
    this.scanning.set(true);
    try {
      await this.api.rescanPlugins();
    } catch (error) {
      this.scanning.set(false);
      this.notify.failure(error, 'Could not rescan the plugins');
    }
  }
}
