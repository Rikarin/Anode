/**
 * The panners' geometry (docs/11-phase-10.md, ADR-0012), pure and shared by the Surround Panner, the 3D Object Panner
 * and the multichannel meter. Angles are in the ADM convention: azimuth 0 straight ahead, **positive to the left**,
 * −180…180; elevation positive up. Points are in a unit square around the listener with x to the right and y to the
 * front (up on screen).
 */

/** A speaker as the panners draw it (the shape of `@anode/api-client`'s `Speaker`). */
export interface PannerSpeaker {
  /** The ADM label (`M+030`, `LFE1`, `U-135`). */
  label: string;
  azimuth: number;
  elevation: number;
  lfe: boolean;
}

/** A point in the panner's unit circle: x right, y front. */
export interface PannerPoint {
  x: number;
  y: number;
}

/** The Surround Panner's puck parameters. */
export interface PuckPosition {
  /** −180…180, positive left. */
  angle: number;
  /** 0 (every speaker) … 1 (a point, on the rim). */
  diversity: number;
}

const Rad = Math.PI / 180;

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

/** An angle folded into −180…180 (180 rather than −180). */
export function wrapAngle(degrees: number): number {
  const wrapped = ((((degrees + 180) % 360) + 360) % 360) - 180;
  return wrapped === -180 ? 180 : wrapped;
}

/** Rounds away floating-point noise (−0 too), so 1e-16 reads as 0. */
function tidy(value: number, digits = 6): number {
  const rounded = Number(value.toFixed(digits));
  return rounded === 0 ? 0 : rounded;
}

/** Where a direction at `radius` lies: ahead is up (y), left is −x. */
export function polarPoint(azimuth: number, radius: number): PannerPoint {
  return { x: tidy(-radius * Math.sin(azimuth * Rad)), y: tidy(radius * Math.cos(azimuth * Rad)) };
}

/** The Surround Panner's puck for an angle and diversity: diversity is the distance from the centre. */
export function puckPoint(position: PuckPosition): PannerPoint {
  return polarPoint(position.angle, clamp(position.diversity, 0, 1));
}

/**
 * The angle and diversity of a puck dragged to `point` (clamped to the circle). At the centre the angle is undefined:
 * `previousAngle` is kept.
 */
export function puckFromPoint(point: PannerPoint, previousAngle = 0): PuckPosition {
  const radius = Math.hypot(point.x, point.y);
  const diversity = tidy(clamp(radius, 0, 1), 4);
  if (radius < 1e-6) {
    return { angle: previousAngle, diversity: 0 };
  }
  return { angle: tidy(wrapAngle(Math.atan2(-point.x, point.y) / Rad), 2), diversity };
}

/**
 * Where a speaker is drawn in a top view: the ear-level ring on the rim, an upper layer (elevation > 0) further in by
 * the cosine of its elevation (so 30° height sits at 0.87; drawn at 0.62 at least, to keep the layers apart). The LFE
 * is null: it has no direction and is drawn apart.
 */
export function speakerPoint(speaker: PannerSpeaker): PannerPoint | null {
  if (speaker.lfe) {
    return null;
  }
  const radius = speaker.elevation > 0 ? Math.min(0.62, Math.cos(speaker.elevation * Rad)) : 1;
  return polarPoint(speaker.azimuth, radius);
}

/** A stereo input's two channels around the puck: left at angle + spread/2, right at angle − spread/2. */
export function spreadPucks(position: PuckPosition, spread: number): [PannerPoint, PannerPoint] {
  const half = clamp(spread, 0, 180) / 2;
  const radius = clamp(position.diversity, 0, 1);
  return [polarPoint(position.angle + half, radius), polarPoint(position.angle - half, radius)];
}

// ---- the 3D Object Panner

/** An object's position (`ObjectPanner` without the size). */
export interface ObjectPosition {
  azimuth: number;
  /** −90…90. */
  elevation: number;
  /** 0…1. */
  distance: number;
}

/** The object in the top view: azimuth and the horizontal distance (the distance projected by the elevation). */
export function objectTopPoint(position: ObjectPosition): PannerPoint {
  return polarPoint(position.azimuth, clamp(position.distance, 0, 1) * Math.cos(position.elevation * Rad));
}

/**
 * The azimuth and distance of an object dragged to `point` in the top view, its elevation kept: the horizontal
 * distance becomes the distance over the cosine of the elevation (at most 1).
 */
