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
import type { EditCommand } from '@anode/api-client';
import {
  cloneScene,
  CYCLE_BAND_TOP,
  type CycleArea,
  documentTheme,
  hitTest,
  type ItemOverride,
  itemsInRect,
  laneAtY,
  LaneIntervalIndex,
  type PixelRect,
  type RegionZone,
  RULER_HEIGHT,
  snapStep,
  snapTick,
  tickAtX,
  type TimelineView,
  yAtLane
} from '@anode/timeline';
import { PeaksCache } from '../state/peaks';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TracksViewStore } from '../state/tracks-view.store';
import { barTicks, TransportStore } from '../state/transport.store';
import { AudioImport } from './audio-import';
import {
  type EditContext,
  fadeInTo,
  fadeOutFrom,
  moveCommands,
  moveRegions,
  newMidiRegion,
  plural,
  splitSpan,
  trimEnd,
  trimMidiEnd,
  trimMidiStart,
  trimStart
} from './region-edit';
import { buildScene, type TracksScene } from './scene';
import { createSurface, type TimelineSurface } from './timeline-surface';

/** Pixels the pointer moves before a press becomes a drag. */
const DragThreshold = 3;

/** A press in progress: what it grabbed and where it started. */
interface Gesture {
  kind: RegionZone | 'marquee' | 'cycle' | 'locate';
  regionId: string | null;
  x: number;
  y: number;
  tick: number;
  lane: number;
  moved: boolean;
  shift: boolean;
  /** The selection before a marquee started (⇧ adds to it). */
  base: ReadonlySet<string>;
}

/** A region of either kind as the pointer edits it: where it is and on which track. */
interface RegionSpan {
  id: string;
  trackId: string;
  startTick: number;
  endTick: number;
  midi: boolean;
}

const Cursors: Record<RegionZone, string> = {
  body: 'default',
  'trim-start': 'ew-resize',
  'trim-end': 'ew-resize',
  'fade-in': 'nesw-resize',
  'fade-out': 'nwse-resize'
};

/**
 * The Tracks area's canvas (ADR-0007): audio regions with their waveforms, names, fades and selection, MIDI regions
 * with their note miniatures, the ruler with bars, the cycle area and the project end, and the playhead, drawn by
 * `@anode/timeline` in its render worker. The pointer edits here: the pointer tool selects (click, ⇧-click, marquee),
 * moves (across tracks of the same kind too), trims edges (an audio region's lower half, a MIDI region's whole height)
 * and drags fades (upper half), the scissors split, the pencil (or a double-click on an empty MIDI lane) makes an empty
 * MIDI region, all snapped to the grid. A drag is a local preview; dropping sends one command batch
 * (docs/01-implementation-plan.md §4.4). The ruler locates the playhead and, in its lower band, draws or toggles the
 * cycle. WAV files dropped here are imported onto the track under the pointer.
 */
@Component({
  selector: 'an-timeline',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'relative block min-h-0 min-w-0 touch-none overflow-hidden',
    '[style.cursor]': 'cursor()',
    '(pointerdown)': 'down($event)',
    '(pointermove)': 'move($event)',
    '(pointerup)': 'up($event)',
    '(pointercancel)': 'cancel()',
    '(wheel)': 'wheel($event)',
    '(dblclick)': 'doubleClick($event)',
    '(dragover)': 'dragOver($event)',
    '(dragleave)': 'dropTarget.set(null)',
    '(drop)': 'drop($event)'
  },
  template: `
    <canvas #canvas class="absolute inset-0 size-full" role="img" [attr.aria-label]="summary()"></canvas>
    @if (dropTarget(); as target) {
      <div
        class="border-primary bg-primary/10 pointer-events-none absolute border-2 border-dashed"
        [style.left.px]="0"
        [style.right.px]="0"
        [style.top.px]="target.y"
        [style.height.px]="target.height"
      ></div>
    }
  `
})
export class Timeline {
  private readonly project = inject(ProjectStore);
  private readonly transport = inject(TransportStore);
  private readonly selection = inject(SelectionStore);
  private readonly view = inject(TracksViewStore);
  private readonly peaks = inject(PeaksCache);
  private readonly audioImport = inject(AudioImport);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');

