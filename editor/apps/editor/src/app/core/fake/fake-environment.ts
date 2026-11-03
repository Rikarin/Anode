import {
  ApiError,
  type EditCommand,
  type EnvironmentObject,
  type EnvironmentObjectKind,
  EnvironmentObjectKinds,
  type EnvironmentParameter,
  type MidiEnvironment,
  type Project
} from '@anode/api-client';
import {
  cableProblem,
  chordProblem,
  kindLimitProblem,
  mergeParameters,
  parameterSpecs,
  tapsProblem,
  transformProblem
} from '../../environment/environment-model';
import { FakeMidiEffects } from './fake-plugins-phase6';

/** A command of one type. */
type Command<T extends EditCommand['type']> = Extract<EditCommand, { type: T }>;

/** What the Environment's commands need from the fake Server. */
export interface EnvironmentContext {
  /** A new id with this prefix (`eob_7`). */
  id(prefix: string): string;
}

function refuse(message: string): never {
  throw new ApiError(422, message);
}

function check(problem: string | null): void {
  if (problem) {
    refuse(problem);
  }
}

/** The arpeggiator's and chord memoriser's parameters: their MIDI FX's, from the fake catalogue. */
function effectParameters(pluginId: string): EnvironmentParameter[] {
  return (FakeMidiEffects.find(p => p.id === pluginId)?.parameters ?? []).map(p => ({
    id: p.id,
    name: p.name,
    min: p.min,
    max: p.max,
    default: p.default
  }));
}

function withEnvironment(project: Project, environment: MidiEnvironment): Project {
  return { ...project, environment };
}

function findObject(environment: MidiEnvironment, id: string): EnvironmentObject {
  return environment.objects.find(o => o.id === id) ?? refuse(`No Environment object ${id}.`);
}

/** The settings a command gives an object of `kind`: each only on the kinds that use it; empty strings clear. */
type Settings = Pick<
  Command<'setEnvironmentObject'>,
  'port' | 'trackId' | 'address' | 'conditions' | 'operations' | 'taps' | 'chord'
>;

function applySettings(project: Project, object: EnvironmentObject, settings: Settings): EnvironmentObject {
  const kind = object.kind;
  const name = EnvironmentObjectKinds[kind].name;
  const only = (value: unknown, kinds: readonly EnvironmentObjectKind[], what: string) => {
    if (value !== null && value !== undefined && !kinds.includes(kind)) {
      refuse(`${name} has no ${what}.`);
    }
  };
  only(settings.port, ['physicalInput', 'midiPort'], 'port');
  only(settings.trackId, ['trackInput', 'controlTarget'], 'track');
  only(settings.address, ['controlTarget'], 'parameter address');
  only(settings.conditions, ['transformer'], 'conditions');
  only(settings.operations, ['transformer'], 'operations');
  only(settings.taps, ['delayLine'], 'taps');
  only(settings.chord, ['chordMemorizer'], 'chord');
  const text = (value: string | null | undefined, current: string | null | undefined) =>
    value === null || value === undefined ? (current ?? null) : value.trim() || null;
  const next: EnvironmentObject = {
    ...object,
    port: text(settings.port, object.port),
    trackId: text(settings.trackId, object.trackId),
    address: text(settings.address, object.address),
    conditions: settings.conditions ? structuredClone(settings.conditions) : (object.conditions ?? null),
    operations: settings.operations ? structuredClone(settings.operations) : (object.operations ?? null),
    taps: settings.taps ? structuredClone(settings.taps) : (object.taps ?? null),
    chord: settings.chord ? (settings.chord.length ? [...settings.chord] : null) : (object.chord ?? null)
  };
  if (next.trackId && !project.tracks.some(t => t.id === next.trackId)) {
    refuse(`No track ${next.trackId}.`);
  }
  if (kind === 'trackInput' && next.trackId) {
    const track = project.tracks.find(t => t.id === next.trackId);
    if (track && (track.kind === 'audio' || track.kind === 'aux' || track.kind === 'vca')) {
      refuse(`${track.name} does not play MIDI.`);
    }
  }
  check(transformProblem(next.conditions, next.operations));
  check(tapsProblem(next.taps));
  check(chordProblem(next.chord));
  return next;
}

function withParameters(
  object: EnvironmentObject,
  given: Readonly<Record<string, number>> | null | undefined
): EnvironmentObject {
  const merged = mergeParameters(object.kind, object.parameters, given, parameterSpecs(object.kind, effectParameters));
  if ('problem' in merged) {
    refuse(merged.problem);
  }
  return { ...object, parameters: merged.parameters };
}

function position(value: number, axis: string): number {
  if (!Number.isFinite(value)) {
    refuse(`An object's ${axis} must be a number.`);
  }
  return Math.round(value * 100) / 100;
}

