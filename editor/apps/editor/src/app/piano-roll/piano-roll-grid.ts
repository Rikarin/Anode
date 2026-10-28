import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import type { EditCommand, MidiRegion } from '@anode/api-client';
import {
  cloneScene,
  hitTest,
  type ItemOverride,
  itemsInRect,
  laneAtY,
  LaneIntervalIndex,
  type PixelRect,
  type RegionZone,
  RULER_HEIGHT,
  snapTick,
  tickAtX,
  type TimelineView,
  xAtTick,
  yAtLane
} from '@anode/timeline';
import { LiveMidiStore } from '../state/live-midi.store';
import { PianoRollStore } from '../state/piano-roll.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TransportStore } from '../state/transport.store';
import { createSurface, type TimelineSurface } from '../tracks/timeline-surface';
import {
  deleteCommands,
  moveNotes,
  noteCommands,
  notesLabel,
  pencilNote,
  type PlacedNote,
  resizeEnd,
  resizeStart
} from './note-edit';
import { buildNotesScene, laneOfPitch, type NotesScene, pitchOfLane } from './notes-scene';

const NoSpans: readonly { start: number; end: number }[] = [];

/** Pixels the pointer moves before a press becomes a drag. */
const DragThreshold = 3;

/** A press in progress. */
interface Gesture {
  kind: 'move' | 'resize-start' | 'resize-end' | 'marquee' | 'locate' | 'pencil' | 'erase';
  /** The note grabbed (move, resize). */
  note: PlacedNote | null;
  x: number;
  y: number;
  tick: number;
  lane: number;
  moved: boolean;
  shift: boolean;
  copy: boolean;
  base: ReadonlySet<number>;
  /** Notes the eraser has crossed. */
  erased: Set<number>;
  /** The note the pencil is drawing. */
  draft: ReturnType<typeof pencilNote>;
  /** A pitch sounding as feedback while the press lasts. */
  sounding: number | null;
}

const Cursors: Record<RegionZone, string> = {
  body: 'grab',
  'trim-start': 'ew-resize',
  'trim-end': 'ew-resize',
  'fade-in': 'ew-resize',
  'fade-out': 'ew-resize'
};

/**
 * The Piano Roll's note grid (ADR-0007): the notes of the regions shown, on a lane per pitch, coloured by velocity,
 * selected ones outlined, with the regions' spans lit and the rest shaded, the ruler and the playhead, drawn by
 * `@anode/timeline`. The pointer selects (click, ⇧-click, marquee), moves notes in time and pitch (⌥ copies), and
 * resizes them at either end; the pencil draws a note (drag for its length) and the eraser deletes what it crosses,
 * all snapped to the Piano Roll's grid. A drag is a local preview; dropping sends one command batch.
 */
