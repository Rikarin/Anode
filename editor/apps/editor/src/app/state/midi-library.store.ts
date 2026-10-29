import { Injectable, inject, signal } from '@angular/core';
import type { ArticulationSet, GrooveTemplate, TransformSet } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { Notify } from '../core/notify';

/**
 * The user library's Phase 7 content (docs/08-phase-7.md): groove templates, MIDI Transform sets (the factory presets
 * first, `factory.…`) and articulation sets. Loaded on demand by the windows that list them; saves and deletes
 * refresh the list.
 */
@Injectable({ providedIn: 'root' })
export class MidiLibraryStore {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);

  readonly grooves = signal<readonly GrooveTemplate[]>([]);
  readonly transformSets = signal<readonly TransformSet[]>([]);
  readonly articulationSets = signal<readonly ArticulationSet[]>([]);

  async loadGrooves(): Promise<void> {
    await this.load(
      () => this.api.libraryGrooves(),
      list => this.grooves.set(list),
      'grooves'
    );
  }

  async loadTransformSets(): Promise<void> {
    await this.load(
      () => this.api.transformSets(),
      list => this.transformSets.set(list),
      'Transform sets'
    );
  }

  async loadArticulationSets(): Promise<void> {
    await this.load(
      () => this.api.libraryArticulationSets(),
      list => this.articulationSets.set(list),
      'articulation sets'
    );
  }

  async saveGroove(groove: GrooveTemplate): Promise<boolean> {
    return this.save(
      () => this.api.saveLibraryGroove(groove),
      () => this.loadGrooves(),
      'Could not save the groove'
    );
  }

  async deleteGroove(id: string): Promise<boolean> {
    return this.save(
      () => this.api.deleteLibraryGroove(id),
      () => this.loadGrooves(),
      'Could not delete the groove'
    );
  }

  async saveTransformSet(set: TransformSet): Promise<boolean> {
    return this.save(
      () => this.api.saveTransformSet(set),
      () => this.loadTransformSets(),
      'Could not save the Transform set'
    );
  }

  async deleteTransformSet(id: string): Promise<boolean> {
    return this.save(
      () => this.api.deleteTransformSet(id),
      () => this.loadTransformSets(),
      'Could not delete the Transform set'
    );
  }

  async saveArticulationSet(set: ArticulationSet): Promise<boolean> {
    return this.save(
      () => this.api.saveLibraryArticulationSet(set),
      () => this.loadArticulationSets(),
      'Could not save the articulation set'
    );
  }

  async deleteArticulationSet(id: string): Promise<boolean> {
    return this.save(
      () => this.api.deleteLibraryArticulationSet(id),
      () => this.loadArticulationSets(),
      'Could not delete the articulation set'
    );
  }

  private async load<T>(fetch: () => Promise<T[]>, set: (list: T[]) => void, what: string): Promise<void> {
    try {
      set(await fetch());
    } catch (error) {
      this.notify.failure(error, `Could not load the library’s ${what}`);
    }
  }

  private async save(send: () => Promise<unknown>, reload: () => Promise<void>, failure: string): Promise<boolean> {
    try {
      await send();
      await reload();
      return true;
    } catch (error) {
      this.notify.failure(error, failure);
      return false;
    }
  }
}
