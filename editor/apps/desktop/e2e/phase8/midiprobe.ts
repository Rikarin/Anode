import { type ChildProcessWithoutNullStreams, spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { createInterface } from 'node:readline';

/*
 * A typed driver for `anode-midiprobe` (docs/09-phase-8.md, workstream A: `tools/Anode.MidiProbe`), the test tool that
 * stands in for hardware in the Phase 8 golden path: it opens Anode's virtual MIDI ports, sends scripted MIDI (SysEx
 * included), records what arrives with host timestamps, acts as a MIDI Clock and MTC source, and joins an Ableton Link
 * session.
 *
 * TO ALIGN AT INTEGRATION with workstream A's README: the command set, field names and replies below are this
 * workstream's assumption, written before the tool existed. The protocol assumed: the probe runs with `--json`, reads
 * one JSON command per line on stdin, `{"id": 1, "cmd": "…", …}`, and writes JSON lines on stdout: a reply per command,
 * `{"id": 1, "ok": true, …}` or `{"id": 1, "ok": false, "error": "…"}`, and unsolicited events, `{"event": "…", …}`.
 * Times are host nanoseconds (`mach_absolute_time` scaled), Link times microseconds (Link's clock).
 */

/** A message the probe received, with the host time it arrived. */
export interface ProbeMessage {
  timeNs: number;
  bytes: number[];
}

/** The probe's view of its Link session. */
export interface ProbeLinkState {
  enabled: boolean;
  peers: number;
  tempo: number;
  playing: boolean;
  /** Beat and phase (at `quantum`) at `timeMicros` on Link's clock. */
  beat: number;
  phase: number;
  timeMicros: number;
}

/** What the probe's MTC decoder made of the quarter frames it received. */
export interface ProbeTimecode {
  /** Seconds of timecode, or null before a whole frame arrived. */
  seconds: number | null;
  frameRate: 'fps24' | 'fps25' | 'fps2997Drop' | 'fps30' | null;
}

type Reply = { id: number; ok: boolean; error?: string } & Record<string, unknown>;

/** Where the probe is: `ANODE_MIDIPROBE`, else the engine's published tool (the path A's build is assumed to use). */
export function probePath(): string {
  return (
    process.env['ANODE_MIDIPROBE'] ??
    resolve(__dirname, '../../../../../engine/artifacts/tools/anode-midiprobe/anode-midiprobe')
  );
}

/** Whether the probe is built here (the golden path skips its hardware steps otherwise). */
export function probeAvailable(): boolean {
  return existsSync(probePath());
}

export class MidiProbe {
  private readonly process: ChildProcessWithoutNullStreams;
  private nextId = 1;
  private readonly pending = new Map<number, { resolve: (reply: Reply) => void; reject: (error: Error) => void }>();
  /** Unsolicited events (`{"event": …}`), oldest first. */
  readonly events: Record<string, unknown>[] = [];
  private readonly stderr: string[] = [];

  private constructor(path: string) {
    this.process = spawn(path, ['--json'], { stdio: ['pipe', 'pipe', 'pipe'] });
    createInterface({ input: this.process.stdout }).on('line', line => this.receive(line));
    createInterface({ input: this.process.stderr }).on('line', line => this.stderr.push(line));
    this.process.on('exit', code => {
      for (const { reject } of this.pending.values()) {
        reject(new Error(`anode-midiprobe exited (${code}): ${this.stderr.slice(-5).join(' / ')}`));
      }
      this.pending.clear();
    });
  }

  /**
   * Starts the probe on a port pair: by default Anode's virtual ports seen from outside, so the probe sends into
   * "Anode In" (Anode's input `anode:in`) and listens on "Anode Out" (`anode:out`).
   */
  static async start(options: { send?: string; receive?: string; path?: string } = {}): Promise<MidiProbe> {
    const probe = new MidiProbe(options.path ?? probePath());
    await probe.request('open', { send: options.send ?? 'Anode In', receive: options.receive ?? 'Anode Out' });
    return probe;
  }

  async stop(): Promise<void> {
    if (this.process.exitCode === null) {
      await this.request('quit').catch(() => undefined);
      this.process.kill();
    }
  }

  /** Sends one command and waits for its reply. */
  async request(cmd: string, fields: Record<string, unknown> = {}, timeoutMs = 10_000): Promise<Reply> {
    const id = this.nextId++;
    const reply = new Promise<Reply>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`anode-midiprobe: no reply to ${cmd} in ${timeoutMs} ms`));
      }, timeoutMs);
      this.pending.set(id, {
        resolve: r => {
          clearTimeout(timer);
          resolve(r);
        },
        reject: e => {
          clearTimeout(timer);
          reject(e);
        }
      });
    });
    this.process.stdin.write(`${JSON.stringify({ id, cmd, ...fields })}\n`);
    const answer = await reply;
    if (!answer.ok) {
      throw new Error(`anode-midiprobe ${cmd}: ${answer.error ?? 'failed'}`);
    }
    return answer;
  }

  private receive(line: string): void {
    let message: Record<string, unknown>;
    try {
      message = JSON.parse(line) as Record<string, unknown>;
    } catch {
      return; // a log line
    }
    if (typeof message['id'] === 'number') {
      this.pending.get(message['id'])?.resolve(message as Reply);
      this.pending.delete(message['id']);
    } else if (typeof message['event'] === 'string') {
      this.events.push(message);
    }
  }

  // ---- sending (`send`: whole messages, SysEx included; `atNs` schedules at a host time)

  send(bytes: readonly number[], atNs?: number): Promise<Reply> {
    return this.request('send', { bytes, ...(atNs === undefined ? {} : { atNs }) });
  }

  /** A control change on a channel (1…16). */
  cc(controller: number, value: number, channel = 1): Promise<Reply> {
    return this.send([0xb0 | (channel - 1), controller, value]);
  }

  noteOn(key: number, velocity = 100, channel = 1): Promise<Reply> {
    return this.send([0x90 | (channel - 1), key, velocity]);
  }

  noteOff(key: number, channel = 1): Promise<Reply> {
    return this.send([0x80 | (channel - 1), key, 0]);
  }

  /** A key pressed and released (a button). */
  async press(key: number, channel = 1): Promise<void> {
    await this.noteOn(key, 127, channel);
    await this.noteOff(key, channel);
  }

  /** A 14-bit pitch bend (0…16383): a Mackie Control fader on `channel` (1…8). */
  pitchBend(value: number, channel = 1): Promise<Reply> {
    const v = Math.max(0, Math.min(16_383, Math.round(value)));
    return this.send([0xe0 | (channel - 1), v & 0x7f, v >> 7]);
  }

  /** A sweep of a 7-bit controller from one value to another, `stepMs` apart (a knob turned by hand). */
  async sweep(controller: number, from: number, to: number, stepMs = 20, channel = 1): Promise<void> {
    const step = from <= to ? 1 : -1;
    for (let v = from; v !== to + step; v += step) {
      await this.cc(controller, v, channel);
      await new Promise(r => setTimeout(r, stepMs));
    }
  }

  // ---- recording

  /** Starts recording what arrives on the receive port (clears what was recorded). */
  record(): Promise<Reply> {
    return this.request('record', { on: true });
  }

  /** Everything recorded since `record`, in arrival order. */
  async recorded(): Promise<ProbeMessage[]> {
    const reply = await this.request('recorded');
    return (reply['messages'] as ProbeMessage[] | undefined) ?? [];
  }

  /** Waits until a recorded message matches, polling. */
  async waitFor(match: (m: ProbeMessage) => boolean, timeoutMs = 10_000): Promise<ProbeMessage> {
    const until = Date.now() + timeoutMs;
    while (Date.now() < until) {
      const found = (await this.recorded()).find(match);
      if (found) {
        return found;
      }
      await new Promise(r => setTimeout(r, 50));
    }
    throw new Error('anode-midiprobe: the expected message did not arrive');
  }

  // ---- sync source and sink

  /** Sends MIDI Clock at a tempo (24 ppq), with Start when `start` (Continue when false and already running). */
  clockStart(bpm: number, start = true): Promise<Reply> {
    return this.request('clock', { bpm, start });
  }

  /** Stops the clock (sends Stop). */
  clockStop(): Promise<Reply> {
    return this.request('clockStop');
  }

  /** Sends MTC quarter frames from a timecode (`hh:mm:ss:ff`), after a full frame. */
  mtcStart(from: string, frameRate: ProbeTimecode['frameRate'] = 'fps25'): Promise<Reply> {
    return this.request('mtc', { from, frameRate });
  }

  mtcStop(): Promise<Reply> {
    return this.request('mtcStop');
  }

  /** The timecode the probe decodes from the MTC it receives. */
  async timecode(): Promise<ProbeTimecode> {
    const reply = await this.request('timecode');
    return {
      seconds: (reply['seconds'] as number | null) ?? null,
      frameRate: (reply['frameRate'] as ProbeTimecode['frameRate']) ?? null
    };
  }

  // ---- Ableton Link

  /** Joins the Link session at a tempo (and the start/stop sync setting). */
  linkJoin(bpm: number, startStopSync = false): Promise<Reply> {
    return this.request('link', { enabled: true, bpm, startStopSync });
  }

  linkLeave(): Promise<Reply> {
    return this.request('link', { enabled: false });
  }

  linkTempo(bpm: number): Promise<Reply> {
    return this.request('linkTempo', { bpm });
  }

  /** The probe's session state now, or at a Link time (microseconds), for a quantum (beats). */
  async linkState(quantum = 4, atMicros?: number): Promise<ProbeLinkState> {
    const reply = await this.request('linkState', { quantum, ...(atMicros === undefined ? {} : { atMicros }) });
    return reply as unknown as ProbeLinkState;
  }
}

