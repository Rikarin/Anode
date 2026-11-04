import type { AudioFile, AudioRegion, Project, SampleRateChangeMode, Take, TakeFolder } from '@anode/api-client';

/** The sample rates a project may have, as the Server's. */
export const ProjectRates = [44_100, 48_000, 88_200, 96_000, 176_400, 192_000];

/** The tempo range of a project, as the Server's. */
const MinBpm = 5;
const MaxBpm = 990;

/** Why a project cannot change as asked, or null, as the Server's checks say it. */
export function rateRefusal(project: Project, rate: number, mode: SampleRateChangeMode): string | null {
  if (!ProjectRates.includes(rate)) {
    return `Sample rate ${rate} Hz is not one of ${ProjectRates.join(', ')}.`;
  }

  if (rate === project.sampleRate) {
    return `The project is at ${rate} Hz already.`;
  }

  if (project.offline.files.length > 0) {
    return `Find the offline files first (${project.offline.files.map(f => `'${f.name}'`).join(', ')}).`;
  }

  if (project.offline.unreadableTracks.length > 0) {
    return 'Repair or remove the unreadable tracks first: their positions cannot be converted.';
  }

  const ratio = rate / project.sampleRate;
  if (
    mode === 'keepSamples' &&
    project.settings.tempoEvents.some(e => e.bpm * ratio < MinBpm || e.bpm * ratio > MaxBpm)
  ) {
    return `Keeping the samples at ${rate} Hz takes a tempo outside ${MinBpm}…${MaxBpm} BPM.`;
  }

  return null;
}

/** The project at another rate, as the Server's `ProjectRate.Changed`: new file ids, every stored frame following. */
export function atRate(project: Project, rate: number, mode: SampleRateChangeMode, newId: () => string): Project {
  const ratio = rate / project.sampleRate;
  const keeps = mode === 'keepSamples';
  const ids = new Map<string, AudioFile>();
  const stem = (path: string): string => path.replace(/\.wav$/, '');
  const files = project.audioFiles.map(file => {
    if (keeps ? file.sampleRate !== project.sampleRate : file.sampleRate === rate) {
      return file;
    }

    const written: AudioFile = {
      ...file,
      id: newId(),
      path: `${stem(file.path)}.${rate}.wav`,
      sampleRate: rate,
      frames: keeps ? file.frames : Math.round(file.frames * ratio),
      version: 0
    };
    ids.set(file.id, written);
    return written;
  });
  const scale = (frames: number): number => (keeps ? frames : Math.round(frames * ratio));
  const span = (fileId: string, offset: number, length: number): { fileOffset: number; length: number } => {
    const file = ids.get(fileId);
    const frames = file?.frames ?? Number.MAX_SAFE_INTEGER;
    const start = Math.min(scale(offset), Math.max(0, frames - 1));
    return { fileOffset: start, length: Math.max(1, Math.min(scale(offset + length), frames) - start) };
  };
  const region = (r: AudioRegion): AudioRegion => {
    if (!ids.has(r.fileId)) {
      return r;
    }

    const placed = span(r.fileId, r.fileOffset, r.length);
    const fadeIn = Math.min(scale(r.fadeIn), placed.length);
    return {
      ...r,
      ...placed,
      fileId: ids.get(r.fileId)?.id ?? r.fileId,
      fadeIn,
      fadeOut: Math.min(scale(r.fadeOut), placed.length - fadeIn),
      flexMarkers: r.flexMarkers.map(m => ({ ...m, fileFrame: scale(m.fileFrame) })),
      pitchNotes: r.pitchNotes.map(n => ({ ...n, startFrame: scale(n.startFrame), endFrame: scale(n.endFrame) }))
    };
  };
  const take = (t: Take): Take => ({
    ...t,
    ...span(t.fileId, t.fileOffset, t.length),
    fileId: ids.get(t.fileId)?.id ?? t.fileId
  });
  const folder = (f: TakeFolder): TakeFolder => ({ ...f, takes: f.takes.map(take), crossfade: scale(f.crossfade) });
  return {
    ...project,
    sampleRate: rate,
    audioFiles: files,
    regions: project.regions.map(region),
    takeFolders: project.takeFolders.map(folder),
    settings: keeps
      ? {
          ...project.settings,
          tempo: project.settings.tempo * ratio,
          tempoEvents: project.settings.tempoEvents.map(e => ({ ...e, bpm: e.bpm * ratio }))
        }
      : project.settings,
    tracks: project.tracks.map(track => ({
      ...track,
      frozenFileId: ids.get(track.frozenFileId ?? '')?.id ?? track.frozenFileId ?? null,
      alternatives: track.alternatives.map(a => ({
        ...a,
        regions: a.regions.map(region),
        takeFolders: a.takeFolders.map(folder)
      }))
    })),
    liveLoops: {
      ...project.liveLoops,
      cells: project.liveLoops.cells.map(cell =>
        cell.content.audio ? { ...cell, content: { ...cell.content, audio: region(cell.content.audio) } } : cell
      )
    }
  };
}
