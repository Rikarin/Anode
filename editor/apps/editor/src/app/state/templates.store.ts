import { Injectable, computed, inject, signal } from '@angular/core';
import type { ProjectTemplate, TrackTemplate } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { Notify } from '../core/notify';
import { JobsStore } from './jobs.store';
import { ProjectStore } from './project.store';
import { SelectionStore } from './selection.store';

/**
 * Project and track templates (docs/04-phase-3.md, "Templates"): the lists the chooser and the project menu show, and
 * saving and using them. Saving a project and using a track template are jobs, followed to their end here.
 */
@Injectable({ providedIn: 'root' })
export class TemplatesStore {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);
  private readonly jobs = inject(JobsStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);

  readonly projectTemplates = signal<ProjectTemplate[]>([]);
  readonly trackTemplates = signal<TrackTemplate[]>([]);
  /** The templates by category, in list order, for the chooser. */
  readonly projectCategories = computed(() => {
    const categories = new Map<string, ProjectTemplate[]>();
    for (const template of this.projectTemplates()) {
      categories.set(template.category, [...(categories.get(template.category) ?? []), template]);
    }

    return [...categories].map(([category, templates]) => ({ category, templates }));
  });

  async refresh(): Promise<void> {
    const [projects, tracks] = await Promise.all([
      this.api.projectTemplates().catch(() => [] as ProjectTemplate[]),
      this.api.trackTemplates().catch(() => [] as TrackTemplate[])
    ]);
    this.projectTemplates.set(projects);
    this.trackTemplates.set(tracks);
  }

  /** Saves the open project, unsaved edits and media included, as a template; it stays as it is. */
  async saveProject(name: string, category: string): Promise<boolean> {
    const done = await this.toEnd(
      () => this.api.saveProjectTemplate({ name: name.trim(), category: category.trim() }),
      'Could not save the template'
    );
    if (done) {
      this.notify.info(`Saved the template “${name.trim()}”.`);
      await this.refresh();
    }

    return done;
  }

  /** Saves the selected tracks, with what they reach, as a template. */
  async saveTracks(name: string, category: string): Promise<boolean> {
    const saved = await this.notify.run(
      () =>
        this.api.saveTrackTemplate({
          name: name.trim(),
          category: category.trim(),
          trackIds: [...this.selection.tracks()]
        }),
      'Could not save the template'
    );
    if (saved) {
      this.notify.info(`Saved “${saved.name}”: ${saved.tracks} ${saved.tracks === 1 ? 'track' : 'tracks'}.`);
      await this.refresh();
    }

    return saved !== null;
  }

  /** Adds a track template's tracks after the project's, one undo step. */
  apply(id: string): Promise<boolean> {
    return this.project.isOpen()
      ? this.toEnd(() => this.api.applyTrackTemplate(id), 'Could not add the tracks')
      : Promise.resolve(false);
  }

  async deleteProject(id: string): Promise<void> {
    await this.notify.run(() => this.api.deleteProjectTemplate(id), 'Could not delete the template');
    await this.refresh();
  }

  async deleteTracks(id: string): Promise<void> {
    await this.notify.run(() => this.api.deleteTrackTemplate(id), 'Could not delete the template');
    await this.refresh();
  }

  private async toEnd(start: () => ReturnType<typeof this.api.saveProjectTemplate>, failure: string): Promise<boolean> {
    const job = await this.notify.run(async () => {
      const ended = await this.jobs.finished(await start());
      if (ended.state === 'failed') {
        throw new Error(ended.message ?? 'It failed.');
      }

      return ended;
    }, failure);
    return job?.state === 'done';
  }
}
