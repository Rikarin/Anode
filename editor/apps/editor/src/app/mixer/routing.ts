import type { IoLabels, Track } from '@anode/api-client';

/** Buses 1…64 (docs/05-phase-4.md). */
export const BusCount = 64;
/** Device output pairs the pickers offer when the I/O labels name none (Output 1-2 … 15-16). */
export const DefaultOutputPairs = 8;

/** Where a strip goes: the master, a bus, or a device output pair (which bypasses the master). */
export type Route = { kind: 'master' } | { kind: 'bus'; bus: number } | { kind: 'output'; pair: number };

/** A strip's `output` string (`master`, `bus:N`, `output:N`) as a route; null for one this editor cannot read. */
export function parseRoute(output: string | null | undefined): Route | null {
  if (!output || output === 'master') {
    return output === 'master' ? { kind: 'master' } : null;
  }
  const match = /^(bus|output):(\d+)$/.exec(output);
  if (!match?.[2]) {
    return null;
  }
  const n = Number(match[2]);
  return match[1] === 'bus' ? { kind: 'bus', bus: n } : { kind: 'output', pair: n };
}

export function busRoute(bus: number): string {
  return `bus:${bus}`;
}

export function outputRoute(pair: number): string {
  return `output:${pair}`;
}

/** The buses a track feeds directly: its sends and, when it goes to a bus, its output. */
export function busesFedBy(track: Track): number[] {
  const route = parseRoute(track.strip.output);
  const buses = track.strip.sends.map(send => send.bus);
  if (route?.kind === 'bus') {
    buses.push(route.bus);
  }
  return [...new Set(buses)];
}

/** The aux tracks listening to each bus. */
function auxByBus(tracks: readonly Track[]): Map<number, Track[]> {
  const map = new Map<number, Track[]>();
  for (const track of tracks) {
    if (track.kind === 'aux' && track.inputBus > 0) {
      map.set(track.inputBus, [...(map.get(track.inputBus) ?? []), track]);
    }
  }
  return map;
}

/** Every bus a signal on bus `from` reaches (through the aux tracks listening to it), `from` included. */
export function reachableBuses(tracks: readonly Track[], from: number, skipTrack?: string): Set<number> {
  const listeners = auxByBus(tracks);
  const seen = new Set<number>([from]);
  const queue = [from];
  while (queue.length > 0) {
    const bus = queue.shift() as number;
    for (const aux of listeners.get(bus) ?? []) {
      if (aux.id === skipTrack) {
        continue;
      }
      for (const next of busesFedBy(aux)) {
        if (!seen.has(next)) {
          seen.add(next);
          queue.push(next);
        }
      }
    }
  }
  return seen;
}

/**
 * Whether routing `trackId` to bus `bus` (by its output or a send) would make a loop, which the Server refuses: an aux
 * track's signal may not come back to the bus it listens to, directly or through other aux tracks. Other tracks are fed
 * by no bus, so they never loop.
 */
export function wouldLoop(tracks: readonly Track[], trackId: string, bus: number): boolean {
  const track = tracks.find(t => t.id === trackId);
  if (track?.kind !== 'aux' || track.inputBus <= 0) {
    return false;
  }
  return reachableBuses(tracks, bus, track.id).has(track.inputBus);
}

/** The buses a picker offers a track: every bus, with whether something already uses it and whether it would loop. */
export function busChoices(
  tracks: readonly Track[],
  trackId: string | null,
  count = BusCount
): { bus: number; used: boolean; loops: boolean }[] {
  const used = new Set(tracks.flatMap(t => [...busesFedBy(t), ...(t.kind === 'aux' ? [t.inputBus] : [])]));
  return Array.from({ length: count }, (_, i) => {
    const bus = i + 1;
    return { bus, used: used.has(bus), loops: trackId !== null && wouldLoop(tracks, trackId, bus) };
  });
}

/** The lowest bus nothing uses yet (a new send's and a new aux's), or 0 when all 64 are taken. */
export function lowestFreeBus(tracks: readonly Track[]): number {
  return busChoices(tracks, null).find(choice => !choice.used)?.bus ?? 0;
}

/** A bus's name: its I/O label, else "Bus N". */
export function busName(bus: number, labels?: IoLabels | null): string {
  return labels?.buses.find(l => l.number === bus)?.name || `Bus ${bus}`;
}

/** A device output pair's name: its I/O label, else "Output 1-2". */
export function outputPairName(pair: number, labels?: IoLabels | null): string {
  return labels?.outputs.find(l => l.number === pair)?.name || `Output ${2 * pair - 1}-${2 * pair}`;
}

/** A strip's output as the Mixer shows it: "Stereo Out", a bus's or an output pair's name. */
export function routeName(output: string, labels?: IoLabels | null): string {
  const route = parseRoute(output);
  switch (route?.kind) {
    case 'master':
      return 'Stereo Out';
    case 'bus':
      return busName(route.bus, labels);
    case 'output':
      return outputPairName(route.pair, labels);
    default:
      return output || 'No Output';
  }
}

/**
 * The strips a track's signal flows through (Logic's signal-flow highlight): the aux tracks its sends and output reach,
 * and theirs in turn, and the master when any of them ends there (`master` in the set). The track itself is included.
 */
export function signalFlow(tracks: readonly Track[], trackId: string | null): Set<string> {
  const flow = new Set<string>();
  if (trackId === null) {
    return flow;
  }
  const listeners = auxByBus(tracks);
  const queue = tracks.filter(t => t.id === trackId);
  while (queue.length > 0) {
    const track = queue.shift() as Track;
    if (flow.has(track.id)) {
      continue;
    }
    flow.add(track.id);
    if (track.kind === 'vca' || track.kind === 'stack') {
      continue;
    }
    if (parseRoute(track.strip.output)?.kind === 'master') {
      flow.add('master');
    }
    for (const bus of busesFedBy(track)) {
      queue.push(...(listeners.get(bus) ?? []));
    }
  }
  return flow;
}
