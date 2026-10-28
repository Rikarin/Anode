import {
  ApiError,
  type AudioFile,
  type AudioRegion,
  type EditCommand,
  type FileAnalysis,
  type FileTempo,
  type FlexMarker,
  type PitchNote,
  type PitchSegment,
  type Project,
  type TempoEvent,
  type Track,
  type TrackAlternative
} from '@anode/api-client';
import { mapBeats } from '../../flex/beat-mapping';
import { quantizeTick } from '../../midi/midi-math';
import { canPitch, RegionTiming } from '../../music/region-timing';
import { TempoMap } from '../../music/tempo-map';
import { meterOf, tempoOf } from './fake-arrange';
import { hash } from './fake-audio';

/** What the flex, Smart Tempo, Varispeed and alternatives commands need from the fake Server. */
export interface FlexContext {
  id(prefix: string): string;
  /** Applies another command (composition: Flex Pitch to MIDI adds a track, a region and notes). */
  apply(project: Project, command: EditCommand): Project;
  /** The file's analysis (transients and beats analysed now if need be). */
  analysis(file: AudioFile): FileAnalysis;
}

/** The analysis hop: about 5 ms. */
export function analysisHop(sampleRate: number): number {
  return Math.round(sampleRate * 0.005);
}

/** The frames between a fake file's hits (as its fake peaks draw them). */
function hitSpacing(file: AudioFile): number {
  return 6_000 + (hash(file.id + file.name) % 18_000);
}

/**
 * A fake file's tempo: a "100 bpm" or "100bpm" in its name, else its hits as eighth notes. Beats every quarter note
 * from frame 0, downbeats every four.
 */
export function fakeTempo(file: AudioFile): FileTempo {
  const named = /(\d{2,3}(?:\.\d+)?)\s*bpm/i.exec(file.name);
  const bpm = named ? Number(named[1]) : Math.round(((60 * file.sampleRate) / (2 * hitSpacing(file))) * 100) / 100;
  const beat = (60 * file.sampleRate) / bpm;
  const beats: number[] = [];
  for (let f = 0; f < file.frames; f += beat) {
    beats.push(Math.round(f));
  }
  return { bpm, variable: false, beats, downbeats: beats.filter((_, i) => i % 4 === 0) };
}

/** A fake file's transients: its hits, or eighth notes of a tempo named in it. */
export function fakeTransients(file: AudioFile): number[] {
  const named = /(\d{2,3}(?:\.\d+)?)\s*bpm/i.exec(file.name);
  const spacing = named ? (30 * file.sampleRate) / Number(named[1]) : hitSpacing(file);
  const transients: number[] = [];
  for (let f = spacing; f < file.frames; f += spacing) {
    transients.push(Math.round(f));
  }
  return transients;
}

/** A fake file's sung notes: about half a second each with short gaps, a few cents off a C major melody. */
export function fakeNotes(file: AudioFile): PitchSegment[] {
  const seed = hash(file.id + file.name);
  const melody = [6000, 6200, 6400, 6500, 6700, 6900, 6700, 6400];
  const notes: PitchSegment[] = [];
  const gap = Math.round(file.sampleRate * 0.12);
  let start = Math.round(file.sampleRate * 0.1);
  for (let k = 0; start < file.frames - gap; k++) {
    const length = Math.round(file.sampleRate * (0.35 + ((seed >>> (k % 24)) & 3) * 0.08));
    const end = Math.min(file.frames, start + length);
    const off = (((seed >>> ((k * 3) % 27)) & 31) - 15) % 16;
    notes.push({
      startFrame: start,
      endFrame: end,
      cents: (melody[k % melody.length] ?? 6000) + off,
      confidence: 0.95
    });
    start = end + gap;
  }
  return notes;
}

