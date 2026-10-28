import {
  ApiError,
  type ArrangementMarker,
  type AudioRegion,
  type CompSegment,
  type MidiRegion,
  type Project,
  type Take,
  type TakeFolder,
  type Track
} from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import { RegionTiming } from '../../music/region-timing';
import { TempoMap } from '../../music/tempo-map';
import { normalizeComp, swipeComp } from '../../tracks/comp';

/** Makes the next id with a prefix (`rgn_12`); the fake Server's counter. */
export type IdMaker = (prefix: string) => string;

export function tempoOf(project: Project): TempoMap {
  return new TempoMap(project.settings.tempoEvents, project.settings.tempo);
}

export function meterOf(project: Project): MeterMap {
  return new MeterMap(project.settings.signatures);
}

/**
 * Where an audio region ends, in ticks: through its timing (Phase 5's RegionTiming: flex, follow) for a whole region,
 * else (a take) under the project's tempo track.
 */
export function audioEnd(project: Project, region: Pick<AudioRegion, 'startTick' | 'length'>): number {
  if ('follow' in region && 'trackId' in region && 'fileId' in region) {
    const r = region as AudioRegion;
    const track = project.tracks.find(t => t.id === r.trackId);
    const file = project.audioFiles.find(f => f.id === r.fileId);
    return new RegionTiming(r, track?.flexOn ?? false, file?.tempo ?? null, {
      tempo: tempoOf(project),
      meter: meterOf(project),
      sampleRate: project.sampleRate
    }).endTick;
  }
  return tempoOf(project).tickAfterFrames(region.startTick, region.length, project.sampleRate);
}

/** Frames between two ticks. */
export function framesBetween(project: Project, from: number, to: number): number {
  return Math.round(tempoOf(project).framesBetween(from, to - from, project.sampleRate));
}

/** Moves everything that starts at or after `from` (regions, take folders, markers, sections) by `by` ticks. */
export function shiftFrom(
  project: Project,
  from: number,
  by: number,
  except: ReadonlySet<string> = new Set()
): Project {
  if (by === 0) {
    return project;
  }
  const moves = (id: string, start: number) => start >= from && !except.has(id);
  return {
    ...project,
    regions: project.regions.map(r => (moves(r.id, r.startTick) ? { ...r, startTick: r.startTick + by } : r)),
    midiRegions: project.midiRegions.map(r => (moves(r.id, r.startTick) ? { ...r, startTick: r.startTick + by } : r)),
    takeFolders: project.takeFolders.map(f => (moves(f.id, f.startTick) ? shiftFolder(f, by) : f)),
    markers: project.markers.map(m => (moves(m.id, m.tick) ? { ...m, tick: m.tick + by } : m)),
    arrangement: project.arrangement.map(s => (moves(s.id, s.startTick) ? { ...s, startTick: s.startTick + by } : s))
  };
}

export function shiftFolder(folder: TakeFolder, by: number): TakeFolder {
  return {
    ...folder,
    startTick: folder.startTick + by,
    takes: folder.takes.map(t => ({ ...t, startTick: t.startTick + by })),
    comp: folder.comp.map(s => ({ ...s, startTick: s.startTick + by, endTick: s.endTick + by }))
  };
}

/** Everything that starts in `[start, end)`: the content of a section. */
function contentIn(project: Project, start: number, end: number) {
  const inside = (tick: number) => tick >= start && tick < end;
  return {
    regions: project.regions.filter(r => inside(r.startTick)),
    midiRegions: project.midiRegions.filter(r => inside(r.startTick)),
    takeFolders: project.takeFolders.filter(f => inside(f.startTick)),
    markers: project.markers.filter(m => inside(m.tick))
  };
}

function withoutIds(project: Project, ids: ReadonlySet<string>): Project {
  return {
    ...project,
    regions: project.regions.filter(r => !ids.has(r.id)),
    midiRegions: project.midiRegions.filter(r => !ids.has(r.id)),
    takeFolders: project.takeFolders.filter(f => !ids.has(f.id)),
    markers: project.markers.filter(m => !ids.has(m.id)),
    arrangement: project.arrangement.filter(s => !ids.has(s.id))
  };
}

