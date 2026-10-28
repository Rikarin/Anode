import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import type { AudioRegion, FileAnalysis, FlexFollow, FlexMarker, FlexMode, PitchNote } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { notesFromSegments } from '../flex/pitch-math';
import { JobsStore } from './jobs.store';
import { ProjectStore } from './project.store';

/** The flex algorithms in Logic's order, with their menu names. */
export const FlexModes: readonly { value: FlexMode; label: string }[] = [
  { value: 'monophonic', label: 'Monophonic' },
  { value: 'pitch', label: 'Flex Pitch' },
  { value: 'slicing', label: 'Slicing' },
  { value: 'rhythmic', label: 'Rhythmic' },
  { value: 'polyphonic', label: 'Polyphonic' },
  { value: 'speed', label: 'Speed (FX)' },
  { value: 'tempophone', label: 'Tempophone (FX)' }
];

export function flexModeLabel(mode: FlexMode): string {
  return FlexModes.find(m => m.value === mode)?.label ?? mode;
}

/** Flex & Follow's choices, as the Region inspector names them. */
export const FollowModes: readonly { value: FlexFollow; label: string }[] = [
  { value: 'off', label: 'Off' },
  { value: 'on', label: 'On' },
  { value: 'bars', label: 'Bars' },
  { value: 'barsAndBeats', label: 'Bars and Beats' }
];

/**
 * Flex Time and Flex Pitch (docs/06-phase-5.md): whether the Tracks area shows flex (⌘F), the files' analyses
 * (transients, beats, pitch) fetched as they are needed and refetched when an `analysis` event says one changed, and
 * the flex edits, each one undo step.
 */
@Injectable({ providedIn: 'root' })
export class FlexStore {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly notify = inject(Notify);
  private readonly jobs = inject(JobsStore);

  /** Show/Hide Flex (⌘F): flex buttons in the track headers, markers over the regions of flexed tracks. */
  readonly showFlex = signal(false);
  /** Analyses by file id. */
  readonly analyses = signal<ReadonlyMap<string, FileAnalysis>>(new Map());
  /** Files whose analysis is being fetched. */
  private readonly loading = new Set<string>();
  /** Files whose pitch analysis job runs, with the regions that wait for their notes. */
  private readonly pitchWaiting = new Map<string, Set<string>>();
  /** Files whose pitch is being analysed (for the editor's progress). */
  readonly analysing = signal<ReadonlySet<string>>(new Set());

  /** Tracks whose flex is on. */
  readonly flexedTracks = computed(
    () =>
      new Set(
        this.project
          .trackList()
          .filter(t => t.flexOn)
          .map(t => t.id)
      ) as ReadonlySet<string>
  );

  constructor() {
    inject(EventStream).events$.subscribe(message => {
      if (message.type === 'analysis') {
        void this.load(message.fileId, true);
      }
    });
    effect(() => {
      // A new project starts with its own files.
      this.project.info();
      untracked(() => {
        this.analyses.set(new Map());
        this.loading.clear();
        this.pitchWaiting.clear();
        this.analysing.set(new Set());
      });
    });
  }

  toggleShowFlex(): void {
    this.showFlex.update(on => !on);
  }

  /** A file's analysis, fetched (once) when it is not here yet; undefined until it arrives. */
  analysis(fileId: string): FileAnalysis | undefined {
    const found = this.analyses().get(fileId);
    if (!found) {
      untracked(() => void this.load(fileId));
    }
    return found;
  }

  /** Fetches a file's analysis (again, with `refresh`). */
  async load(fileId: string, refresh = false): Promise<FileAnalysis | null> {
    if (this.loading.has(fileId) || (!refresh && this.analyses().has(fileId))) {
      return this.analyses().get(fileId) ?? null;
    }
    this.loading.add(fileId);
    try {
      const analysis = await this.api.fileAnalysis(fileId);
      this.analyses.update(map => new Map(map).set(fileId, analysis));
      this.notesArrived(analysis);
      return analysis;
    } catch {
      return null; // a file another client removed, or no analysis: nothing to draw
    } finally {
      this.loading.delete(fileId);
    }
  }

  /** Turns a track's flex on or off. */
  setTrackFlex(trackId: string, on: boolean): Promise<unknown> {
    return this.project.execute([{ type: 'setTrackFlex', trackId, on }], on ? 'Flex On' : 'Flex Off');
  }