/** A fake file's analysis: transients and tempo always; the pitch curve and notes only `withPitch`. */
export function fakeAnalysis(file: AudioFile, withPitch: boolean): FileAnalysis {
  const hop = analysisHop(file.sampleRate);
  const notes = withPitch ? fakeNotes(file) : [];
  const pitch: (number | null)[] = [];
  if (withPitch) {
    const hops = Math.ceil(file.frames / hop);
    let k = 0;
    for (let h = 0; h < hops; h++) {
      const frame = h * hop;
      while (k < notes.length && (notes[k]?.endFrame ?? 0) <= frame) {
        k++;
      }
      const note = notes[k];
      // A little vibrato (5.5 Hz, ±20 cents) over each note; unvoiced between.
      pitch.push(
        note && frame >= note.startFrame
          ? Math.round(
              (note.cents + 20 * Math.sin((2 * Math.PI * 5.5 * (frame - note.startFrame)) / file.sampleRate)) * 100
            ) / 100
          : null
      );
    }
  }
  return {
    fileId: file.id,
    hop,
    transients: fakeTransients(file),
    tempo: fakeTempo(file),
    pitch,
    notes,
    polyphonic: false
  };
}

/** A region's timing in a project (its track's flex, its file's tempo, the tempo and signature tracks). */
export function timingOf(project: Project, region: AudioRegion): RegionTiming {
  const track = project.tracks.find(t => t.id === region.trackId);
  const file = project.audioFiles.find(f => f.id === region.fileId);
  return new RegionTiming(region, track?.flexOn ?? false, file?.tempo ?? null, {
    tempo: tempoOf(project),
    meter: meterOf(project),
    sampleRate: project.sampleRate
  });
}

/** Strip Silence's stretches of a region's file, in file frames, inside the region (the fake's sound: its notes). */
export function soundStretches(
  file: AudioFile,
  region: AudioRegion,
  thresholdDb: number,
  minTimeMs: number,
  preAttackMs: number,
  postReleaseMs: number
): [number, number][] {
  // Every fake note sounds at about −12 dB, the gaps at −90 dB.
  if (thresholdDb >= -12) {
    return [];
  }
  const ms = file.sampleRate / 1000;
  const first = region.fileOffset;
  const last = region.fileOffset + region.length;
  const stretches: [number, number][] = [];
  for (const note of fakeNotes(file)) {
    if (note.endFrame <= first || note.startFrame >= last) {
      continue;
    }
    const a = Math.max(first, note.startFrame - preAttackMs * ms);
    const b = Math.min(last, note.endFrame + postReleaseMs * ms);
    const previous = stretches[stretches.length - 1];
    // Silence shorter than the minimum time does not split.
    if (previous && a - previous[1] < minTimeMs * ms) {
      previous[1] = Math.max(previous[1], b);
    } else {
      stretches.push([Math.round(a), Math.round(b)]);
    }
  }
  return stretches;
}

const KindTrack: Track['kind'][] = ['audio'];

/**
 * The Phase 5 commands (docs/06-phase-5.md, Commands.cs): flex, Flex Pitch, Smart Tempo, beat mapping, Strip Silence,
 * track alternatives, Varispeed. Undefined for any other command.
 */
