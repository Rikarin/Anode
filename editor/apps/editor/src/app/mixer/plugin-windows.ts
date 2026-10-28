import { Injectable, inject, signal } from '@angular/core';
import { ApiError, type Insert } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { ConnectionService } from '../core/connection';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';

/** A problem key: the insert (or `instrument`) on a track (or the master). */
export function problemKey(trackId: string | null, insertId: string | null): string {
  return `${trackId ?? 'master'}/${insertId ?? 'instrument'}`;
}

/**
 * Opens an insert's editor (docs/05-phase-4.md): a plugin with its own editor gets its native window in the desktop app
 * (`/plugins/editor`, the engine opens it next to this window); everything else, and every plugin in a browser, the
 * generic editor (a floating window of its parameters). Also keeps the plugins the engine had to bypass
 * (`pluginProblem`), so their slots show it, and tells the person.
 */
@Injectable({ providedIn: 'root' })
export class PluginWindows {
  private readonly api = inject(ANODE_API);
  private readonly ui = inject(UiStore);
  private readonly plugins = inject(PluginsStore);
  private readonly project = inject(ProjectStore);
  private readonly notify = inject(Notify);
  private readonly desktop = inject(ConnectionService).desktop;

  /** Inserts (and instruments) the engine bypassed after a crash or a failed load, by {@link problemKey}. */
  readonly problems = signal<ReadonlySet<string>>(new Set());

  constructor() {
    inject(EventStream).events$.subscribe(message => {
      if (message.type !== 'pluginProblem') {
        return;
      }
      this.problems.update(set => new Set(set).add(problemKey(message.trackId, message.insertId)));
      const plugin = this.plugins.plugin(message.pluginId)?.name ?? message.pluginId;
      const track = message.trackId ? (this.project.track(message.trackId)?.name ?? 'a track') : 'the master';
      this.notify.error(`${plugin} on ${track} stopped and is bypassed: ${message.message}`);
    });
  }

  hasProblem(trackId: string | null, insertId: string): boolean {
    return this.problems().has(problemKey(trackId, insertId));
  }

  /** Opens an insert's editor: native in the desktop app when the plugin has one, else the generic window. */
  async open(trackId: string | null, insert: Insert): Promise<void> {
    const plugin = this.plugins.plugin(insert.pluginId);
    if (plugin?.hasEditor && this.desktop) {
      try {
        await this.api.openPluginEditor({ trackId, insertId: insert.id, open: true });
        return;
      } catch (error) {
        // No window on this machine (a headless engine, a plugin that refuses): the generic editor instead.
        if (!(error instanceof ApiError && (error.status === 409 || error.status === 501))) {
          this.notify.failure(error, `Could not open ${insert.name}`);
          return;
        }
      }
    }
    this.ui.openPluginWindow({ trackId, insertId: insert.id });
  }
}
