import { computed, DestroyRef, inject, Injectable, signal } from '@angular/core';
import { ApiError, ListenIn, type ListenInState, listenInUrl } from '@anode/api-client';
import { ANODE_API } from '../../core/api';
import { ConnectionService } from '../../core/connection';
import { EventStream } from '../../core/events';
import { FAKE_SERVER } from '../../core/fake/fake-token';
import { effectiveSpeed } from '../../music/varispeed';
import { ProjectStore } from '../../state/project.store';
import { TransportStore } from '../../state/transport.store';
import { createDecoder } from './decoders';
import type { JitterStats } from './jitter-buffer';
import { behindMs, ListenInClient, type ListenInDeps, type ListenInStatus } from './listen-in-client';
import { createPlayer, DirectPlayer, type ListenInPlayer, WorkletPlayer } from './player';

/** How often the remote refreshes what it shows of the stream. */
const StatsMs = 250;

/**
 * Listen-in on the remote (docs/11-phase-10.md): the toggle, the stream's state and its latency. In a browser it plays
 * through an AudioWorklet; with the fake Server (`?fake`, unit tests) the socket is the fake's stream. The jitter
 * buffer's stats come from the player; the latency is how far the heard transport is behind the Server's
 * ({@link behindMs}), and the Server's own ring-to-socket delay (`ListenInState.latencyMs`) shows beside it.
 */
@Injectable({ providedIn: 'root' })
export class ListenInStore {
  private readonly api = inject(ANODE_API);
  private readonly connection = inject(ConnectionService);
  private readonly fake = inject(FAKE_SERVER, { optional: true });
  private readonly transport = inject(TransportStore);
  private readonly project = inject(ProjectStore);

  readonly status = signal<ListenInStatus>('off');
  readonly error = signal<string | null>(null);
  readonly decoderName = signal<string | null>(null);
  readonly stats = signal<JitterStats | null>(null);
  /** How far behind the Server's transport the remote hears it, in ms (null: stopped, or not yet known). */
  readonly latencyMs = signal<number | null>(null);
  /** The Server's state: listeners and its own delay. */
  readonly server = signal<ListenInState>({ on: false, listeners: 0, latencyMs: null });

  private client: ListenInClient | null = null;
  private timer: ReturnType<typeof setInterval> | null = null;
  /** The players the store makes; tests swap in a direct one (no audio device). */
  playerFactory: (context: AudioContext | null) => Promise<ListenInPlayer> = async context =>
    context
      ? createPlayer(context, {
          projectSampleRate: this.client?.format?.projectSampleRate ?? 48000,
          frameSamples: this.client?.format?.frameSamples ?? ListenIn.frameSamples
        })
      : new DirectPlayer({ projectSampleRate: this.client?.format?.projectSampleRate ?? 48000 });

  constructor() {
    inject(EventStream).events$.subscribe(message => {
      if (message.type === 'listenIn') {
        this.server.set(message.state);
      }
    });
    inject(DestroyRef).onDestroy(() => this.stop());
    // The golden path's hook (apps/desktop/e2e/phase10): capture what the remote hears.
    (
      globalThis as {
        anodeListenIn?: Pick<ListenInStore, 'startCapture' | 'capture'> & { decoder(): string | null };
      }
    ).anodeListenIn = {
      startCapture: seconds => this.startCapture(seconds),
      capture: () => this.capture(),
      decoder: () => this.decoderName()
    };
  }

  readonly on = computed(() => this.status() !== 'off' && this.status() !== 'failed');

  /** The toggle: turns listen-in on (in the click, so the audio context may start) or off. */
  async toggle(): Promise<void> {
    if (this.on()) {
      this.stop();
    } else {
      await this.start();
    }
  }