/**
 * `moveSection` as the contract states it: the section and what starts in it go to `toTick` (a section boundary or the
 * end of the last section), material from there on moves later by its length, and a move closes the gap it left.
 */
export function moveSection(project: Project, sectionId: string, toTick: number, copy: boolean, id: IdMaker): Project {
  const section = project.arrangement.find(s => s.id === sectionId);
  if (!section) {
    throw new ApiError(422, `No section ${sectionId}.`);
  }
  const boundaries = new Set([
    ...project.arrangement.map(s => s.startTick),
    Math.max(...project.arrangement.map(s => s.startTick + s.lengthTicks))
  ]);
  if (!boundaries.has(toTick)) {
    throw new ApiError(422, 'A section moves to a section boundary or the end of the last section.');
  }
  const length = section.lengthTicks;
  const start = section.startTick;
  if (!copy && (toTick === start || toTick === start + length)) {
    return project; // where it is already
  }
  // Make room at the target.
  let next = shiftFrom(project, toTick, length);
  const movedStart = start >= toTick ? start + length : start;
  const offset = toTick - movedStart;
  // The copy (for a move, the original content and section are then removed).
  const shiftedContent = contentIn(next, movedStart, movedStart + length);
  next = {
    ...next,
    regions: [
      ...next.regions,
      ...shiftedContent.regions.map(r => ({ ...r, id: id('rgn'), startTick: r.startTick + offset }))
    ],
    midiRegions: [
      ...next.midiRegions,
      ...shiftedContent.midiRegions.map(r => ({ ...r, id: id('mid'), startTick: r.startTick + offset }))
    ],
    takeFolders: [...next.takeFolders, ...shiftedContent.takeFolders.map(f => copyFolder(shiftFolder(f, offset), id))],
    markers: [...next.markers, ...shiftedContent.markers.map(m => ({ ...m, id: id('mrk'), tick: m.tick + offset }))],
    arrangement: [...next.arrangement, { ...section, id: copy ? id('sec') : section.id, startTick: toTick }]
  };
  if (!copy) {
    const original = new Set([
      ...shiftedContent.regions.map(r => r.id),
      ...shiftedContent.midiRegions.map(r => r.id),
      ...shiftedContent.takeFolders.map(f => f.id),
      ...shiftedContent.markers.map(m => m.id)
    ]);
    // The section itself was re-added at the target with its id: drop the one at the old place.
    next = {
      ...withoutIds(next, original),
      arrangement: next.arrangement.filter(s => !(s.id === section.id && s.startTick === movedStart))
    };
    next = shiftFrom(next, movedStart + length, -length);
  }
  return { ...next, arrangement: sortSections(next.arrangement) };
}

/** Deletes sections; with their content, everything that starts in them goes and later material closes the gaps. */
export function deleteSections(project: Project, ids: readonly string[], removeContent: boolean): Project {
  const sections = ids.map(sectionId => {
    const s = project.arrangement.find(x => x.id === sectionId);
    if (!s) {
      throw new ApiError(422, `No section ${sectionId}.`);
    }
    return s;
  });
  let next: Project = { ...project, arrangement: project.arrangement.filter(s => !ids.includes(s.id)) };
  if (!removeContent) {
    return next;
  }
  // Latest first, so earlier positions stay valid.
  for (const s of [...sections].sort((a, b) => b.startTick - a.startTick)) {
    const content = contentIn(next, s.startTick, s.startTick + s.lengthTicks);
    next = withoutIds(
      next,
      new Set([...content.regions, ...content.midiRegions, ...content.takeFolders, ...content.markers].map(x => x.id))
    );
    next = shiftFrom(next, s.startTick + s.lengthTicks, -s.lengthTicks);
  }
  return next;
}

export function sortSections(sections: readonly ArrangementMarker[]): ArrangementMarker[] {
  return [...sections].sort((a, b) => a.startTick - b.startTick);
}

