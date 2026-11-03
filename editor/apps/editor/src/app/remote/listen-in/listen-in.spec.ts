import { ListenIn, readListenInFrame } from '@anode/api-client';
import { encodeFakeFrame, FakeToneHz, FakeToneLevel } from '../../core/fake/fake-phase10';
import { FakeAnodeServer } from '../../core/fake/fake-server';
import { codecOf, interleave, Pcm16Decoder } from './decoders';
import { type DecodedFrame, JitterBuffer } from './jitter-buffer';
import { behindMs, ListenInClient, parseFormat } from './listen-in-client';
import { DirectPlayer, ProcessorName, workletSource } from './player';

const FrameMs = 20;

function frame(sequence: number, arrivalMs = sequence * FrameMs, flags = 1, value = 0.5): DecodedFrame {
  return {
    sequence,
    transportSample: sequence * 960,
    flags,
    samples: new Float32Array(960 * 2).fill(value),
    arrivalMs
  };
}

/** Pulls `ms` of audio (render quanta of 128 at 48 kHz). */
function pull(buffer: JitterBuffer, ms: number): Float32Array {
  const quanta = Math.round((ms * 48) / 128);
  const out = new Float32Array(quanta * 128);
  const left = new Float32Array(128);
  const right = new Float32Array(128);
  for (let q = 0; q < quanta; q++) {
    buffer.pull(left, right);
    out.set(left, q * 128);
  }
  return out;
}

describe('listen-in frames', () => {
  it('reads the header and packet the fake Server writes (ListenIn.WriteHeader’s layout)', () => {
    const samples = new Float32Array([0.5, -0.5, 1, -1]);
    const message = encodeFakeFrame(7, 123_456_789_012, ListenIn.flagPlaying | ListenIn.flagDiscontinuity, samples);
    const read = readListenInFrame(message);
    expect(read).toMatchObject({ sequence: 7, transportSample: 123_456_789_012, flags: 3 });
    expect(read?.packet.byteLength).toBe(8);
    expect(readListenInFrame(message.slice(0, 10))).toBeNull();
    expect(readListenInFrame(message.slice(0, 20))).toBeNull();
  });

  it('parses the format message, and knows the fake’s PCM from Opus', () => {
    const format = parseFormat(
      '{"sampleRate":48000,"channels":2,"frameSamples":960,"bitrate":128000,"projectSampleRate":44100}'
    );
    expect(format?.projectSampleRate).toBe(44_100);
    expect(codecOf(format ?? ({} as never))).toBe('opus');
    expect(codecOf({ ...(format ?? ({} as never)), codec: 'pcm16' })).toBe('pcm16');
    expect(parseFormat('{"sampleRate":0}')).toBeNull();
    expect(parseFormat('not json')).toBeNull();
  });

  it('decodes the test PCM to interleaved stereo', () => {
    const out: Float32Array[] = [];
    const decoder = new Pcm16Decoder((_, samples) => out.push(samples));
    const message = encodeFakeFrame(0, 0, 1, new Float32Array([0.5, -0.25, 0, 1]));
    decoder.decode(readListenInFrame(message)?.packet ?? new Uint8Array(), 0);
    expect([...(out[0] ?? [])].map(v => Math.round(v * 1000) / 1000)).toEqual([0.5, -0.25, 0, 1]);
    expect([...interleave([new Float32Array([1, 2])], 2)]).toEqual([1, 1, 2, 2]);
  });
});