// ---- recorded streams, as the exit criterion measures them

/** MIDI Clock ticks (0xF8) among recorded messages. */
export function clockTicks(messages: readonly ProbeMessage[]): number[] {
  return messages.filter(m => m.bytes[0] === 0xf8).map(m => m.timeNs);
}

/** The mean interval between ticks and the worst deviation from it, in milliseconds. */
export function tickJitter(times: readonly number[]): { meanMs: number; jitterMs: number } {
  const gaps = times.slice(1).map((t, i) => (t - (times[i] ?? t)) / 1e6);
  const meanMs = gaps.reduce((a, b) => a + b, 0) / Math.max(1, gaps.length);
  const jitterMs = Math.max(0, ...gaps.map(g => Math.abs(g - meanMs)));
  return { meanMs, jitterMs };
}

/** A Song Position Pointer's position, in sixteenths, or null for another message. */
export function songPosition(message: ProbeMessage): number | null {
  const [status, lsb = 0, msb = 0] = message.bytes;
  return status === 0xf2 ? lsb | (msb << 7) : null;
}

/** Mackie Control's scribble strip SysEx (F0 00 00 66 14 12 offset text… F7): the offset and the text. */
export function scribbleStrip(message: ProbeMessage): { offset: number; text: string } | null {
  const b = message.bytes;
  if (b[0] !== 0xf0 || b[1] !== 0 || b[2] !== 0 || b[3] !== 0x66 || b[5] !== 0x12) {
    return null;
  }
  return { offset: b[6] ?? 0, text: String.fromCharCode(...b.slice(7, -1)) };
}
