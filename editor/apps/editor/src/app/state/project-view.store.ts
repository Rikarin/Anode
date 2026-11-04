import { DestroyRef, Injectable, effect, inject, signal, untracked } from '@angular/core';
import { FlexStore } from './flex.store';
import { ProjectStore } from './project.store';
import { TracksViewStore } from './tracks-view.store';

const KeyPrefix = 'anode.view.';
/** How long the view rests before it is stored (the Tracks area scrolls while playing). */
const SaveDelayMs = 500;

/** A piece of the view a project remembers: its value as JSON, and how to take it back (undefined: the defaults). */
export interface ViewPart {
  value(): unknown;
  restore(saved: unknown): void;
}

/**
 * The view settings each project remembers, as in Logic: Show Flex, the Tracks area's zoom, position, tools and snap,
 * and the Mixer's view and filters. A project opens with the view it was left in, a project not seen before with the
 * defaults, and nothing carries from one project to the next.
 *
 * They are kept in this window's storage, by project id: they do not travel with the project, and they are not edits,
 * so changing them leaves it saved.
 */
@Injectable({ providedIn: 'root' })
export class ProjectViewStore {
  private readonly project = inject(ProjectStore);
  private readonly parts = signal<ReadonlyMap<string, ViewPart>>(new Map());
  private projectId: string | null = null;
  private saved: Record<string, unknown> = {};
  private saveTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    const view = inject(TracksViewStore);
    const flex = inject(FlexStore);
    this.keep('tracks', { value: () => view.remembered(), restore: saved => view.restore(saved) });
    this.keep('flex', { value: () => flex.showFlex(), restore: saved => flex.showFlex.set(saved === true) });
    effect(() => {
      const id = this.project.info()?.id ?? null;
      untracked(() => this.switchTo(id));
    });
    effect(() => {
      for (const part of this.parts().values()) {
        part.value();
      }
      untracked(() => this.scheduleSave());
    });
    const pageHide = (): void => this.store();
    window.addEventListener('pagehide', pageHide);
    inject(DestroyRef).onDestroy(() => {
      window.removeEventListener('pagehide', pageHide);
      this.cancelSave();
    });
  }

  /** Adds a part of the view (a store that exists only once its pane does adds itself): it starts as the project left it. */
  keep(key: string, part: ViewPart): void {
    this.parts.update(parts => new Map(parts).set(key, part));
    if (this.projectId !== null && key in this.saved) {
      part.restore(this.saved[key]);
    }
  }

  private switchTo(id: string | null): void {
    if (id === this.projectId) {
      return;
    }

    this.store();
    const first = this.projectId === null;
    this.projectId = id;
    this.saved = id === null ? {} : read(id);
    // Opening a first project with nothing kept leaves the defaults the window already shows.
    if (!first || Object.keys(this.saved).length > 0) {
      for (const [key, part] of this.parts()) {
        part.restore(this.saved[key]);
      }
    }
  }

  private scheduleSave(): void {
    this.cancelSave();
    this.saveTimer = setTimeout(() => this.store(), SaveDelayMs);
  }

  /** Stores the view of the project it belongs to. */
  private store(): void {
    this.cancelSave();
    if (this.projectId === null) {
      return;
    }

    const view: Record<string, unknown> = {};
    for (const [key, part] of untracked(this.parts)) {
      view[key] = untracked(() => part.value());
    }
    this.saved = view;
    try {
      localStorage.setItem(KeyPrefix + this.projectId, JSON.stringify(view));
    } catch {
      // Storage is full or off: the view stays for this session.
    }
  }

  private cancelSave(): void {
    if (this.saveTimer !== null) {
      clearTimeout(this.saveTimer);
      this.saveTimer = null;
    }
  }
}

function read(projectId: string): Record<string, unknown> {
  try {
    const view: unknown = JSON.parse(localStorage.getItem(KeyPrefix + projectId) ?? '{}');
    return typeof view === 'object' && view !== null ? (view as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}
