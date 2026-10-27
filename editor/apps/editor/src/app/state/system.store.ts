import { Injectable, computed, inject, signal } from '@angular/core';
import { type EngineStatus, type SystemInfo } from '@anode/api-client';
import { ANODE_API } from '../core/api';

/**
 * The Server's version and its engine host's status (`GET /api/v1/system`), refreshed on every (re)connection and kept
 * current by the Server's `engine` events.
 */
@Injectable({ providedIn: 'root' })
export class SystemStore {
  private readonly api = inject(ANODE_API);

  readonly info = signal<SystemInfo | null>(null);
  readonly engineState = computed(() => this.info()?.engine.state ?? null);

  async refresh(): Promise<void> {
    this.info.set(await this.api.system());
  }

  /** The engine host's link changed (an `engine` event). */
  applyEngine(engine: EngineStatus): void {
    this.info.update(info => (info ? { ...info, engine } : info));
  }
}
