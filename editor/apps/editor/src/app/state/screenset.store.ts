import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { ApiError, type Screenset } from '@anode/api-client';
import type { XuiDockManagerLayout } from '@xui/dock-manager';
import { ANODE_API } from '../core/api';
import { Notify } from '../core/notify';
import { defaultLayout, parseLayout, showPane } from '../pages/layout';
import { ProjectStore } from './project.store';

/** How long the layout rests before an unlocked screenset keeps it. */
const SaveDelayMs = 600;

/**
 * Screensets 1–9 (Logic's): each keeps a layout of the main window's panes, saved in the project
 * (`/project/screensets`, not an undo step). Number keys recall them. An unlocked screenset keeps whatever the person
 * does to its layout; a locked one returns to its saved layout whenever it is recalled. A number with nothing saved
 * starts from the default layout.
 */
@Injectable({ providedIn: 'root' })
export class ScreensetStore {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly notify = inject(Notify);

  readonly screensets = signal<ReadonlyMap<number, Screenset>>(new Map());
  readonly current = signal(1);
  /** The main window's layout (the dock manager binds it two-way). */
  readonly layout = signal<XuiDockManagerLayout>(defaultLayout());
  readonly currentSet = computed(() => this.screensets().get(this.current()) ?? null);
  readonly name = computed(() => this.currentSet()?.name ?? `Screenset ${this.current()}`);
  readonly locked = computed(() => this.currentSet()?.locked ?? false);

  private saveTimer: ReturnType<typeof setTimeout> | null = null;
  /** The save on its way to the Server, if any. */
  private saving: Promise<void> | null = null;
  /** False once the Server answered that it keeps no screensets (not implemented): they then live in this window. */
  private remote = true;
  /** The layout as last applied or saved, so a recall does not count as a change. */
  private settled = '';

  constructor() {
    effect(() => {
      const id = this.project.info()?.id;
      untracked(() => (id ? void this.load() : this.reset()));
    });
    effect(() => {
      const layout = this.layout();
      untracked(() => this.layoutChanged(layout));
    });
    inject(DestroyRef).onDestroy(() => this.cancelSave());
  }

  /** The project's screensets, then screenset 1 (or the default layout). */
  async load(): Promise<void> {
    try {
      const list = await this.api.screensets();
      this.remote = true;
      this.screensets.set(new Map(list.map(s => [s.number, s])));
    } catch (error) {
      this.remote = !unavailable(error);
      this.screensets.set(new Map());
    }
    this.apply(1);
  }

  /** Switches to screenset `number`, keeping the current one's layout first when it is unlocked. */
  async recall(number: number): Promise<void> {
    if (number < 1 || number > 9 || !this.project.isOpen()) {
      return;
    }
    if (this.saveTimer) {
      this.cancelSave();
      await this.save(this.current());
    }
    this.apply(number);
  }

  /** Saves the current layout as the current screenset (also when locked: Logic's "Save as"). */
  async saveCurrent(): Promise<void> {
    await this.save(this.current(), { layout: JSON.stringify(this.layout()) });
  }

  async rename(name: string): Promise<void> {
    const trimmed = name.trim();
    if (trimmed) {
      await this.save(this.current(), { name: trimmed });
    }
  }

  async toggleLock(): Promise<void> {
    await this.save(this.current(), { locked: !this.locked(), layout: JSON.stringify(this.layout()) });
  }

  /** Copies the current layout into screenset `number` and switches to it. */
  async duplicateTo(number: number): Promise<void> {
    const layout = JSON.stringify(this.layout());
    this.current.set(number);
    await this.save(number, { layout, name: `${this.name()} copy`, locked: false });
  }

  /** Brings a pane forward (the View key commands). */
  showPane(contentId: string): void {
    this.layout.set(showPane(this.layout(), contentId));
  }

  private apply(number: number): void {
    this.current.set(number);
    const saved = this.screensets().get(number);
    const layout = (saved && parseLayout(saved.layout)) ?? defaultLayout();
    this.settled = JSON.stringify(layout);
    this.layout.set(layout);
  }

  private reset(): void {
    this.cancelSave();
    this.screensets.set(new Map());
    this.current.set(1);
    const layout = defaultLayout();
    this.settled = JSON.stringify(layout);
    this.layout.set(layout);
  }

  /**
   * Keeps a layout change that is still resting now, and waits for any save on its way: a project save after it then
   * includes the layout, rather than the layout marking the just-saved project unsaved again.
   */
  async flush(): Promise<void> {
    if (this.saveTimer) {
      this.cancelSave();
      void this.save(this.current());
    }
    await this.saving;
  }

  private layoutChanged(layout: XuiDockManagerLayout): void {
    const text = JSON.stringify(layout);
    if (text === this.settled || this.locked() || !this.project.isOpen()) {
      return;
    }
    this.cancelSave();
    this.saveTimer = setTimeout(() => {
      this.saveTimer = null;
      void this.save(this.current());
    }, SaveDelayMs);
  }

  private save(number: number, change: Partial<Screenset> = {}): Promise<void> {
    const saving = this.send(number, change).finally(() => {
      if (this.saving === saving) {
        this.saving = null;
      }
    });
    this.saving = saving;
    return saving;
  }

  private async send(number: number, change: Partial<Screenset>): Promise<void> {
    const existing = this.screensets().get(number);
    const screenset: Screenset = {
      number,
      name: existing?.name ?? `Screenset ${number}`,
      locked: existing?.locked ?? false,
      layout: number === this.current() ? JSON.stringify(this.layout()) : (existing?.layout ?? ''),
      ...change
    };
    if (number === this.current()) {
      this.settled = screenset.layout;
    }
    this.screensets.update(map => new Map(map).set(number, screenset));
    if (!this.remote) {
      return;
    }
    try {
      await this.api.saveScreenset(screenset);
      this.project.markDirty();
    } catch (error) {
      if (unavailable(error)) {
        this.remote = false;
      } else {
        this.notify.failure(error, 'Could not save the screenset');
      }
    }
  }

  private cancelSave(): void {
    if (this.saveTimer) {
      clearTimeout(this.saveTimer);
      this.saveTimer = null;
    }
  }
}

/** Whether an error says the Server has no screensets at all (a Server before Phase 3), rather than a refusal. */
function unavailable(error: unknown): boolean {
  return error instanceof ApiError && (error.status >= 500 || error.status === 405);
}
