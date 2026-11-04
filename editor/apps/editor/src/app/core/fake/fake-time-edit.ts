import { ApiError, type AutomationPoint, type EditCommand, type Project } from '@anode/api-client';
import { automationValueAt } from '@anode/timeline';

/** Commands the fake's time edits are made of. */
export interface TimeEditContext {
  /** Applies a split or a loop conversion. */
  apply(project: Project, command: EditCommand): Project;
  /** Where an audio region ends, in ticks. */
  endOf(project: Project, region: Project['regions'][number]): number;
}

/** What an edit of time does to positions, as the Model's `TimeChange`. */
interface TimeChange {
  at: number;
  removed: number;
  added: number;
}

const startOf = (c: TimeChange, tick: number): number =>
  tick < c.at ? tick : tick >= c.at + c.removed ? tick + c.added - c.removed : c.at;
const stopOf = (c: TimeChange, tick: number): number =>
  tick <= c.at ? tick : tick >= c.at + c.removed ? tick + c.added - c.removed : c.at;
const inside = (c: TimeChange, tick: number): boolean => tick >= c.at && tick < c.at + c.removed;

/** Splits what crosses `tick`, loops into regions first. Take folders stay whole here, unlike the Server. */
function splitAt(project: Project, tick: number, context: TimeEditContext): Project {
  const crossing = [...project.regions, ...project.midiRegions].filter(r => r.loopTicks > 0 && r.startTick < tick);
  let next = crossing.length
    ? context.apply(project, { type: 'convertLoops', regionIds: crossing.map(r => r.id) })
    : project;
  for (const r of next.regions) {
    if (r.startTick < tick && tick < context.endOf(next, r) - 1e-6) {
      next = context.apply(next, { type: 'splitRegion', regionId: r.id, atTick: tick });
    }
  }
  for (const r of next.midiRegions) {
    if (r.startTick < tick && tick < r.startTick + r.lengthTicks) {
      next = context.apply(next, { type: 'splitRegion', regionId: r.id, atTick: tick });
    }
  }
  return next;
}

/** Moves regions: those after the change by it; those inside a cut go, and a ripple leaves anything else alone. */
function movedRegions(project: Project, c: TimeChange, ripple: boolean): Project {
  const moved = <T extends { startTick: number }>(list: readonly T[]): T[] =>
    list.flatMap(r => {
      if (r.startTick >= c.at + c.removed) {
        return [{ ...r, startTick: startOf(c, r.startTick) }];
      }
      return !ripple && inside(c, r.startTick) ? [] : [r];
    });
  const spanned = <T extends { startTick: number; lengthTicks: number }>(list: readonly T[]): T[] =>
    list.flatMap(r => {
      if (ripple && r.startTick < c.at + c.removed) {
        return [r];
      }
      const startTick = startOf(c, r.startTick);
      const end = stopOf(c, r.startTick + r.lengthTicks);
      return end > startTick ? [{ ...r, startTick, lengthTicks: end - startTick }] : [];
    });
  return {
    ...project,
    regions: moved(project.regions),
    midiRegions: moved(project.midiRegions),
    takeFolders: moved(project.takeFolders).map(f => {
      const by = f.startTick - (project.takeFolders.find(o => o.id === f.id)?.startTick ?? f.startTick);
      return {
        ...f,
        takes: f.takes.map(t => ({ ...t, startTick: t.startTick + by })),
        comp: f.comp.map(s => ({ ...s, startTick: s.startTick + by, endTick: s.endTick + by }))
      };
    }),
    patternRegions: spanned(project.patternRegions),
    sessionPlayerRegions: spanned(project.sessionPlayerRegions)
  };
}

/** A global track after a change: the event in force at a cut's end holds from its start. */
function shifted<T extends { tick: number }>(events: readonly T[], c: TimeChange, anchored: boolean): T[] {
  const kept: T[] = [];
  const after: T[] = [];
  let held: T | null = null;
  for (const e of events) {
    if (e.tick < c.at || (anchored && e.tick === 0 && c.removed === 0)) {
      kept.push(e);
    } else if (e.tick >= c.at + c.removed) {
      after.push({ ...e, tick: startOf(c, e.tick) });
    } else {
      held = e;
    }
  }
  const firstAfter = after[0];
  if (held && !(firstAfter && firstAfter.tick === c.at)) {
    kept.push({ ...held, tick: c.at });
  }
  return [...kept, ...after];
}

/** Each signature change on the nearest bar line of the one before it. */
function onBarLines<T extends { tick: number; numerator: number; denominator: number }>(events: readonly T[]): T[] {
  const placed: T[] = [];
  for (const e of events) {
    const previous = placed[placed.length - 1];
    if (!previous) {
      placed.push(e);
      continue;
    }
    const bar = (previous.numerator * 3840) / previous.denominator;
    const bars = Math.max(1, Math.round((e.tick - previous.tick) / bar));
    placed.push({ ...e, tick: previous.tick + bars * bar });
  }
  return placed;
}