export function applyFlexCommand(project: Project, command: EditCommand, ctx: FlexContext): Project | undefined {
  const track = (id: string): Track => {
    const found = project.tracks.find(t => t.id === id);
    if (!found) {
      throw new ApiError(422, `No track ${id}.`);
    }
    return found;
  };
  const region = (id: string): AudioRegion => {
    const found = project.regions.find(r => r.id === id);
    if (!found) {
      throw new ApiError(422, `No audio region ${id}.`);
    }
    return found;
  };
  const fileOf = (r: Pick<AudioRegion, 'fileId'>): AudioFile => {
    const found = project.audioFiles.find(f => f.id === r.fileId);
    if (!found) {
      throw new ApiError(422, `No audio file ${r.fileId}.`);
    }
    return found;
  };
  const withRegion = (p: Project, next: AudioRegion): Project => ({
    ...p,
    regions: p.regions.map(r => (r.id === next.id ? next : r))
  });
  const withTrack = (p: Project, next: Track): Project => ({
    ...p,
    tracks: p.tracks.map(t => (t.id === next.id ? next : t))
  });
  /** The file with its tempo analysed (part of the edit, so it undoes with it). */
  const withFileTempo = (p: Project, file: AudioFile): Project =>
    file.tempo
      ? p
      : {
          ...p,
          audioFiles: p.audioFiles.map(f => (f.id === file.id ? { ...f, tempo: ctx.analysis(file).tempo } : f))
        };

  switch (command.type) {
    case 'setTrackFlex': {
      const t = track(command.trackId);
      if (!KindTrack.includes(t.kind)) {
        throw new ApiError(422, `${t.name} is not an audio track: flex works on audio.`);
      }
      return withTrack(project, { ...t, flexOn: command.on ?? t.flexOn, flexMode: command.mode ?? t.flexMode });
    }
    case 'setRegionFlex': {
      const r = region(command.regionId);
      const t = track(r.trackId);
      const follow = command.follow ?? r.follow;
      const transpose = command.transpose ?? r.transpose;
      const fineTune = command.fineTune ?? r.fineTune;
      if (!Number.isInteger(transpose) || Math.abs(transpose) > 36) {
        throw new ApiError(422, 'A region transposes by −36 to 36 semitones.');
      }
      if (!Number.isInteger(fineTune) || Math.abs(fineTune) > 50) {
        throw new ApiError(422, 'A region fine-tunes by −50 to 50 cents.');
      }
      if ((transpose !== 0 || fineTune !== 0) && !canPitch({ follow }, t.flexOn)) {
        throw new ApiError(422, 'Transpose and fine tune need Flex or Follow on.');
      }
      const next = follow === 'off' ? project : withFileTempo(project, fileOf(r));
      return withRegion(next, { ...r, follow, transpose, fineTune });
    }
    case 'setFlexMarkers': {
      const r = region(command.regionId);
      if (!track(r.trackId).flexOn) {
        throw new ApiError(422, 'Flex markers need the track’s flex on.');
      }
      // Markers outside the region or out of order are kept but ignored by its timing (the Model's rule), not refused.
      const markers = [...command.markers].sort((a, b) => a.fileFrame - b.fileFrame);
      return withRegion(project, { ...r, flexMarkers: markers });
    }
    case 'quantizeAudio': {
      if (!(command.gridTicks > 0)) {
        throw new ApiError(422, 'Quantise audio needs a grid.');
      }
      let next = project;
      for (const id of command.regionIds) {
        const r = region(id);
        const t = next.tracks.find(x => x.id === r.trackId) as Track;
        if (!t.flexOn) {
          next = withTrack(next, { ...t, flexOn: true });
        }
        const timing = timingOf(next, r);
        const transients = ctx
          .analysis(fileOf(r))
          .transients.filter(f => f > r.fileOffset && f < r.fileOffset + r.length);
        const markers: FlexMarker[] = [];
        for (const frame of transients) {
          const target = quantizeTick(timing.tickAt(frame), command.gridTicks, command.strength, command.swing);
          const tick = Math.round(target - r.startTick);
          const previous = markers[markers.length - 1];
          if (tick > (previous?.tick ?? 0) && tick < timing.lengthTicks) {
            markers.push({ fileFrame: frame, tick });
          }
        }
        next = withRegion(next, { ...r, flexMarkers: markers });
      }
      return next;
    }
    case 'setPitchNotes': {
      const r = region(command.regionId);
      if (command.notes.length > 0 && !canPitch(r, track(r.trackId).flexOn)) {
        throw new ApiError(422, 'Pitch notes need Flex or Follow on.');
      }
      for (const n of command.notes) {
        if (n.endFrame <= n.startFrame || n.startFrame < r.fileOffset || n.endFrame > r.fileOffset + r.length) {
          throw new ApiError(422, 'A pitch note lies inside its region’s audio.');
        }
        if (n.drift < 0 || n.drift > 2 || n.vibrato < 0 || n.vibrato > 2) {
          throw new ApiError(422, 'Drift and vibrato are 0 to 2.');
        }
      }
      return withRegion(project, { ...r, pitchNotes: [...command.notes].sort((a, b) => a.startFrame - b.startFrame) });
    }
    case 'flexPitchToMidi': {
      const r = region(command.regionId);
      if (r.pitchNotes.length === 0) {
        throw new ApiError(422, `${r.name} has no pitch notes: turn on Flex Pitch first.`);
      }
      const timing = timingOf(project, r);
      let next = project;
      let trackId = command.trackId ?? null;
      if (trackId) {
        const t = track(trackId);
        if (t.kind !== 'instrument' && t.kind !== 'midi') {
          throw new ApiError(422, `${t.name} cannot hold MIDI.`);
        }
      } else {
        const index = project.tracks.findIndex(t => t.id === r.trackId) + 1;
        next = ctx.apply(next, { type: 'addTrack', kind: 'instrument', stereo: true, index, name: `${r.name} MIDI` });
        trackId = next.tracks[index]?.id ?? null;
      }
      const start = Math.round(timing.startTick);
      next = ctx.apply(next, {
        type: 'addMidiRegion',
        trackId: trackId as string,
        startTick: start,
        lengthTicks: Math.max(1, Math.round(timing.endTick) - start),
        name: r.name
      });
      const midi = next.midiRegions[next.midiRegions.length - 1];
      return ctx.apply(next, {
        type: 'addNotes',
        regionId: midi?.id ?? '',
        notes: r.pitchNotes.map(n => {
          const a = timing.tickAt(n.startFrame);
          const b = timing.tickAt(n.endFrame);
          return {
            pitch: Math.min(127, Math.max(0, Math.round(n.targetCents / 100))),
            velocity: Math.min(127, Math.max(1, Math.round(100 * 10 ** (n.gainDb / 20)))),
            channel: 1,
            startTick: Math.round(a - start),
            lengthTicks: Math.max(1, Math.round(b - a))
          };
        })
      });
    }
    case 'setFileTempo': {
      const file = project.audioFiles.find(f => f.id === command.fileId);
      if (!file) {
        throw new ApiError(422, `No audio file ${command.fileId}.`);
      }
      const tempo = command.tempo;
      if (tempo) {
        if (!(tempo.bpm >= 20 && tempo.bpm <= 999)) {
          throw new ApiError(422, 'A file tempo is 20 to 999 bpm.');
        }
        const ascending = (list: readonly number[]) => list.every((f, i) => i === 0 || f > (list[i - 1] ?? 0));
        if (!ascending(tempo.beats) || !ascending(tempo.downbeats)) {
          throw new ApiError(422, 'Beats and downbeats are in ascending order.');
        }
      }
      return { ...project, audioFiles: project.audioFiles.map(f => (f.id === file.id ? { ...f, tempo } : f)) };
    }
    case 'adaptTempo': {
      const r = region(command.regionId);
      const file = fileOf(r);
      const next = withFileTempo(project, file);
      const tempo = next.audioFiles.find(f => f.id === file.id)?.tempo as FileTempo;
      return withRegion(adaptTempo(next, r, tempo), { ...r, follow: 'off', flexMarkers: [] });
    }
    case 'mapBeats':
      try {
        const events = mapBeats(project.settings.tempoEvents, command.mappings, project.sampleRate);
        return {
          ...project,
          settings: { ...project.settings, tempoEvents: events, tempo: events[0]?.bpm ?? project.settings.tempo }
        };
      } catch (error) {
        throw new ApiError(422, error instanceof Error ? error.message : String(error));
      }
    case 'stripSilence': {
      let next = project;
      for (const id of command.regionIds) {
        const r = region(id);
        const file = fileOf(r);
        const timing = timingOf(project, r);
        const stretches = soundStretches(
          file,
          r,
          command.thresholdDb,
          command.minTimeMs,
          command.preAttackMs,
          command.postReleaseMs
        );
        const pieces: AudioRegion[] = stretches.map(([a, b], i) => ({
          ...r,
          id: i === 0 ? r.id : ctx.id('rgn'),
          startTick: Math.round(timing.tickAt(a)),
          fileOffset: a,
          length: b - a,
          fadeIn: 0,
          fadeOut: 0,
          flexMarkers: [],
          pitchNotes: r.pitchNotes.filter(n => n.startFrame >= a && n.endFrame <= b)
        }));
        next = { ...next, regions: [...next.regions.filter(x => x.id !== r.id), ...pieces] };
      }
      return next;
    }
    case 'newTrackAlternative': {
      const t = track(command.trackId);
      const live = liveContent(project, t.id);
      let alternatives = t.alternatives;
      let activeId = t.alternativeId;
      if (!activeId) {
        activeId = ctx.id('alt');
        alternatives = [{ id: activeId, name: 'A', regions: [], midiRegions: [], takeFolders: [] }];
      }
      // The active one keeps its name; its content is snapshotted as it is set aside.
      alternatives = alternatives.map(a => (a.id === activeId ? { ...a, ...live } : a));
      const id = ctx.id('alt');
      const name = command.name?.trim() || String.fromCharCode(65 + Math.min(25, alternatives.length));
      const copy = command.duplicate ? copyContent(live, ctx) : { regions: [], midiRegions: [], takeFolders: [] };
      alternatives = [...alternatives, { id, name, regions: [], midiRegions: [], takeFolders: [] }];
      return withContent(withTrack(project, { ...t, alternatives, alternativeId: id }), t.id, copy);
    }
    case 'selectTrackAlternative': {
      const t = track(command.trackId);
      const target = t.alternatives.find(a => a.id === command.alternativeId);
      if (!target) {
        throw new ApiError(422, `${t.name} has no alternative ${command.alternativeId}.`);
      }
      if (target.id === t.alternativeId) {
        return project;
      }
      const live = liveContent(project, t.id);
      const alternatives = t.alternatives.map(a =>
        a.id === t.alternativeId
          ? { ...a, ...live }
          : a.id === target.id
            ? { ...a, regions: [], midiRegions: [], takeFolders: [] }
            : a
      );
      return withContent(withTrack(project, { ...t, alternatives, alternativeId: target.id }), t.id, target);
    }
    case 'setTrackAlternative': {
      const t = track(command.trackId);
      if (!t.alternatives.some(a => a.id === command.alternativeId)) {
        throw new ApiError(422, `${t.name} has no alternative ${command.alternativeId}.`);
      }
      if (!command.name.trim()) {
        throw new ApiError(422, 'An alternative needs a name.');
      }
      return withTrack(project, {
        ...t,
        alternatives: t.alternatives.map(a =>
          a.id === command.alternativeId ? { ...a, name: command.name.trim() } : a
        )
      });
    }
    case 'deleteTrackAlternative': {
      const t = track(command.trackId);
      if (!t.alternatives.some(a => a.id === command.alternativeId)) {
        throw new ApiError(422, `${t.name} has no alternative ${command.alternativeId}.`);
      }
      let next = project;
      if (command.alternativeId === t.alternativeId) {
        // The next alternative, or the previous one when it was the last.
        const index = t.alternatives.findIndex(a => a.id === t.alternativeId);
        const other = t.alternatives[index + 1] ?? t.alternatives[index - 1];
        if (!other) {
          throw new ApiError(422, 'A track keeps at least one alternative.');
        }
        next = applyFlexCommand(
          project,
          { type: 'selectTrackAlternative', trackId: t.id, alternativeId: other.id },
          ctx
        ) as Project;
      }
      const after = next.tracks.find(x => x.id === t.id) as Track;
      return withTrack(next, {
        ...after,
        alternatives: after.alternatives.filter(a => a.id !== command.alternativeId)
      });
    }
    case 'setVarispeed': {
      const v = project.settings.varispeed;
      const speed = command.speed ?? v.speed;
      if (!(speed >= 0.5 && speed <= 1.5)) {
        throw new ApiError(422, 'Varispeed is 50 % to 150 %.');
      }
      return {
        ...project,
        settings: {
          ...project.settings,
          varispeed: { enabled: command.enabled ?? v.enabled, mode: command.mode ?? v.mode, speed }
        }
      };
    }
    case 'setSmartTempo': {
      const s = project.settings.smartTempo;
      return {
        ...project,
        settings: {
          ...project.settings,
          smartTempo: {
            mode: command.mode ?? s.mode,
            defaultFollow: command.defaultFollow ?? s.defaultFollow,
            analyzeImports: command.analyzeImports ?? s.analyzeImports
          }
        }
      };
    }
    default:
      return undefined;
  }
}

