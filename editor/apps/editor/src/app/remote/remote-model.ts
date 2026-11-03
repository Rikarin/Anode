import type { KeyCommandPad, PadSet, RemotePads, Track } from '@anode/api-client';
import { type DrumPadInfo, gmDrumPads } from '@anode/audio-ui/lazy';

/** The remote's mixer shows its strips in banks of 8, as Logic Remote and a control surface do. */
export const BankSize = 8;

/** The tracks the remote's mixer shows: every track but VCAs (a VCA has no pan, sends or signal to arm). */
export function mixerTracks(tracks: readonly Track[]): Track[] {
  return tracks.filter(t => t.kind !== 'vca');
}

export function bankCount(count: number): number {
  return Math.max(1, Math.ceil(count / BankSize));
}

/** A bank's tracks (bank 0 is the first eight), the bank clamped to the ones there are. */
export function bankOf<T>(
  items: readonly T[],
  bank: number
): { bank: number; items: T[]; first: number; last: number } {
  const clamped = Math.min(bankCount(items.length) - 1, Math.max(0, bank));
  const first = clamped * BankSize;
  const slice = items.slice(first, first + BankSize);
  return { bank: clamped, items: slice, first: first + 1, last: first + slice.length };
}

/** The bank a track is in (so the mixer follows the selection). */
export function bankOfIndex(index: number): number {
  return index < 0 ? 0 : Math.floor(index / BankSize);
}

/** The drum pads for a track: its Drum Machine Designer's pads (by note, named), else the 16 GM pads. */
export function drumPadsOf(track: Track | null | undefined): DrumPadInfo[] {
  const pads = track?.drumMachine?.pads;
  return pads?.length ? pads.map(pad => ({ note: pad.note, name: pad.name, color: null })) : gmDrumPads();
}

// ---- key-command pads

/** A new pad's id within its set. */
export function newPadId(set: PadSet): string {
  let n = set.pads.length + 1;
  while (set.pads.some(p => p.id === `pad${n}`)) {
    n++;
  }
  return `pad${n}`;
}

/** A new set's id. */
export function newSetId(pads: RemotePads): string {
  let n = pads.sets.length + 1;
  while (pads.sets.some(s => s.id === `set${n}`)) {
    n++;
  }
  return `set${n}`;
}

export function withSet(pads: RemotePads, set: PadSet): RemotePads {
  const exists = pads.sets.some(s => s.id === set.id);
  return { sets: exists ? pads.sets.map(s => (s.id === set.id ? set : s)) : [...pads.sets, set] };
}

export function withoutSet(pads: RemotePads, setId: string): RemotePads {
  return { sets: pads.sets.filter(s => s.id !== setId) };
}

export function addPad(set: PadSet, commandId: string, label: string): PadSet {
  const pad: KeyCommandPad = { id: newPadId(set), label, commandId, color: null };
  return { ...set, pads: [...set.pads, pad] };
}

export function removePad(set: PadSet, padId: string): PadSet {
  return { ...set, pads: set.pads.filter(p => p.id !== padId) };
}

export function updatePad(set: PadSet, padId: string, change: Partial<Omit<KeyCommandPad, 'id'>>): PadSet {
  return { ...set, pads: set.pads.map(p => (p.id === padId ? { ...p, ...change } : p)) };
}

/** Moves a pad one place earlier (−1) or later (+1). */
export function movePad(set: PadSet, padId: string, by: -1 | 1): PadSet {
  const index = set.pads.findIndex(p => p.id === padId);
  const to = index + by;
  if (index < 0 || to < 0 || to >= set.pads.length) {
    return set;
  }
  const pads = [...set.pads];
  const [pad] = pads.splice(index, 1);
  if (pad) {
    pads.splice(to, 0, pad);
  }
  return { ...set, pads };
}
