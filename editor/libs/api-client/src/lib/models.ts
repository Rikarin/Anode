import type { components } from '../generated/schema';

type Schemas = components['schemas'];

export type SystemInfo = Schemas['SystemInfo'];
export type EngineStatus = Schemas['EngineStatus'];
export type ProblemDetails = Schemas['ProblemDetails'];

/**
 * What the Server sends on `/ws`. The first message after connecting is `connected`; `engine` reports a change of the link to the engine host; `resync` replaces events a client
 * fell too far behind to receive, so it refetches what it shows. Project deltas and telemetry frames join this union
 * as the Server grows them (docs/01-implementation-plan.md §4.2–4.3).
 */
export type ServerMessage =
  | { type: 'connected'; version: string }
  | { type: 'engine'; engine: EngineStatus }
  | { type: 'resync' };
