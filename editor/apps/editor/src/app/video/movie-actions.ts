import { Injectable, inject } from '@angular/core';
import type { Job } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { Notify } from '../core/notify';
import { JobsStore } from '../state/jobs.store';
import { ProjectStore } from '../state/project.store';

/**
 * The Movie menu's commands without a dialog (docs/11-phase-10.md): Remove Movie (one undo step) and Import Audio from
 * Movie (a `movieAudioImport` job; the track comes when it is done). The app loads this module on first use, so the
 * video code stays out of the initial bundle.
 */
@Injectable({ providedIn: 'root' })
export class MovieActions {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);
  private readonly project = inject(ProjectStore);
  private readonly jobs = inject(JobsStore);

  /** Movie ▸ Remove Movie: the project keeps the file (a copied one stays in Movies/). */
  async removeMovie(): Promise<void> {
    if (!this.project.movie()) {
      this.notify.info('The project has no movie.');
      return;
    }
    await this.notify.run(() => this.api.removeMovie(), 'Could not remove the movie');
  }

  /** Movie ▸ Import Audio from Movie: the movie's audio as a new track at its first frame. */
  async importAudio(trackName?: string): Promise<Job | null> {
    const movie = this.project.movie();
    if (!movie) {
      this.notify.info('The project has no movie.');
      return null;
    }
    if (!movie.audio) {
      this.notify.info(`${movie.name} has no audio.`);
      return null;
    }
    return this.jobs.interchange(
      () => this.api.importMovieAudio({ trackName: trackName ?? null }),
      'Could not import the movie audio'
    );
  }
}
