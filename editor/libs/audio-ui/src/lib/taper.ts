/** The level a fader shows as −∞: the contract's floor (`ChannelStrip.volumeDb`, −∞ as −144). */
export const SilenceDb = -144;
/** The top of a fader's travel. */
export const MaxFaderDb = 6;

/**
 * The fader's taper as (travel, dB) points, interpolated linearly between them: like a console or Logic, the useful
 * range around 0 dB gets most of the travel (0 dB sits at about three quarters), and the bottom few percent fall away
 * to silence.
 */
const Taper: readonly (readonly [number, number])[] = [
  [0, SilenceDb],
  [0.02, -60],
  [0.08, -45],
  [0.18, -30],
  [0.3, -20],
  [0.45, -12],
  [0.6, -6],
  [0.78, 0],
  [1, MaxFaderDb]
];

/** Where on the fader's travel (0 bottom … 1 top) a level in dB sits. */
export function faderPosition(db: number): number {
  if (!Number.isFinite(db) || db <= SilenceDb) {
    return 0;
  }

  if (db >= MaxFaderDb) {
    return 1;
  }

  for (let i = 1; i < Taper.length; i++) {
    const [p1, d1] = Taper[i] ?? [1, MaxFaderDb];
    if (db <= d1) {
      const [p0, d0] = Taper[i - 1] ?? [0, SilenceDb];
      return p0 + ((db - d0) / (d1 - d0)) * (p1 - p0);
    }
  }

  return 1;
}

/** The level in dB at a position on the fader's travel (the inverse of {@link faderPosition}). */
export function faderDb(position: number): number {
  if (!(position > 0)) {
    return SilenceDb;
  }

  if (position >= 1) {
    return MaxFaderDb;
  }

  for (let i = 1; i < Taper.length; i++) {
    const [p1, d1] = Taper[i] ?? [1, MaxFaderDb];
    if (position <= p1) {
      const [p0, d0] = Taper[i - 1] ?? [0, SilenceDb];
      return d0 + ((position - p0) / (p1 - p0)) * (d1 - d0);
    }
  }

  return MaxFaderDb;
}

/** "−6.0 dB", "+2.5 dB", "−∞ dB" (to one decimal; −∞ at the floor). */
export function formatDb(db: number, unit = true): string {
  const text = db <= SilenceDb + 0.05 || !Number.isFinite(db) ? '−∞' : signed(db.toFixed(1));
  return unit ? `${text} dB` : text;
}

/** A pan position as Logic shows it: −64 (hard left) … +63 (hard right), as "L64", "C" or "R63". */
export function formatPan(pan: number): string {
  const steps = Math.round(Math.min(1, Math.max(-1, pan)) * 64);
  if (steps === 0) {
    return 'C';
  }

  return steps < 0 ? `L${-steps}` : `R${Math.min(63, steps)}`;
}

/** A linear peak level (1 = 0 dBFS) in dBFS; −∞ for silence. */
export function linearToDb(level: number): number {
  return level > 0 ? 20 * Math.log10(level) : -Infinity;
}

/** "-0.0" reads as "0.0"; a minus sign rather than a hyphen, and a plus on positive values. */
function signed(fixed: string): string {
  const value = Number(fixed);
  if (value === 0) {
    return '0.0';
  }

  return value > 0 ? `+${fixed}` : `−${fixed.slice(1)}`;
}