describe('the jitter buffer', () => {
  it('primes to its target, then plays the frames in sequence order', () => {
    const buffer = new JitterBuffer();
    expect(buffer.targetMs()).toBe(60);
    buffer.push(frame(1, 20, 1, 0.2));
    buffer.push(frame(0, 21, 1, 0.1));
    expect(buffer.stats().priming).toBe(true);
    buffer.push(frame(2, 40, 1, 0.3));
    expect(buffer.stats()).toMatchObject({ priming: false, bufferedMs: 60 });
    const out = pull(buffer, 40);
    expect(out[0]).toBeCloseTo(0.1);
    expect(out[960 + 10]).toBeCloseTo(0.2);
    expect(buffer.stats().playingTransportSample).toBe(1920);
  });

  it('reports the transport sample of what plays now, at the project’s rate', () => {
    const buffer = new JitterBuffer({ projectSampleRate: 96_000 });
    [0, 1, 2].forEach(s => buffer.push({ ...frame(s), transportSample: s * 1920 }));
    pull(buffer, 10);
    // 10 ms in: 480 samples at 48 kHz, 960 at 96 kHz (quantum-rounded: 4 × 128 = 512 → 1024).
    expect(buffer.playingTransportSample()).toBe(1024);
    expect(behindMs(1024 + 9600, buffer.playingTransportSample(), 96_000, 5)).toBeCloseTo(105);
    expect(behindMs(0, null, 48_000, 0)).toBeNull();
  });

  it('drops late and duplicate frames, and plays a lost one as silence', () => {
    const buffer = new JitterBuffer();
    [0, 1, 3, 4].forEach(s => buffer.push(frame(s, s * FrameMs, 1, 1)));
    buffer.push(frame(1, 90, 1, 1));
    expect(buffer.stats().dropped).toBe(1);
    const out = pull(buffer, 80);
    // Frame 2 never came: 40–60 ms is silent.
    expect(out[45 * 48]).toBe(0);
    expect(out[65 * 48]).toBe(1);
    expect(buffer.stats().lost).toBe(1);
    buffer.push(frame(0, 100));
    expect(buffer.stats().dropped).toBe(2);
  });

  it('resyncs on a discontinuity: what was queued goes, and it primes again', () => {
    const buffer = new JitterBuffer();
    [0, 1, 2, 3].forEach(s => buffer.push(frame(s)));
    pull(buffer, 20);
    buffer.push(frame(50, 100, ListenIn.flagPlaying | ListenIn.flagDiscontinuity, 0.7));
    expect(buffer.stats()).toMatchObject({ priming: true, resyncs: 1, bufferedMs: 20 });
    buffer.push(frame(51, 120, 1, 0.7));
    buffer.push(frame(52, 140, 1, 0.7));
    expect(pull(buffer, 10)[0]).toBeCloseTo(0.7);
  });

  it('underruns into silence, primes again and buffers deeper', () => {
    const buffer = new JitterBuffer();
    [0, 1, 2].forEach(s => buffer.push(frame(s)));
    const out = pull(buffer, 80);
    expect(out[70 * 48]).toBe(0);
    expect(buffer.stats()).toMatchObject({ underruns: 1, priming: true });
    expect(buffer.targetMs()).toBe(80);
  });

  it('adapts its target to the inter-arrival jitter, within 60–120 ms', () => {
    // Plays in step with the frames (render quanta catch up with 20 ms a frame).
    const play = (arrival: (s: number) => number) => {
      const buffer = new JitterBuffer();
      const left = new Float32Array(128);
      const right = new Float32Array(128);
      let played = 0;
      for (let s = 0; s < 60; s++) {
        buffer.push(frame(s, arrival(s)));
        while (s >= 3 && played + 128 <= (s - 3) * 960) {
          buffer.pull(left, right);
          played += 128;
        }
      }
      return buffer;
    };
    const steady = play(s => s * FrameMs);
    expect(steady.targetMs()).toBe(60);
    expect(steady.stats().underruns).toBe(0);
    const jittery = play(s => s * FrameMs + (s % 2 ? 25 : 0));
    expect(jittery.stats().jitterMs).toBeGreaterThan(15);
    expect(jittery.targetMs()).toBe(120);
  });

  it('trims back to the target when the sender runs ahead', () => {
    const buffer = new JitterBuffer();
    for (let s = 0; s < 12; s++) {
      buffer.push(frame(s, s * FrameMs));
    }
    expect(buffer.bufferedMs()).toBeLessThanOrEqual(2 * buffer.targetMs() + FrameMs);
    expect(buffer.stats().dropped).toBeGreaterThan(0);
  });
});