function addObject(project: Project, command: Command<'addEnvironmentObject'>, ctx: EnvironmentContext): Project {
  if (!(command.kind in EnvironmentObjectKinds)) {
    refuse(`${command.kind as string} is not an Environment object kind.`);
  }
  let environment = project.environment;
  let layerId = command.layerId ?? environment.layers[0]?.id ?? null;
  if (layerId && !environment.layers.some(l => l.id === layerId)) {
    refuse(`No Environment layer ${layerId}.`);
  }
  if (!layerId) {
    layerId = ctx.id('env');
    environment = { ...environment, layers: [{ id: layerId, name: 'Layer 1' }] };
  }
  check(kindLimitProblem(environment, command.kind, command.port?.trim() || null));
  let object: EnvironmentObject = {
    id: ctx.id('eob'),
    layerId,
    kind: command.kind,
    name: command.name?.trim() || EnvironmentObjectKinds[command.kind].name,
    x: position(command.x, 'x'),
    y: position(command.y, 'y'),
    parameters: {},
    port: null,
    trackId: null,
    address: null,
    conditions: null,
    operations: null,
    taps: null,
    chord: null
  };
  object = withParameters(applySettings(project, object, command), command.parameters);
  return withEnvironment(project, { ...environment, objects: [...environment.objects, object] });
}

function setObject(project: Project, command: Command<'setEnvironmentObject'>): Project {
  const environment = project.environment;
  const object = findObject(environment, command.objectId);
  if (command.layerId && !environment.layers.some(l => l.id === command.layerId)) {
    refuse(`No Environment layer ${command.layerId}.`);
  }
  let next: EnvironmentObject = {
    ...object,
    name: command.name?.trim() || object.name,
    layerId: command.layerId ?? object.layerId,
    x: command.x === null || command.x === undefined ? object.x : position(command.x, 'x'),
    y: command.y === null || command.y === undefined ? object.y : position(command.y, 'y')
  };
  next = withParameters(applySettings(project, next, command), command.parameters);
  check(kindLimitProblem(environment, next.kind, next.port, next.id));
  return withEnvironment(project, {
    ...environment,
    objects: environment.objects.map(o => (o.id === next.id ? next : o))
  });
}

function removeObjects(environment: MidiEnvironment, ids: ReadonlySet<string>): MidiEnvironment {
  return {
    ...environment,
    objects: environment.objects.filter(o => !ids.has(o.id)),
    cables: environment.cables.filter(c => !ids.has(c.fromObjectId) && !ids.has(c.toObjectId))
  };
}

/**
 * The Environment's commands (docs/12-phase-11.md, ADR-0015), validated as the Server does: known kinds, inlets and
 * outlets that exist, no duplicate cables and no cycles, one physical input for every input and one sequencer input,
 * parameters in range (the arpeggiator's and chord memoriser's are their MIDI FX's), each setting on its kinds only.
 */
export function applyEnvironmentCommand(project: Project, command: EditCommand, ctx: EnvironmentContext): Project {
  const environment = project.environment;
  switch (command.type) {
    case 'addEnvironmentLayer': {
      const name = command.name?.trim() || `Layer ${environment.layers.length + 1}`;
      return withEnvironment(project, { ...environment, layers: [...environment.layers, { id: ctx.id('env'), name }] });
    }
    case 'setEnvironmentLayer': {
      const layer = environment.layers.find(l => l.id === command.layerId) ?? refuse(`No layer ${command.layerId}.`);
      const renamed = { ...layer, name: command.name?.trim() || layer.name };
      const layers = environment.layers.filter(l => l.id !== layer.id);
      const at =
        command.index === null || command.index === undefined
          ? environment.layers.indexOf(layer)
          : Math.min(layers.length, Math.max(0, Math.round(command.index)));
      layers.splice(at, 0, renamed);
      return withEnvironment(project, { ...environment, layers });
    }
    case 'removeEnvironmentLayer': {
      if (!environment.layers.some(l => l.id === command.layerId)) {
        refuse(`No layer ${command.layerId}.`);
      }
      const ids = new Set(environment.objects.filter(o => o.layerId === command.layerId).map(o => o.id));
      return withEnvironment(project, {
        ...removeObjects(environment, ids),
        layers: environment.layers.filter(l => l.id !== command.layerId)
      });
    }
    case 'addEnvironmentObject':
      return addObject(project, command, ctx);
    case 'setEnvironmentObject':
      return setObject(project, command);
    case 'removeEnvironmentObjects': {
      command.objectIds.forEach(id => findObject(environment, id));
      return withEnvironment(project, removeObjects(environment, new Set(command.objectIds)));
    }
    case 'connectEnvironment': {
      const { fromObjectId, outlet, toObjectId, inlet } = command;
      check(cableProblem(environment, fromObjectId, outlet, toObjectId, inlet));
      return withEnvironment(project, {
        ...environment,
        cables: [...environment.cables, { id: ctx.id('ecb'), fromObjectId, outlet, toObjectId, inlet }]
      });
    }
    case 'disconnectEnvironment': {
      for (const id of command.cableIds) {
        if (!environment.cables.some(c => c.id === id)) {
          refuse(`No cable ${id}.`);
        }
      }
      const ids = new Set(command.cableIds);
      return withEnvironment(project, { ...environment, cables: environment.cables.filter(c => !ids.has(c.id)) });
    }
    default:
      return project;
  }
}