@Component({
  selector: 'an-piano-roll-grid',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'relative block min-h-0 min-w-0 touch-none overflow-hidden',
    '[style.cursor]': 'cursor()',
    '(pointerdown)': 'down($event)',
    '(pointermove)': 'move($event)',
    '(pointerup)': 'up($event)',
    '(pointercancel)': 'cancel()',
    '(wheel)': 'wheel($event)',
    '(dblclick)': 'doubleClick($event)'
  },
  template: `<canvas #canvas class="absolute inset-0 size-full" role="img" [attr.aria-label]="summary()"></canvas>`
})
export class PianoRollGrid {
  private readonly project = inject(ProjectStore);
  private readonly transport = inject(TransportStore);
  private readonly selection = inject(SelectionStore);
  private readonly live = inject(LiveMidiStore);
  protected readonly store = inject(PianoRollStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');

  private surface: TimelineSurface | null = null;
  private readonly ready = signal(false);
  private width = 1;
  /** The canvas height in CSS pixels, for keeping the scroll inside the keyboard. */
  readonly height = signal(1);
  private drawn: (NotesScene & { index: LaneIntervalIndex }) | null = null;
  private gesture: Gesture | null = null;
  private overrides: ItemOverride[] | null = null;
  private marquee: PixelRect | null = null;
  private dirty = true;
  private lastView: TimelineView | null = null;
  /** The regions last scrolled into view, so a new set is revealed once. */
  private revealed = '';

  protected readonly cursor = signal('default');
  protected readonly summary = computed(() => {
    const regions = this.store.regions();
    const notes = regions.reduce((sum, r) => sum + r.notes.length, 0);
    return `Piano Roll: ${notes} ${notes === 1 ? 'note' : 'notes'} in ${regions.length} ${regions.length === 1 ? 'region' : 'regions'}`;
  });

  private readonly scene = computed(() => {
    const signature = this.project.settings().signature;
    return buildNotesScene(
      this.store.regions(),
      this.selection.notes(),
      (signature.numerator * 4) / signature.denominator
    );
  });

  constructor() {
    afterNextRender(() => void this.start());
    effect(() => {
      const scene = this.scene();
      const ready = this.ready();
      untracked(() => this.pushScene(scene, ready));
    });
    effect(() => {
      const regions = this.store.regions();
      const height = this.height();
      untracked(() => this.reveal(regions, height));
    });

    let frame = 0;
    const tick = () => {
      this.drawFrame();
      frame = requestAnimationFrame(tick);
    };
    afterNextRender(() => {
      if (typeof requestAnimationFrame === 'function') {
        frame = requestAnimationFrame(tick);
      }
    });
    const resize = typeof ResizeObserver === 'function' ? new ResizeObserver(() => this.measure()) : null;
    afterNextRender(() => resize?.observe(this.host.nativeElement));
    inject(DestroyRef).onDestroy(() => {
      if (typeof cancelAnimationFrame === 'function') {
        cancelAnimationFrame(frame);
      }
      resize?.disconnect();
      this.surface?.dispose();
      this.release();
    });
  }

  /** Lanes that fit below the ruler. */
  visibleLanes(): number {
    return Math.max(1, (this.height() - RULER_HEIGHT) / this.store.laneHeight());
  }

  // ---- drawing

  private async start(): Promise<void> {
    this.measure();
    try {
      this.surface = await createSurface(this.canvas().nativeElement, this.width, this.height(), devicePixelRatio || 1);
    } catch (error) {
      console.error('Anode: the Piano Roll renderer did not start', error);
    }
    this.ready.set(this.surface !== null);
  }

  private measure(): void {
    const rect = this.host.nativeElement.getBoundingClientRect();
    this.width = Math.max(1, Math.round(rect.width));
    this.height.set(Math.max(1, Math.round(rect.height)));
    this.surface?.resize(this.width, this.height(), devicePixelRatio || 1);
    this.dirty = true;
  }

  private pushScene(scene: NotesScene, ready: boolean): void {
    this.drawn = { ...scene, index: new LaneIntervalIndex(scene.scene.items, scene.scene.laneCount) };
    if (ready) {
      this.surface?.setScene(cloneScene(scene.scene));
      this.dirty = true;
    }
  }

  /** A new set of regions scrolls into view: its start at the left, its notes (or C3) in the middle. */
  private reveal(regions: readonly MidiRegion[], height: number): void {
    const key = regions.map(r => r.id).join(',');
    if (key === this.revealed || regions.length === 0 || height <= RULER_HEIGHT) {
      return;
    }

    this.revealed = key;
    const first = regions[0];
    const pitches = regions.flatMap(r => r.notes.map(n => n.pitch));
    const middle = pitches.length > 0 ? (Math.min(...pitches) + Math.max(...pitches)) / 2 : 60;
    const visible = this.visibleLanes();
    this.store.originTick.set(Math.max(0, (first?.startTick ?? 0) - 240));
    this.store.scrollLanesTo(laneOfPitch(Math.round(middle)) - visible / 2, visible);
  }

  currentView(now = performance.now()): TimelineView {
    const spans = this.drawn?.spans ?? NoSpans;
    return {
      originTick: this.store.originTick(),
      pixelsPerTick: this.store.pixelsPerTick(),
      firstLane: this.store.firstLane(),
      laneHeight: this.store.laneHeight(),
      playheadTick: this.transport.positionAt(now),
      cycle: null,
      endTick: null,
      spans,
      marquee: this.marquee,
      overrides: this.overrides
    };
  }

  private drawFrame(): void {
    if (!this.surface) {
      return;
    }

    const view = this.currentView();
    if (this.dirty || !sameView(view, this.lastView)) {
      this.dirty = false;
      this.lastView = view;
      this.surface.frame(view);
    }
  }

  // ---- pointer

  private local(event: { clientX: number; clientY: number }): { x: number; y: number } {
    const rect = this.host.nativeElement.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }

  private snap(): number {
    return this.store.snap() ? this.store.grid() : 0;
  }

  protected down(event: PointerEvent): void {
    if (event.button !== 0 || !this.drawn) {
      return;
    }

    const { x, y } = this.local(event);
    const view = this.currentView();
    const tick = tickAtX(view, x);
    const lane = Math.floor(laneAtY(view, y));
    capture(this.host.nativeElement, event.pointerId, true);
    const start = (kind: Gesture['kind'], note: PlacedNote | null = null): Gesture => {
      this.gesture = {
        kind,
        note,
        x,
        y,
        tick,
        lane,
        moved: false,
        shift: event.shiftKey,
        copy: event.altKey,
        base: this.selection.notes(),
        erased: new Set(),
        draft: null,
        sounding: null
      };
      return this.gesture;
    };

    if (y < RULER_HEIGHT) {
      this.transport.locate(snapTick(tick, this.snap()));
      start('locate');
      return;
    }

    const hit = hitTest(this.drawn.scene, this.drawn.index, view, x, y);
    const note = hit ? this.drawn.notes[hit.index] : undefined;
    const tool = this.store.tool();
    if (tool === 'eraser') {
      const gesture = start('erase');
      if (note) {
        gesture.erased.add(note.id);
        this.overrides = this.erasedOverrides(gesture.erased);
      }
      return;
    }

    if (note && hit) {
      if (event.shiftKey) {
        this.selection.toggleNote(note.id);
      } else if (!this.selection.notes().has(note.id)) {
        this.selection.selectNotes([note.id]);
      }
      const kind = hit.zone === 'trim-start' ? 'resize-start' : hit.zone === 'trim-end' ? 'resize-end' : 'move';
      start(kind, note).sounding = this.sound(note.pitch, note.velocity);
      return;
    }

    if (tool === 'pencil') {
      const draft = pencilNote(
        this.drawn.spans,
        tick,
        pitchOfLane(lane),
        this.snap(),
        this.store.noteLength(),
        this.store.velocity()
      );
      if (draft) {
        const gesture = start('pencil');
        gesture.draft = draft;
        gesture.sounding = this.sound(draft.pitch, draft.velocity);
        this.marquee = this.draftRect(draft, view);
      }
      return;
    }

    if (!event.shiftKey) {
      this.selection.clearNotes();
    }
    start('marquee');
  }

  protected move(event: PointerEvent): void {
    const { x, y } = this.local(event);
    const gesture = this.gesture;
    if (!gesture) {
      this.hover(x, y);
      return;
    }

    if (!gesture.moved && Math.hypot(x - gesture.x, y - gesture.y) < DragThreshold) {
      return;
    }

    gesture.moved = true;
    const view = this.currentView();
    const tick = tickAtX(view, x);
    switch (gesture.kind) {
      case 'locate':
        this.transport.locate(snapTick(tick, this.snap()));
        break;
      case 'marquee': {
        this.marquee = {
          x: Math.min(gesture.x, x),
          y: Math.max(RULER_HEIGHT, Math.min(gesture.y, y)),
          width: Math.abs(x - gesture.x),
          height: Math.abs(y - Math.max(RULER_HEIGHT, gesture.y))
        };
        const drawn = this.drawn;
        if (drawn) {
          const found = itemsInRect(drawn.scene, drawn.index, view, this.marquee).map(i => drawn.notes[i]?.id ?? -1);
          this.selection.selectNotes(gesture.shift ? [...gesture.base, ...found] : found);
        }
        break;
      }
      case 'pencil':
        if (gesture.draft) {
          const snap = this.snap();
          const region = this.drawn?.spans.find(s => s.id === gesture.draft?.regionId);
          const absolute = (region?.start ?? 0) + gesture.draft.startTick;
          const end = snap > 0 ? Math.ceil(tick / snap) * snap : Math.round(tick);
          gesture.draft = { ...gesture.draft, lengthTicks: Math.max(snap || 10, end - absolute) };
          this.marquee = this.draftRect(gesture.draft, view);
        }
        break;
      case 'erase': {
        const hit = this.drawn ? hitTest(this.drawn.scene, this.drawn.index, view, x, y) : null;
        const note = hit ? this.drawn?.notes[hit.index] : undefined;
        if (note && !gesture.erased.has(note.id)) {
          gesture.erased.add(note.id);
          this.overrides = this.erasedOverrides(gesture.erased);
        }
        break;
      }
      default:
        this.overrides = this.edited(gesture, tick, Math.floor(laneAtY(view, y))).map(n => ({
          index: this.drawn?.indexOf.get(n.id) ?? -1,
          start: n.start,
          length: n.length,
          lane: laneOfPitch(n.pitch)
        }));
        break;
    }
  }

  protected up(event: PointerEvent): void {
    const gesture = this.gesture;
    this.gesture = null;
    capture(this.host.nativeElement, event.pointerId, false);
    if (!gesture) {
      return;
    }

    if (gesture.sounding !== null) {
      this.live.noteOff(gesture.sounding);
    }

    const finish = (commands: EditCommand[], label: string) => {
      if (commands.length === 0) {
        this.overrides = null;
        this.marquee = null;
        return Promise.resolve(null);
      }
      // The preview stays until the edit's delta has redrawn the notes (or it was refused), so nothing jumps back.
      return this.project.execute(commands, label).finally(() => {
        this.overrides = null;
        this.marquee = null;
      });
    };

    switch (gesture.kind) {
      case 'marquee':
      case 'locate':
        this.marquee = null;
        return;
      case 'erase': {
        const notes = this.drawn?.notes.filter(n => gesture.erased.has(n.id)) ?? [];
        void finish(deleteCommands(notes), notesLabel('Delete', notes.length));
        return;
      }
      case 'pencil': {
        const draft = gesture.draft;
        if (!draft) {
          return;
        }
        this.store.noteLength.set(draft.lengthTicks);
        const before = new Set(this.drawn?.notes.map(n => n.id));
        void finish(
          [
            {
              type: 'addNotes',
              regionId: draft.regionId,
              notes: [
                {
                  pitch: draft.pitch,
                  velocity: draft.velocity,
                  channel: 1,
                  startTick: draft.startTick,
                  lengthTicks: draft.lengthTicks
                }
              ]
            }
          ],
          'Create Note'
        ).then(result => {
          const op = result?.delta.ops.find(o => o.op === 'midiRegionUpserted');
          if (op?.op === 'midiRegionUpserted') {
            const added = op.region.notes.filter(n => !before.has(n.id)).map(n => n.id);
            this.selection.selectNotes(added);
          }
        });
        return;
      }
      default:
        break;
    }

    if (!gesture.moved) {
      if (!gesture.shift && gesture.note) {
        this.selection.selectNotes([gesture.note.id]);
      }
      return;
    }

    const { x, y } = this.local(event);
    const view = this.currentView();
    const edited = this.edited(gesture, tickAtX(view, x), Math.floor(laneAtY(view, y)));
    const original = new Map((this.drawn?.notes ?? []).map(n => [n.id, n]));
    const regions = new Map(this.store.regions().map(r => [r.id, r]));
    const copy = gesture.kind === 'move' && gesture.copy;
    const commands = noteCommands(edited, original, regions, copy);
    if (gesture.kind !== 'move' && gesture.note) {
      const resized = edited.find(n => n.id === gesture.note?.id);
      if (resized) {
        this.store.noteLength.set(resized.length);
      }
    }
    const verb = copy ? 'Copy' : gesture.kind === 'move' ? 'Move' : 'Resize';
    void finish(commands, notesLabel(verb, edited.length));
  }

  protected cancel(): void {
    if (this.gesture?.sounding != null) {
      this.live.noteOff(this.gesture.sounding);
    }
    this.gesture = null;
    this.overrides = null;
    this.marquee = null;
  }

  /** The selected notes where a move or resize by the pointer's travel puts them. */
  private edited(gesture: Gesture, tick: number, lane: number): PlacedNote[] {
    const grabbed = gesture.note;
    const selected = this.selection.notes();
    const notes = (this.drawn?.notes ?? []).filter(n => selected.has(n.id));
    if (!grabbed) {
      return [];
    }

    const snap = this.snap();
    switch (gesture.kind) {
      case 'move':
        return moveNotes(notes, grabbed, tick - gesture.tick, -(lane - gesture.lane), snap);
      case 'resize-end': {
        const delta = resizeEnd(grabbed, tick, snap).length - grabbed.length;
        return notes.map(n => ({ ...n, length: Math.max(snap || 10, n.length + delta) }));
      }
      case 'resize-start': {
        const moved = resizeStart(grabbed, tick, snap);
        const delta = moved.start - grabbed.start;
        return notes.map(n => {
          const end = n.start + n.length;
          const start = Math.max(0, Math.min(end - (snap || 10), n.start + delta));
          return { ...n, start, length: end - start };
        });
      }
      default:
        return [];
    }
  }

  /** Erased notes are hidden (moved off the lanes) until the delete's delta arrives. */
  private erasedOverrides(erased: ReadonlySet<number>): ItemOverride[] {
    return [...erased].map(id => ({ index: this.drawn?.indexOf.get(id) ?? -1, lane: 1 << 20 }));
  }

  private draftRect(draft: NonNullable<Gesture['draft']>, view: TimelineView): PixelRect {
    const region = this.drawn?.spans.find(s => s.id === draft.regionId);
    const start = (region?.start ?? 0) + draft.startTick;
    const x = xAtTick(view, start);
    return {
      x,
      y: yAtLane(view, laneOfPitch(draft.pitch)),
      width: Math.max(2, xAtTick(view, start + draft.lengthTicks) - x),
      height: view.laneHeight
    };
  }

  /** Plays a note on the track while the press lasts, when the Piano Roll's track is the one live MIDI plays. */
  private sound(pitch: number, velocity: number): number | null {
    if (this.store.track()?.id !== this.live.target()?.id) {
      return null;
    }
    this.live.noteOn(pitch, velocity);
    return pitch;
  }

  private release(): void {
    if (this.gesture?.sounding != null) {
      this.live.noteOff(this.gesture.sounding);
    }
  }

  private hover(x: number, y: number): void {
    if (y < RULER_HEIGHT) {
      this.cursor.set('pointer');
      return;
    }

    const hit = this.drawn ? hitTest(this.drawn.scene, this.drawn.index, this.currentView(), x, y) : null;
    const tool = this.store.tool();
    if (tool === 'eraser') {
      this.cursor.set(hit ? 'not-allowed' : 'default');
    } else if (hit) {
      this.cursor.set(Cursors[hit.zone]);
    } else {
      this.cursor.set(tool === 'pencil' ? 'copy' : 'default');
    }
  }

  // ---- wheel, double-click

  protected wheel(event: WheelEvent): void {
    event.preventDefault();
    const { x } = this.local(event);
    if (event.metaKey || event.ctrlKey) {
      this.store.zoom(Math.exp(-event.deltaY * 0.004), x);
    } else if (event.altKey) {
      this.store.zoomLanes(Math.exp(-event.deltaY * 0.004));
    } else if (event.shiftKey) {
      this.store.scrollBy(event.deltaY || event.deltaX, 0, this.visibleLanes());
    } else {
      this.store.scrollBy(event.deltaX, event.deltaY, this.visibleLanes());
    }
  }

  /** A double-click on an empty spot draws a note there, as the pencil does. */
  protected doubleClick(event: MouseEvent): void {
    const { x, y } = this.local(event);
    if (y <= RULER_HEIGHT || !this.drawn || this.store.tool() !== 'pointer') {
      return;
    }

    const view = this.currentView();
    if (hitTest(this.drawn.scene, this.drawn.index, view, x, y)) {
      return;
    }

    const draft = pencilNote(
      this.drawn.spans,
      tickAtX(view, x),
      pitchOfLane(laneAtY(view, y)),
      this.snap(),
      this.store.noteLength(),
      this.store.velocity()
    );
    if (draft) {
      const { regionId, ...note } = draft;
      void this.project.execute([{ type: 'addNotes', regionId, notes: [{ ...note, channel: 1 }] }], 'Create Note');
    }
  }
}

/** Captures (or releases) the pointer where the platform can (jsdom cannot). */
function capture(element: HTMLElement, pointerId: number, on: boolean): void {
  if ('setPointerCapture' in element && typeof element.setPointerCapture === 'function') {
    if (on) {
      element.setPointerCapture(pointerId);
    } else if (element.hasPointerCapture(pointerId)) {
      element.releasePointerCapture(pointerId);
    }
  }
}

function sameView(a: TimelineView, b: TimelineView | null): boolean {
  return (
    b !== null &&
    a.originTick === b.originTick &&
    a.pixelsPerTick === b.pixelsPerTick &&
    a.firstLane === b.firstLane &&
    a.laneHeight === b.laneHeight &&
    a.playheadTick === b.playheadTick &&
    a.marquee === b.marquee &&
    a.overrides === b.overrides &&
    a.spans === b.spans
  );
}