describe('the AudioWorklet', () => {
  it('embeds the jitter buffer and registers a processor that plays it', () => {
    let registered: unknown = null;
    class Processor {
      readonly port = { onmessage: (_: unknown) => undefined, postMessage: vi.fn() };
    }
    // eslint-disable-next-line @typescript-eslint/no-implied-eval
    const load = new Function('AudioWorkletProcessor', 'registerProcessor', workletSource()) as (
      base: unknown,
      register: (name: string, ctor: unknown) => void
    ) => void;
    load(Processor, (name, ctor) => {
      expect(name).toBe(ProcessorName);
      registered = ctor;
    });
    expect(registered).not.toBeNull();
    const Ctor = registered as new (options: unknown) => {
      port: { onmessage: (e: unknown) => void; postMessage: ReturnType<typeof vi.fn> };
      process: (inputs: unknown, outputs: Float32Array[][]) => boolean;
    };
    const node = new Ctor({ processorOptions: { minTargetMs: 60 } });
    [0, 1, 2].forEach(s => node.port.onmessage({ data: { type: 'frame', frame: frame(s, s * FrameMs, 1, 0.25) } }));
    const outputs = [[new Float32Array(128), new Float32Array(128)]];
    for (let q = 0; q < 16; q++) {
      expect(node.process([], outputs)).toBe(true);
    }
    expect(outputs[0]?.[1]?.[0]).toBeCloseTo(0.25);
    expect(node.port.postMessage).toHaveBeenCalledWith(expect.objectContaining({ type: 'stats' }));
  });
});

describe('the listen-in client', () => {
  it('reads the fake Server’s stream: format, frames, the tone decoded into the jitter buffer', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Listen' });
    await server.controlTransport({ command: 'play' });
    let socket: ReturnType<FakeAnodeServer['openListenIn']> | null = null;
    const player = new DirectPlayer({}, true);
    let now = 0;
    const { createDecoder } = await import('./decoders');
    const client = new ListenInClient({
      open: () => (socket = server.openListenIn()),
      decoder: (format, output, onError) => createDecoder(format, output, onError),
      player: () => Promise.resolve(player),
      now: () => now
    });
    client.start();
    expect(client.status).toBe('connecting');
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(client.status).toBe('buffering');
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(client.decoderName).toBe('PCM (test)');
    for (let f = 0; f < 10; f++) {
      now += FrameMs;
      (socket as unknown as { pump(n: number): void }).pump(1);
    }
    expect(client.status).toBe('playing');
    expect(client.framesReceived).toBe(10);
    player.render(30);
    const left = player.played.filter((_, i) => i % 2 === 0);
    // A 1 kHz tone at −12 dBFS: its peak, and 48 samples a period (zero crossings every 24).
    const peak = Math.max(...left.map(Math.abs));
    expect(20 * Math.log10(peak)).toBeCloseTo(20 * Math.log10(FakeToneLevel), 0);
    let crossings = 0;
    for (let i = 1; i < left.length; i++) {
      if ((left[i - 1] ?? 0) < 0 && (left[i] ?? 0) >= 0) {
        crossings++;
      }
    }
    expect(crossings / (left.length / 48_000)).toBeCloseTo(FakeToneHz, -2);
    expect(player.stats().playingTransportSample).not.toBeNull();
    client.stop();
    expect(client.status).toBe('off');
    expect((await server.listenInState()).listeners).toBe(0);
  });

  it('fails with the Server’s reason when the socket cannot open', () => {
    const client = new ListenInClient({
      open: () => {
        throw new Error('Listen-in has 4 listeners already.');
      },
      decoder: () => Promise.reject(new Error('unused')),
      player: () => Promise.reject(new Error('unused')),
      now: () => 0
    });
    client.start();
    expect(client.status).toBe('failed');
    expect(client.error).toContain('4 listeners');
  });
});