/** Refuses a section that would overlap another. */
export function checkSections(sections: readonly ArrangementMarker[]): void {
  const sorted = sortSections(sections);
  for (let i = 1; i < sorted.length; i++) {
    const a = sorted[i - 1] as ArrangementMarker;
    const b = sorted[i] as ArrangementMarker;
    if (b.startTick < a.startTick + a.lengthTicks) {
      throw new ApiError(422, `${b.name} would overlap ${a.name}: sections do not overlap.`);
    }
  }
  if (sorted.some(s => s.lengthTicks <= 0 || s.startTick < 0)) {
    throw new ApiError(422, 'A section needs a start and a length.');
  }
}

/** A take folder with fresh ids for it and its takes. */
export function copyFolder(folder: TakeFolder, id: IdMaker): TakeFolder {
  const ids = new Map(folder.takes.map(t => [t.id, id('tak')]));
  return {
    ...folder,
    id: id('tkf'),
    takes: folder.takes.map(t => ({ ...t, id: ids.get(t.id) ?? t.id })),
    comp: folder.comp.map(s => ({ ...s, takeId: ids.get(s.takeId) ?? s.takeId }))
  };
}

/** `copyRegions`: copies of audio and MIDI regions and take folders, `offset` ticks away, on `trackId` when given. */
export function copyRegions(
  project: Project,
  regionIds: readonly string[],
  offset: number,
  trackId: string | null | undefined,
  id: IdMaker
): Project {
  let next = project;
  for (const regionId of regionIds) {
    const audio = project.regions.find(r => r.id === regionId);
    const midi = project.midiRegions.find(r => r.id === regionId);
    const folder = project.takeFolders.find(f => f.id === regionId);
    const start = audio?.startTick ?? midi?.startTick ?? folder?.startTick;
    if (start === undefined) {
      throw new ApiError(422, `No region ${regionId}.`);
    }
    if (start + offset < 0) {
      throw new ApiError(422, 'A copy cannot start before the project start.');
    }
    const target = trackId ?? audio?.trackId ?? midi?.trackId ?? folder?.trackId ?? '';
    const track = project.tracks.find(t => t.id === target);
    if (!track) {
      throw new ApiError(422, `No track ${target}.`);
    }
    if ((track.kind === 'audio') !== (midi === undefined) || track.kind === 'stack') {
      throw new ApiError(422, `${track.name} cannot hold that region.`);
    }
    if (audio) {
      next = {
        ...next,
        regions: [...next.regions, { ...audio, id: id('rgn'), startTick: start + offset, trackId: target }]
      };
    } else if (midi) {
      next = {
        ...next,
        midiRegions: [
          ...next.midiRegions,
          { ...structuredClone(midi), id: id('mid'), startTick: start + offset, trackId: target }
        ]
      };
    } else if (folder) {
      next = {
        ...next,
        takeFolders: [...next.takeFolders, { ...copyFolder(shiftFolder(folder, offset), id), trackId: target }]
      };
    }
  }
  return next;
}

/**
 * `joinRegions`: MIDI regions merge their notes into the first; audio regions only when they play one contiguous
 * stretch of one file (each starting where the previous ends, in time and in the file).
 */
