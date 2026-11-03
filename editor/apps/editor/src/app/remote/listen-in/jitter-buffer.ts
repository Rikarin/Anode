/** A decoded listen-in frame: what the jitter buffer holds (stereo, interleaved, at 48 kHz). */
export interface DecodedFrame {
  sequence: number;
  /** The project sample of the frame's first sample (at the project's rate), −1 when the transport was stopped. */
  transportSample: number;
  flags: number;
  /** Interleaved stereo samples. */
  samples: Float32Array;
  /** When it arrived (ms, the receiver's clock): the inter-arrival jitter is measured from it. */
  arrivalMs: number;
}

/** What the jitter buffer reports (the player shows it, the latency is computed from it). */
export interface JitterStats {
  /** Audio queued, in ms. */
  bufferedMs: number;
  /** The level it primes to and keeps, in ms. */
  targetMs: number;
  /** The measured inter-arrival jitter (RFC 3550's estimator), in ms. */
  jitterMs: number;
  underruns: number;
  /** Frames dropped as late, duplicated or trimmed. */
  dropped: number;
  /** Frames lost (a gap in the sequence played as silence). */
  lost: number;
  /** Resyncs (a discontinuity, a gap too large, an underrun). */
  resyncs: number;
  /** The transport sample of the next sample to play (at the project's rate), null when stopped or nothing plays. */
  playingTransportSample: number | null;
  priming: boolean;
}

/** The buffer's tuning (all in ms, but `frameSamples` and the rates). */
export interface JitterOptions {
  sampleRate: number;
  projectSampleRate: number;
  frameSamples: number;
  minTargetMs: number;
  maxTargetMs: number;
}

/**
 * The listen-in jitter buffer (docs/11-phase-10.md, "Listen-in"): Opus frames arrive every 20 ms over a WebSocket, a
 * little early or late; the buffer orders them by sequence, primes to a target level before it plays, and keeps that
 * level as frames come and go:
 * - **target**: 60–120 ms, adapting to the measured inter-arrival jitter J (RFC 3550: J += (|D| − J) / 16, D the
 *   difference between the arrival spacing and the 20 ms frame spacing): target = clamp(40 + 4 J, 60, …) + 20 per recent
 *   underrun (up to 60, wearing off as it plays), at most 120;
 * - **resync** (flush and prime again from the frame that comes): on a frame flagged discontinuity (the transport
 *   located, started or stopped), on a gap of more than 5 frames, and after an underrun;
 * - **loss**: a missing frame with later ones queued plays as silence (the sequence moves on); late and duplicate
 *   frames are dropped;
 * - **drift**: more than twice the target queued (the Server's clock ahead of the device's) drops the oldest frames back
 *   to the target.
 * Plain code with no imports, so the AudioWorklet (worklet-source.ts) embeds the class as its source; the unit tests
 * drive it directly.
 */
export class JitterBuffer {
  private readonly options: JitterOptions;
  private queue: DecodedFrame[] = [];
  /** The frame being played and the sample (interleaved pairs) within it. */
  private current: DecodedFrame | null = null;
  private offset = 0;
  private nextSequence = -1;
  /** Whether a frame played since the last resync (an older frame is late from then on). */
  private started = false;
  private primingState = true;
  private lastArrival = -1;
  private jitter = 0;
  private penaltyMs = 0;
  private counts = { underruns: 0, dropped: 0, lost: 0, resyncs: 0 };

  constructor(options: Partial<JitterOptions> = {}) {
    this.options = {
      sampleRate: options.sampleRate ?? 48000,
      projectSampleRate: options.projectSampleRate ?? 48000,
      frameSamples: options.frameSamples ?? 960,
      minTargetMs: options.minTargetMs ?? 60,
      maxTargetMs: options.maxTargetMs ?? 120
    };
  }

  /** The level to prime to and keep, in ms. */
  targetMs(): number {
    const { minTargetMs, maxTargetMs } = this.options;
    return Math.min(maxTargetMs, Math.max(minTargetMs, 40 + 4 * this.jitter) + this.penaltyMs);
  }

  /** Audio queued (the rest of the current frame and the queue), in ms. */
  bufferedMs(): number {
    let samples = this.current ? this.current.samples.length / 2 - this.offset : 0;
    for (const frame of this.queue) {
      samples += frame.samples.length / 2;
    }
    return (samples / this.options.sampleRate) * 1000;
  }

