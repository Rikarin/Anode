import { type ChildProcessWithoutNullStreams, spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { createInterface } from 'node:readline';

/*
 * A typed driver for `anode-midiprobe` (workstream A, engine/tools/Anode.MidiProbe; its README is the protocol), the
 * test tool that stands in for hardware in the Phase 8 golden path: it opens Anode's virtual MIDI ports, sends MIDI
 * (SysEx included), records what arrives with host timestamps, plays a Mackie Control or HUI, acts as a MIDI Clock and
 * MTC master, analyses the clock and MTC Anode sends, and joins the Ableton Link session.
 *
 * `anode-midiprobe serve`: one JSON command a line on stdin (`{"id", "cmd", …}`), one JSON object a line on stdout
 * (the first `{"event": "ready"}`; each answer carries the command's `id` and `ok`, or `error`). Times are host
 * nanoseconds on the clock Anode's host stamps MIDI with; bytes go as `hex`.
 */

/** A message the probe received: its host time (ns) and bytes. */
export interface ProbeMessage {
  timeNs: number;
  bytes: number[];
}

/** `analyzeClock`: the clocks received and their spacing. */
export interface ClockAnalysis {
  clocks: number;
  meanMs: number;
  medianMs: number;
  bpm: number;
  maxDeviationMs: number;
  p99DeviationMs: number;
  /** Intervals more than 0.5 ms off the median (a stalled device thread shows as one). */
  outliers: number;
  stdDevMs: number;
  minMs: number;
  maxMs: number;
  transport: { t: number; kind: 'start' | 'continue' | 'stop' | 'songPosition'; sixteenths?: number }[];
}

/** `analyzeMtc`: the timecode received, as positions at host times. */
export interface MtcAnalysis {
  rate: string;
  count: number;
  positions: { t: number; seconds: number; label: string; fullFrame: boolean }[];
  last: { t: number; seconds: number; label: string; fullFrame: boolean } | null;
  maxDriftFrames: number;
}

/** `mcuState`: what Anode sent a Mackie Control. */
export interface McuState {
  faders: number[];
  master: number;
  lines: string[];
  strips: { top: string; bottom: string }[];
  leds: Record<string, number>;
  timecode: string;
  assignment: string;
  meters: number[];
}

/** The probe's Link session (`tempo`, `peers`, `playing`, `now`, and for `linkState` the beats at the times asked). */
export interface ProbeLinkState {
  tempo: number;
  peers: number;
  playing: boolean;
  now: number;
  beats?: { at: number; beat: number; phase: number }[];
}

type Answer = { id: number; ok: boolean; error?: string } & Record<string, unknown>;

/** Where the probe is: `ANODE_MIDIPROBE`, else the engine's Release build (e2e global setup builds it). */
export function probePath(): string {
  return (
    process.env['ANODE_MIDIPROBE'] ??
    resolve(__dirname, '../../../../../engine/tools/Anode.MidiProbe/bin/Release/net10.0/anode-midiprobe')
  );
}

export function probeAvailable(): boolean {
  return existsSync(probePath());
}

function hex(bytes: readonly number[]): string {
  return bytes.map(b => b.toString(16).padStart(2, '0')).join('');
}

function bytesOf(text: string): number[] {
  const out: number[] = [];
  for (let i = 0; i + 1 < text.length; i += 2) {
    out.push(parseInt(text.slice(i, i + 2), 16));
  }
  return out;
}

export class MidiProbe {
  private readonly process: ChildProcessWithoutNullStreams;
  private nextId = 1;
  private readonly pending = new Map<number, { resolve: (answer: Answer) => void; reject: (error: Error) => void }>();
  private readonly stderr: string[] = [];
  private readonly ready: Promise<void>;

  private constructor(path: string) {
    this.process = spawn(path, ['serve'], { stdio: ['pipe', 'pipe', 'pipe'] });
    let started: () => void = () => undefined;
    this.ready = new Promise(r => (started = r));
    createInterface({ input: this.process.stdout }).on('line', line => this.receive(line, started));
    createInterface({ input: this.process.stderr }).on('line', line => this.stderr.push(line));
    this.process.on('exit', code => {
      for (const { reject } of this.pending.values()) {
        reject(new Error(`anode-midiprobe exited (${code}): ${this.stderr.slice(-5).join(' / ')}`));
      }
      this.pending.clear();
    });
  }

  /**
   * Starts the probe and opens Anode's virtual ports from outside: it records "Anode Out" (`anode:out`) and sends to
   * "Anode In" (`anode:in`), waiting for them to appear (the app's host publishes them).
   */
  static async start(path = probePath()): Promise<MidiProbe> {
    const probe = new MidiProbe(path);
    await probe.ready;
    await probe.request('open', { in: 'anode:out', out: 'anode:in', timeoutMs: 30_000 }, 40_000);
    return probe;
  }

  async stop(): Promise<void> {
    if (this.process.exitCode === null) {
      await this.request('quit', {}, 5_000).catch(() => undefined);
      this.process.kill();
    }
  }

  /** Sends one command and waits for its answer (`ok: false` throws). */
  async request(cmd: string, fields: Record<string, unknown> = {}, timeoutMs = 15_000): Promise<Answer> {
    const id = this.nextId++;
    const answer = new Promise<Answer>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`anode-midiprobe: no answer to ${cmd} in ${timeoutMs} ms`));
      }, timeoutMs);
      this.pending.set(id, {
        resolve: a => {
          clearTimeout(timer);
          resolve(a);
        },
        reject: e => {
          clearTimeout(timer);
          reject(e);
        }
      });
    });
    this.process.stdin.write(`${JSON.stringify({ id, cmd, ...fields })}\n`);
    const result = await answer;
    if (!result.ok) {
      throw new Error(`anode-midiprobe ${cmd}: ${result.error ?? 'failed'}`);
    }
    return result;
  }

  private receive(line: string, started: () => void): void {
    let message: Record<string, unknown>;
    try {
      message = JSON.parse(line) as Record<string, unknown>;
    } catch {
      return;
    }
    if (message['event'] === 'ready') {
      started();
    } else if (typeof message['id'] === 'number') {
      this.pending.get(message['id'])?.resolve(message as Answer);
      this.pending.delete(message['id']);
    }
  }

  /** The probe's host clock now (ns). */
  async now(): Promise<number> {
    return Number((await this.request('now'))['now']);
  }

  // ---- sending

  /** One or more whole messages, now (or `inMs` from now). */
  send(bytes: readonly number[], inMs?: number): Promise<Answer> {
    return this.request('send', { hex: hex(bytes), ...(inMs === undefined ? {} : { inMs }) });
  }

  cc(controller: number, value: number, channel = 1): Promise<Answer> {
    return this.send([0xb0 | (channel - 1), controller, value]);
  }

  noteOn(key: number, velocity = 100, channel = 1): Promise<Answer> {
    return this.send([0x90 | (channel - 1), key, velocity]);
  }

  noteOff(key: number, channel = 1): Promise<Answer> {
    return this.send([0x80 | (channel - 1), key, 0]);
  }

  /** A button: note on, then note off. */
  async press(key: number, channel = 1): Promise<void> {
    await this.noteOn(key, 127, channel);
    await this.noteOff(key, channel);
  }

  /** A knob turned by hand: a 7-bit controller from one value to another, `stepMs` apart, timestamped exactly. */
  async sweep(controller: number, from: number, to: number, stepMs = 20, channel = 1): Promise<void> {
    const step = from <= to ? 1 : -1;
    const events: { t: number; hex: string }[] = [];
    for (let v = from, i = 0; v !== to + step; v += step, i++) {
      events.push({ t: i * stepMs, hex: hex([0xb0 | (channel - 1), controller, v]) });
    }
    await this.request('script', { events }, 15_000 + events.length * stepMs);
  }

  // ---- recording

  /** Forgets what was received so far. */
  async record(): Promise<void> {
    await this.request('clear');
  }

  /** What was received (since `clear`), in arrival order; `since` (ns) and `prefix` (hex) narrow it. */
  async recorded(options: { since?: number; prefix?: string } = {}): Promise<ProbeMessage[]> {
    const answer = await this.request('received', options);
    return ((answer['messages'] as { t: number; hex: string }[] | undefined) ?? []).map(m => ({
      timeNs: m.t,
      bytes: bytesOf(m.hex)
    }));
  }

  /** Waits for `count` messages starting with `prefix` (hex) to have arrived. */
  async wait(prefix: string, count = 1, timeoutMs = 10_000, since?: number): Promise<ProbeMessage[]> {
    const answer = await this.request(
      'wait',
      { prefix, count, timeoutMs, ...(since === undefined ? {} : { since }) },
      timeoutMs + 5_000
    );
    return ((answer['messages'] as { t: number; hex: string }[] | undefined) ?? []).map(m => ({
      timeNs: m.t,
      bytes: bytesOf(m.hex)
    }));
  }

  // ---- analyses

  async analyzeClock(since?: number): Promise<ClockAnalysis> {
    return (await this.request('analyzeClock', since === undefined ? {} : { since })) as unknown as ClockAnalysis;
  }

  async analyzeMtc(rate: string, since?: number): Promise<MtcAnalysis> {
    return (await this.request('analyzeMtc', {
      rate,
      ...(since === undefined ? {} : { since })
    })) as unknown as MtcAnalysis;
  }

  async mcuState(since?: number): Promise<McuState> {
    return (await this.request('mcuState', since === undefined ? {} : { since })) as unknown as McuState;
  }

  // ---- control surfaces

  /** A Mackie Control fader (strip 0–7) to `value` (0…1), touched and released. */
  async mcuFader(strip: number, value: number): Promise<void> {
    await this.request('mcuFader', { strip, value });
  }

  /** A Mackie Control button by name (README), clicked. */
  async mcuButton(button: string, strip?: number): Promise<void> {
    await this.request('mcuButton', { button, ...(strip === undefined ? {} : { strip }) });
  }

  /** A HUI fader (strip 0–7) to `value` (0…1), with touch. */
  async huiFader(strip: number, value: number): Promise<void> {
    await this.request('huiFader', { strip, value });
  }

  /** Answers Anode's HUI pings. */
  async huiPing(on = true): Promise<void> {
    await this.request('huiPing', { on });
  }

  // ---- MIDI Clock and MTC master

  /** Clock at `bpm` with Start; the answer's `start` is the host time of the first clock after Start. */
  async clockStart(bpm: number): Promise<number> {
    return Number((await this.request('clock', { bpm, start: true }))['start']);
  }

  async clockStop(): Promise<void> {
    await this.request('clockStop');
  }

  /** MTC from `from` (hh:mm:ss:ff): a full frame, then quarter frames. The answer's `start` is when it is `from`. */
  async mtcStart(from: string, rate = 'fps25'): Promise<number> {
    return Number((await this.request('mtc', { from, rate }))['start']);
  }

  async mtcStop(): Promise<void> {
    await this.request('mtcStop');
  }

  // ---- Ableton Link

  /** Joins, waits for Anode in the session, then sets the session's tempo. */
  async linkJoin(bpm: number): Promise<ProbeLinkState> {
    return (await this.request('linkJoin', { bpm, peers: 1, timeoutMs: 15_000 }, 25_000)) as unknown as ProbeLinkState;
  }

  async linkTempo(bpm: number): Promise<void> {
    await this.request('linkTempo', { bpm });
  }

  async linkState(quantum = 4, at?: number[]): Promise<ProbeLinkState> {
    return (await this.request('linkState', { quantum, ...(at ? { at } : {}) })) as unknown as ProbeLinkState;
  }

  /** How far the MIDI Clock Anode sends (following Link) is from the session's beat. */
  async linkClockError(since?: number, quantum = 4): Promise<{ clocks: number; maxAbsMs: number; meanMs: number }> {
    return (await this.request('linkClockError', {
      quantum,
      ...(since === undefined ? {} : { since })
    })) as unknown as { clocks: number; maxAbsMs: number; meanMs: number };
  }

  async linkLeave(): Promise<void> {
    await this.request('linkLeave');
  }
}

/** A Song Position Pointer's position in sixteenths, or null for another message. */
export function songPosition(message: ProbeMessage): number | null {
  const [status, lsb = 0, msb = 0] = message.bytes;
  return status === 0xf2 ? lsb | (msb << 7) : null;
}