  private surface: TimelineSurface | null = null;
  private readonly ready = signal(false);
  private width = 1;
  private height = 1;
  /** The scene as drawn, kept on this thread for hit-testing (the worker has its own copy). */
  private drawn: (TracksScene & { index: LaneIntervalIndex }) | null = null;
  private readonly fileKeys = new Map<string, number>();
  private readonly sentWaveforms = new Set<string>();
  private gesture: Gesture | null = null;
  private overrides: ItemOverride[] | null = null;
  private marquee: PixelRect | null = null;
  private cycleDraft: CycleArea | null = null;
  /** Set when something the view does not show changed (scene, waveform, size): the next frame is sent regardless. */
  private dirty = true;
  private lastView: TimelineView | null = null;

  protected readonly cursor = signal('default');
  protected readonly dropTarget = signal<{ y: number; height: number } | null>(null);
  protected readonly summary = computed(() => {
    const regions = this.project.regions().size + this.project.midiRegions().size;
    const tracks = this.project.trackIds().length;
    return `Tracks area: ${regions} ${regions === 1 ? 'region' : 'regions'} on ${tracks} ${tracks === 1 ? 'track' : 'tracks'}`;
  });

  private readonly scene = computed(() => {
    const signature = this.project.settings().signature;
    return buildScene({
      tracks: this.project.trackList(),
      regions: this.project.regionList(),
      midiRegions: this.project.midiRegionList(),
      framesPerTick: this.project.framesPerTick(),
      beatsPerBar: (signature.numerator * 4) / signature.denominator,
      selected: this.selection.regions(),
      waveformKey: fileId => this.fileKey(fileId)
    });
  });

  constructor() {
    afterNextRender(() => void this.start());
    effect(() => {
      const scene = this.scene();
      const ready = this.ready();
      untracked(() => this.pushScene(scene, ready));
    });
    effect(() => {
      const files = new Set(this.project.regionList().map(region => region.fileId));
      const level = this.peaks.levelFor(this.project.framesPerTick() / this.view.pixelsPerTick());
      if (this.ready()) {
        untracked(() => files.forEach(fileId => this.loadPeaks(fileId, level)));
      }
    });
    effect(() => {
      // A new project starts with its own files: forget what the renderer was sent.
      this.project.info();
      untracked(() => this.sentWaveforms.clear());
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
    });
  }

  // ---- drawing

  private async start(): Promise<void> {
    this.measure();
    try {
      this.surface = await createSurface(this.canvas().nativeElement, this.width, this.height, devicePixelRatio || 1);
    } catch (error) {
      console.error('Anode: the timeline renderer did not start', error);
    }
    this.ready.set(this.surface !== null);
  }

  private measure(): void {
    const rect = this.host.nativeElement.getBoundingClientRect();
    this.width = Math.max(1, Math.round(rect.width));
    this.height = Math.max(1, Math.round(rect.height));
    this.surface?.resize(this.width, this.height, devicePixelRatio || 1);
    this.dirty = true;
  }

  private pushScene(scene: TracksScene, ready: boolean): void {
    this.drawn = { ...scene, index: new LaneIntervalIndex(scene.scene.items, scene.scene.laneCount) };
    if (ready) {
      this.surface?.setScene({ ...cloneScene(scene.scene), theme: documentTheme() });
      this.dirty = true;
    }
  }

  private fileKey(fileId: string): number {
    let key = this.fileKeys.get(fileId);
    if (key === undefined) {
      key = this.fileKeys.size;
      this.fileKeys.set(fileId, key);
    }
    return key;
  }

  private loadPeaks(fileId: string, level: number): void {
    const sent = `${fileId}@${level}`;
    if (this.sentWaveforms.has(sent)) {
      return;
    }

    this.sentWaveforms.add(sent);
    void this.peaks.get(fileId, level).then(waveform => {
      if (waveform && this.surface) {
        this.surface.setWaveform(this.fileKey(fileId), waveform);
        this.dirty = true;
      } else {
        this.sentWaveforms.delete(sent);
      }
    });
  }

