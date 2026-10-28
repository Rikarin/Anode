import { Injectable, inject, signal } from '@angular/core';
import type { ProjectAlternative, ProjectBackup, ProjectSummary } from '@anode/api-client';
import { ANODE_API, type CreateProject } from '../core/api';
import { Notify } from '../core/notify';
import { ProjectStore } from './project.store';

/**
 * Creating, opening, saving and closing projects, and the recent list: the project chooser's and the File menu's
 * actions. Anything that would drop unsaved edits asks first (Cathode's guard: a plain confirm, which the desktop app
 * also asks before it quits).
 */
@Injectable({ providedIn: 'root' })
export class ProjectSession {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly notify = inject(Notify);

  readonly recent = signal<ProjectSummary[]>([]);
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

    return this.load(`Opening ${path}…`, () => this.api.openProject(path.trim()), 'Could not open the project');
  }

  async save(): Promise<boolean> {
    if (!this.project.isOpen()) {
      return false;
    }

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
      }

      return project !== null;
    } finally {
      this.busy.set(null);
      this.project.loading.set(false);
    }
  }
}
