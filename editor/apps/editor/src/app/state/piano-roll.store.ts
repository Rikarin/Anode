import { Injectable, computed, inject, signal } from '@angular/core';
import type { MidiRegion } from '@anode/api-client';
import { Ppq } from '../midi/midi-math';
import { ProjectStore } from './project.store';
import { SelectionStore } from './selection.store';

/** The Piano Roll's tools (Logic's Left-click tool menu, the part Phase 2 has). */
export type PianoRollTool = 'pointer' | 'pencil' | 'eraser';

export const MinNoteHeight = 6;
export const MaxNoteHeight = 32;

/**
 * Where the Piano Roll looks and how it edits: its own horizontal zoom and scroll, the pitch lane at the top
 * (fractional; lane 0 is pitch 127) and lane height, the tool, snap, the grid it snaps and quantises to, and the
 * velocity and length the pencil uses. Which regions it shows follows the selection: the selected MIDI regions of one
 * track (the selected track's when it has some), else every region of the selected instrument or MIDI track.
 */
@Injectable({ providedIn: 'root' })
export class PianoRollStore {
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);

  readonly pixelsPerQuarter = signal(96);
  readonly originTick = signal(0);
  readonly firstLane = signal(127 - 84);
  readonly laneHeight = signal(12);
  readonly tool = signal<PianoRollTool>('pointer');
  readonly snap = signal(true);
  /** The grid the Piano Roll snaps to and Q quantises to, in ticks. */
  readonly grid = signal(Ppq / 4);
  /** Q's strength, 0…1. */
  readonly quantizeStrength = signal(1);
  /** Phase 7: a groove template of the project Q quantises to instead of the grid (null: the grid). */
  readonly quantizeGroove = signal<string | null>(null);
  /** Phase 7: Q with a groove also scales velocities by the groove's. */
  readonly grooveVelocity = signal(false);
  readonly velocity = signal(100);
  /** The length the pencil gives a note: the last one drawn or resized. */
  readonly noteLength = signal(Ppq / 4);

  readonly pixelsPerTick = computed(() => this.pixelsPerQuarter() / Ppq);

  /** The regions shown, by start. */
  readonly regions = computed<readonly MidiRegion[]>(() => {
    const midi = this.project.midiRegions();
    const selected = [...this.selection.regions()]
      .map(id => midi.get(id))
      .filter((r): r is MidiRegion => r !== undefined);
    const selectedTrack = this.selection.track();
    let shown: MidiRegion[];
    if (selected.length > 0) {
      const trackId = selected.some(r => r.trackId === selectedTrack) ? selectedTrack : selected[0]?.trackId;
      shown = selected.filter(r => r.trackId === trackId);
    } else {
      const track = selectedTrack ? this.project.tracks().get(selectedTrack) : undefined;
      shown = track && track.kind !== 'audio' ? [...midi.values()].filter(r => r.trackId === track.id) : [];
    }
    return shown.sort((a, b) => a.startTick - b.startTick);
  });

  readonly track = computed(() => {
    const first = this.regions()[0];
    const id = first?.trackId ?? this.selection.track();
    const track = id ? this.project.tracks().get(id) : undefined;
    return track && track.kind !== 'audio' ? track : null;
  });

  /** Zooms by `factor`, keeping the tick under `anchorX` in place. */
  zoom(factor: number, anchorX = 0): void {
    const before = this.pixelsPerTick();
    const anchorTick = this.originTick() + anchorX / before;
    const zoom = Math.min(1920, Math.max(8, this.pixelsPerQuarter() * factor));
    this.pixelsPerQuarter.set(zoom);
    this.originTick.set(Math.max(0, anchorTick - anchorX / (zoom / Ppq)));
  }

  /** Scrolls by CSS pixels: `dx` along time, `dy` across pitches, within the keyboard. */
  scrollBy(dx: number, dy: number, visibleLanes: number): void {
    if (dx) {
      this.originTick.update(origin => Math.max(0, origin + dx / this.pixelsPerTick()));
    }
    if (dy) {
      this.scrollLanesTo(this.firstLane() + dy / this.laneHeight(), visibleLanes);
    }
  }

  scrollLanesTo(lane: number, visibleLanes: number): void {
    const max = Math.max(0, 128 - visibleLanes);
    this.firstLane.set(Math.min(max, Math.max(0, lane)));
  }

  zoomLanes(factor: number): void {
    this.laneHeight.update(height => Math.round(Math.min(MaxNoteHeight, Math.max(MinNoteHeight, height * factor))));
  }
}
