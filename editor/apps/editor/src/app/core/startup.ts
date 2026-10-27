import { Injectable, inject, signal } from '@angular/core';
import { SystemStore } from '../state/system.store';
import { ConnectionService } from './connection';
import type { ServerTarget } from './desktop';
import { EventStream } from './events';

/** Connects to the Server, loads what the shell shows, and opens the event stream. */
@Injectable({ providedIn: 'root' })
export class Startup {
  private readonly connection = inject(ConnectionService);
  private readonly events = inject(EventStream);
  private readonly system = inject(SystemStore);

  /** `token`: a browser reached a Server that wants an access token it does not have (or has a wrong one). */
  readonly state = signal<'starting' | 'ready' | 'failed' | 'token'>('starting');
  readonly error = signal<string | null>(null);
  /** In the desktop app, which Server it uses: the startup screen offers a way back to this computer's engine. */
  readonly target = signal<ServerTarget | null>(null);

  constructor() {
    this.events.connected$.subscribe(() => void this.system.refresh().catch(() => undefined));
    this.events.events$.subscribe(message => {
      if (message.type === 'engine') {
        this.system.applyEngine(message.engine);
      }
    });
  }

  async start(): Promise<void> {
    this.state.set('starting');
    try {
      this.target.set((await this.connection.desktop?.target()) ?? null);
      await this.connection.connect();
      if (!this.connection.desktop && (await this.connection.refused())) {
        this.state.set('token');
        return;
      }

      await this.system.refresh();
      this.events.start();
      this.state.set('ready');
    } catch (error) {
      this.error.set(failure(error, this.connection.connection()?.baseUrl));
      this.state.set('failed');
    }
  }

  /** Connects again with a token typed in the browser. */
  async useToken(token: string): Promise<void> {
    this.connection.useToken(token);
    await this.start();
  }
}

/** What went wrong, without the wrapping Electron adds to an error from the desktop app's main process. */
export function failure(error: unknown, baseUrl?: string): string {
  const message = error instanceof Error ? error.message : String(error);
  const cleaned = message.replace(/^Error invoking remote method '[^']*': (?:Error: )?/, '');
  return cleaned === 'Failed to fetch' && baseUrl ? `No answer from ${baseUrl}. Is anode-server running?` : cleaned;
}
