import { type ListenInFormat, readListenInFrame } from '@anode/api-client';
import type { DecodedOutput, ListenInDecoder } from './decoders';
import type { ListenInPlayer } from './player';

/** As much of a `WebSocket` as the client uses (a real one, or the fake Server's stream). */
export interface ListenInSocketLike {
  binaryType: BinaryType;
  onopen: ((event: Event) => void) | null;
  onmessage: ((event: MessageEvent<string | ArrayBuffer>) => void) | null;
  onclose: ((event: CloseEvent) => void) | null;
  onerror: ((event: Event) => void) | null;
  close(): void;
}

export type ListenInStatus = 'off' | 'connecting' | 'buffering' | 'playing' | 'failed';

/** The stream's format as the socket's first message says, with the fake Server's codec when it names one. */
export type StreamFormat = ListenInFormat & { codec?: string };

/** What the client needs: the socket, a decoder and a player for the format, the clock. */
export interface ListenInDeps {
  open(): ListenInSocketLike;
  decoder(format: StreamFormat, output: DecodedOutput, onError: (error: Error) => void): Promise<ListenInDecoder>;
  player(format: StreamFormat): Promise<ListenInPlayer>;
  now(): number;
}

/** Parses the socket's first (text) message: the stream's format; null when it is not one. */
export function parseFormat(text: string): StreamFormat | null {
  try {
    const value = JSON.parse(text) as Partial<StreamFormat>;
    const numbers = [value.sampleRate, value.channels, value.frameSamples, value.bitrate, value.projectSampleRate];
    return numbers.every(n => typeof n === 'number' && n > 0) ? (value as StreamFormat) : null;
  } catch {
    return null;
  }
}

/**
 * The listen-in client (docs/11-phase-10.md, ADR-0013): opens the socket, reads the `ListenInFormat` text message, then
 * one binary frame per Opus packet (`readListenInFrame`): decodes each (in order) and hands it, with its sequence,
 * transport sample, flags and arrival time, to the player's jitter buffer. Frames that arrive before the decoder is
 * ready wait for it.
 */
export class ListenInClient {
  status: ListenInStatus = 'off';
  error: string | null = null;
  format: StreamFormat | null = null;
  decoderName: string | null = null;
  framesReceived = 0;
  /** Sees every decoded frame (its transport sample and interleaved samples) before the player: the store's capture. */
  tap: ((transportSample: number, samples: Float32Array) => void) | null = null;
  private socket: ListenInSocketLike | null = null;
  private decoder: ListenInDecoder | null = null;
  private player: ListenInPlayer | null = null;
  private readonly waiting: ArrayBuffer[] = [];
  /** Frame metadata by sequence until its audio is decoded. */
  private readonly meta = new Map<number, { transportSample: number; flags: number; arrivalMs: number }>();

  constructor(
    private readonly deps: ListenInDeps,
    private readonly changed: () => void = () => undefined
  ) {}

  get activePlayer(): ListenInPlayer | null {
    return this.player;
  }

  start(): void {
    this.stop();
    this.set('connecting');
    let socket: ListenInSocketLike;
    try {
      socket = this.deps.open();
    } catch (error) {
      this.fail(error instanceof Error ? error.message : String(error));
      return;
    }
    this.socket = socket;
    socket.binaryType = 'arraybuffer';
    socket.onmessage = event => this.receive(event.data);
    socket.onerror = () => this.fail('The listen-in stream failed.');
    socket.onclose = () => {
      if (this.socket === socket && this.status !== 'failed') {
        this.fail('The Server closed the listen-in stream.');
      }
    };
  }

  stop(): void {
    const socket = this.socket;
    this.socket = null;
    if (socket) {
      socket.onmessage = null;
      socket.onclose = null;
      socket.onerror = null;
      socket.close();
    }
    this.decoder?.close();
    this.decoder = null;
    this.player?.close();
    this.player = null;
    this.waiting.length = 0;
    this.meta.clear();
    this.format = null;
    this.framesReceived = 0;
    if (this.status !== 'failed') {
      this.set('off');
    }
  }

  private receive(data: string | ArrayBuffer): void {
    if (typeof data === 'string') {
      const format = parseFormat(data);
      if (!format) {
        this.fail('The listen-in stream did not start with its format.');
        return;
      }
      this.format = format;
      this.set('buffering');
      void this.prepare(format);
      return;
    }
    this.framesReceived++;
    if (!this.decoder || !this.player) {
      this.waiting.push(data);
      return;
    }
    this.frame(data);
  }

  private async prepare(format: StreamFormat): Promise<void> {
    try {
      const player = await this.deps.player(format);
      const decoder = await this.deps.decoder(
        format,
        (tag, samples) => this.decoded(tag, samples),
        error => this.fail(`Could not decode the stream: ${error.message}`)
      );
      if (!this.socket) {
        player.close();
        decoder.close();
        return;
      }
      this.player = player;
      this.decoder = decoder;
      this.decoderName = decoder.name;
      for (const message of this.waiting.splice(0)) {
        this.frame(message);
      }
      this.changed();
    } catch (error) {
      this.fail(error instanceof Error ? error.message : String(error));
    }
  }

  private frame(message: ArrayBuffer): void {
    const frame = readListenInFrame(message);
    if (!frame || !this.decoder) {
      return;
    }
    this.meta.set(frame.sequence, {
      transportSample: frame.transportSample,
      flags: frame.flags,
      arrivalMs: this.deps.now()
    });
    // The packet is a view into the message: copy it, as a decoder may keep it.
    this.decoder.decode(frame.packet.slice(), frame.sequence);
  }

  private decoded(sequence: number, samples: Float32Array): void {
    const meta = this.meta.get(sequence);
    this.meta.delete(sequence);
    if (!meta || !this.player) {
      return;
    }
    this.tap?.(meta.transportSample, samples);
    this.player.push({ sequence, samples, ...meta });
    if (this.status === 'buffering') {
      this.set('playing');
    }
  }

  private fail(message: string): void {
    this.error = message;
    this.status = 'failed';
    const socket = this.socket;
    this.socket = null;
    if (socket) {
      socket.onclose = null;
      socket.close();
    }
    this.decoder?.close();
    this.decoder = null;
    this.player?.close();
    this.player = null;
    this.changed();
  }

  private set(status: ListenInStatus): void {
    this.status = status;
    if (status !== 'failed') {
      this.error = null;
    }
    this.changed();
  }
}

/**
 * How far behind the Server's transport the remote hears it, in ms: the Server's transport now minus the transport
 * sample of what plays now (both at the project's rate), plus the device's own latency after the worklet. Null when
 * nothing with a transport position plays (stopped).
 */
export function behindMs(
  serverTransportSample: number,
  playingTransportSample: number | null,
  projectSampleRate: number,
  outputLatencyMs: number
): number | null {
  if (playingTransportSample === null || projectSampleRate <= 0) {
    return null;
  }
  return ((serverTransportSample - playingTransportSample) / projectSampleRate) * 1000 + outputLatencyMs;
}
