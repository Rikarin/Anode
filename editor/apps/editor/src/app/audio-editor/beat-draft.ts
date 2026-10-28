import type { FileTempo } from '@anode/api-client';

/**
 * The Smart Tempo Editor's edits of a file tempo (a draft until applied): beats and downbeats in file frames,
 * ascending, the downbeats a subset of the beats; the tempo is recomputed from the beats after every edit.
 */

/** Frames a beat keeps from its neighbours. */
const MinGap = 1;

/** The average tempo of beats (their span over their count), else `fallback`. */
export function averageBpm(beats: readonly number[], sampleRate: number, fallback: number): number {
  const first = beats[0];
  const last = beats[beats.length - 1];
  if (first === undefined || last === undefined || beats.length < 2 || last <= first) {
    return fallback;
  }
  return Math.round(((60 * sampleRate * (beats.length - 1)) / (last - first)) * 100) / 100;
}

/** Whether the beat intervals vary by more than 2 % around their mean. */
export function variesTempo(beats: readonly number[]): boolean {
  if (beats.length < 3) {
    return false;
  }
  const gaps = beats.slice(1).map((b, i) => b - (beats[i] ?? 0));
  const mean = gaps.reduce((a, b) => a + b, 0) / gaps.length;
  return gaps.some(g => Math.abs(g - mean) > mean * 0.02);
}

function rebuild(tempo: FileTempo, beats: number[], downbeats: number[], sampleRate: number): FileTempo {
  const set = new Set(beats);
  return {
    bpm: averageBpm(beats, sampleRate, tempo.bpm),
    variable: variesTempo(beats),
    beats,
    downbeats: downbeats.filter(d => set.has(d)).sort((a, b) => a - b)
  };
}

/** Moves beat `index` to `frame`, between its neighbours; a downbeat stays one. */
export function moveBeat(tempo: FileTempo, index: number, frame: number, sampleRate: number): FileTempo {
  const old = tempo.beats[index];
  if (old === undefined) {
    return tempo;
  }
  const lo = (tempo.beats[index - 1] ?? -Infinity) + MinGap;
  const hi = (tempo.beats[index + 1] ?? Infinity) - MinGap;
  const to = Math.round(Math.min(hi, Math.max(Math.max(0, lo), frame)));
  const beats = tempo.beats.map((b, i) => (i === index ? to : b));
  const downbeats = tempo.downbeats.map(d => (d === old ? to : d));
  return rebuild(tempo, beats, downbeats, sampleRate);
}

/** Adds a beat at `frame` (unless one is there). */
export function addBeat(tempo: FileTempo, frame: number, sampleRate: number): FileTempo {
  const at = Math.max(0, Math.round(frame));
  if (tempo.beats.includes(at)) {
    return tempo;
  }
  return rebuild(
    tempo,
    [...tempo.beats, at].sort((a, b) => a - b),
    [...tempo.downbeats],
    sampleRate
  );
}

/** Removes beat `index` (and its downbeat). */
export function removeBeat(tempo: FileTempo, index: number, sampleRate: number): FileTempo {
  const old = tempo.beats[index];
  if (old === undefined) {
    return tempo;
  }
  return rebuild(
    tempo,
    tempo.beats.filter((_, i) => i !== index),
    tempo.downbeats.filter(d => d !== old),
    sampleRate
  );
}

/** Makes beat `index` a downbeat, or a plain beat again. */
export function toggleDownbeat(tempo: FileTempo, index: number, sampleRate: number): FileTempo {
  const beat = tempo.beats[index];
  if (beat === undefined) {
    return tempo;
  }
  const downbeats = tempo.downbeats.includes(beat)
    ? tempo.downbeats.filter(d => d !== beat)
    : [...tempo.downbeats, beat];
  return rebuild(tempo, [...tempo.beats], downbeats, sampleRate);
}

/** Double tempo: a beat between every two (the downbeats stay). */
export function doubleTempo(tempo: FileTempo, sampleRate: number): FileTempo {
  const beats: number[] = [];
  tempo.beats.forEach((b, i) => {
    beats.push(b);
    const next = tempo.beats[i + 1];
    if (next !== undefined) {
      beats.push(Math.round((b + next) / 2));
    }
  });
  return rebuild(tempo, beats, [...tempo.downbeats], sampleRate);
}

/**
 * Half tempo: every other beat, counted from the first downbeat (or the first beat), and every other downbeat (a bar
 * now spans twice the audio).
 */
export function halveTempo(tempo: FileTempo, sampleRate: number): FileTempo {
  const anchor = Math.max(0, tempo.beats.indexOf(tempo.downbeats[0] ?? tempo.beats[0] ?? 0));
  const beats = tempo.beats.filter((_, i) => Math.abs(i - anchor) % 2 === 0);
  const downbeats = tempo.downbeats.filter((_, i) => i % 2 === 0);
  return rebuild(tempo, beats, downbeats, sampleRate);
}
