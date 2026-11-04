import { ApiError, type AudioRegion, type Project } from '@anode/api-client';
import { loopEnd, loopRepeats, nextStartAfter } from '../../tracks/region-loop';

/** A region's loop as a command sets it: 0 or more ticks. */
export function checkedLoop(value: number | null | undefined, current: number): number {
  if (value === null || value === undefined) {
    return current;
  }
  if (!(value >= 0)) {
    throw new ApiError(422, 'A region loop is 0 or more ticks.');
  }
  return Math.round(value);
}

/** A looped region is not split or joined: its repeats become regions first. */
export function refuseLooped(region: { id: string; loopTicks: number }): void {
  if (region.loopTicks > 0) {
    throw new ApiError(422, `Region '${region.id}' loops: convert its loops to regions first.`);
  }
}

/** The start of every region of any kind on a track. */
function startsOn(project: Project, trackId: string): number[] {
  return [
    ...project.regions,
    ...project.midiRegions,
    ...project.patternRegions,
    ...project.sessionPlayerRegions,
    ...project.takeFolders
  ]
    .filter(r => r.trackId === trackId)
    .map(r => r.startTick);
}

/** `convertLoops`: each repeat becomes a region of its own, the last one cut where it stopped playing. */
export function convertLoops(
  project: Project,
  ids: readonly string[],
  endOf: (region: AudioRegion) => number,
  id: (prefix: string) => string,
  nextEvent: () => number
): Project {
  const wanted = new Set(ids);
  const until = (trackId: string, start: number, end: number, loop: number) =>
    loopEnd(end, loop, nextStartAfter(startsOn(project, trackId), start));
  return {
    ...project,
    regions: project.regions.flatMap(r => {
      if (!wanted.has(r.id) || r.loopTicks === 0) {
        return [r];
      }
      const end = endOf(r);
      const period = end - r.startTick;
      const rate = r.length / period;
      const stop = until(r.trackId, r.startTick, end, r.loopTicks);
      const repeats = loopRepeats(r.startTick, period, stop).map(repeat => ({
        ...r,
        id: id('rgn'),
        startTick: Math.round(repeat.startTick),
        length: Math.round(repeat.lengthTicks * rate),
        loopTicks: 0
      }));
      return [{ ...r, loopTicks: 0 }, ...repeats];
    }),
    midiRegions: project.midiRegions.flatMap(r => {
      if (!wanted.has(r.id) || r.loopTicks === 0) {
        return [r];
      }
      const end = r.startTick + r.lengthTicks;
      const stop = until(r.trackId, r.startTick, end, r.loopTicks);
      const repeats = loopRepeats(r.startTick, r.lengthTicks, stop).map(repeat => ({
        ...r,
        id: id('mid'),
        startTick: repeat.startTick,
        lengthTicks: repeat.lengthTicks,
        loopTicks: 0,
        notes: r.notes.map(n => ({ ...n, id: nextEvent() })),
        controls: r.controls.map(c => ({ ...c, id: nextEvent() }))
      }));
      return [{ ...r, loopTicks: 0 }, ...repeats];
    }),
    patternRegions: project.patternRegions.flatMap(r => {
      if (!wanted.has(r.id) || r.loopTicks === 0) {
        return [r];
      }
      const end = r.startTick + r.lengthTicks;
      const stop = until(r.trackId, r.startTick, end, r.loopTicks);
      const repeats = loopRepeats(r.startTick, r.lengthTicks, stop).map(repeat => ({
        ...r,
        id: id('pat'),
        startTick: repeat.startTick,
        lengthTicks: repeat.lengthTicks,
        loopTicks: 0
      }));
      return [{ ...r, loopTicks: 0 }, ...repeats];
    })
  };
}
