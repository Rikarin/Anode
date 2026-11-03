import { computed, inject, Injectable, signal } from '@angular/core';
import type { PadSet, RemotePads } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { Notify } from '../core/notify';
import { addPad, movePad, newSetId, removePad, updatePad, withoutSet, withSet } from './remote-model';

/**
 * The remote's key-command pad sets (`/remote/pads`, user settings): loaded when the remote opens, the set shown, and
 * edits (add, rename, remove and move pads; add, rename and remove sets), each saved at once. A refused save puts the
 * last saved sets back.
 */
@Injectable({ providedIn: 'root' })
export class RemotePadsStore {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);

  readonly pads = signal<RemotePads>({ sets: [] });
  readonly loaded = signal(false);
  readonly setId = signal<string | null>(null);
  readonly editing = signal(false);
  readonly set = computed<PadSet | null>(() => {
    const sets = this.pads().sets;
    return sets.find(s => s.id === this.setId()) ?? sets[0] ?? null;
  });

  async load(): Promise<void> {
    const pads = await this.notify.run(() => this.api.remotePads(), 'Could not load the pads');
    if (pads) {
      this.pads.set(pads);
      this.loaded.set(true);
    }
  }

  addPad(commandId: string, label: string): Promise<void> {
    const set = this.set();
    return set ? this.save(withSet(this.pads(), addPad(set, commandId, label))) : Promise.resolve();
  }

  updatePad(padId: string, change: { label?: string; commandId?: string; color?: string | null }): Promise<void> {
    const set = this.set();
    return set ? this.save(withSet(this.pads(), updatePad(set, padId, change))) : Promise.resolve();
  }

  removePad(padId: string): Promise<void> {
    const set = this.set();
    return set ? this.save(withSet(this.pads(), removePad(set, padId))) : Promise.resolve();
  }

  movePad(padId: string, by: -1 | 1): Promise<void> {
    const set = this.set();
    return set ? this.save(withSet(this.pads(), movePad(set, padId, by))) : Promise.resolve();
  }

  async addSet(name: string): Promise<void> {
    const id = newSetId(this.pads());
    await this.save(withSet(this.pads(), { id, name: name.trim() || 'Pads', pads: [] }));
    this.setId.set(id);
  }

  renameSet(name: string): Promise<void> {
    const set = this.set();
    return set && name.trim() ? this.save(withSet(this.pads(), { ...set, name: name.trim() })) : Promise.resolve();
  }

  removeSet(): Promise<void> {
    const set = this.set();
    return set ? this.save(withoutSet(this.pads(), set.id)) : Promise.resolve();
  }

  private async save(next: RemotePads): Promise<void> {
    const previous = this.pads();
    this.pads.set(next);
    const saved = await this.notify.run(() => this.api.setRemotePads(next), 'Could not save the pads');
    this.pads.set(saved ?? previous);
  }
}
