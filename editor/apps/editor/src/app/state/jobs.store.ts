import { Injectable, computed, inject, signal } from '@angular/core';
import type { BounceRequest, Job } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { ConnectionService } from '../core/connection';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { ProjectStore } from './project.store';

/** Whether a job still runs (queued or running). */
export function isActive(job: Job | undefined | null): boolean {
  return job?.state === 'queued' || job?.state === 'running';
}

/** A file's name from its absolute path. */
export function fileName(path: string): string {
  return path.split(/[\\/]/).pop() ?? path;
}

/**
 * The Server's long-running jobs (bounce and freeze, docs/05-phase-4.md): started here, followed through the `job`
 * events on `/ws` (and a poll when one is started, in case its first events came before the answer). A finished bounce
 * says so with a toast that reveals the file (in the desktop app); a failed job reports its message.
 */
@Injectable({ providedIn: 'root' })
export class JobsStore {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);
  private readonly project = inject(ProjectStore);
  private readonly desktop = inject(ConnectionService).desktop;

  /** Every job this editor has seen, by id. */
  readonly jobs = signal<ReadonlyMap<string, Job>>(new Map());
  /** The freeze job of each track this editor started or saw, by track id. */
  private readonly freezes = signal<ReadonlyMap<string, string>>(new Map());
  /** The bounce the Bounce dialog follows. */
  readonly bounceId = signal<string | null>(null);
  readonly bounceJob = computed(() => {
    const id = this.bounceId();
    return id ? (this.jobs().get(id) ?? null) : null;
  });
  /** Jobs that are not finished. */
  readonly active = computed(() => [...this.jobs().values()].filter(isActive));

  constructor() {
    inject(EventStream).events$.subscribe(message => {
      if (message.type === 'job') {
        this.update(message.job);
      }
    });
  }

  /** A track's freeze job while it runs (or the last one), if any. */
  freezeOf(trackId: string): Job | undefined {
    const id = this.freezes().get(trackId);
    return id ? this.jobs().get(id) : undefined;
  }

  /** Starts freezing a track; the delta that freezes it comes when the job is done. */
  async freeze(trackId: string): Promise<Job | null> {
    const job = await this.notify.run(() => this.api.freezeTrack(trackId), 'Could not freeze the track');
    if (job) {
      this.freezes.update(map => new Map(map).set(trackId, job.id));
      this.update(job);
    }
    return job;
  }

  /** Plays a frozen track live again (one undo step). */
  unfreeze(trackId: string): Promise<unknown> {
    return this.project.execute([{ type: 'unfreezeTrack', trackId }], 'Unfreeze Track');
  }

  /** Starts a bounce; the dialog follows it through {@link bounceJob}. */
  async bounce(request: BounceRequest): Promise<Job | null> {
    const job = await this.notify.run(() => this.api.bounce(request), 'Could not bounce');
    if (job) {
      this.bounceId.set(job.id);
      this.update(job);
    }
    return job;
  }

  async cancel(id: string): Promise<void> {
    const job = await this.notify.run(() => this.api.cancelJob(id), 'Could not cancel');
    if (job) {
      this.update(job);
    }
  }

  /** Shows a bounced file in Finder (the desktop app, on the machine that wrote it). */
  async reveal(path: string): Promise<void> {
    const shown = await this.desktop?.reveal?.(path);
    if (!shown) {
      this.notify.info(`Saved as ${path}`);
    }
  }

  /** Whether files can be revealed here (the desktop app with its own engine). */
  readonly canReveal = typeof this.desktop?.reveal === 'function';

  /** Takes a job's newest state, telling the person when it finished. */
  update(job: Job): void {
    const previous = this.jobs().get(job.id);
    if (previous && !isActive(previous)) {
      return; // finished already: a late progress event changes nothing
    }
    this.jobs.update(map => new Map(map).set(job.id, job));
    if (isActive(job)) {
      return;
    }
    switch (job.state) {
      case 'done':
        if (job.kind === 'bounce') {
          const first = job.files[0];
          const what = job.files.length === 1 && first ? fileName(first) : `${job.files.length} files`;
          this.notify.success(
            `Bounced ${what}`,
            first && this.canReveal ? { text: 'Show in Finder', run: () => void this.reveal(first) } : undefined
          );
        }
        break;
      case 'failed':
        this.notify.error(
          `${job.kind === 'bounce' ? 'Bounce' : 'Freeze'} failed${job.message ? `: ${job.message}` : ''}`
        );
        break;
      default:
        break;
    }
  }
}
