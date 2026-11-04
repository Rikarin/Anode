import type { ImportProjectTracksRequest, Project, ProjectContents, Track } from '@anode/api-client';

const MaxBus = 64;
const MaxTracks = 1_000;

/** What a package on the fake disk holds, for the Import from Project browser. */
export function contentsOf(project: Project): ProjectContents {
  return {
    name: project.name,
    path: project.path,
    sampleRate: project.sampleRate,
    tempo: project.settings.tempo,
    tracks: project.tracks.map(t => ({
      id: t.id,
      name: t.name,
      kind: t.kind,
      color: t.color,
      parentId: t.parentId ?? null,
      regions:
        project.regions.filter(r => r.trackId === t.id).length +
        project.midiRegions.filter(r => r.trackId === t.id).length,
      inserts: t.strip.inserts.length,
      sends: t.strip.sends.length,
      lanes: project.automation.filter(l => l.trackId === t.id).length
    }))
  };
}

function busOf(output: string): number | null {
  return output.startsWith('bus:') ? Number(output.slice(4)) : null;
}

/** The chosen tracks, a stack's members and, with sends, the auxes they reach, in the source's order. */
export function bundledTracks(source: Project, ids: readonly string[], sends: boolean): Track[] {
  const chosen = new Set(ids);
  let grew = true;
  while (grew) {
    grew = false;
    for (const track of source.tracks) {
      const fed = source.tracks
        .filter(t => chosen.has(t.id))
        .flatMap(t => [...t.strip.sends.map(s => s.bus), busOf(t.strip.output)]);
      const reached = sends && track.kind === 'aux' && fed.includes(track.inputBus);
      const member = track.parentId !== null && track.parentId !== undefined && chosen.has(track.parentId);
      if (!chosen.has(track.id) && (member || reached)) {
        chosen.add(track.id);
        grew = true;
      }
    }
  }

  return source.tracks.filter(t => chosen.has(t.id));
}

/**
 * `target` with the chosen tracks of `source` after its own, as the Server's `Edits.InsertTracks`: new ids throughout,
 * and buses the target does not use. Throws (the job fails) when it has too few left.
 */
export function withImportedTracks(
  target: Project,
  source: Project,
  request: ImportProjectTracksRequest,
  newId: (prefix: string) => string
): Project {
  const parts = {
    regions: request.regions ?? true,
    inserts: request.inserts ?? true,
    sends: request.sends ?? true,
    automation: request.automation ?? true
  };
  const tracks = bundledTracks(source, request.trackIds, parts.sends);
  if (target.tracks.length + tracks.length > MaxTracks) {
    throw new Error(`A project holds at most ${MaxTracks} tracks.`);
  }

  const taken = new Set([
    ...target.tracks.flatMap(t => t.strip.sends.map(s => s.bus)),
    ...target.tracks.map(t => busOf(t.strip.output) ?? 0),
    ...target.tracks.filter(t => t.kind === 'aux').map(t => t.inputBus)
  ]);
  const buses = new Map<number, number>();
  const busFor = (bus: number): number => {
    let mapped = buses.get(bus);
    if (mapped === undefined) {
      mapped = 1;
      while (taken.has(mapped)) {
        mapped++;
      }

      if (mapped > MaxBus) {
        throw new Error(`The tracks need more buses than the ${MaxBus} a project has.`);
      }

      taken.add(mapped);
      buses.set(bus, mapped);
    }

    return mapped;
  };
  const ids = new Map(tracks.map(t => [t.id, newId('trk')]));
  const mapped = (id: string): string => ids.get(id) ?? id;
  const regions = parts.regions ? source.regions.filter(r => ids.has(r.trackId)) : [];
  const files = new Map(
    [...new Set(regions.map(r => r.fileId))].flatMap(id => {
      const file = source.audioFiles.find(f => f.id === id);
      return file ? [[id, { ...file, id: newId('aud') }] as const] : [];
    })
  );
  const added: Track[] = tracks.map(track => {
    const output = busOf(track.strip.output);
    return {
      ...track,
      id: mapped(track.id),
      parentId: ids.has(track.parentId ?? '') ? mapped(track.parentId ?? '') : null,
      recordArmed: false,
      inputMonitoring: false,
      inputBus: track.kind === 'aux' ? busFor(track.inputBus) : 0,
      strip: {
        ...track.strip,
        inserts: parts.inserts ? track.strip.inserts : [],
        sends: parts.sends ? track.strip.sends.map(s => ({ ...s, bus: busFor(s.bus) })) : [],
        output: output === null ? track.strip.output : parts.sends ? `bus:${busFor(output)}` : 'master',
        groups: [],
        vcaId: null
      }
    };
  });
  return {
    ...target,
    tracks: [...target.tracks, ...added],
    audioFiles: [...target.audioFiles, ...files.values()],
    regions: [
      ...target.regions,
      ...regions.map(r => ({
        ...r,
        id: newId('rgn'),
        trackId: mapped(r.trackId),
        fileId: files.get(r.fileId)?.id ?? r.fileId
      }))
    ],
    midiRegions: [
      ...target.midiRegions,
      ...(parts.regions ? source.midiRegions.filter(r => ids.has(r.trackId)) : []).map(r => ({
        ...r,
        id: newId('mid'),
        trackId: mapped(r.trackId)
      }))
    ],
    automation: [
      ...target.automation,
      ...(parts.automation ? source.automation.filter(l => ids.has(l.trackId ?? '')) : []).map(l => ({
        ...l,
        id: newId('aut'),
        trackId: mapped(l.trackId ?? '')
      }))
    ]
  };
}