  /** The view this frame: zoom, scroll, playhead, cycle, project end and any drag preview. */
  currentView(now = performance.now()): TimelineView {
    const settings = this.project.settings();
    const playheadTick = this.transport.positionAt(now);
    return {
      originTick: this.view.originTick(),
      pixelsPerTick: this.view.pixelsPerTick(),
      firstLane: this.view.firstLane(),
      laneHeight: this.view.laneHeight(),
      playheadTick,
      cycle: this.cycleDraft ?? {
        enabled: settings.cycle.enabled,
        start: settings.cycle.startTick,
        end: settings.cycle.endTick
      },
      endTick: settings.endTick,
      marquee: this.marquee,
      overrides: this.overrides
    };
  }

  private drawFrame(): void {
    if (!this.surface) {
      return;
    }

    const now = performance.now();
    if (this.transport.playing() && this.view.follow() && !this.gesture) {
      this.view.reveal(this.transport.positionAt(now), this.width);
    }

    const view = this.currentView(now);
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

  private context(): EditContext {
    const signature = this.project.settings().signature;
    const step = snapStep(960, (signature.numerator * 4) / signature.denominator, this.view.pixelsPerTick());
    return { framesPerTick: this.project.framesPerTick(), snap: this.view.snap() ? step : 0 };
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
    const start = (kind: Gesture['kind'], regionId: string | null = null): void => {
      this.gesture = {
        kind,
        regionId,
        x,
        y,
        tick,
        lane,
        moved: false,
        shift: event.shiftKey,
        base: this.selection.regions()
      };
    };

    if (y < RULER_HEIGHT) {
      if (y >= CYCLE_BAND_TOP) {
        start('cycle');
      } else {
        this.transport.locate(snapTick(tick, this.context().snap));
        start('locate');
      }
      return;
    }

    const hit = hitTest(this.drawn.scene, this.drawn.index, view, x, y);
    const regionId = hit ? (this.drawn.regionIds[hit.index] ?? null) : null;
    const region = regionId ? this.span(regionId) : undefined;
    if (this.view.tool() === 'scissors') {
      if (region) {
        const at = splitSpan(region.startTick, region.endTick, tick, this.context().snap);
        if (at !== null) {
          void this.project.execute([{ type: 'splitRegion', regionId: region.id, atTick: at }], 'Split Region');
        }
      }
      return;
    }

    if (this.view.tool() === 'pencil' && !region) {
      this.createMidiRegion(lane, tick);
      return;
    }

    if (hit && region) {
      if (event.shiftKey) {
        this.selection.toggle(region.id);
      } else if (!this.selection.isSelected(region.id)) {
        this.selection.select([region.id]);
      }
      this.selection.track.set(region.trackId);
      start(hit.zone, region.id);
      return;
    }

    if (!event.shiftKey) {
      this.selection.clear();
    }
    const trackId = this.project.trackIds()[lane];
    if (trackId) {
      this.selection.track.set(trackId);
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
    const context = this.context();
    switch (gesture.kind) {
      case 'locate':
        this.transport.locate(snapTick(tick, context.snap));
        break;
      case 'cycle': {
        const a = snapTick(gesture.tick, context.snap);
        const b = snapTick(tick, context.snap);
        this.cycleDraft = { enabled: true, start: Math.min(a, b), end: Math.max(a, b) };
        break;
      }
      case 'marquee': {
        this.marquee = {
          x: Math.min(gesture.x, x),
          y: Math.max(RULER_HEIGHT, Math.min(gesture.y, y)),
          width: Math.abs(x - gesture.x),
          height: Math.abs(y - Math.max(RULER_HEIGHT, gesture.y))
        };
        if (this.drawn) {
          const found = itemsInRect(this.drawn.scene, this.drawn.index, view, this.marquee).map(
            i => this.drawn?.regionIds[i] ?? ''
          );
          this.selection.select(gesture.shift ? [...gesture.base, ...found] : found);
        }
        break;
      }
      default:
        this.overrides = this.preview(gesture, tick, Math.floor(laneAtY(view, y)), context);
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

    if (gesture.kind === 'cycle') {
      const draft = this.cycleDraft;
      this.cycleDraft = null;
      if (gesture.moved && draft && draft.end > draft.start) {
        this.transport.setCycleRange(draft.start, draft.end);
      } else if (!gesture.moved) {
        this.transport.toggleCycle();
      }
      return;
    }

    if (gesture.kind === 'marquee' || gesture.kind === 'locate') {
      this.marquee = null;
      return;
    }

    if (!gesture.moved) {
      // A click on one of several selected regions selects just it.
      if (!gesture.shift && gesture.regionId) {
        this.selection.select([gesture.regionId]);
      }
      return;
    }

    const { x, y } = this.local(event);
    const view = this.currentView();
    const commands = this.commit(gesture, tickAtX(view, x), Math.floor(laneAtY(view, y)), this.context());
    if (commands.commands.length === 0) {
      this.overrides = null;
      return;
    }

    // The preview stays until the edit's delta has redrawn the regions (or it was refused), so nothing jumps back.
    void this.project.execute(commands.commands, commands.label).finally(() => (this.overrides = null));
  }

  protected cancel(): void {
    this.gesture = null;
    this.overrides = null;
    this.marquee = null;
    this.cycleDraft = null;
  }

  /** Where a drag puts the regions it moves or the region it trims or fades. */
  private preview(gesture: Gesture, tick: number, lane: number, context: EditContext): ItemOverride[] {
    const drawn = this.drawn;
    const index = gesture.regionId ? drawn?.indexOf.get(gesture.regionId) : undefined;
    if (!drawn || !gesture.regionId || index === undefined) {
      return [];
    }

    if (gesture.kind === 'body') {
      return this.moved(gesture, tick, lane, context).map(m => ({
        index: drawn.indexOf.get(m.id) ?? -1,
        start: m.startTick,
        lane: m.lane
      }));
    }

    const midi = this.project.midiRegions().get(gesture.regionId);
    if (midi) {
      // A trimmed start keeps the notes in time: the miniatures' origin stays (fileOffset is in ticks for MIDI).
      const t =
        gesture.kind === 'trim-start'
          ? trimMidiStart(midi, tick, context.snap)
          : gesture.kind === 'trim-end'
            ? trimMidiEnd(midi, tick, context.snap)
            : null;
      return t ? [{ index, start: t.startTick, length: t.lengthTicks, fileOffset: t.startTick - midi.startTick }] : [];
    }

    const region = this.project.regions().get(gesture.regionId);
    if (!region) {
      return [];
    }

    switch (gesture.kind) {
      case 'trim-start': {
        const t = trimStart(region, tick, context);
        return [{ index, start: t.startTick, length: t.length / context.framesPerTick, fileOffset: t.fileOffset }];
      }
      case 'trim-end': {
        const t = trimEnd(region, tick, this.fileFrames(region), context);
        return [{ index, length: t.length / context.framesPerTick }];
      }
      case 'fade-in':
        return [{ index, fadeIn: fadeInTo(region, tick, context) }];
      case 'fade-out':
        return [{ index, fadeOut: fadeOutFrom(region, tick, context) }];
      default:
        return [];
    }
  }

  /** The command batch a finished drag sends. */
  private commit(
    gesture: Gesture,
    tick: number,
    lane: number,
    context: EditContext
  ): { commands: EditCommand[]; label: string } {
    if (gesture.kind === 'body') {
      const moved = this.moved(gesture, tick, lane, context);
      const all = new Map<string, { startTick: number; trackId: string }>([
        ...this.project.regions(),
        ...this.project.midiRegions()
      ]);
      const tracks = this.project.tracks();
      const commands = moveCommands(
        moved,
        all,
        l => this.project.trackIds()[l],
        (id, trackId) => (tracks.get(trackId)?.kind === 'audio') === !id.startsWith('mid_')
      );
      return { commands, label: plural('Move', commands.length) };
    }

    const midi = gesture.regionId ? this.project.midiRegions().get(gesture.regionId) : undefined;
    if (midi) {
      if (gesture.kind !== 'trim-start' && gesture.kind !== 'trim-end') {
        return { commands: [], label: '' };
      }
      const t =
        gesture.kind === 'trim-start' ? trimMidiStart(midi, tick, context.snap) : trimMidiEnd(midi, tick, context.snap);
      const same = t.startTick === midi.startTick && t.lengthTicks === midi.lengthTicks;
      return {
        commands: same ? [] : [{ type: 'resizeMidiRegion', regionId: midi.id, ...t }],
        label: 'Resize Region'
      };
    }

    const regions = this.project.regions();
    const region = gesture.regionId ? regions.get(gesture.regionId) : undefined;
    if (!region) {
      return { commands: [], label: '' };
    }

    switch (gesture.kind) {
      case 'trim-start':
      case 'trim-end': {
        const t =
          gesture.kind === 'trim-start'
            ? trimStart(region, tick, context)
            : trimEnd(region, tick, this.fileFrames(region), context);
        const same = t.startTick === region.startTick && t.length === region.length;
        return {
          commands: same ? [] : [{ type: 'resizeRegion', regionId: region.id, ...t }],
          label: 'Resize Region'
        };
      }
      case 'fade-in': {
        const fadeIn = fadeInTo(region, tick, context);
        return {
          commands: fadeIn === region.fadeIn ? [] : [{ type: 'setRegion', regionId: region.id, fadeIn }],
          label: 'Fade In'
        };
      }
      case 'fade-out': {
        const fadeOut = fadeOutFrom(region, tick, context);
        return {
          commands: fadeOut === region.fadeOut ? [] : [{ type: 'setRegion', regionId: region.id, fadeOut }],
          label: 'Fade Out'
        };
      }
      default:
        return { commands: [], label: '' };
    }
  }

  /** The selected regions where a move by the pointer's travel puts them. */
  private moved(gesture: Gesture, tick: number, lane: number, context: EditContext) {
    const lanes = new Map(this.project.trackIds().map((id, l) => [id, l]));
    const grabbed = gesture.regionId ? this.span(gesture.regionId) : undefined;
    const selected = [...this.selection.regions()]
      .map(id => this.span(id))
      .filter((r): r is RegionSpan => r !== undefined)
      .map(r => ({ id: r.id, startTick: r.startTick, lane: lanes.get(r.trackId) ?? 0 }));
    return moveRegions(
      selected,
      grabbed ?? { startTick: 0 },
      tick - gesture.tick,
      lane - gesture.lane,
      this.project.trackIds().length,
      context
    );
  }

  private fileFrames(region: { fileId: string; fileOffset: number; length: number }): number {
    return this.project.files().get(region.fileId)?.frames ?? region.fileOffset + region.length;
  }

  /** A region of either kind by id. */
  private span(id: string): RegionSpan | undefined {
    const midi = this.project.midiRegions().get(id);
    if (midi) {
      return {
        id,
        trackId: midi.trackId,
        startTick: midi.startTick,
        endTick: midi.startTick + midi.lengthTicks,
        midi: true
      };
    }
    const audio = this.project.regions().get(id);
    return audio
      ? {
          id,
          trackId: audio.trackId,
          startTick: audio.startTick,
          endTick: this.project.regionEnd(audio),
          midi: false
        }
      : undefined;
  }

  /** An empty one-bar MIDI region at `tick` on lane `lane`, when that lane's track takes MIDI. */
  private createMidiRegion(lane: number, tick: number): void {
    const trackId = this.project.trackIds()[lane];
    const track = trackId ? this.project.track(trackId) : undefined;
    if (!track || track.kind === 'audio') {
      return;
    }

    const region = newMidiRegion(tick, barTicks(this.project.settings().signature));
    this.selection.track.set(track.id);
    void this.project
      .execute([{ type: 'addMidiRegion', trackId: track.id, ...region }], 'Create MIDI Region')
      .then(result => {
        const added = result?.delta.ops.find(op => op.op === 'midiRegionUpserted');
        if (added?.op === 'midiRegionUpserted') {
          this.selection.select([added.region.id]);
        }
      });
  }

  private hover(x: number, y: number): void {
    if (y < RULER_HEIGHT) {
      this.cursor.set(y >= CYCLE_BAND_TOP ? 'col-resize' : 'pointer');
      return;
    }

    if (this.view.tool() === 'scissors') {
      this.cursor.set('crosshair');
      return;
    }

    if (this.view.tool() === 'pencil') {
      this.cursor.set('copy');
      return;
    }

    const hit = this.drawn ? hitTest(this.drawn.scene, this.drawn.index, this.currentView(), x, y) : null;
    this.cursor.set(hit ? Cursors[hit.zone] : 'default');
  }

  // ---- wheel, double-click, drop

  protected wheel(event: WheelEvent): void {
    event.preventDefault();
    const { x } = this.local(event);
    if (event.metaKey || event.ctrlKey) {
      this.view.zoom(Math.exp(-event.deltaY * 0.004), x);
    } else if (event.altKey) {
      this.view.zoomLanes(Math.exp(-event.deltaY * 0.004));
    } else if (event.shiftKey) {
      this.view.scrollBy(event.deltaY || event.deltaX, 0, this.project.trackIds().length);
    } else {
      this.view.scrollBy(event.deltaX, event.deltaY, this.project.trackIds().length);
    }
  }

  /**
   * Below the last track, a double-click adds one (as in Logic); on an empty spot of an instrument or MIDI track it
   * creates an empty MIDI region there.
   */
  protected doubleClick(event: MouseEvent): void {
    const { x, y } = this.local(event);
    if (y <= RULER_HEIGHT || !this.drawn) {
      return;
    }

    const view = this.currentView();
    const lane = laneAtY(view, y);
    if (lane >= this.project.trackIds().length) {
      void this.project.execute([{ type: 'addTrack', stereo: false }], 'New Track');
    } else if (!hitTest(this.drawn.scene, this.drawn.index, view, x, y)) {
      this.createMidiRegion(Math.floor(lane), tickAtX(view, x));
    }
  }

  protected dragOver(event: DragEvent): void {
    if (!event.dataTransfer?.types.includes('Files')) {
      return;
    }

    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
    const { y } = this.local(event);
    const view = this.currentView();
    const lane = Math.floor(laneAtY(view, Math.max(RULER_HEIGHT, y)));
    const count = this.project.trackIds().length;
    const top = yAtLane(view, Math.min(lane, count));
    this.dropTarget.set({ y: top, height: lane < count ? view.laneHeight : Math.max(8, this.height - top) });
  }

  protected drop(event: DragEvent): void {
    this.dropTarget.set(null);
    const files = [...(event.dataTransfer?.files ?? [])];
    if (files.length === 0) {
      return;
    }

    event.preventDefault();
    const { x, y } = this.local(event);
    const view = this.currentView();
    const lane = Math.floor(laneAtY(view, Math.max(RULER_HEIGHT, y)));
    const trackId = this.project.trackIds()[lane] ?? null;
    const tick = snapTick(tickAtX(view, x), this.context().snap);
    void this.audioImport.importFiles(files, trackId, tick);
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

/** Whether two views draw the same frame (the overrides and marquee are replaced, never mutated). */
function sameView(a: TimelineView, b: TimelineView | null): boolean {
  return (
    b !== null &&
    a.originTick === b.originTick &&
    a.pixelsPerTick === b.pixelsPerTick &&
    a.firstLane === b.firstLane &&
    a.laneHeight === b.laneHeight &&
    a.playheadTick === b.playheadTick &&
    a.endTick === b.endTick &&
    a.marquee === b.marquee &&
    a.overrides === b.overrides &&
    a.cycle?.enabled === b.cycle?.enabled &&
    a.cycle?.start === b.cycle?.start &&
    a.cycle?.end === b.cycle?.end
  );
}
