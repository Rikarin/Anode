import { type DecodedFrame, JitterBuffer, type JitterOptions, type JitterStats } from './jitter-buffer';

/** Where decoded frames go: the AudioWorklet player in a browser, the direct one in tests. */
export interface ListenInPlayer {
  push(frame: DecodedFrame): void;
  /** The newest stats (the worklet sends them about every 40 ms). */
  stats(): JitterStats | null;
  /** The device side's latency after the worklet (`outputLatency` + `baseLatency`), in ms. */
  outputLatencyMs(): number;
  close(): void;
}

/** The worklet processor's name. */
export const ProcessorName = 'anode-listen-in';

/**
 * The AudioWorklet's source: the {@link JitterBuffer} class, embedded from its own (compiled) source so the worklet
 * and the unit-tested code are one, and a processor that pulls a render quantum from it and posts its stats every 16
 * quanta. Built at run time and loaded as a Blob URL, so the build needs no separate worklet entry point.
 */
export function workletSource(): string {
  return `const JitterBuffer = (${JitterBuffer.toString()});
class ListenInProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    this.buffer = new JitterBuffer(options.processorOptions || {});
    this.quanta = 0;
    this.port.onmessage = event => {
      const message = event.data;
      if (message.type === 'frame') this.buffer.push(message.frame);
      else if (message.type === 'resync') this.buffer.resync();
    };
  }
  process(inputs, outputs) {
    const out = outputs[0];
    const left = out[0];
    if (left) {
      const right = out[1] || new Float32Array(left.length);
      this.buffer.pull(left, right);
    }
    if (++this.quanta % 16 === 0) this.port.postMessage({ type: 'stats', stats: this.buffer.stats() });
    return true;
  }
}
registerProcessor('${ProcessorName}', ListenInProcessor);
`;
}

/**
 * Plays through an AudioWorklet (ADR-0013): an `AudioContext` at 48 kHz (the stream's rate, so no resampling here),
 * the processor loaded from {@link workletSource}, frames posted to it (their sample buffers transferred). The context
 * must be created in a user gesture (the listen-in toggle's click): {@link createContext} does that first.
 */
export class WorkletPlayer implements ListenInPlayer {
  private latest: JitterStats | null = null;

  private constructor(
    private readonly context: AudioContext,
    private readonly node: AudioWorkletNode,
    private readonly url: string
  ) {
    node.port.onmessage = (event: MessageEvent<{ type: string; stats: JitterStats }>) => {
      if (event.data.type === 'stats') {
        this.latest = event.data.stats;
      }
    };
  }

  /** The context, made and resumed synchronously in the gesture that turned listen-in on. */
  static createContext(): AudioContext {
    const context = new AudioContext({ sampleRate: 48000, latencyHint: 'interactive' });
    void context.resume();
    return context;
  }

  static async create(context: AudioContext, options: Partial<JitterOptions>): Promise<WorkletPlayer> {
    const url = URL.createObjectURL(new Blob([workletSource()], { type: 'text/javascript' }));
    await context.audioWorklet.addModule(url);
    const node = new AudioWorkletNode(context, ProcessorName, {
      numberOfInputs: 0,
      numberOfOutputs: 1,
      outputChannelCount: [2],
      processorOptions: options
    });
    node.connect(context.destination);
    return new WorkletPlayer(context, node, url);
  }

  push(frame: DecodedFrame): void {
    this.node.port.postMessage({ type: 'frame', frame }, [frame.samples.buffer]);
  }

  stats(): JitterStats | null {
    return this.latest;
  }

  outputLatencyMs(): number {
    return ((this.context.outputLatency || 0) + (this.context.baseLatency || 0)) * 1000;
  }

  close(): void {
    this.node.disconnect();
    this.node.port.onmessage = null;
    URL.revokeObjectURL(this.url);
    void this.context.close();
  }
}

/** A player without audio (tests, and the stats without a device): the jitter buffer, pulled by {@link render}. */
export class DirectPlayer implements ListenInPlayer {
  readonly buffer: JitterBuffer;
  /** What it played, interleaved, when asked to keep it. */
  readonly played: number[] = [];

  constructor(
    options: Partial<JitterOptions>,
    private readonly keep = false
  ) {
    this.buffer = new JitterBuffer(options);
  }

  push(frame: DecodedFrame): void {
    this.buffer.push(frame);
  }

  /** Plays `quanta` render quanta of 128 frames. */
  render(quanta: number): void {
    const left = new Float32Array(128);
    const right = new Float32Array(128);
    for (let q = 0; q < quanta; q++) {
      this.buffer.pull(left, right);
      if (this.keep) {
        for (let i = 0; i < 128; i++) {
          this.played.push(left[i] ?? 0, right[i] ?? 0);
        }
      }
    }
  }

  stats(): JitterStats {
    return this.buffer.stats();
  }

  outputLatencyMs(): number {
    return 0;
  }

  close(): void {
    this.buffer.resync();
  }
}
