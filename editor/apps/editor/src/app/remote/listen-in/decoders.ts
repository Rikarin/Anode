import type { ListenInFormat } from '@anode/api-client';

/**
 * A listen-in packet decoder: Opus (WebCodecs' `AudioDecoder`, else the `opus-decoder` WebAssembly build, loaded
 * lazily) or, for the fake Server's stream, 16-bit PCM. `decode` hands back interleaved stereo at 48 kHz through
 * `output`, in order, tagged with the caller's `tag` (the frame it came from).
 */
export interface ListenInDecoder {
  readonly name: string;
  decode(packet: Uint8Array, tag: number): void;
  close(): void;
}

export type DecodedOutput = (tag: number, samples: Float32Array) => void;

/** The codec of a stream: the Server's is Opus; the fake Server says `pcm16` beside its format. */
export type ListenInCodec = 'opus' | 'pcm16';

export function codecOf(format: ListenInFormat & { codec?: string }): ListenInCodec {
  return format.codec === 'pcm16' ? 'pcm16' : 'opus';
}

/** Interleaves planar channels (a mono stream is played on both sides). */
export function interleave(channels: readonly Float32Array[], frames: number): Float32Array {
  const out = new Float32Array(frames * 2);
  const left = channels[0] ?? new Float32Array(frames);
  const right = channels[1] ?? left;
  for (let i = 0; i < frames; i++) {
    out[i * 2] = left[i] ?? 0;
    out[i * 2 + 1] = right[i] ?? 0;
  }
  return out;
}

/** The fake Server's test codec: 16-bit little-endian interleaved PCM (fake-phase10.ts, `encodeFakeFrame`). */
export class Pcm16Decoder implements ListenInDecoder {
  readonly name = 'PCM (test)';

  constructor(
    private readonly output: DecodedOutput,
    private readonly channels = 2
  ) {}

  decode(packet: Uint8Array, tag: number): void {
    const view = new DataView(packet.buffer, packet.byteOffset, packet.byteLength);
    const count = Math.floor(packet.byteLength / 2);
    const frames = Math.floor(count / this.channels);
    const planar = Array.from({ length: this.channels }, () => new Float32Array(frames));
    for (let i = 0; i < frames; i++) {
      for (let c = 0; c < this.channels; c++) {
        const plane = planar[c] as Float32Array;
        plane[i] = view.getInt16((i * this.channels + c) * 2, true) / 32768;
      }
    }
    this.output(tag, interleave(planar, frames));
  }

  close(): void {
    // nothing held
  }
}

/** Opus through WebCodecs (Chrome, Edge, Safari 16.4+ for Opus); each packet a chunk stamped by its frame. */
export class WebCodecsOpusDecoder implements ListenInDecoder {
  readonly name = 'Opus (WebCodecs)';
  private readonly decoder: AudioDecoder;
  private readonly frameMicros: number;

  constructor(format: ListenInFormat, output: DecodedOutput, onError: (error: Error) => void) {
    this.frameMicros = (format.frameSamples / format.sampleRate) * 1_000_000;
    this.decoder = new AudioDecoder({
      output: data => {
        try {
          const frames = data.numberOfFrames;
          const planes = Array.from({ length: Math.min(2, data.numberOfChannels) }, (_, c) => {
            const plane = new Float32Array(frames);
            data.copyTo(plane, { planeIndex: c, format: 'f32-planar' });
            return plane;
          });
          output(Math.round(data.timestamp / this.frameMicros), interleave(planes, frames));
        } finally {
          data.close();
        }
      },
      error: error => onError(error instanceof Error ? error : new Error(String(error)))
    });
    this.decoder.configure(opusConfig(format));
  }

  decode(packet: Uint8Array, tag: number): void {
    this.decoder.decode(
      new EncodedAudioChunk({ type: 'key', timestamp: Math.round(tag * this.frameMicros), data: packet })
    );
  }

  close(): void {
    if (this.decoder.state !== 'closed') {
      this.decoder.close();
    }
  }
}

function opusConfig(format: ListenInFormat): AudioDecoderConfig {
  return { codec: 'opus', sampleRate: format.sampleRate, numberOfChannels: format.channels };
}

/** Whether this browser's WebCodecs decodes the stream's Opus. */
export async function webCodecsOpus(format: ListenInFormat): Promise<boolean> {
  if (typeof AudioDecoder === 'undefined' || typeof EncodedAudioChunk === 'undefined') {
    return false;
  }
  try {
    return (await AudioDecoder.isConfigSupported(opusConfig(format))).supported === true;
  } catch {
    return false;
  }
}

/**
 * Opus through `opus-decoder` (libopus as WebAssembly, MIT/BSD; ADR-0008), for browsers whose WebCodecs lacks Opus
 * (Firefox before 130, older Safari): imported on first use, so it is not in any bundle that does not need it.
 */
export async function wasmOpusDecoder(format: ListenInFormat, output: DecodedOutput): Promise<ListenInDecoder> {
  const { OpusDecoder } = await import('opus-decoder');
  const decoder = new OpusDecoder({ channels: format.channels, sampleRate: 48000 });
  await decoder.ready;
  return {
    name: 'Opus (WebAssembly)',
    decode(packet: Uint8Array, tag: number) {
      const decoded = decoder.decodeFrame(packet);
      if (decoded.samplesDecoded > 0) {
        output(tag, interleave(decoded.channelData, decoded.samplesDecoded));
      }
    },
    close() {
      decoder.free();
    }
  };
}

/** The decoder for a stream: PCM for the fake Server's, WebCodecs' Opus where it can, else the WebAssembly one. */
export async function createDecoder(
  format: ListenInFormat & { codec?: string },
  output: DecodedOutput,
  onError: (error: Error) => void
): Promise<ListenInDecoder> {
  if (codecOf(format) === 'pcm16') {
    return new Pcm16Decoder(output, format.channels);
  }
  if (await webCodecsOpus(format)) {
    return new WebCodecsOpusDecoder(format, output, onError);
  }
  return wasmOpusDecoder(format, output);
}
