import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { eventsUrl, type ServerMessage } from '@anode/api-client';
import { Subject } from 'rxjs';
import { ConnectionService } from './connection';
import { FAKE_SERVER } from './fake/fake-backend';

const MaxBackoffMs = 5_000;

/** The events URL with this editor's client id, which the Server uses to release what the editor left on. */
export function clientEventsUrl(url: string, client: string): string {
  const withClient = new URL(url);
  withClient.searchParams.set('client', client);
  return withClient.toString();
}

/** A random id for this editor (crypto.randomUUID needs a secure context, which a studio on the LAN is not). */
function newClientId(): string {
  return Array.from({ length: 4 }, () => Math.random().toString(36).slice(2, 8)).join('');
}

/**
 * The Server's WebSocket. Reconnects with backoff; every (re)connection emits on {@link connected$} so the stores resync
 * rather than trust that no event was missed. So does a `resync` message: the Server sends it in place of events this
 * client fell too far behind to receive. Telemetry (meters, the playhead) will bypass change detection here, read from
 * animation frames, as in Cathode (docs/01-implementation-plan.md §4.2).
 */
@Injectable({ providedIn: 'root' })
export class EventStream {
  private readonly connection = inject(ConnectionService);
  private readonly fake = inject(FAKE_SERVER, { optional: true });
  private unsubscribeFake: (() => void) | null = null;
  private socket: WebSocket | null = null;
  private retry: ReturnType<typeof setTimeout> | null = null;
  private attempts = 0;
  private stopped = false;

  readonly status = signal<'connecting' | 'open' | 'closed'>('closed');
  readonly events$ = new Subject<ServerMessage>();
  readonly connected$ = new Subject<void>();
  readonly clientId = newClientId();

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stop());
  }

  start(): void {
    this.stopped = false;
    if (this.fake) {
      this.unsubscribeFake ??= this.fake.subscribe(message => this.dispatch(message));
      return;
    }

    this.open();
  }

  stop(): void {
    this.stopped = true;
    if (this.retry) {
      clearTimeout(this.retry);
    }

    this.socket?.close();
    this.socket = null;
    this.unsubscribeFake?.();
    this.unsubscribeFake = null;
  }

  private open(): void {
    const connection = this.connection.connection();
    if (!connection || this.stopped) {
      return;
    }

    this.status.set('connecting');
    const socket = new WebSocket(clientEventsUrl(eventsUrl(connection), this.clientId));
    this.socket = socket;
    socket.onmessage = message => this.receive(message.data);
    socket.onclose = () => {
      if (this.socket !== socket) {
        return;
      }

      this.status.set('closed');
      this.socket = null;
      if (!this.stopped) {
        const delay = Math.min(MaxBackoffMs, 250 * 2 ** this.attempts++);
        this.retry = setTimeout(() => this.open(), delay);
      }
    };
  }

  /** Handles one message from the Server; public for tests. */
  receive(data: unknown): void {
    if (typeof data !== 'string') {
      return;
    }

    // Typed loosely on purpose: a newer Server may send kinds this editor does not know yet.
    let message: { type?: unknown };
    try {
      message = JSON.parse(data) as { type?: unknown };
    } catch {
      return; // not a message this editor can read: skip it rather than stop reading the stream
    }

    this.dispatch(message);
  }

  /** Handles one parsed message (from the socket, or from the in-memory fake Server). */
  dispatch(message: { type?: unknown }): void {
    if (message.type === 'connected') {
      this.attempts = 0;
      this.status.set('open');
      this.connected$.next();
    } else if (message.type === 'resync') {
      this.connected$.next();
    } else if (typeof message.type === 'string') {
      this.events$.next(message as ServerMessage);
    }
  }
}
