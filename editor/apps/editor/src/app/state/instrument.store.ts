import { Injectable, inject } from '@angular/core';
import type { MidiInputSettings, MidiOutputSettings } from '@anode/api-client';
import { Coalescer } from '../core/coalescer';
import { PluginsStore } from './plugins.store';
import { ProjectStore } from './project.store';

/**
 * Instrument and MIDI I/O edits for the track inspector. Parameter drags are optimistic and coalesced as fader drags
 * are: each parameter has at most one `setParameter` in flight and shows the value being sent until the delta confirms
 * it; the Server folds a drag's batches into one undo step (docs/03-phase-2.md).
 */
@Injectable({ providedIn: 'root' })
export class InstrumentStore {
  private readonly project = inject(ProjectStore);
  private readonly plugins = inject(PluginsStore);
  private readonly coalescer = new Coalescer<number>((key, value) => {
    const split = key.indexOf('|');
    const trackId = key.slice(0, split);
    const parameterId = key.slice(split + 1);
    const name = this.parameterName(trackId, parameterId);
    return this.project.execute([{ type: 'setParameter', trackId, parameterId, value }], name);
  });

  /** A parameter's value to show: the one being sent, else the confirmed one, else its default. Reactive. */
  value(trackId: string, parameterId: string): number {
    const pending = this.coalescer.pending(key(trackId, parameterId));
    if (pending !== undefined) {
      return pending;
    }

    const instrument = this.project.track(trackId)?.instrument;
    return (
      instrument?.parameters[parameterId] ??
      this.plugins.plugin(instrument?.pluginId)?.parameters.find(p => p.id === parameterId)?.default ??
      0
    );
  }

  /** Sets a parameter during a drag: shown at once, sent at most one request at a time. */
  set(trackId: string, parameterId: string, value: number): Promise<void> {
    return this.coalescer.set(key(trackId, parameterId), value);
  }

  setInstrument(trackId: string, pluginId: string): void {
    const name = this.plugins.plugin(pluginId)?.name ?? pluginId;
    void this.project.execute([{ type: 'setInstrument', trackId, pluginId }], `Instrument ${name}`);
  }

  loadFile(trackId: string, path: string): void {
    const trimmed = path.trim();
    if (trimmed) {
      void this.project.execute([{ type: 'loadInstrumentFile', trackId, path: trimmed }], 'Load Instrument File');
    }
  }

  setMidiInput(trackId: string, input: MidiInputSettings): void {
    void this.project.execute([{ type: 'setMidiIo', trackId, input, output: null }], 'MIDI Input');
  }

  setMidiOutput(trackId: string, output: MidiOutputSettings): void {
    void this.project.execute([{ type: 'setMidiIo', trackId, input: null, output }], 'MIDI Output');
  }

  private parameterName(trackId: string, parameterId: string): string {
    const instrument = this.project.track(trackId)?.instrument;
    return this.plugins.plugin(instrument?.pluginId)?.parameters.find(p => p.id === parameterId)?.name ?? 'Parameter';
  }
}

function key(trackId: string, parameterId: string): string {
  return `${trackId}|${parameterId}`;
}