  async start(): Promise<void> {
    // The context first, synchronously in the gesture; nothing plays without one (no AudioWorklet: the direct player).
    const context = !this.fake && typeof AudioContext !== 'undefined' ? WorkletPlayer.createContext() : null;
    try {
      const state = await this.api.listenInState();
      this.server.set(state);
      if (state.listeners >= ListenIn.maxListeners) {
        void context?.close();
        this.failWith(`Listen-in has ${ListenIn.maxListeners} listeners already: one has to stop first.`);
        return;
      }
    } catch {
      // An older Server without the state: try the socket.
    }
    const deps: ListenInDeps = {
      open: () => this.openSocket(),
      decoder: (format, output, onError) => createDecoder(format, output, onError),
      player: () => this.playerFactory(context),
      now: () => performance.now()
    };
    const client = new ListenInClient(deps, () => this.sync());
    client.tap = (transportSample, samples) => this.captureFrame(transportSample, samples);
    this.client = client;
    client.start();
    this.timer ??= setInterval(() => this.refresh(), StatsMs);
  }

  stop(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
    this.client?.stop();
    this.client = null;
    this.status.set('off');
    this.stats.set(null);
    this.latencyMs.set(null);
  }

  // ---- capture (tests and the golden path): the decoded stream as heard, with the transport sample it started at

  private captured: { left: Float32Array; length: number; transportSample: number } | null = null;

  /**
   * Starts keeping the next `seconds` of decoded audio (the left channel at the stream's 48 kHz), from the first frame
   * that has a transport position. The Playwright golden path reads it through `window.anodeListenIn` to find the test
   * tone's frequency, level and delay.
   */
  startCapture(seconds: number): void {
    this.captured = {
      left: new Float32Array(Math.round(seconds * ListenIn.sampleRate)),
      length: 0,
      transportSample: -1
    };
  }

  /** What {@link startCapture} kept so far: the samples, and the project sample (transport) of the first. */
  capture(): { samples: Float32Array; transportSample: number; sampleRate: number; projectSampleRate: number } | null {
    const c = this.captured;
    return c
      ? {
          samples: c.left.slice(0, c.length),
          transportSample: c.transportSample,
          sampleRate: ListenIn.sampleRate,
          projectSampleRate: this.client?.format?.projectSampleRate ?? this.project.info()?.sampleRate ?? 48000
        }
      : null;
  }

  private captureFrame(transportSample: number, samples: Float32Array): void {
    const c = this.captured;
    if (!c || c.length >= c.left.length || (c.length === 0 && transportSample < 0)) {
      return;
    }
    if (c.length === 0) {
      c.transportSample = transportSample;
    }
    const channels = this.client?.format?.channels ?? 2;
    for (let i = 0; i < samples.length && c.length < c.left.length; i += channels) {
      c.left[c.length++] = samples[i] ?? 0;
    }
  }

  /** The current player (tests drive a direct one). */
  get player(): ListenInPlayer | null {
    return this.client?.activePlayer ?? null;
  }

  /** Updates the stats and the latency from the player and the transport (every {@link StatsMs}; tests call it). */
  refresh(now = performance.now()): void {
    const player = this.player;
    const stats = player?.stats() ?? null;
    this.stats.set(stats);
    const rate = this.client?.format?.projectSampleRate ?? this.project.info()?.sampleRate ?? 48000;
    const speed = effectiveSpeed(this.project.settings().varispeed);
    const serverSample = this.transport.playing() ? Math.round((this.transport.secondsAt(now) / speed) * rate) : null;
    this.latencyMs.set(
      serverSample === null || !stats
        ? null
        : behindMs(serverSample, stats.playingTransportSample, rate, player?.outputLatencyMs() ?? 0)
    );
  }

  private openSocket() {
    if (this.fake) {
      try {
        return this.fake.openListenIn();
      } catch (error) {
        throw new Error(
          error instanceof ApiError && error.status === 503
            ? `Listen-in has ${ListenIn.maxListeners} listeners already: one has to stop first.`
            : error instanceof Error
              ? error.message
              : String(error),
          { cause: error }
        );
      }
    }
    const connection = this.connection.connection();
    if (!connection) {
      throw new Error('Not connected to a Server.');
    }
    return new WebSocket(listenInUrl(connection));
  }

  private sync(): void {
    const client = this.client;
    if (!client) {
      return;
    }
    this.status.set(client.status);
    this.error.set(client.error);
    this.decoderName.set(client.decoderName);
    if (client.status === 'failed' && this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  private failWith(message: string): void {
    this.status.set('failed');
    this.error.set(message);
  }
}
