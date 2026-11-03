import {
  ApiError,
  type EditCommand,
  type Insert,
  type MasteringAnalysis,
  MasteringBands,
  type MasteringCharacter,
  MasteringParameters,
  type MasteringReading,
  type MasteringSettings,
  type Project
} from '@anode/api-client';
import { masteringInsert, masteringSettingsOf } from '../../mastering/mastering-math';
import type { MixContext } from './fake-mix';

/** A command of one type. */
type Command<T extends EditCommand['type']> = Extract<EditCommand, { type: T }>;

function refuse(message: string): never {
  throw new ApiError(422, message);
}

/** The characters in their parameter's order (`character` 0…3). */
export const Characters: readonly MasteringCharacter[] = ['clean', 'valve', 'punch', 'transparent'];

/** The settings' ranges (`MasteringParameters`). */
const Ranges = {
  targetLufs: { min: -24, max: -6, label: 'The target' },
  ceilingDbtp: { min: -3, max: 0, label: 'The ceiling' },
  eq: { min: 0, max: 1, label: 'EQ' },
  dynamics: { min: 0, max: 1, label: 'Dynamics' },
  width: { min: 0, max: 2, label: 'Width' }
} as const;

/** The master's Mastering Assistant: `insertId`'s, else the last one; null when the master has none. */
export function findAssistant(project: Project, insertId?: string | null): Insert | null {
  const inserts = project.master.inserts;
  if (insertId) {
    const insert = inserts.find(i => i.id === insertId);
    if (insert?.pluginId !== MasteringParameters.pluginId) {
      refuse(`The master has no Mastering Assistant ${insertId}.`);
    }
    return insert;
  }
  return masteringInsert(project.master);
}

function requireAssistant(project: Project, insertId?: string | null): Insert {
  return findAssistant(project, insertId) ?? refuse('The master has no Mastering Assistant.');
}

/** An assistant's settings, from its parameters (the editor's reading, which the window uses too). */
export const settingsOf = masteringSettingsOf;

function withAssistant(project: Project, insert: Insert): Project {
  return {
    ...project,
    master: { ...project.master, inserts: project.master.inserts.map(i => (i.id === insert.id ? insert : i)) }
  };
}

function checked(value: number, key: keyof typeof Ranges): number {
  const range = Ranges[key];
  if (!Number.isFinite(value) || value < range.min || value > range.max) {
    refuse(`${range.label} is ${range.min} to ${range.max}.`);
  }
  return value;
}

function setMastering(project: Project, command: Command<'setMastering'>): Project {
  const insert = requireAssistant(project, command.insertId);
  const parameters = { ...insert.parameters };
  if (command.character != null) {
    const index = Characters.indexOf(command.character);
    if (index < 0) {
      refuse(`${command.character} is not a character.`);
    }
    parameters[MasteringParameters.character] = index;
  }
  for (const key of Object.keys(Ranges) as (keyof typeof Ranges)[]) {
    const value = command[key];
    if (value != null) {
      parameters[MasteringParameters[key]] = checked(value, key);
    }
  }
  if (command.matchedBypass != null) {
    parameters[MasteringParameters.matchedBypass] = command.matchedBypass ? 1 : 0;
  }
  return withAssistant(project, { ...insert, parameters });
}

function checkAnalysis(analysis: MasteringAnalysis): MasteringAnalysis {
  if (analysis.spectrumDb.length !== MasteringBands.length || analysis.curveDb.length !== MasteringBands.length) {
    refuse(`An analysis has a value for each of the ${MasteringBands.length} bands.`);
  }
  if (!(analysis.endTick > analysis.startTick) || analysis.startTick < 0) {
    refuse('An analysis covers a range.');
  }
  return analysis;
}

/** The Mastering Assistant's commands (docs/12-phase-11.md): its settings are its insert's parameters. */
export function applyMasteringCommand(project: Project, command: EditCommand, _ctx: MixContext): Project {
  switch (command.type) {
    case 'setMastering':
      return setMastering(project, command);
    case 'setMasteringAnalysis': {
      const insert = requireAssistant(project, command.insertId);
      return withAssistant(project, { ...insert, mastering: structuredClone(checkAnalysis(command.analysis)) });
    }
    default:
      return project;
  }
}