export function joinRegions(project: Project, ids: readonly string[]): Project {
  if (ids.length < 2) {
    throw new ApiError(422, 'Join needs two regions or more.');
  }
  const midi = ids.map(i => project.midiRegions.find(r => r.id === i));
  if (midi.every(r => r !== undefined)) {
    const regions = midi.sort((a, b) => a.startTick - b.startTick);
    const first = regions[0] as MidiRegion;
    if (new Set(regions.map(r => r.trackId)).size !== 1) {
      throw new ApiError(422, 'Joined regions must be on one track.');
    }
    const end = Math.max(...regions.map(r => r.startTick + r.lengthTicks));
    let noteId = Math.max(0, ...regions.flatMap(r => r.notes.map(n => n.id)));
    const joined: MidiRegion = {
      ...first,
      lengthTicks: end - first.startTick,
      notes: regions
        .flatMap(r =>
          r.notes
            .filter(n => n.startTick >= 0 && n.startTick < r.lengthTicks)
            .map(n => ({
              ...n,
              startTick: n.startTick + r.startTick - first.startTick,
              id: r === first ? n.id : ++noteId
            }))
        )
        .sort((a, b) => a.startTick - b.startTick || a.pitch - b.pitch),
      controls: regions.flatMap(r => r.controls.map(c => ({ ...c, tick: c.tick + r.startTick - first.startTick })))
    };
    return {
      ...project,
      midiRegions: [...project.midiRegions.filter(r => !ids.includes(r.id)), joined]
    };
  }
  const audio = ids.map(i => project.regions.find(r => r.id === i));
  if (!audio.every(r => r !== undefined)) {
    throw new ApiError(422, 'Join takes regions of one kind.');
  }
  const regions = audio.sort((a, b) => a.startTick - b.startTick);
  const first = regions[0] as AudioRegion;
  for (let i = 1; i < regions.length; i++) {
    const a = regions[i - 1] as AudioRegion;
    const b = regions[i] as AudioRegion;
    if (
      b.trackId !== a.trackId ||
      b.fileId !== a.fileId ||
      b.fileOffset !== a.fileOffset + a.length ||
      Math.abs(b.startTick - audioEnd(project, a)) > 1
    ) {
      throw new ApiError(
        422,
        'These regions do not play one stretch of one file: joining them needs a bounce (Phase 4).'
      );
    }
  }
  const last = regions.at(-1) as AudioRegion;
  const joined: AudioRegion = {
    ...first,
    length: last.fileOffset + last.length - first.fileOffset,
    fadeOut: last.fadeOut,
    fadeOutCurve: last.fadeOutCurve
  };
  return { ...project, regions: [...project.regions.filter(r => !ids.includes(r.id)), joined] };
}

/** `packTakeFolder`: overlapping or touching audio regions of one track become a take folder, one take each. */
export function packTakeFolder(
  project: Project,
  ids: readonly string[],
  name: string | null | undefined,
  id: IdMaker
): Project {
  const regions = ids.map(i => project.regions.find(r => r.id === i));
  if (regions.length < 1 || !regions.every(r => r !== undefined)) {
    throw new ApiError(422, 'Pack takes audio regions.');
  }
  const sorted = regions.sort((a, b) => a.startTick - b.startTick);
  if (new Set(sorted.map(r => r.trackId)).size !== 1) {
    throw new ApiError(422, 'Packed regions must be on one track.');
  }
  let end = audioEnd(project, sorted[0] as AudioRegion);
  for (const r of sorted.slice(1)) {
    if (r.startTick > end + 1) {
      throw new ApiError(422, 'Packed regions must overlap or touch.');
    }
    end = Math.max(end, audioEnd(project, r));
  }
  const start = (sorted[0] as AudioRegion).startTick;
  const takes: Take[] = sorted.map((r, i) => ({
    id: id('tak'),
    name: `Take ${i + 1}`,
    fileId: r.fileId,
    startTick: r.startTick,
    fileOffset: r.fileOffset,
    length: r.length
  }));
  let comp: CompSegment[] = [];
  takes.forEach(t => {
    comp = swipeComp(comp, t.id, t.startTick, audioEnd(project, t), { startTick: start, lengthTicks: end - start });
  });
  const folder: TakeFolder = {
    id: id('tkf'),
    trackId: (sorted[0] as AudioRegion).trackId,
    name: name || (sorted[0] as AudioRegion).name,
    startTick: start,
    lengthTicks: Math.round(end - start),
    takes,
    comp,
    crossfade: Math.round(project.sampleRate / 100),
    muted: false
  };
  return {
    ...project,
    regions: project.regions.filter(r => !ids.includes(r.id)),
    takeFolders: [...project.takeFolders, folder]
  };
}

