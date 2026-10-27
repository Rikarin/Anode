import type { MidiRegion, Note, RegionPlayback } from '@anode/api-client';

/** Ticks per quarter note (the contract's time base). */
export const Ppq = 960;

/** Region playback that changes nothing: no transpose, no velocity offset, no quantise. */
export const NeutralPlayback: RegionPlayback = {
  transpose: 0,
  velocity: 0,
  quantizeTicks: 0,
  quantizeStrength: 1,
  swing: 0
};

/** Note-on and note-off status bytes for MIDI channel 1…16. */
export function noteOn(channel = 1): number {
  return 0x90 | ((Math.min(16, Math.max(1, channel)) - 1) & 0x0f);
}

export function noteOff(channel = 1): number {
  return 0x80 | ((Math.min(16, Math.max(1, channel)) - 1) & 0x0f);
}

/** Whether a raw message is a note-on with a velocity (a note-on at velocity 0 is a note-off). */
export function isNoteOn(status: number, velocity: number): boolean {
  return (status & 0xf0) === 0x90 && velocity > 0;
}

export function isNoteOff(status: number, velocity: number): boolean {
  return (status & 0xf0) === 0x80 || ((status & 0xf0) === 0x90 && velocity === 0);
}

const NoteNames = ['C', 'C♯', 'D', 'D♯', 'E', 'F', 'F♯', 'G', 'G♯', 'A', 'A♯', 'B'];

/** A pitch's name as Logic shows it: middle C (60) is C3. */
export function noteName(pitch: number): string {
  return `${NoteNames[((pitch % 12) + 12) % 12] ?? '?'}${Math.floor(pitch / 12) - 2}`;
}

/** Whether a pitch is a black key. */
export function isBlackKey(pitch: number): boolean {
  return [1, 3, 6, 8, 10].includes(((pitch % 12) + 12) % 12);
}

/**
 * Where a quantise grid puts `tick`: grid points every `grid` ticks from 0, every second one delayed by `swing`
 * (0…1) of half a step, then the tick moved `strength` (0…1) of the way there (the contract's region playback and
 * `quantizeNotes`). A grid of 0 leaves it.
 */
export function quantizeTick(tick: number, grid: number, strength = 1, swing = 0): number {
  if (!(grid > 0)) {
    return tick;
  }

  const delay = (Math.min(1, Math.max(0, swing)) * grid) / 2;
  // The nearest of the straight point below, the one above, and their swung versions.
  const k = Math.floor(tick / grid);
  let best = tick;
  let distance = Infinity;
  for (const step of [k - 1, k, k + 1, k + 2]) {
    if (step < 0) {
      continue;
    }
    const point = step * grid + (step % 2 === 1 ? delay : 0);
    const d = Math.abs(point - tick);
    if (d < distance) {
      distance = d;
      best = point;
    }
  }

  return Math.round(tick + (best - tick) * Math.min(1, Math.max(0, strength)));
}

/**
 * A region's notes after a destructive quantise (`quantizeNotes`): starts moved towards the project's grid (notes are
 * relative to the region's start, the grid is absolute), lengths kept. Only `ids` when given.
 */
export function quantizeNotes(
  region: Pick<MidiRegion, 'startTick' | 'notes'>,
  grid: number,
  strength = 1,
  swing = 0,
  ids?: ReadonlySet<number>
): Note[] {
  return region.notes.map(note => {
    if (ids && !ids.has(note.id)) {
      return note;
    }

    const absolute = region.startTick + note.startTick;
    return { ...note, startTick: quantizeTick(absolute, grid, strength, swing) - region.startTick };
  });
}

/** How a note plays under its region's playback: transposed (0…127), velocity offset (1…127), start quantised. */
export function playedNote(note: Note, region: Pick<MidiRegion, 'startTick' | 'playback'>): Note {
  const p = region.playback;
  const start =
    p.quantizeTicks > 0
      ? quantizeTick(region.startTick + note.startTick, p.quantizeTicks, p.quantizeStrength, p.swing) - region.startTick
      : note.startTick;
  return {
    ...note,
    pitch: Math.min(127, Math.max(0, note.pitch + p.transpose)),
    velocity: Math.min(127, Math.max(1, note.velocity + p.velocity)),
    startTick: start
  };
}

/** The quantise grids the Piano Roll and the Region inspector offer, in ticks (Logic's list, without triplets). */
export const QuantizeGrids: readonly { ticks: number; label: string }[] = [
  { ticks: Ppq * 4, label: '1/1 note' },
  { ticks: Ppq * 2, label: '1/2 note' },
  { ticks: Ppq, label: '1/4 note' },
  { ticks: Ppq / 2, label: '1/8 note' },
  { ticks: Ppq / 4, label: '1/16 note' },
  { ticks: Ppq / 8, label: '1/32 note' },
  { ticks: Ppq / 16, label: '1/64 note' },
  { ticks: (Ppq * 2) / 3, label: '1/4 triplet' },
  { ticks: Ppq / 3, label: '1/8 triplet' },
  { ticks: Ppq / 6, label: '1/16 triplet' }
];

/** The label of a grid in ticks ("1/16 note"), or "Off" for 0. */
export function gridLabel(ticks: number): string {
  if (ticks <= 0) {
    return 'Off';
  }
  return QuantizeGrids.find(grid => grid.ticks === ticks)?.label ?? `${ticks} ticks`;
}

/**
 * Musical typing (Logic's): the home row plays a white-key octave from C, the row above its black keys, Z and X shift
 * the octave down and up. Keys by `KeyboardEvent.code`, so the layout of the keyboard does not matter. Semitones from
 * the octave's C.
 */
export const MusicalTypingKeys: Readonly<Record<string, number>> = {
  KeyA: 0,
  KeyW: 1,
  KeyS: 2,
  KeyE: 3,
  KeyD: 4,
  KeyF: 5,
  KeyT: 6,
  KeyG: 7,
  KeyY: 8,
  KeyH: 9,
  KeyU: 10,
  KeyJ: 11,
  KeyK: 12
};

/** Velocity as a note colour (Logic's ramp: cool and dark when soft, warm and bright when hard), `#rrggbb`. */
export function velocityColor(velocity: number): string {
  const v = Math.min(127, Math.max(1, velocity)) / 127;
  // Hue from blue-green (190°) through green and yellow to red (0°), brighter as it gets louder.
  const hue = 190 * (1 - v);
  const lightness = 0.42 + 0.16 * v;
  return hslHex(hue, 0.72, lightness);
}

function hslHex(h: number, s: number, l: number): string {
  const a = s * Math.min(l, 1 - l);
  const f = (n: number) => {
    const k = (n + h / 30) % 12;
    const c = l - a * Math.max(-1, Math.min(k - 3, 9 - k, 1));
    return Math.round(c * 255)
      .toString(16)
      .padStart(2, '0');
  };
  return `#${f(0)}${f(8)}${f(4)}`;
}