export function objectFromTopPoint(point: PannerPoint, previous: ObjectPosition): ObjectPosition {
  const horizontal = clamp(Math.hypot(point.x, point.y), 0, 1);
  const cos = Math.cos(previous.elevation * Rad);
  const distance = tidy(clamp(cos > 1e-6 ? horizontal / cos : previous.distance, 0, 1), 4);
  const azimuth = horizontal < 1e-6 ? previous.azimuth : tidy(wrapAngle(Math.atan2(-point.x, point.y) / Rad), 2);
  return { azimuth, elevation: previous.elevation, distance };
}

/**
 * The object in the side view (seen from the right, the front to the right): x the horizontal distance (0…1), y the
 * height (−1…1).
 */
export function objectSidePoint(position: ObjectPosition): PannerPoint {
  const d = clamp(position.distance, 0, 1);
  return { x: tidy(d * Math.cos(position.elevation * Rad)), y: tidy(d * Math.sin(position.elevation * Rad)) };
}

/** The elevation and distance of an object dragged to `point` in the side view, its azimuth kept. */
export function objectFromSidePoint(point: PannerPoint, previous: ObjectPosition): ObjectPosition {
  const x = Math.max(0, point.x);
  const distance = tidy(clamp(Math.hypot(x, point.y), 0, 1), 4);
  const elevation = distance < 1e-6 ? previous.elevation : tidy(clamp(Math.atan2(point.y, x) / Rad, -90, 90), 2);
  return { azimuth: previous.azimuth, elevation, distance };
}

// ---- labels

const ShortLabels: Readonly<Record<string, string>> = {
  'M+030': 'L',
  'M-030': 'R',
  'M+000': 'C',
  LFE1: 'LFE',
  'M+110': 'Ls',
  'M-110': 'Rs',
  'M+090': 'Lss',
  'M-090': 'Rss',
  'M+135': 'Lrs',
  'M-135': 'Rrs',
  'M+060': 'Lw',
  'M-060': 'Rw',
  'M+045': 'L',
  'M-045': 'R',
  'U+045': 'Ltf',
  'U-045': 'Rtf',
  'U+090': 'Ltm',
  'U-090': 'Rtm',
  'U+135': 'Ltr',
  'U-135': 'Rtr'
};

/** A speaker's short name as mixers print it (L, R, C, LFE, Ls, Rs, Lss, Ltf …), from its ADM label. */
export function shortSpeakerLabel(label: string): string {
  return ShortLabels[label] ?? label;
}

const LongLabels: Readonly<Record<string, string>> = {
  L: 'Left',
  R: 'Right',
  C: 'Centre',
  LFE: 'LFE',
  Ls: 'Left Surround',
  Rs: 'Right Surround',
  Lss: 'Left Side',
  Rss: 'Right Side',
  Lrs: 'Left Rear',
  Rrs: 'Right Rear',
  Lw: 'Left Wide',
  Rw: 'Right Wide',
  Ltf: 'Top Front Left',
  Rtf: 'Top Front Right',
  Ltm: 'Top Middle Left',
  Rtm: 'Top Middle Right',
  Ltr: 'Top Rear Left',
  Rtr: 'Top Rear Right'
};

/** A speaker's full name for screen readers ("Left Surround"). */
export function speakerName(label: string): string {
  const short = shortSpeakerLabel(label);
  return LongLabels[short] ?? short;
}

/** An angle as people read it: "0°", "110° L", "45° R", "180°". */
export function formatAngle(degrees: number): string {
  const a = Math.round(wrapAngle(degrees));
  if (a === 0 || a === 180) {
    return `${a}°`;
  }
  return a > 0 ? `${a}° L` : `${-a}° R`;
}

/** The lowest level a panner's level control shows above −∞; at or below it the level is off (−144 dB). */
export const PannerLevelFloorDb = -60;
/** Off, as the contract stores it. */
export const PannerSilenceDb = -144;

/** A panner level as a knob shows it: −144 (off) at the knob's floor. */
export function levelToKnob(db: number): number {
  return Math.max(PannerLevelFloorDb, Math.min(0, db));
}

/** A knob's value back to a panner level: the floor is off. */
export function knobToLevel(value: number): number {
  return value <= PannerLevelFloorDb ? PannerSilenceDb : Math.min(0, value);
}

/** "−6.0 dB", "−∞ dB". */
export function formatLevel(db: number): string {
  if (db <= PannerLevelFloorDb) {
    return '−∞ dB';
  }
  const text = db.toFixed(1);
  return `${Number(text) === 0 ? '0.0' : text.replace('-', '−')} dB`;
}
