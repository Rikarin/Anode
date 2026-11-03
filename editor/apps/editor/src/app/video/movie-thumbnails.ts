import { DestroyRef, Injectable, effect, inject, signal, untracked } from '@angular/core';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { ProjectStore } from '../state/project.store';
import { MovieMedia } from './media-urls';

/** The height the movie track asks thumbnails at (the Server caches per height; one height keeps one cache). */
export const ThumbnailHeight = 64;
/** How long requests gather before one `renderMovieThumbnails` job asks for them all. */
const BatchMs = 120;
/** The most frames one request asks for (the Server takes 2000). */
const MaxBatch = 500;

/**
 * The movie's thumbnails (the movie track's): which frames the Server has cached at {@link ThumbnailHeight}
 * (`movieThumbnails`), and requests for missing ones gathered into `thumbnails` jobs (`renderMovieThumbnails`). When a
 * job finishes the list is read again, and the track shows the new frames. A new movie starts a new cache.
 */
@Injectable({ providedIn: 'root' })
export class MovieThumbnailCache {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly media = inject(MovieMedia);

  /** The frames cached at {@link ThumbnailHeight}. */
  readonly cached = signal<ReadonlySet<number>>(new Set());
  /** The thumbnails' width (the movie's aspect at the height). */
  readonly width = signal(ThumbnailHeight);

  private readonly pending = new Set<number>();
  private readonly queued = new Set<number>();
  private readonly jobs = new Set<string>();
  private timer: ReturnType<typeof setTimeout> | null = null;
  /** The movie the cache is for (its path and frame count). */
  private movieKey: string | null = null;

  constructor() {
    effect(() => {
      const movie = this.project.movie();
      const key = movie ? `${movie.path}|${movie.frames}` : null;
      untracked(() => {
        if (key !== this.movieKey) {
          this.movieKey = key;
          this.pending.clear();
          this.queued.clear();
          this.cached.set(new Set());
          if (movie) {
            this.width.set(Math.max(2, Math.round((ThumbnailHeight * movie.width) / movie.height)));
            void this.refresh();
          }
        }
      });
    });
    const subscription = inject(EventStream).events$.subscribe(message => {
      if (message.type === 'job' && this.jobs.has(message.job.id) && message.job.state !== 'queued') {
        if (message.job.state !== 'running') {
          this.jobs.delete(message.job.id);
          void this.refresh();
        }
      }
    });
    inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
  }

  /** A cached frame's image URL, else null (the track shows a placeholder until it comes). */
  url(frame: number): string | null {
    return this.cached().has(frame) ? this.media.frameUrl(frame, ThumbnailHeight) : null;
  }

  /** Asks for the frames that are neither cached nor asked for yet; they are sent together shortly. */
  request(frames: readonly number[]): void {
    const cached = this.cached();
    const movie = this.project.movie();
    if (!movie) {
      return;
    }
    for (const frame of frames) {
      if (frame >= 0 && frame < movie.frames && !cached.has(frame) && !this.pending.has(frame)) {
        this.queued.add(frame);
      }
    }
    if (this.queued.size > 0 && !this.timer) {
      this.timer = setTimeout(() => void this.flush(), BatchMs);
    }
  }

  /** Sends what is queued now (tests call it rather than wait). */
  async flush(): Promise<void> {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    while (this.queued.size > 0) {
      const frames = [...this.queued].slice(0, MaxBatch).sort((a, b) => a - b);
      frames.forEach(f => {
        this.queued.delete(f);
        this.pending.add(f);
      });
      try {
        const job = await this.api.renderMovieThumbnails({ frames, height: ThumbnailHeight });
        this.jobs.add(job.id);
      } catch {
        frames.forEach(f => this.pending.delete(f)); // asked again when shown again
      }
    }
  }

  /** Reads which frames the Server has cached. */
  async refresh(): Promise<void> {
    if (!this.project.movie()) {
      return;
    }
    try {
      const list = await this.api.movieThumbnails(ThumbnailHeight);
      list.frames.forEach(f => this.pending.delete(f));
      this.width.set(list.width);
      this.cached.set(new Set(list.frames));
    } catch {
      // no movie any more: the next movie starts again
    }
  }
}
