import { Injectable, inject, signal } from '@angular/core';
import type {
  Job,
  ProjectAlternative,
  ProjectBackup,
  ProjectSummary,
  RecoverableProject,
  SampleRateChangeMode
} from '@anode/api-client';
import { ANODE_API, type CreateProject } from '../core/api';
import { Notify } from '../core/notify';
import { JobsStore } from './jobs.store';
import { ProjectStore } from './project.store';
import { ScreensetStore } from './screenset.store';
import { UiStore } from './ui.store';

/**
 * Creating, opening, saving and closing projects, and the recent list: the project chooser's and the File menu's
 * actions. Anything that would drop unsaved edits asks first (Cathode's guard: a plain confirm, which the desktop app
 * also asks before it quits).
 */
@Injectable({ providedIn: 'root' })
export class ProjectSession {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly screensets = inject(ScreensetStore);
  private readonly jobs = inject(JobsStore);
  private readonly notify = inject(Notify);
  private readonly ui = inject(UiStore);

  readonly recent = signal<ProjectSummary[]>([]);
  /** Projects with autosaved edits nobody saved: what the project chooser offers after a crash. */
  readonly recoverable = signal<RecoverableProject[]>([]);
  /** The open project's alternatives and backups (the project menu), as last listed. */
  readonly alternatives = signal<ProjectAlternative[]>([]);
  readonly backups = signal<ProjectBackup[]>([]);
  /** What is being done ("Opening Demo…"), or null. */
  readonly busy = signal<string | null>(null);

  /** Asks the person a yes/no question; replaced in tests. */
  confirm: (message: string) => boolean = message => window.confirm(message);

  async refreshRecent(): Promise<void> {
    const recent = await this.notify.run(() => this.api.recentProjects(), 'Could not list recent projects');
    this.recent.set(recent ?? []);
  }

  async refreshRecoverable(): Promise<void> {
    const recoverable = await this.notify.run(
      () => this.api.recoverableProjects(),
      'Could not list autosaved projects'
    );
    this.recoverable.set(recoverable ?? []);
  }

  /** Opens a project with its autosaved edits, unsaved; its saved version stays as it is until the next save. */
  async recover(path: string): Promise<boolean> {
    if (!this.mayDiscard('recover this project')) {
      return false;
    }

    const recovered = await this.load(
      'Recovering…',
      () => this.api.recoverProject(path),
      'Could not recover the project'
    );
    if (recovered) {
      this.recoverable.update(projects => projects.filter(p => p.path !== path));
    }

    return recovered;
  }

  /** Stops offering a project's autosaved edits; the Server keeps them as the project's newest backup. */
  async discardRecovery(path: string): Promise<boolean> {
    const discarded = await this.notify.run(async () => {
      await this.api.discardRecovery(path);
      return true;
    }, 'Could not discard the autosaved edits');
    if (discarded) {
      this.recoverable.update(projects => projects.filter(p => p.path !== path));
    }

    return discarded === true;
  }

  /**
   * Whether the desktop app may quit. Told to discard unsaved edits, it closes the project, so the Server does not
   * autosave them on its way out and offer them at the next launch; they stay in a backup.
   */
  async mayQuit(): Promise<boolean> {
    const discards = this.project.isOpen() && this.project.dirty();
    if (!this.mayDiscard('quit')) {
      return false;
    }

    if (discards) {
      await this.api.closeProject().catch(() => undefined);
    }

    return true;
  }

  /** Whether the open project's unsaved edits may be dropped to `then` ("quit"): asks when there are any. */
  mayDiscard(then: string): boolean {
    const info = this.project.info();
    return (
      !info || !this.project.dirty() || this.confirm(`“${info.name}” has unsaved changes. Discard them and ${then}?`)
    );
  }

  async create(request: CreateProject): Promise<boolean> {
    if (!this.mayDiscard('create a new project')) {
      return false;
    }

    return this.load(
      `Creating ${request.name}…`,
      () => this.api.createProject(request),
      'Could not create the project'
    );
  }

  async open(path: string): Promise<boolean> {
    if (!path.trim() || !this.mayDiscard('open another project')) {
      return false;
    }

    const folder = path.trim();
    if (await this.wantsRecovery(folder)) {
      return this.load(`Recovering ${folder}…`, () => this.api.recoverProject(folder), 'Could not recover the project');
    }

    return this.load(`Opening ${path}…`, () => this.api.openProject(folder), 'Could not open the project');
  }

  /**
   * Whether to open `folder` with its autosaved edits: the person is asked when it has some. Opening the saved version
   * instead keeps them as a backup.
   */
  private async wantsRecovery(folder: string): Promise<boolean> {
    const projects = await this.api.recoverableProjects().catch(() => [] as RecoverableProject[]);
    const pending = projects.find(p => p.path === folder.replace(/[/\\]+$/, ''));
    return (
      pending !== undefined &&
      this.confirm(
        `“${pending.name}” has edits autosaved at ${new Date(pending.autosaved).toLocaleString()} that were never ` +
          'saved. Recover them? If not, the saved version opens and the edits stay in a backup.'
      )
    );
  }

