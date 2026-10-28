import { Injectable, inject } from '@angular/core';
import type { Track, TrackAlternative } from '@anode/api-client';
import { ProjectStore } from './project.store';

/**
 * Track alternatives (Logic's): a track's live regions are its active alternative; the others wait as snapshots. New
 * (empty), duplicate, rename, delete and select are each one undo step.
 */
@Injectable({ providedIn: 'root' })
export class TrackAlternativesStore {
  private readonly project = inject(ProjectStore);

  /** A track's alternatives in order, and which one plays (none until it has any). */
  of(track: Track): { alternatives: readonly TrackAlternative[]; activeId: string | null } {
    return { alternatives: track.alternatives, activeId: track.alternativeId ?? null };
  }

  /** The active alternative's name, or null when the track has none. */
  activeName(track: Track): string | null {
    return track.alternatives.find(a => a.id === track.alternativeId)?.name ?? null;
  }

  create(trackId: string, duplicate = false, name: string | null = null): Promise<unknown> {
    return this.project.execute(
      [{ type: 'newTrackAlternative', trackId, duplicate, name }],
      duplicate ? 'Duplicate Track Alternative' : 'New Track Alternative'
    );
  }

  select(trackId: string, alternativeId: string): Promise<unknown> {
    return this.project.execute(
      [{ type: 'selectTrackAlternative', trackId, alternativeId }],
      'Select Track Alternative'
    );
  }

  rename(trackId: string, alternativeId: string, name: string): Promise<unknown> {
    return this.project.execute(
      [{ type: 'setTrackAlternative', trackId, alternativeId, name }],
      'Rename Track Alternative'
    );
  }

  delete(trackId: string, alternativeId: string): Promise<unknown> {
    return this.project.execute(
      [{ type: 'deleteTrackAlternative', trackId, alternativeId }],
      'Delete Track Alternative'
    );
  }
}
