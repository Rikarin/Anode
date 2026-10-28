import { Injectable, inject, signal } from '@angular/core';
import type { IoLabel, IoLabels } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';

export const NoLabels: IoLabels = { inputs: [], outputs: [], buses: [] };

/** Labels with a name changed (an empty name removes it), in number order. */
export function withLabel(labels: readonly IoLabel[], number: number, name: string): IoLabel[] {
  const trimmed = name.trim();
  const rest = labels.filter(l => l.number !== number);
  return (trimmed ? [...rest, { number, name: trimmed }] : rest).sort((a, b) => a.number - b.number);
}

/**
 * The user's names for inputs, output pairs and buses (Logic's I/O Labels, `/settings/io-labels`): the pickers and
 * strips show them in place of "Bus 3" and "Output 1-2". Fetched on every (re)connection; saved as a whole.
 */
@Injectable({ providedIn: 'root' })
export class IoLabelsStore {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);
  readonly labels = signal<IoLabels>(NoLabels);

  constructor() {
    const events = inject(EventStream);
    events.connected$.subscribe(() => void this.refetch());
    if (events.status() === 'open') {
      void this.refetch();
    }
  }

  async refetch(): Promise<void> {
    try {
      this.labels.set(await this.api.ioLabels());
    } catch {
      // An older Server has no I/O labels: the default names stand.
      this.labels.set(NoLabels);
    }
  }

  /** Saves the labels; resolves with whether the Server took them. */
  async save(labels: IoLabels): Promise<boolean> {
    const saved = await this.notify.run(() => this.api.saveIoLabels(labels), 'Could not save the I/O labels');
    if (saved) {
      this.labels.set(saved);
    }
    return saved !== null;
  }
}
