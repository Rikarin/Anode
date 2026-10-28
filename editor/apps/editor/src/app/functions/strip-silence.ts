import { peakRange, type Waveform } from '@anode/timeline';

/** Strip Silence's settings (StripSilenceCommand), as the window edits them. */
export interface SilenceSettings {
  thresholdDb: number;
  minTimeMs: number;
  preAttackMs: number;
  postReleaseMs: number;
}

export const DefaultSilence: SilenceSettings = { thresholdDb: -40, minTimeMs: 100, preAttackMs: 20, postReleaseMs: 50 };

/** A level envelope: the absolute peak of each `framesPerBlock` frames from `firstFrame`. */
export interface Envelope {
  readonly firstFrame: number;
  readonly framesPerBlock: number;
  readonly levels: readonly number[];
}

/** A region's envelope from its file's peaks: `blocks` blocks over `[from, to)` frames (at most one per peak). */
export function envelopeOf(waveform: Waveform, from: number, to: number, blocks = 400): Envelope {
  const framesPerBlock = Math.max(waveform.framesPerPeak, Math.ceil((to - from) / Math.max(1, blocks)));
  const range = [0, 0];
  const levels: number[] = [];
  for (let f = from; f < to; f += framesPerBlock) {
    levels.push(
      peakRange(waveform, f, Math.min(to, f + framesPerBlock), range)
        ? Math.max(Math.abs(range[0] ?? 0), Math.abs(range[1] ?? 0))
        : 0
    );
  }
  return { firstFrame: from, framesPerBlock, levels };
}

/**
 * The stretches (file frames, `[start, end)`) Strip Silence keeps of `[from, to)`, by Logic's rules on a level
 * envelope: blocks at or above the threshold sound; a silence shorter than the minimum time does not split (the
 * stretches either side join); each stretch is widened by the pre-attack before and the post-release after, within
 * the region, and stretches that then overlap join. The Server decides on its own RMS analysis; this is the window's
 * live preview.
 */
export function silenceStretches(
  envelope: Envelope,
  settings: SilenceSettings,
  sampleRate: number,
  from: number,
  to: number
): [number, number][] {
  const threshold = 10 ** (settings.thresholdDb / 20);
  const ms = sampleRate / 1000;
  const { firstFrame, framesPerBlock, levels } = envelope;
  const runs: [number, number][] = [];
  levels.forEach((level, i) => {
    if (level < threshold) {
      return;
    }
    const a = firstFrame + i * framesPerBlock;
    const b = Math.min(to, a + framesPerBlock);
    const last = runs[runs.length - 1];
    if (last && a - last[1] < settings.minTimeMs * ms) {
      last[1] = b;
    } else {
      runs.push([a, b]);
    }
  });
  const widened: [number, number][] = [];
  for (const [a, b] of runs) {
    const start = Math.max(from, Math.round(a - settings.preAttackMs * ms));
    const end = Math.min(to, Math.round(b + settings.postReleaseMs * ms));
    const last = widened[widened.length - 1];
    if (last && start <= last[1]) {
      last[1] = Math.max(last[1], end);
    } else {
      widened.push([start, end]);
    }
  }
  return widened;
}
