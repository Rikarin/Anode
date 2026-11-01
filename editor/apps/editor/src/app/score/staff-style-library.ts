import { Injectable, computed, inject, signal } from '@angular/core';
import { type StaffStyle, StaffStyleIds } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { ProjectStore } from '../state/project.store';

/** The factory staff styles (`GET /library/staff-styles`, fetched once) and the project's, as one list. */
@Injectable({ providedIn: 'root' })
export class StaffStyleLibrary {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private loading: Promise<void> | null = null;

  readonly factory = signal<readonly StaffStyle[]>([]);
  /** The project's styles, then the factory's. */
  readonly all = computed(() => [...this.project.score().staffStyles, ...this.factory()]);

  /** Fetches the factory styles the first time. */
  load(): Promise<void> {
    this.loading ??= this.api.staffStyles().then(
      styles => this.factory.set(styles),
      () => {
        this.loading = null;
      }
    );
    return this.loading;
  }

  /** The style a track uses: its own, else the default (piano); null until the library has loaded. */
  styleOf(staffStyleId: string | null | undefined): StaffStyle | null {
    const all = this.all();
    return (
      all.find(s => s.id === (staffStyleId ?? StaffStyleIds.default)) ??
      all.find(s => s.id === StaffStyleIds.default) ??
      null
    );
  }
}