/** `flattenTakeFolder`: regions playing the comp, crossfaded over `crossfade` frames at boundaries between them. */
export function flattenTakeFolder(project: Project, folderId: string, id: IdMaker): Project {
  const folder = project.takeFolders.find(f => f.id === folderId);
  if (!folder) {
    throw new ApiError(422, `No take folder ${folderId}.`);
  }
  const files = new Map(project.audioFiles.map(f => [f.id, f]));
  const half = Math.round(folder.crossfade / 2);
  const segments = normalizeComp(folder.comp);
  const regions: AudioRegion[] = segments.flatMap((s, i) => {
    const take = folder.takes.find(t => t.id === s.takeId);
    if (!take) {
      return [];
    }
    const before = segments[i - 1];
    const after = segments[i + 1];
    const joinsBefore = before?.endTick === s.startTick;
    const joinsAfter = after?.startTick === s.endTick;
    let offset = take.fileOffset + framesBetween(project, take.startTick, s.startTick);
    let length = framesBetween(project, s.startTick, s.endTick);
    let startTick = s.startTick;
    let fadeIn = 0;
    if (joinsBefore && offset - half >= 0) {
      offset -= half;
      length += half;
      startTick = tempoOf(project).tickAt(tempoOf(project).secondsAt(s.startTick) - half / project.sampleRate);
      fadeIn = 2 * half;
    }
    const fileFrames = files.get(take.fileId)?.frames ?? offset + length;
    const fadeOut = joinsAfter && offset + length + half <= fileFrames ? 2 * half : 0;
    length += fadeOut / 2;
    return [
      {
        id: id('rgn'),
        trackId: folder.trackId,
        fileId: take.fileId,
        name: `${folder.name}: ${take.name}`,
        startTick: Math.max(0, Math.round(startTick)),
        fileOffset: Math.max(0, offset),
        length: Math.max(1, length),
        gainDb: 0,
        fadeIn,
        fadeOut,
        fadeInCurve: 0,
        fadeOutCurve: 0,
        muted: folder.muted,
        follow: 'off',
        flexMarkers: [],
        transpose: 0,
        fineTune: 0,
        pitchNotes: []
      }
    ];
  });
  return {
    ...project,
    takeFolders: project.takeFolders.filter(f => f.id !== folderId),
    regions: [...project.regions, ...regions]
  };
}

/** Checks a comp against its folder: segments inside it, of its takes, in order and not overlapping. */
export function checkComp(folder: TakeFolder, segments: readonly CompSegment[]): CompSegment[] {
  const sorted = [...segments].sort((a, b) => a.startTick - b.startTick);
  let last = folder.startTick;
  for (const s of sorted) {
    if (!folder.takes.some(t => t.id === s.takeId)) {
      throw new ApiError(422, `${folder.name} has no take ${s.takeId}.`);
    }
    if (s.startTick < last || s.endTick <= s.startTick || s.endTick > folder.startTick + folder.lengthTicks) {
      throw new ApiError(422, 'Comp segments lie inside the folder, in order, without overlapping.');
    }
    last = s.endTick;
  }
  return sorted.map(s => ({ ...s, startTick: Math.round(s.startTick), endTick: Math.round(s.endTick) }));
}

/** Tracks reordered so every stack's members follow it, in their order. */
export function groupStacks(tracks: readonly Track[]): Track[] {
  const result: Track[] = [];
  for (const t of tracks) {
    if (t.parentId && tracks.some(s => s.id === t.parentId)) {
      continue;
    }
    result.push(t);
    if (t.kind === 'stack') {
      result.push(...tracks.filter(m => m.parentId === t.id));
    }
  }
  return result;
}

/** Removes or trims what a recording covering `[from, to)` on a track replaces (replace mode). */
export function replaceRange(project: Project, trackId: string, from: number, to: number, id: IdMaker): Project {
  const regions: AudioRegion[] = [];
  for (const r of project.regions) {
    const end = audioEnd(project, r);
    if (r.trackId !== trackId || end <= from || r.startTick >= to) {
      regions.push(r);
      continue;
    }
    if (r.startTick < from) {
      regions.push({ ...r, length: framesBetween(project, r.startTick, from), fadeOut: 0 });
    }
    if (end > to) {
      const cut = framesBetween(project, r.startTick, to);
      regions.push({
        ...r,
        id: id('rgn'),
        startTick: Math.round(to),
        fileOffset: r.fileOffset + cut,
        length: r.length - cut,
        fadeIn: 0
      });
    }
  }
  return {
    ...project,
    regions,
    takeFolders: project.takeFolders.filter(
      f => f.trackId !== trackId || f.startTick >= to || f.startTick + f.lengthTicks <= from
    )
  };
}
