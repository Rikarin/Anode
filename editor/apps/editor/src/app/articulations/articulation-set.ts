import type {
  Articulation,
  ArticulationOutput,
  ArticulationOutputKind,
  ArticulationSet,
  ArticulationSwitch,
  ArticulationSwitchKind,
  EditCommand
} from '@anode/api-client';
import { noteName } from '../midi/midi-math';

/** The highest articulation id (0 is "none"). */
export const MaxArticulationId = 254;

export const SwitchKinds: readonly { value: ArticulationSwitchKind; label: string }[] = [
  { value: 'note', label: 'Note' },
  { value: 'controlChange', label: 'Control Change' }
];

export const OutputKinds: readonly { value: ArticulationOutputKind; label: string }[] = [
  { value: 'note', label: 'Note' },
  { value: 'controlChange', label: 'Control Change' },
  { value: 'program', label: 'Program' },
  { value: 'channel', label: 'Channel' }
];

/** A new set's id (`ars_…`, as the contract wants of sets in a project). */
export function newArticulationSetId(): string {
  const random =
    typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
      ? crypto.randomUUID().replaceAll('-', '')
      : Math.random().toString(16).slice(2);
  return `ars_${random.slice(0, 12)}`;
}

/** An empty set to start a new one from. */
export function emptyArticulationSet(id = newArticulationSetId()): ArticulationSet {
  return { id, name: 'Articulation Set', articulations: [], switches: [], outputs: [] };
}

/** The lowest free articulation id, or null when all 254 are taken. */
export function nextArticulationId(set: ArticulationSet): number | null {
  const taken = new Set(set.articulations.map(a => a.id));
  for (let id = 1; id <= MaxArticulationId; id++) {
    if (!taken.has(id)) {
      return id;
    }
  }
  return null;
}

/** The set with a new articulation (the lowest free id, named after it); unchanged when it is full. */
export function addArticulation(set: ArticulationSet): ArticulationSet {
  const id = nextArticulationId(set);
  if (id === null) {
    return set;
  }
  const articulations = [...set.articulations, { id, name: `Articulation ${id}`, symbol: null }];
  return { ...set, articulations: articulations.sort((a, b) => a.id - b.id) };
}

/** The set without articulation `index` (by position), and the switches and outputs that named it. */
export function removeArticulation(set: ArticulationSet, index: number): ArticulationSet {
  const removed = set.articulations[index];
  if (!removed) {
    return set;
  }
  const others = set.articulations.filter((_, i) => i !== index);
  const stillNamed = (articulationId: number) =>
    articulationId !== removed.id || others.some(a => a.id === articulationId);
  return {
    ...set,
    articulations: others,
    switches: set.switches.filter(s => stillNamed(s.articulationId)),
    outputs: set.outputs.filter(o => stillNamed(o.articulationId))
  };
}

/** A new input switch for the set's first articulation: a keyswitch note from C-1 up. */
export function newSwitch(set: ArticulationSet): ArticulationSwitch {
  const used = new Set(set.switches.filter(s => s.kind === 'note').map(s => s.number));
  let number = 0;
  while (used.has(number) && number < 127) {
    number++;
  }
  return { articulationId: set.articulations[0]?.id ?? 1, kind: 'note', number, value: null };
}

/**
 * A new output for the next articulation without one (else the first): a keyswitch note (one tick at velocity
 * `value`) from C0 (24) up, past the keys already used.
 */
export function newOutput(set: ArticulationSet): ArticulationOutput {
  const covered = new Set(set.outputs.map(o => o.articulationId));
  const articulation = set.articulations.find(a => !covered.has(a.id)) ?? set.articulations[0];
  const used = new Set(set.outputs.filter(o => o.kind === 'note').map(o => o.number));
  let number = 24;
  while (used.has(number) && number < 127) {
    number++;
  }
  return { articulationId: articulation?.id ?? 1, kind: 'note', number, value: 100 };
}

/** An output changed to `kind`, its datum moved into that kind's range (a program's and a channel's `number` is 0). */
export function withOutputKind(output: ArticulationOutput, kind: ArticulationOutputKind): ArticulationOutput {
  switch (kind) {
    case 'note':
      return { ...output, kind, number: clamp(output.number, 0, 127, 24), value: clamp(output.value, 1, 127, 100) };
    case 'controlChange':
      return { ...output, kind, number: clamp(output.number, 0, 127, 32), value: clamp(output.value, 0, 127, 0) };
    case 'program':
      return { ...output, kind, number: 0, value: clamp(output.value, 0, 127, 0) };
    case 'channel':
      return { ...output, kind, number: 0, value: clamp(output.value, 1, 16, 1) };
  }
}

function clamp(value: number, min: number, max: number, fallback: number): number {
  return Number.isFinite(value) && value >= min && value <= max ? value : fallback;
}

/** The range of an output's `value` for its kind. */
export function outputValueRange(kind: ArticulationOutputKind): { min: number; max: number } {
  return kind === 'note' ? { min: 1, max: 127 } : kind === 'channel' ? { min: 1, max: 16 } : { min: 0, max: 127 };
}