  /** Takes a frame as it arrives. */
  push(frame: DecodedFrame): void {
    const frameMs = (this.options.frameSamples / this.options.sampleRate) * 1000;
    if (this.lastArrival >= 0) {
      const d = Math.abs(frame.arrivalMs - this.lastArrival - frameMs);
      this.jitter += (Math.min(d, 1000) - this.jitter) / 16;
    }
    this.lastArrival = frame.arrivalMs;

    if ((frame.flags & 2) !== 0) {
      // A discontinuity (located, started, stopped): what is queued belongs to the old position.
      this.resync();
    } else {
      // A gap of more than 5 frames after the newest one held: start again from here.
      const newest = this.queue[this.queue.length - 1]?.sequence ?? this.current?.sequence ?? -1;
      if (newest >= 0 && frame.sequence > newest + 6) {
        this.resync();
      }
    }
    if (this.nextSequence >= 0 && frame.sequence < this.nextSequence) {
      if (this.started) {
        this.counts.dropped++;
        return;
      }
      // Out of order before anything played: it goes first.
      this.nextSequence = frame.sequence;
    }
    if (this.queue.some(f => f.sequence === frame.sequence)) {
      this.counts.dropped++;
      return;
    }
    this.queue.push(frame);
    this.queue.sort((a, b) => a.sequence - b.sequence);
    if (this.nextSequence < 0) {
      this.nextSequence = this.queue[0]?.sequence ?? frame.sequence;
    }

    // Too much queued: the sender's clock runs ahead; drop the oldest back to the target.
    const target = this.targetMs();
    while (!this.primingState && this.bufferedMs() > 2 * target + frameMs && this.queue.length > 1) {
      const old = this.queue.shift();
      if (old) {
        this.nextSequence = old.sequence + 1;
        this.counts.dropped++;
      }
    }
    if (this.primingState && this.bufferedMs() >= target) {
      this.primingState = false;
    }
  }

  /**
   * Fills `left` and `right` (one render quantum) with what plays now; silence while priming or after an underrun.
   * Returns false when it played nothing.
   */
  pull(left: Float32Array, right: Float32Array): boolean {
    const n = left.length;
    if (this.primingState) {
      left.fill(0);
      right.fill(0);
      return false;
    }
    let i = 0;
    while (i < n) {
      if (!this.current || this.offset * 2 >= this.current.samples.length) {
        if (!this.advance()) {
          // Underrun: silence, and prime again (a little deeper).
          left.fill(0, i);
          right.fill(0, i);
          this.counts.underruns++;
          this.penaltyMs = Math.min(60, this.penaltyMs + 20);
          this.primingState = true;
          this.current = null;
          return i > 0;
        }
      }
      const frame = this.current as DecodedFrame;
      const available = frame.samples.length / 2 - this.offset;
      const count = Math.min(available, n - i);
      for (let k = 0; k < count; k++) {
        left[i + k] = frame.samples[(this.offset + k) * 2] ?? 0;
        right[i + k] = frame.samples[(this.offset + k) * 2 + 1] ?? 0;
      }
      this.offset += count;
      i += count;
    }
    // Underrun penalties wear off at 5 ms a second of clean playback.
    this.penaltyMs = Math.max(0, this.penaltyMs - (5 * n) / this.options.sampleRate);
    return true;
  }

  /** The transport sample of the next sample to play (at the project's rate); null stopped or idle. */
  playingTransportSample(): number | null {
    const frame = this.current;
    if (!frame || this.primingState || frame.transportSample < 0) {
      return null;
    }
    return frame.transportSample + Math.round((this.offset * this.options.projectSampleRate) / this.options.sampleRate);
  }

  stats(): JitterStats {
    return {
      bufferedMs: this.bufferedMs(),
      targetMs: this.targetMs(),
      jitterMs: this.jitter,
      underruns: this.counts.underruns,
      dropped: this.counts.dropped,
      lost: this.counts.lost,
      resyncs: this.counts.resyncs,
      playingTransportSample: this.playingTransportSample(),
      priming: this.primingState
    };
  }

  /** Flushes and primes again from the next frame. */
  resync(): void {
    if (this.queue.length > 0 || this.current) {
      this.counts.resyncs++;
    }
    this.queue = [];
    this.current = null;
    this.offset = 0;
    this.nextSequence = -1;
    this.started = false;
    this.primingState = true;
  }

  /** Moves to the next frame in sequence; a missing one (with later ones queued) plays as silence. */
  private advance(): boolean {
    const next = this.queue[0];
    if (!next) {
      return false;
    }
    if (next.sequence > this.nextSequence) {
      // Lost: one frame of silence stands in for it, at the place it had.
      this.counts.lost++;
      const missing = this.nextSequence;
      const spacing = Math.round(
        (this.options.frameSamples * this.options.projectSampleRate) / this.options.sampleRate
      );
      this.current = {
        sequence: missing,
        transportSample: next.transportSample < 0 ? -1 : next.transportSample - (next.sequence - missing) * spacing,
        flags: 0,
        samples: new Float32Array(this.options.frameSamples * 2),
        arrivalMs: next.arrivalMs
      };
      this.nextSequence = missing + 1;
    } else {
      this.current = this.queue.shift() ?? null;
      this.nextSequence = next.sequence + 1;
    }
    this.offset = 0;
    this.started = true;
    return true;
  }
}
