import { computed, DestroyRef, inject, Injectable, signal } from '@angular/core';
import type { DrumPad, Pattern, PatternPlayMode, PatternRegion, PatternRowKind, Track } from '@anode/api-client';
import type { StepEditMode, StepValueMode } from '@anode/audio-ui';
import { Coalescer } from '../core/coalescer';
import { EventStream } from '../core/events';
import { isNoteOn } from '../midi/midi-math';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TransportStore } from '../state/transport.store';
import {
  addRow,
  applyTemplate,
  newRow,
  type PatternTemplate,
  randomise,
  type RandomiseWhat,
  recordLive,
  recordStep,
  removeRow,
  setRowLength,
  setRowMuted,
  setRowNote,
  setRowPlayMode,
  setRowRate,
  setStepValue,
  setSwing,
  toggleStep
} from './pattern-edit';

/** Whether a track takes pattern regions: a software instrument track or a Drum Machine Designer. */
export function takesPatterns(track: Track | undefined | null): track is Track {
  return !!track && (track.kind === 'instrument' || track.drumMachine !== null);
}

/**
 * The Step Sequencer's state and edits (Logic's pattern editor, docs/07-phase-6.md): the selected pattern region, the
 * edit mode, the selected row, and step and live record. Every edit sends one `setPattern` with the whole pattern (one
 * undo step), optimistically: edits build on the pattern being sent, at most one request is in flight per region and
 * only the latest waiting pattern is sent after it ({@link Coalescer}). A drag shows its pattern locally and sends once,
 * when it ends, so it is one request and one undo step.
 */
@Injectable({ providedIn: 'root' })
export class StepSequencerStore {
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly transport = inject(TransportStore);
  private readonly coalescer = new Coalescer<{ pattern: Pattern; label: string }>((regionId, { pattern, label }) =>
    this.project.execute([{ type: 'setPattern', regionId, pattern }], label)
  );
  /** A drag's pattern, shown until the drag ends. */
  private readonly draft = signal<{ regionId: string; pattern: Pattern } | null>(null);
  /** The seed of the next Randomise (client-side; the same seed gives the same pattern). */
  private randomSeed = 1;

  readonly mode = signal<StepEditMode>('onOff');
  /** The row Randomise's "selected row" acts on (a click on its header). */
  readonly selectedRow = signal<string | null>(null);
  /** Step record: live MIDI on the region's track lights the next step of its row. */
  readonly stepRecord = signal(false);
  /** The step the next step-recorded note lights. */
  readonly stepCursor = signal(0);
  /** Live record: while playing, live MIDI lights the step under the playhead. */
  readonly liveRecord = signal(false);

  /** The pattern region the Step Sequencer edits: the first selected one. */
  readonly region = computed<PatternRegion | null>(() => {
    const regions = this.project.patternRegions();
    for (const id of this.selection.regions()) {
      const region = regions.get(id);
      if (region) {
        return region;
      }
    }
    return null;
  });
  readonly track = computed(() => {
    const region = this.region();
    return region ? (this.project.track(region.trackId) ?? null) : null;
  });
  /** The region track's Drum Machine Designer pads, or null on an instrument track. */
  readonly pads = computed<readonly DrumPad[] | null>(() => this.track()?.drumMachine?.pads ?? null);
  /** The pattern shown: a drag's, else the one being sent, else the stored one. */
  readonly pattern = computed<Pattern | null>(() => {
    const region = this.region();
    if (!region) {
      return null;
    }
    const draft = this.draft();
    if (draft?.regionId === region.id) {
      return draft.pattern;
    }
    return this.coalescer.pending(region.id)?.pattern ?? region.pattern;
  });
  /** The track a new pattern region goes on: the selected track, when it takes patterns. */
  readonly targetTrack = computed(() => {
    const id = this.selection.track();
    const track = id ? this.project.track(id) : undefined;
    return takesPatterns(track) ? track : null;
  });

  constructor() {
    const subscription = inject(EventStream).events$.subscribe(message => {
      if (message.type !== 'midiActivity') {
        return;
      }
      for (const event of message.events) {
        if (isNoteOn(event.status, event.data2) && event.trackId) {
          this.noteIn(event.trackId, event.data1, event.data2);
        }
      }
    });
    inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
  }

  // ---- editing

  /** Applies an edit to the pattern shown and sends it (one undo step named `label`). */
  edit(change: (pattern: Pattern) => Pattern, label: string): Promise<void> {
    const region = this.region();
    if (!region) {
      return Promise.resolve();
    }
    const draft = this.draft();
    this.draft.set(null);
    const base = this.coalescer.pending(region.id)?.pattern ?? region.pattern;
    const next = change(draft?.regionId === region.id ? draft.pattern : base);
    if (JSON.stringify(next) === JSON.stringify(base)) {
      return Promise.resolve();
    }
    return this.coalescer.set(region.id, { pattern: next, label });
  }

  /** Shows a drag's edit without sending it; {@link edit} sends the drag's last value. */
  preview(change: (pattern: Pattern) => Pattern): void {
    const region = this.region();
    if (!region) {
      return;
    }
    const base = this.coalescer.pending(region.id)?.pattern ?? region.pattern;
    this.draft.set({ regionId: region.id, pattern: change(base) });
  }

  toggleStep(rowId: string, index: number): Promise<void> {
    return this.edit(p => toggleStep(p, rowId, index), 'Toggle Step');
  }

  /** A step's value in a value mode, during a drag (`done` false) or when set. */
  setStep(rowId: string, index: number, mode: StepValueMode, value: number, done = true): Promise<void> {
    const change = (p: Pattern) => setStepValue(p, rowId, index, mode, value);
    if (!done) {
      this.preview(change);
      return Promise.resolve();
    }
    return this.edit(change, `Step ${ModeLabels[mode]}`);
  }