  /** Sets a track's flex algorithm (turning flex on, as choosing one in Logic does). */
  setFlexMode(trackId: string, mode: FlexMode): Promise<unknown> {
    return this.project.execute(
      [{ type: 'setTrackFlex', trackId, on: true, mode }],
      `Flex Mode: ${flexModeLabel(mode)}`
    );
  }

  setFollow(regionIds: readonly string[], follow: FlexFollow): Promise<unknown> {
    return this.project.execute(
      regionIds.map(regionId => ({ type: 'setRegionFlex', regionId, follow })),
      `Flex & Follow: ${FollowModes.find(f => f.value === follow)?.label ?? follow}`
    );
  }

  setTranspose(regionIds: readonly string[], transpose: number): Promise<unknown> {
    return this.project.execute(
      regionIds.map(regionId => ({ type: 'setRegionFlex', regionId, transpose: Math.round(transpose) })),
      'Transpose Region'
    );
  }

  setFineTune(regionIds: readonly string[], fineTune: number): Promise<unknown> {
    return this.project.execute(
      regionIds.map(regionId => ({ type: 'setRegionFlex', regionId, fineTune: Math.round(fineTune) })),
      'Fine Tune Region'
    );
  }

  setMarkers(regionId: string, markers: readonly FlexMarker[], label = 'Move Flex Marker'): Promise<unknown> {
    return this.project.execute([{ type: 'setFlexMarkers', regionId, markers: [...markers] }], label);
  }

  quantize(regionIds: readonly string[], gridTicks: number, strength = 1, swing = 0): Promise<unknown> {
    if (regionIds.length === 0) {
      return Promise.resolve(null);
    }
    return this.project.execute(
      [{ type: 'quantizeAudio', regionIds: [...regionIds], gridTicks, strength, swing }],
      'Quantize Audio'
    );
  }

  setPitchNotes(regionId: string, notes: readonly PitchNote[], label = 'Edit Pitch'): Promise<unknown> {
    return this.project.execute([{ type: 'setPitchNotes', regionId, notes: [...notes] }], label);
  }

  /**
   * Flex Pitch on a region: its track's flex on in Pitch mode, its file's pitch analysed (a job) if it is not yet, and
   * its notes made from the analysis once it is there.
   */
  async enableFlexPitch(region: AudioRegion): Promise<void> {
    const track = this.project.track(region.trackId);
    if (track && (!track.flexOn || track.flexMode !== 'pitch')) {
      await this.setFlexMode(track.id, 'pitch');
    }
    const analysis = this.analyses().get(region.fileId) ?? (await this.load(region.fileId));
    if (analysis && analysis.pitch.length > 0) {
      await this.makeNotes(region, analysis);
      return;
    }
    const waiting = this.pitchWaiting.get(region.fileId) ?? new Set<string>();
    waiting.add(region.id);
    this.pitchWaiting.set(region.fileId, waiting);
    if (waiting.size === 1) {
      this.analysing.update(set => new Set(set).add(region.fileId));
      await this.jobs.analyze(region.fileId);
    }
  }

  /** Flex Pitch to MIDI: a MIDI region of the notes on a new instrument track (or `trackId`). */
  flexPitchToMidi(regionId: string, trackId: string | null = null): Promise<unknown> {
    return this.project.execute([{ type: 'flexPitchToMidi', regionId, trackId }], 'Create MIDI Track from Flex Pitch');
  }

  private notesArrived(analysis: FileAnalysis): void {
    const waiting = this.pitchWaiting.get(analysis.fileId);
    if (!waiting || analysis.pitch.length === 0) {
      return;
    }
    this.pitchWaiting.delete(analysis.fileId);
    this.analysing.update(set => {
      const next = new Set(set);
      next.delete(analysis.fileId);
      return next;
    });
    for (const id of waiting) {
      const region = this.project.regions().get(id);
      if (region) {
        void this.makeNotes(region, analysis);
      }
    }
  }

  private async makeNotes(region: AudioRegion, analysis: FileAnalysis): Promise<void> {
    if (region.pitchNotes.length > 0) {
      return;
    }
    const notes = notesFromSegments(analysis.notes, region.fileOffset, region.fileOffset + region.length);
    if (notes.length === 0) {
      this.notify.info(`No notes were found in ${region.name}.`);
      return;
    }
    await this.setPitchNotes(region.id, notes, 'Flex Pitch');
  }
}