/**
 * What is wrong with a set, as the Server checks it (fake-phase7.ts `checkArticulationSet`, Articulations.cs): ids
 * 1…254 and unique, switches and outputs naming an articulation of the set with data in range; plus a name and an
 * `ars_` id. Empty when it can be saved.
 */
export function articulationSetProblems(set: ArticulationSet): string[] {
  const problems: string[] = [];
  if (!set.id.startsWith('ars_')) {
    problems.push('The set’s id must start with ars_.');
  }
  if (!set.name.trim()) {
    problems.push('The set needs a name.');
  }
  const ids = new Set<number>();
  set.articulations.forEach((a: Articulation, i) => {
    if (!Number.isInteger(a.id) || a.id < 1 || a.id > MaxArticulationId) {
      problems.push(`Articulation ${i + 1}: the id must be 1…254.`);
    } else if (ids.has(a.id)) {
      problems.push(`Articulation ${i + 1}: id ${a.id} is used twice.`);
    }
    ids.add(a.id);
    if (!a.name.trim()) {
      problems.push(`Articulation ${i + 1} needs a name.`);
    }
  });
  set.switches.forEach((s, i) => {
    if (!ids.has(s.articulationId)) {
      problems.push(`Switch ${i + 1} names no articulation of the set.`);
    }
    if (!byte(s.number)) {
      problems.push(`Switch ${i + 1}: the ${s.kind === 'note' ? 'key' : 'controller'} must be 0…127.`);
    }
    if (s.value != null && !byte(s.value)) {
      problems.push(`Switch ${i + 1}: the value must be 0…127.`);
    }
  });
  set.outputs.forEach((o, i) => {
    if (!ids.has(o.articulationId)) {
      problems.push(`Output ${i + 1} names no articulation of the set.`);
    }
    if ((o.kind === 'note' || o.kind === 'controlChange') && !byte(o.number)) {
      problems.push(`Output ${i + 1}: the ${o.kind === 'note' ? 'key' : 'controller'} must be 0…127.`);
    }
    const range = outputValueRange(o.kind);
    if (!Number.isInteger(o.value) || o.value < range.min || o.value > range.max) {
      problems.push(`Output ${i + 1}: the value must be ${range.min}…${range.max}.`);
    }
  });
  return problems;
}

function byte(value: number): boolean {
  return Number.isInteger(value) && value >= 0 && value <= 127;
}

/** The set as saved: names trimmed, empty symbols dropped, articulations by id. */
export function cleanArticulationSet(set: ArticulationSet): ArticulationSet {
  return {
    ...set,
    name: set.name.trim(),
    articulations: set.articulations
      .map(a => ({ id: a.id, name: a.name.trim(), symbol: a.symbol?.trim() || null }))
      .sort((a, b) => a.id - b.id),
    switches: set.switches.map(s => ({ ...s, value: s.value ?? null })),
    outputs: set.outputs.map(o => (o.kind === 'program' || o.kind === 'channel' ? { ...o, number: 0 } : o))
  };
}

/**
 * The commands that save a set into the project (one undo step): `setArticulationSet`, and for a new set opened from
 * an instrument or MIDI track, `assignArticulationSet` to that track.
 */
export function saveArticulationSetCommands(set: ArticulationSet, assignTo: string | null): EditCommand[] {
  const commands: EditCommand[] = [{ type: 'setArticulationSet', set: cleanArticulationSet(set) }];
  if (assignTo) {
    commands.push({ type: 'assignArticulationSet', trackId: assignTo, setId: set.id });
  }
  return commands;
}

/** A library set's content in the set being edited (its id kept, so saving replaces it). */
export function loadInto(set: ArticulationSet, from: ArticulationSet): ArticulationSet {
  return structuredClone({ ...from, id: set.id });
}

const PitchClasses: Readonly<Record<string, number>> = { C: 0, D: 2, E: 4, F: 5, G: 7, A: 9, B: 11 };

/**
 * A pitch typed as a number (0…127) or a note name in Logic's octaves (C3 = 60; `#`/`♯` and `b`/`♭`, octaves from
 * -2); null when it is neither.
 */
export function parsePitch(text: string): number | null {
  const trimmed = text.trim();
  if (/^\d+$/.test(trimmed)) {
    const value = Number(trimmed);
    return value <= 127 ? value : null;
  }
  const match = /^([A-Ga-g])([#♯b♭]?)(-?\d)$/.exec(trimmed);
  if (!match) {
    return null;
  }
  const [, letter = 'C', accidental = '', octave = '0'] = match;
  const shift = accidental === '#' || accidental === '♯' ? 1 : accidental === 'b' || accidental === '♭' ? -1 : 0;
  const pitch = (Number(octave) + 2) * 12 + (PitchClasses[letter.toUpperCase()] ?? 0) + shift;
  return pitch >= 0 && pitch <= 127 ? pitch : null;
}

/** A pitch as Logic names it, with its number: "C0 (24)". */
export function pitchLabel(pitch: number): string {
  return `${noteName(pitch)} (${pitch})`;
}

/** An articulation's short label: its symbol, else its name. */
export function articulationLabel(articulation: Pick<Articulation, 'name' | 'symbol'>): string {
  return articulation.symbol?.trim() || articulation.name;
}