/**
 * The tempo events from a file tempo over a region's range: a step per beat (one event when the tempo is steady), and
 * the tempo that was in force after the region's end back there.
 */
export function adaptTempo(project: Project, region: AudioRegion, tempo: FileTempo): Project {
  const map = tempoOf(project);
  const sr = project.sampleRate;
  const start = region.startTick;
  const secondsAtStart = map.secondsAt(start);
  const endSeconds = secondsAtStart + region.length / sr;
  const events: TempoEvent[] = [];
  const beats = tempo.beats.filter(f => f >= region.fileOffset && f < region.fileOffset + region.length);
  let tick = start;
  if (tempo.variable && beats.length > 1) {
    // Before the first beat, the average tempo; then each beat's own.
    const first = beats[0] as number;
    tick += ((first - region.fileOffset) / sr) * (tempo.bpm / 60) * 960;
    events.push({ tick: start, bpm: round2(tempo.bpm), curve: 'step', shape: 0 });
    for (let i = 0; i < beats.length - 1; i++) {
      const frames = (beats[i + 1] as number) - (beats[i] as number);
      events.push({ tick: Math.round(tick), bpm: round2((60 * sr) / frames), curve: 'step', shape: 0 });
      tick += 960;
    }
  } else {
    events.push({ tick: start, bpm: round2(tempo.bpm), curve: 'step', shape: 0 });
  }
  const adapted = [...project.settings.tempoEvents.filter(e => e.tick < start), ...events];
  // After the region, the old tempo again from where the region now ends, and the later events moved with it.
  const oldEnd = map.tickAt(endSeconds);
  const newEnd = Math.round(new TempoMap(adapted).tickAt(endSeconds));
  const shift = newEnd - Math.round(oldEnd);
  const later = project.settings.tempoEvents.filter(e => e.tick > oldEnd).map(e => ({ ...e, tick: e.tick + shift }));
  const resumed: TempoEvent[] = later.some(e => e.tick === newEnd)
    ? []
    : [{ tick: newEnd, bpm: map.bpmAt(oldEnd), curve: 'step', shape: 0 }];
  const tempoEvents = [...adapted, ...resumed, ...later]
    .filter((e, i, all) => i === 0 || e.tick > (all[i - 1]?.tick ?? -1))
    .map((e, i) => (i === 0 ? { ...e, tick: 0 } : e));
  return {
    ...project,
    settings: { ...project.settings, tempoEvents, tempo: tempoEvents[0]?.bpm ?? project.settings.tempo }
  };
}