  async save(): Promise<boolean> {
    if (!this.project.isOpen()) {
      return false;
    }

    // A layout change still on its way would mark the saved project unsaved again: it goes first.
    await this.screensets.flush();
    const revision = this.project.revision();
    const saved = await this.notify.run(() => this.api.saveProject(), 'Could not save');
    if (saved) {
      // Saved at the revision asked for; an edit that landed meanwhile keeps the project dirty.
      if (this.project.revision() === revision) {
        this.project.dirty.set(false);
      }
      this.recent.update(recent => [saved, ...recent.filter(p => p.path !== saved.path)]);
    }

    return saved !== null;
  }

  /** File ▸ Save As…: the whole package at `path`, unsaved edits included; the project goes on there. */
  async saveAs(path: string): Promise<boolean> {
    const job = await this.packageJob(() => this.api.saveProjectAs(path.trim()), 'Could not save the project');
    if (job) {
      this.notify.info(`Saved as “${job.files[0] ?? path}”.`);
      void this.refreshRecent();
    }

    return job !== null;
  }

  /** File ▸ Save a Copy…: the whole package at `path`, unsaved edits included; the open project stays. */
  async saveCopy(path: string): Promise<boolean> {
    const job = await this.packageJob(() => this.api.saveProjectCopy(path.trim()), 'Could not save a copy');
    if (job) {
      this.notify.info(`Saved a copy at “${job.files[0] ?? path}”.`);
      void this.refreshRecent();
    }

    return job !== null;
  }

  /**
   * File ▸ Project Settings ▸ Sample Rate…: the Server keeps a backup, saves the project at the new rate and reopens it,
   * so the project comes back on its `projectOpened` event.
   */
  async changeSampleRate(sampleRate: number, mode: SampleRateChangeMode): Promise<boolean> {
    const job = await this.packageJob(
      () => this.api.changeSampleRate(sampleRate, mode),
      'Could not change the sample rate'
    );
    if (job) {
      this.notify.info(`The project is now at ${sampleRate} Hz. Its last save is a backup.`);
    }

    return job !== null;
  }

  /** Runs a job that writes a package to its end: the job when it is done, null (reported) otherwise. */
  private async packageJob(start: () => Promise<Job>, failure: string): Promise<Job | null> {
    if (!this.project.isOpen()) {
      return null;
    }

    // A layout change still on its way would be left out: it goes first.
    await this.screensets.flush();
    this.busy.set('Saving…');
    try {
      const job = await this.notify.run(async () => {
        const ended = await this.jobs.finished(await start());
        if (ended.state === 'failed') {
          throw new Error(ended.message ?? 'It failed.');
        }

        return ended;
      }, failure);
      return job?.state === 'done' ? job : null;
    } finally {
      this.busy.set(null);
    }
  }

  /** Lists the open project's alternatives and backups (when the project menu opens). */
  async refreshVersions(): Promise<void> {
    if (!this.project.isOpen()) {
      return;
    }
    const [alternatives, backups] = await Promise.all([
      this.api.alternatives().catch(() => [] as ProjectAlternative[]),
      this.api.backups().catch(() => [] as ProjectBackup[])
    ]);
    this.alternatives.set(alternatives);
    this.backups.set(backups);
  }

  /** Saves the open project as a new alternative, which becomes current (Logic's File ▸ Alternatives ▸ New). */
  async saveAlternative(name: string): Promise<boolean> {
    if (!this.project.isOpen() || !name.trim()) {
      return false;
    }
    const created = await this.notify.run(
      () => this.api.createAlternative(name.trim()),
      'Could not save the alternative'
    );
    if (created) {
      this.notify.info(`Saved the alternative “${created.name}”.`);
      await this.refreshVersions();
    }
    return created !== null;
  }

  /** Switches to another alternative (the Server keeps unsaved edits in a backup first). */
  async openAlternative(id: string): Promise<boolean> {
    return this.load('Opening the alternative…', () => this.api.openAlternative(id), 'Could not open the alternative');
  }

  /** Replaces the open project with a backup, after asking when there are unsaved edits. */
  async revertToBackup(id: string): Promise<boolean> {
    if (!this.mayDiscard('revert to the backup')) {
      return false;
    }
    return this.load('Reverting…', () => this.api.revertToBackup(id), 'Could not revert to the backup');
  }

  async close(): Promise<boolean> {
    if (!this.project.isOpen() || !this.mayDiscard('close it')) {
      return false;
    }

    const closed = await this.notify.run(async () => {
      await this.api.closeProject();
      return true;
    }, 'Could not close the project');
    if (closed) {
      this.project.clear();
      void this.refreshRecent();
    }

    return closed === true;
  }

  private async load(
    busy: string,
    request: () => ReturnType<typeof this.api.openProject>,
    failure: string
  ): Promise<boolean> {
    this.busy.set(busy);
    this.project.loading.set(true);
    try {
      const project = await this.notify.run(request, failure);
      if (project) {
        this.project.load(project);
        this.ui.missingFilesOpen.set(project.offline.files.length + project.offline.unreadableTracks.length > 0);
      }

      return project !== null;
    } finally {
      this.busy.set(null);
      this.project.loading.set(false);
    }
  }
}