/** A lane's points after a change, holding through an insertion and keeping both values over a cut. */
function movedPoints(points: readonly AutomationPoint[], c: TimeChange): AutomationPoint[] {
  const last = points[points.length - 1];
  if (!last || last.tick < c.at) {
    return [...points];
  }
  const end = c.at + c.removed;
  const before = points.filter(p => p.tick < c.at);
  const after = points.filter(p => p.tick >= end).map(p => ({ ...p, tick: startOf(c, p.tick) }));
  const at = automationValueAt(points, c.at) ?? 0;
  if (c.removed === 0) {
    return before.length > 0 && after.length > 0
      ? [...before, { tick: c.at, value: at, curve: 0 }, { tick: c.at + c.added, value: at, curve: 0 }, ...after]
      : [...before, ...after];
  }
  const right = automationValueAt(points, end) ?? 0;
  return [
    ...before,
    ...(before.length > 0 && at !== right ? [{ tick: c.at, value: at, curve: 0 }] : []),
    { tick: c.at, value: right, curve: 0 },
    ...after
  ];
}

/** Everything but regions after a change of time. */
function movedGlobals(project: Project, c: TimeChange): Project {
  const s = project.settings;
  const cycleEnd = stopOf(c, s.cycle.endTick);
  const cycleStart = startOf(c, s.cycle.startTick);
  const punchIn = startOf(c, s.recording.punchInTick);
  const punchOut = stopOf(c, s.recording.punchOutTick);
  const span = <T extends { startTick: number; lengthTicks: number }>(list: readonly T[]): T[] =>
    list.flatMap(r => {
      const startTick = startOf(c, r.startTick);
      const end = stopOf(c, r.startTick + r.lengthTicks);
      return end > startTick ? [{ ...r, startTick, lengthTicks: end - startTick }] : [];
    });
  const tempoEvents = shifted(s.tempoEvents, c, true);
  const signatures = onBarLines(shifted(s.signatures, c, true));
  return {
    ...project,
    settings: {
      ...s,
      tempo: tempoEvents[0]?.bpm ?? s.tempo,
      tempoEvents,
      signatures,
      signature: {
        numerator: signatures[0]?.numerator ?? s.signature.numerator,
        denominator: signatures[0]?.denominator ?? s.signature.denominator
      },
      keys: shifted(s.keys, c, true),
      cycle: {
        ...s.cycle,
        startTick: cycleStart,
        endTick: cycleEnd > cycleStart ? cycleEnd : cycleStart + s.cycle.endTick - s.cycle.startTick
      },
      recording: {
        ...s.recording,
        punchInTick: punchIn,
        punchOutTick: punchOut > punchIn ? punchOut : punchIn + s.recording.punchOutTick - s.recording.punchInTick
      },
      endTick: Math.max(1, stopOf(c, s.endTick))
    },
    markers: project.markers.flatMap(m =>
      inside(c, m.tick)
        ? []
        : [{ ...m, tick: startOf(c, m.tick), lengthTicks: stopOf(c, m.tick + m.lengthTicks) - startOf(c, m.tick) }]
    ),
    arrangement: span(project.arrangement),
    automation: project.automation.map(l => ({ ...l, points: movedPoints(l.points, c) })),
    chordTrack: {
      chords: shifted(project.chordTrack.chords, c, false),
      scales: shifted(project.chordTrack.scales, c, false)
    },
    ...(project.movie ? { movie: { ...project.movie, startTick: startOf(c, project.movie.startTick) } } : {})
  };
}

/** `insertTime`, `cutTime` and `rippleDelete`, as the Model's (a ramp's tempo is not held, and take folders stay whole). */
export function applyTimeEdit(
  project: Project,
  command: Extract<EditCommand, { type: 'insertTime' | 'cutTime' | 'rippleDelete' }>,
  context: TimeEditContext
): Project {
  switch (command.type) {
    case 'insertTime': {
      if (!(command.lengthTicks > 0) || !(command.atTick >= 0)) {
        throw new ApiError(422, 'Insert time needs a place and a length.');
      }
      const change = { at: command.atTick, removed: 0, added: command.lengthTicks };
      return movedGlobals(movedRegions(splitAt(project, command.atTick, context), change, false), change);
    }
    case 'cutTime': {
      if (!(command.endTick > command.startTick) || !(command.startTick >= 0)) {
        throw new ApiError(422, 'A cut ends after it starts.');
      }
      const change = { at: command.startTick, removed: command.endTick - command.startTick, added: 0 };
      const split = splitAt(splitAt(project, command.startTick, context), command.endTick, context);
      return movedGlobals(movedRegions(split, change, false), change);
    }
    case 'rippleDelete': {
      const ends = new Map<string, [number, number]>();
      for (const r of project.regions) {
        ends.set(r.id, [r.startTick, context.endOf(project, r) + r.loopTicks]);
      }
      for (const r of [...project.midiRegions, ...project.patternRegions]) {
        ends.set(r.id, [r.startTick, r.startTick + r.lengthTicks + r.loopTicks]);
      }
      for (const r of [...project.sessionPlayerRegions, ...project.takeFolders]) {
        ends.set(r.id, [r.startTick, r.startTick + r.lengthTicks]);
      }
      const spans = command.regionIds.map(id => {
        const span = ends.get(id);
        if (!span) {
          throw new ApiError(422, `No region ${id}.`);
        }
        return span;
      });
      const merged: [number, number][] = [];
      for (const [start, end] of [...spans].sort((a, b) => a[0] - b[0])) {
        const last = merged[merged.length - 1];
        if (last && start <= last[1]) {
          last[1] = Math.max(last[1], end);
        } else {
          merged.push([start, end]);
        }
      }
      let next = context.apply(project, { type: 'deleteRegions', regionIds: [...command.regionIds] });
      for (const [start, end] of merged.reverse()) {
        const change = { at: start, removed: end - start, added: 0 };
        next = movedGlobals(movedRegions(next, change, true), change);
      }
      return next;
    }
  }
}