/**
 * A character's reference curve over the bands (dB, relative): Clean a gentle pink tilt, Valve warm (low lift, a soft
 * top), Punch the low end and presence forward, Transparent nearly flat.
 */
export function characterCurve(character: MasteringCharacter): number[] {
  return MasteringBands.map(hz => {
    const octave = Math.log2(hz / 1000);
    const bump = (centre: number, width: number, gain: number) =>
      gain * Math.exp(-((Math.log2(hz / centre) / width) ** 2));
    switch (character) {
      case 'valve':
        return round(-0.6 * octave + bump(100, 1.2, 1.5) - bump(12_000, 1, 2));
      case 'punch':
        return round(-0.5 * octave + bump(70, 1, 2.5) + bump(3_000, 1, 1.5) - bump(400, 1, 1));
      case 'transparent':
        return round(-0.2 * octave);
      default:
        return round(-0.45 * octave);
    }
  });
}

function round(value: number): number {
  return Math.round(value * 10) / 10;
}

/**
 * The fake analysis of the master before the assistant over a range: deterministic numbers from the mix (every
 * unmuted track adds its level: louder tracks make a louder, denser master; more tracks a wider one), a spectrum that
 * leans to the low end with a dip per track, against the character's curve.
 */
export function fakeMasteringAnalysis(
  project: Project,
  settings: MasteringSettings,
  startTick: number,
  endTick: number,
  revision: number
): MasteringAnalysis {
  const playing = project.tracks.filter(t => !t.strip.mute && t.kind !== 'vca' && t.parentId === null);
  const power = playing.reduce((sum, t) => sum + 10 ** (t.strip.volumeDb / 10), 0) || 0.01;
  const integrated = round(-24 + 10 * Math.log10(power) + project.master.volumeDb);
  const spectrum = MasteringBands.map((hz, band) => {
    const octave = Math.log2(hz / 1000);
    const ripple = playing.reduce((sum, t, i) => sum + Math.sin(band * 0.7 + i * 1.9 + t.name.length) * 0.8, 0);
    return round(-1.2 * octave - (hz > 12_000 ? 3 : 0) + ripple / Math.max(1, playing.length));
  });
  return {
    startTick,
    endTick,
    revision,
    integratedLufs: integrated,
    maxShortTermLufs: round(integrated + 3.5),
    loudnessRange: round(4 + playing.length * 0.8),
    truePeakDbtp: round(Math.min(2, integrated + 15)),
    crestFactorDb: round(Math.max(6, 16 - playing.length)),
    width: round(Math.min(1, 0.25 + playing.length * 0.1)),
    spectrumDb: spectrum,
    curveDb: characterCurve(settings.character)
  };
}

/**
 * The assistant's live readings while playing (the `mastering` event): the target's loudness with a slow wobble, the
 * limiter working by what the analysis says the master lacks; null when the master has no assistant.
 */
export function fakeMasteringReading(project: Project, positionTick: number): MasteringReading | null {
  const insert = findAssistant(project);
  if (!insert || insert.bypassed) {
    return null;
  }
  const settings = settingsOf(insert);
  const analysis = insert.mastering ?? null;
  const wobble = Math.sin(positionTick / 960) * 1.5;
  const lift = analysis ? Math.max(0, settings.targetLufs - analysis.integratedLufs) : 0;
  return {
    insertId: insert.id,
    momentaryLufs: round(settings.targetLufs + wobble),
    shortTermLufs: round(settings.targetLufs + wobble / 2),
    integratedLufs: settings.targetLufs,
    truePeakDbtp: round(settings.ceilingDbtp - 0.3 + Math.abs(wobble) / 10),
    compressorReductionDb: round(-settings.dynamics * (1.5 + Math.abs(wobble) / 2)),
    limiterReductionDb: round(-Math.min(12, lift / 2 + Math.abs(wobble)))
  };
}