  setRowLength(rowId: string, length: number): Promise<void> {
    return this.edit(p => setRowLength(p, rowId, length), 'Row Length');
  }

  setRowRate(rowId: string, stepTicks: number | null): Promise<void> {
    return this.edit(p => setRowRate(p, rowId, stepTicks), 'Row Rate');
  }

  setRowPlayMode(rowId: string, playMode: PatternPlayMode): Promise<void> {
    return this.edit(p => setRowPlayMode(p, rowId, playMode), 'Row Play Mode');
  }

  toggleRowMute(rowId: string): Promise<void> {
    const row = this.pattern()?.rows.find(r => r.id === rowId);
    return this.edit(p => setRowMuted(p, rowId, !row?.muted), row?.muted ? 'Unmute Row' : 'Mute Row');
  }

  setRowNote(rowId: string, note: number, kind?: PatternRowKind): Promise<void> {
    return this.edit(p => setRowNote(p, rowId, note, this.pads() ?? [], kind), 'Row Note');
  }

  addRow(kind: PatternRowKind, note: number): Promise<void> {
    return this.edit(p => addRow(p, newRow(p, kind, note, this.pads() ?? [])), 'Add Row');
  }

  removeRow(rowId: string): Promise<void> {
    if (this.selectedRow() === rowId) {
      this.selectedRow.set(null);
    }
    return this.edit(p => removeRow(p, rowId), 'Delete Row');
  }

  /** Swing, 0…1: `done` false while the knob is dragged. */
  setSwing(swing: number, done = true): Promise<void> {
    if (!done) {
      this.preview(p => setSwing(p, swing));
      return Promise.resolve();
    }
    return this.edit(p => setSwing(p, swing), 'Swing');
  }

  applyTemplate(template: PatternTemplate): Promise<void> {
    return this.edit(p => applyTemplate(p, template, this.pads()), template.label);
  }

  /** Randomises every row's steps or velocities, or the selected row's, from the next seed. */
  randomise(what: RandomiseWhat, selectedRow = false): Promise<void> {
    const rowId = selectedRow ? this.selectedRow() : null;
    if (selectedRow && !rowId) {
      return Promise.resolve();
    }
    const seed = this.randomSeed++;
    return this.edit(p => randomise(p, what, seed, rowId), what === 'steps' ? 'Randomize Steps' : 'Randomize Velocity');
  }

  /** A new chance seed for the region (the server draws it): the chance steps play differently. */
  reseed(): Promise<unknown> {
    const region = this.region();
    return region
      ? this.project.execute([{ type: 'reseedPattern', regionId: region.id }], 'Reseed Pattern')
      : Promise.resolve();
  }

  /** Replaces the region with a MIDI region of exactly the notes one pass plays, and selects it. */
  async convertToMidi(): Promise<void> {
    const region = this.region();
    if (!region) {
      return;
    }
    const result = await this.project.execute(
      [{ type: 'convertPatternToMidi', regionId: region.id }],
      'Convert to MIDI Region'
    );
    const added = result?.delta.ops.find(op => op.op === 'midiRegionUpserted');
    if (added?.op === 'midiRegionUpserted') {
      this.selection.select([added.region.id]);
    }
  }

  /**
   * A one-bar pattern region (the track's default pattern) on `trackId`, or the selected instrument or DMD track, at the
   * bar under the playhead (or `tick`); selected once made.
   */
  async newRegion(trackId?: string, tick?: number): Promise<string | null> {
    const track = trackId ? this.project.track(trackId) : this.targetTrack();
    if (!takesPatterns(track)) {
      return null;
    }
    const meter = this.project.meter();
    const at = Math.max(0, tick ?? this.transport.positionAt(performance.now()));
    const startTick = Math.max(0, Math.round(meter.barStart(Math.floor(meter.barAt(at)))));
    const result = await this.project.execute(
      [{ type: 'addPatternRegion', trackId: track.id, startTick, pattern: null }],
      'Create Pattern Region'
    );
    const added = result?.delta.ops.find(op => op.op === 'patternRegionUpserted');
    if (added?.op !== 'patternRegionUpserted') {
      return null;
    }
    this.selection.track.set(track.id);
    this.selection.select([added.region.id]);
    return added.region.id;
  }

  // ---- recording

  toggleStepRecord(): void {
    const on = !this.stepRecord();
    this.stepRecord.set(on);
    if (on) {
      this.stepCursor.set(0);
    }
  }

  toggleLiveRecord(): void {
    this.liveRecord.update(on => !on);
  }

  /** A note-on heard on a track: step or live record it when it is the region's track. */
  noteIn(trackId: string, pitch: number, velocity: number, now = performance.now()): void {
    const region = this.region();
    const pattern = this.pattern();
    if (!region || !pattern || region.trackId !== trackId) {
      return;
    }
    const pads = this.pads();
    if (this.liveRecord() && this.transport.playing()) {
      const tick = this.transport.positionAt(now) - region.startTick;
      if (tick >= 0 && tick < region.lengthTicks) {
        void this.edit(p => recordLive(p, pitch, velocity, tick, pads) ?? p, 'Live Record');
      }
      return;
    }
    if (this.stepRecord()) {
      const cursor = this.stepCursor();
      const recorded = recordStep(pattern, pitch, velocity, cursor, pads);
      if (recorded) {
        void this.edit(() => recorded.pattern, 'Step Record');
        this.stepCursor.set(cursor + 1);
      }
    }
  }
}

const ModeLabels: Record<StepValueMode, string> = {
  velocity: 'Velocity',
  gate: 'Gate',
  repeats: 'Repeats',
  chance: 'Chance',
  offset: 'Offset'
};