function round2(value: number): number {
  return Math.round(value * 100) / 100;
}

type Content = Pick<TrackAlternative, 'regions' | 'midiRegions' | 'takeFolders'>;

function liveContent(project: Project, trackId: string): Content {
  return {
    regions: project.regions.filter(r => r.trackId === trackId),
    midiRegions: project.midiRegions.filter(r => r.trackId === trackId),
    takeFolders: project.takeFolders.filter(f => f.trackId === trackId)
  };
}

/** The track's live content replaced by `content`. */
function withContent(project: Project, trackId: string, content: Content): Project {
  return {
    ...project,
    regions: [...project.regions.filter(r => r.trackId !== trackId), ...content.regions],
    midiRegions: [...project.midiRegions.filter(r => r.trackId !== trackId), ...content.midiRegions],
    takeFolders: [...project.takeFolders.filter(f => f.trackId !== trackId), ...content.takeFolders]
  };
}

/** A copy of content with new ids (ids stay unique project-wide). */
function copyContent(content: Content, ctx: FlexContext): Content {
  return {
    regions: content.regions.map(r => ({ ...r, id: ctx.id('rgn') })),
    midiRegions: content.midiRegions.map(r => ({ ...r, id: ctx.id('mid') })),
    takeFolders: content.takeFolders.map(f => ({ ...f, id: ctx.id('tkf') }))
  };
}

/** The pitch notes of a region, from its file's analysed notes (Flex Pitch turned on). */
export function initialPitchNotes(analysis: FileAnalysis, region: AudioRegion): PitchNote[] {
  const from = region.fileOffset;
  const to = region.fileOffset + region.length;
  return analysis.notes
    .filter(s => s.endFrame > from && s.startFrame < to)
    .map(s => ({
      startFrame: Math.max(from, s.startFrame),
      endFrame: Math.min(to, s.endFrame),
      detectedCents: s.cents,
      targetCents: s.cents,
      drift: 1,
      vibrato: 1,
      formantCents: 0,
      gainDb: 0
    }));
}
