import { Injectable, computed, inject } from '@angular/core';
import { type LaneRow, laneRows } from './lanes';
import { ProjectStore } from './project.store';
import { TracksViewStore } from './tracks-view.store';

/**
 * The Tracks area's rows (tracks, minus members of collapsed stacks, plus the takes of open take folders), shared by
 * the headers and the timeline so a lane and its header always agree.
 */
@Injectable({ providedIn: 'root' })
export class LanesStore {
  private readonly project = inject(ProjectStore);
  private readonly view = inject(TracksViewStore);

  readonly rows = computed<readonly LaneRow[]>(() =>
    laneRows(this.project.trackList(), this.project.takeFolderList(), this.view.expandedFolders())
  );

  /** The track on a lane (a take row's is its folder's track). */
  trackAt(lane: number): string | undefined {
    return this.rows()[lane]?.track.id;
  }

  /** The lane of a track's own row, or -1 when hidden. */
  laneOf(trackId: string): number {
    return this.rows().findIndex(row => row.kind === 'track' && row.id === trackId);
  }

  readonly count = computed(() => this.rows().length);
}
