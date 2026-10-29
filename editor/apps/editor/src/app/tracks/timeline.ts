import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  input,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import type { AudioRegion, EditCommand } from '@anode/api-client';
import {
  cloneScene,
  CYCLE_BAND_TOP,
  type CycleArea,
  hitTest,
  type ItemOverride,
  itemsInRect,
  laneAtY,
  LaneIntervalIndex,
  type PixelRect,
  type RegionZone,
  RULER_HEIGHT,
  tickAtX,
  type TimelineView,
  xAtTick,
  yAtLane
} from '@anode/timeline';
import { XuiInputImports } from '@xui/input';
import { LoopDragType, LoopsStore } from '../loops/loops.store';
import { ProjectFileType, ServerFileType } from '../state/browser.store';
import { LanesStore } from '../state/lanes.store';
import { PeaksCache } from '../state/peaks';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { ThemeStore } from '../state/theme.store';
import { type TracksTool, TracksViewStore } from '../state/tracks-view.store';
import { barTicks, TransportStore } from '../state/transport.store';
import { AudioImport } from './audio-import';
import { swipeComp } from './comp';
import { applyDragMode, type Span } from './drag-modes';
import { FlexOverlay } from './flex-overlay';
import {
  type EditContext,
  fadeInTo,
  fadeOutFrom,
  moveCommands,
  moveRegions,
  newMidiRegion,
  plural,
  snapIn,
  splitSpan,
  trimEnd,
  trimMidiEnd,
  trimMidiStart,
  trimStart
} from './region-edit';
import { buildScene, type TracksScene } from './scene';
import { snapBy, type SnapContext, snapUnit } from './snap';
import { createSurface, type TimelineSurface } from './timeline-surface';
import { adjustmentCommands, crossfadeCommands, muteCommands, type RegionLookup, renameCommand } from './tool-edits';

/** Pixels the pointer moves before a press becomes a drag. */
const DragThreshold = 3;
/** How close (CSS pixels) to where two regions meet the Fade tool makes a crossfade. */
const BoundaryPx = 8;

type GestureKind =
  | RegionZone
  | 'marquee'
  | 'cycle'
  | 'locate'
  | 'punch'
  | 'swipe'
  | 'range'
  | 'zoom'
  | 'solo'
  | 'fade-tool'
  | 'curve-in'
  | 'curve-out'
  | 'xfade';

/** A press in progress: what it grabbed and where it started. */
interface Gesture {
  kind: GestureKind;
  regionId: string | null;
  x: number;
  y: number;
  tick: number;
  lane: number;
  moved: boolean;
  shift: boolean;
  alt: boolean;
  /** The selection before a marquee started (⇧ adds to it). */
  base: ReadonlySet<string>;
  /** The Fade tool: the region after the boundary of a crossfade, or which edge a fade drags. */
  other?: string;
  edge?: 'in' | 'out';
}

/** A region of any kind as the pointer edits it: where it is and on which track. */
interface RegionSpan {
  id: string;
  trackId: string;
  startTick: number;
  endTick: number;
  kind: 'audio' | 'midi' | 'folder' | 'pattern';
}

/** What a right-click (or the T key) opens the menu on. */
export type MenuTarget =
  /** On a lane: the track and tick under the pointer, for "Create Pattern Region". */
  | { kind: 'tools'; trackId?: string; tick?: number }
  | { kind: 'folder'; folderId: string; takeId: string | null }
  | { kind: 'regions'; ids: readonly string[] };

const Cursors: Record<RegionZone, string> = {
  body: 'default',
  'trim-start': 'ew-resize',
  'trim-end': 'ew-resize',
  'fade-in': 'nesw-resize',
  'fade-out': 'nwse-resize'
};

const ToolCursors: Record<TracksTool, string> = {
  pointer: 'default',
  pencil: 'copy',
  eraser: 'not-allowed',
  text: 'text',
  scissors: 'crosshair',
  glue: 'cell',
  solo: 'help',
  mute: 'pointer',
  zoom: 'zoom-in',
  fade: 'nwse-resize',
  marquee: 'crosshair'
};

/**
 * The Tracks area's canvas (ADR-0007): audio regions with waveforms, names, fades and their curves; MIDI regions with
 * note miniatures; take folders showing their comp, and an open folder's takes in rows under it; the ruler with bars of
 * the signature track, the cycle area and the autopunch range; the playhead. Drawn by `@anode/timeline` in its render
 * worker, in the light or dark palette as the appearance changes.
 *
 * The pointer edits with the left-click tool (the ⌘-click tool with ⌘): Pointer selects, moves (⌥ copies), trims and
 * drags fades (⌃⇧ on a fade changes its curve); a drag on a take is Quick Swipe Comping; Pencil makes MIDI regions;
 * Eraser deletes; Text renames; Scissors split; Glue joins; Solo plays from the click while held; Mute mutes; Zoom
 * zooms (⌥ out, drag a range); Fade drags fades, their curves (vertically, inside a fade) and crossfades (across where
 * two regions meet); Marquee selects a range to delete. Moves follow the drag mode and everything snaps by the snap
 * mode. A drag is a local preview; dropping sends one command batch (docs/01-implementation-plan.md §4.4). The ruler
 * locates the playhead and, in its lower band, draws or toggles the cycle, or the punch range under autopunch. WAV and
 * MIDI files, and files from the browsers, dropped here are imported onto the row under the pointer.
 */
@Component({
  selector: 'an-timeline',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FlexOverlay, XuiInputImports],
  host: {
    class: 'relative block min-h-0 min-w-0 touch-none overflow-hidden',
    '[style.cursor]': 'cursor()',
    '(pointerdown)': 'down($event)',
    '(pointermove)': 'move($event)',
    '(pointerup)': 'up($event)',
    '(pointercancel)': 'cancel()',
    '(pointerleave)': 'pointerAt.set(null)',
    '(wheel)': 'wheel($event)',
    '(dblclick)': 'doubleClick($event)',
    '(dragover)': 'dragOver($event)',
    '(dragleave)': 'dropTarget.set(null)',
    '(drop)': 'drop($event)'
  },
  template: `
    <canvas #canvas class="absolute inset-0 size-full" role="img" [attr.aria-label]="summary()"></canvas>
    <an-flex-overlay [lanesTop]="lanesTop()" />
    @if (dropTarget(); as target) {
      <div
        class="border-primary bg-primary/10 pointer-events-none absolute border-2 border-dashed"
        [style.left.px]="0"
        [style.right.px]="0"
        [style.top.px]="target.y"
        [style.height.px]="target.height"
      ></div>
    }
    @if (renaming(); as r) {
      <input
        #rename
        xuiInput
        class="absolute h-5 px-1 text-[11px]"
        aria-label="Region name"
        data-testid="region-rename"
        [style.left.px]="r.x"
        [style.top.px]="r.y"
        [style.width.px]="r.width"
        [value]="r.name"
        (pointerdown)="$event.stopPropagation()"
        (keydown.enter)="commitRename(rename.value)"
        (keydown.escape)="renaming.set(null)"
        (blur)="commitRename(rename.value)"
      />
    }
  `
})
export class Timeline {
  private readonly project = inject(ProjectStore);
  private readonly transport = inject(TransportStore);
  private readonly selection = inject(SelectionStore);
  private readonly view = inject(TracksViewStore);
  private readonly lanes = inject(LanesStore);
  private readonly peaks = inject(PeaksCache);
  private readonly theme = inject(ThemeStore);
  private readonly audioImport = inject(AudioImport);
  private readonly loops = inject(LoopsStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly renameInput = viewChild<ElementRef<HTMLInputElement>>('rename');

  /** Pixels between the ruler and the lanes: the global tracks shown there. */
  readonly globalHeight = input(0);

  private surface: TimelineSurface | null = null;
  private readonly ready = signal(false);
  private width = 1;
  private height = 1;
  /** The scene as drawn, kept on this thread for hit-testing (the worker has its own copy). */
  private drawn: (TracksScene & { index: LaneIntervalIndex; parts: Map<string, number[]> }) | null = null;
  private readonly fileKeys = new Map<string, number>();
  private readonly sentWaveforms = new Set<string>();
  private gesture: Gesture | null = null;
  private overrides: ItemOverride[] | null = null;
  private marqueeRect: PixelRect | null = null;
  private cycleDraft: CycleArea | null = null;
  private punchDraft: CycleArea | null = null;
  /** Set when something the view does not show changed (scene, waveform, size): the next frame is sent regardless. */
  private dirty = true;
  private lastView: TimelineView | null = null;

  protected readonly cursor = signal('default');
  protected readonly dropTarget = signal<{ y: number; height: number } | null>(null);
  /** The Text tool's field over a region. */
  protected readonly renaming = signal<{ id: string; name: string; x: number; y: number; width: number } | null>(null);
  /** Where the pointer is over the timeline, in client pixels (the T key opens the tool menu there). */
  readonly pointerAt = signal<{ x: number; y: number } | null>(null);
  /** What the context menu opens on (set by a right-click before the menu opens). */
  readonly menuTarget = signal<MenuTarget>({ kind: 'tools' });

  protected readonly summary = computed(() => {
    const regions =
      this.project.regions().size +
      this.project.midiRegions().size +
      this.project.takeFolders().size +
      this.project.patternRegions().size;
    const tracks = this.project.trackIds().length;
    return `Tracks area: ${regions} ${regions === 1 ? 'region' : 'regions'} on ${tracks} ${tracks === 1 ? 'track' : 'tracks'}`;
  });

  private readonly scene = computed(() => {
    const settings = this.project.settings();
    const signature = settings.signature;
    return buildScene({
      tracks: this.project.trackList(),
      rows: this.lanes.rows(),
      regions: this.project.regionList(),
      midiRegions: this.project.midiRegionList(),
      patternRegions: this.project.patternRegionList(),
      keys: settings.keys,
      takeFolders: this.project.takeFolderList(),
      framesPerTick: this.project.framesPerTick(),
      rate: span => this.project.regionRate(span),
      timing: region => this.project.timing(region),
      beatsPerBar: (signature.numerator * 4) / signature.denominator,
      meter: settings.signatures,
      selected: this.selection.regions(),
      waveformKey: fileId => this.fileKey(fileId)
    });
  });

  constructor() {
    afterNextRender(() => void this.start());
    effect(() => {
      const scene = this.scene();
      const ready = this.ready();
      const theme = this.theme.appearance();
      untracked(() => this.pushScene(scene, ready, theme));
    });
    effect(() => {
      const files = new Set([
        ...this.project.regionList().map(region => region.fileId),
        ...this.project.takeFolderList().flatMap(folder => folder.takes.map(take => take.fileId))
      ]);
      const level = this.peaks.levelFor(this.project.framesPerTick() / this.view.pixelsPerTick());
      // A file's new version (the Audio File Editor) brings new peaks.
      this.project.files();
      if (this.ready()) {
        untracked(() => files.forEach(fileId => this.loadPeaks(fileId, level)));
      }
    });
    effect(() => {
      // A new project starts with its own files: forget what the renderer was sent.
      this.project.info();
      untracked(() => this.sentWaveforms.clear());
    });
    effect(() => {
      const input = this.renameInput()?.nativeElement;
      input?.focus();
      input?.select();
    });
    effect(() => {
      this.globalHeight();
      this.view.marquee();
      this.dirty = true;
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

  private pushScene(scene: TracksScene, ready: boolean, theme: 'dark' | 'light'): void {
    const parts = new Map<string, number[]>();
    scene.regionIds.forEach((id, i) => {
      if (scene.indexOf.get(id) !== i) {
        parts.set(id, [...(parts.get(id) ?? []), i]);
      }
    });
    this.drawn = { ...scene, parts, index: new LaneIntervalIndex(scene.scene.items, scene.scene.laneCount) };
    if (ready) {
      this.surface?.setScene({ ...cloneScene(scene.scene), theme });
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
    const sent = `${fileId}@${this.project.files().get(fileId)?.version ?? 0}@${level}`;
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

  protected lanesTop(): number {
    return RULER_HEIGHT + this.globalHeight();
  }

  /** The view this frame: zoom, scroll, playhead, cycle, punch, project end and any drag preview. */
  currentView(now = performance.now()): TimelineView {
    const settings = this.project.settings();
    const playheadTick = this.transport.positionAt(now);
    const recording = settings.recording;
    return {
      originTick: this.view.originTick(),
      pixelsPerTick: this.view.pixelsPerTick(),
      firstLane: this.view.firstLane(),
      laneHeight: this.view.laneHeight(),
      lanesTop: this.lanesTop(),
      playheadTick,
      cycle: this.cycleDraft ?? {
        enabled: settings.cycle.enabled,
        start: settings.cycle.startTick,
        end: settings.cycle.endTick
      },
      punch:
        this.punchDraft ??
        (recording.autopunch ? { enabled: true, start: recording.punchInTick, end: recording.punchOutTick } : null),
      endTick: settings.endTick,
      marquee: this.marqueeRect ?? this.rangeRect(),
      overrides: this.overrides
    };
  }

  /** The marquee tool's selection as a rectangle. */
  private rangeRect(): PixelRect | null {
    const range = this.view.marquee();
    if (!range) {
      return null;
    }
    const view = { ...(this.lastView ?? this.baseView()) };
    const x0 = xAtTick(view, range.startTick);
    const x1 = xAtTick(view, range.endTick);
    const y0 = yAtLane(view, range.laneFrom);
    const y1 = yAtLane(view, range.laneTo + 1);
    return { x: x0, y: y0, width: x1 - x0, height: y1 - y0 };
  }

  private baseView(): TimelineView {
    return {
      originTick: this.view.originTick(),
      pixelsPerTick: this.view.pixelsPerTick(),
      firstLane: this.view.firstLane(),
      laneHeight: this.view.laneHeight(),
      lanesTop: this.lanesTop(),
      playheadTick: 0
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

  // ---- context

  private local(event: { clientX: number; clientY: number }): { x: number; y: number } {
    const rect = this.host.nativeElement.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }

  private snapContext(): SnapContext {
    return {
      meter: this.project.meter(),
      tempo: this.project.tempoMap(),
      sampleRate: this.project.info()?.sampleRate ?? 48_000,
      pixelsPerTick: this.view.pixelsPerTick()
    };
  }

  /** How edits snap and measure audio: the snap mode (or none), and frames per tick over `region`. */
  private context(region?: Pick<AudioRegion, 'startTick' | 'length'>): EditContext {
    const snap = this.snapContext();
    const mode = this.view.snapMode();
    const on = this.view.snap();
    return {
      framesPerTick: region ? this.project.regionRate(region) : this.project.framesPerTick(),
      snap: on ? snapUnit(0, mode, snap) : 0,
      snapTo: tick => (on ? snapBy(tick, mode, snap) : Math.max(0, Math.round(tick)))
    };
  }

  private lookup(): RegionLookup {
    const tempo = this.project.tempoMap();
    const rate = this.project.info()?.sampleRate ?? 48_000;
    return {
      regions: this.project.regions(),
      midiRegions: this.project.midiRegions(),
      takeFolders: this.project.takeFolders(),
      patternRegions: this.project.patternRegions(),
      endOf: r => this.project.regionEnd(r),
      frames: (from, to) => Math.round(tempo.framesBetween(from, to - from, rate)),
      fileFrames: fileId => this.project.files().get(fileId)?.frames ?? Infinity
    };
  }

  /** The region of any kind under (x, y), with its hit zone. */
  private hitAt(x: number, y: number, view: TimelineView): { id: string; zone: RegionZone; index: number } | null {
    if (!this.drawn) {
      return null;
    }
    const hit = hitTest(this.drawn.scene, this.drawn.index, view, x, y);
    const id = hit ? this.drawn.regionIds[hit.index] : undefined;
    return hit && id ? { id, zone: hit.zone, index: hit.index } : null;
  }

  // ---- pointer

  protected down(event: PointerEvent): void {
    if (!this.drawn) {
      return;
    }
    const { x, y } = this.local(event);
    const view = this.currentView();
    if (event.button === 2) {
      this.menuTarget.set(this.menuFor(x, y, view));
      return;
    }
    if (event.button !== 0) {
      return;
    }

    const tick = tickAtX(view, x);
    const lane = Math.floor(laneAtY(view, y));
    capture(this.host.nativeElement, event.pointerId, true);
    const start = (kind: GestureKind, regionId: string | null = null, extra: Partial<Gesture> = {}): void => {
      this.gesture = {
        kind,
        regionId,
        x,
        y,
        tick,
        lane,
        moved: false,
        shift: event.shiftKey,
        alt: event.altKey,
        base: this.selection.regions(),
        ...extra
      };
    };

    if (y < RULER_HEIGHT) {
      const autopunch = this.project.settings().recording.autopunch;
      if (autopunch && y >= RULER_HEIGHT - 6) {
        start('punch');
      } else if (y >= CYCLE_BAND_TOP) {
        start('cycle');
      } else {
        this.transport.locate(snapIn(this.context(), tick));
        start('locate');
      }
      return;
    }
    if (y < this.lanesTop()) {
      return; // the global tracks handle their own band
    }

    const tool = this.view.toolFor(event);
    const hit = this.hitAt(x, y, view);
    const row = this.lanes.rows()[lane];
    const region = hit ? this.span(hit.id) : undefined;
    if (this.view.marquee() && tool !== 'marquee') {
      this.view.marquee.set(null);
    }

    switch (tool) {
      case 'scissors':
        if (region && region.kind !== 'folder' && region.kind !== 'pattern') {
          const at = splitSpan(region.startTick, region.endTick, snapIn(this.context(), tick), 0);
          if (at !== null) {
            void this.project.execute([{ type: 'splitRegion', regionId: region.id, atTick: at }], 'Split Region');
          }
        }
        return;
      case 'pencil':
        if (!region) {
          this.createMidiRegion(lane, tick);
          return;
        }
        break;
      case 'eraser':
        if (region) {
          const ids = this.selection.isSelected(region.id) ? [...this.selection.regions()] : [region.id];
          void this.project.execute([{ type: 'deleteRegions', regionIds: ids }], plural('Delete', ids.length));
        }
        return;
      case 'mute':
        if (region) {
          const ids = this.selection.isSelected(region.id) ? [...this.selection.regions()] : [region.id];
          const commands = muteCommands(ids, this.lookup());
          void this.project.execute(commands, plural('Mute', commands.length));
        }
        return;
      case 'glue':
        if (region && region.kind !== 'folder' && region.kind !== 'pattern') {
          const ids = [...new Set([...this.selection.regions(), region.id])].filter(
            id => this.span(id)?.trackId === region.trackId && this.span(id)?.kind === region.kind
          );
          if (ids.length > 1) {
            void this.project.execute([{ type: 'joinRegions', regionIds: ids }], 'Join Regions');
          }
        }
        return;
      case 'text':
        if (region && hit) {
          this.startRename(region.id, hit.index, view);
        }
        return;
      case 'solo':
        this.transport.locate(snapIn(this.context(), tick));
        this.transport.play();
        if (region) {
          this.selection.select([region.id]);
        }
        start('solo');
        return;
      case 'zoom':
        start('zoom');
        return;
      case 'marquee':
        this.selection.clear();
        start('range');
        return;
      case 'fade':
        this.startFade(start, x, lane, view, hit?.id ?? null);
        return;
      case 'pointer':
        break;
    }

    // Pointer (and Pencil over a region).
    if (row?.kind === 'take' && hit?.id === row.id) {
      this.selection.track.set(row.track.id);
      start('swipe', row.id);
      return;
    }

    if (hit && region) {
      if (region.kind === 'folder' && this.onDisclosure(region, x, y, lane, view)) {
        this.view.toggleFolder(region.id);
        return;
      }
      const audio = this.project.regions().get(region.id);
      if (audio && event.ctrlKey && event.shiftKey && (hit.zone === 'fade-in' || hit.zone === 'fade-out')) {
        this.selection.select([region.id]);
        start(hit.zone === 'fade-in' ? 'curve-in' : 'curve-out', region.id);
        return;
      }
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
    const trackId = this.lanes.trackAt(lane);
    if (trackId) {
      this.selection.selectTrack(trackId, event.shiftKey || event.metaKey);
    }
    start('marquee');
  }

  /** The Fade tool's press: a curve inside a fade, a crossfade where two regions meet, else a fade from the nearer edge. */
  private startFade(
    start: (kind: GestureKind, regionId?: string | null, extra?: Partial<Gesture>) => void,
    x: number,
    lane: number,
    view: TimelineView,
    hitId: string | null
  ): void {
    const tick = tickAtX(view, x);
    const trackId = this.lanes.rows()[lane]?.kind === 'track' ? this.lanes.trackAt(lane) : undefined;
    const onTrack = [...this.project.regions().values()].filter(r => r.trackId === trackId);
    const tolerance = BoundaryPx / view.pixelsPerTick;
    // Where one region ends and the next starts (touching or overlapping), near the pointer.
    for (const earlier of onTrack) {
      const end = this.project.regionEnd(earlier);
      const later = onTrack.find(r => {
        if (r.id === earlier.id || r.startTick <= earlier.startTick || r.startTick > end + 1) {
          return false;
        }
        // Touching: near the boundary; overlapping: anywhere over the overlap.
        return tick >= r.startTick - tolerance && tick <= Math.max(end, r.startTick) + tolerance;
      });
      if (later) {
        start('xfade', earlier.id, { other: later.id });
        return;
      }
    }
    const region = hitId ? this.project.regions().get(hitId) : undefined;
    if (!region) {
      return;
    }
    this.selection.select([region.id]);
    const rate = this.project.regionRate(region);
    const end = this.project.regionEnd(region);
    if (region.fadeIn > 0 && tick <= region.startTick + region.fadeIn / rate) {
      start('curve-in', region.id);
    } else if (region.fadeOut > 0 && tick >= end - region.fadeOut / rate) {
      start('curve-out', region.id);
    } else {
      start('fade-tool', region.id, { edge: tick - region.startTick < end - tick ? 'in' : 'out' });
    }
  }

  protected move(event: PointerEvent): void {
    this.pointerAt.set({ x: event.clientX, y: event.clientY });
    const { x, y } = this.local(event);
    const gesture = this.gesture;
    if (!gesture) {
      this.hover(x, y, event);
      return;
    }

    if (!gesture.moved && Math.hypot(x - gesture.x, y - gesture.y) < DragThreshold) {
      return;
    }

    gesture.moved = true;
    const view = this.currentView();
    const tick = tickAtX(view, x);
    const context = this.context();
    const top = this.lanesTop();
    switch (gesture.kind) {
      case 'locate':
        this.transport.locate(snapIn(context, tick));
        break;
      case 'cycle':
      case 'punch': {
        const a = snapIn(context, gesture.tick);
        const b = snapIn(context, tick);
        const draft = { enabled: true, start: Math.min(a, b), end: Math.max(a, b) };
        if (gesture.kind === 'cycle') {
          this.cycleDraft = draft;
        } else {
          this.punchDraft = draft;
        }
        break;
      }
      case 'marquee': {
        this.marqueeRect = {
          x: Math.min(gesture.x, x),
          y: Math.max(top, Math.min(gesture.y, y)),
          width: Math.abs(x - gesture.x),
          height: Math.abs(y - Math.max(top, gesture.y))
        };
        if (this.drawn) {
          const drawn = this.drawn;
          const found = itemsInRect(drawn.scene, drawn.index, view, this.marqueeRect)
            .filter(i => drawn.indexOf.get(drawn.regionIds[i] ?? '') === i)
            .map(i => drawn.regionIds[i] ?? '')
            .filter(id => !id.includes('/'));
          this.selection.select(gesture.shift ? [...gesture.base, ...found] : found);
        }
        break;
      }
      case 'swipe':
      case 'zoom': {
        const laneTop = gesture.kind === 'swipe' ? yAtLane(view, gesture.lane) : top;
        const laneHeight = gesture.kind === 'swipe' ? view.laneHeight : this.height - top;
        const a = gesture.kind === 'swipe' ? xAtTick(view, snapIn(context, gesture.tick)) : gesture.x;
        const b = gesture.kind === 'swipe' ? xAtTick(view, snapIn(context, tick)) : x;
        this.marqueeRect = { x: Math.min(a, b), y: laneTop, width: Math.abs(b - a), height: laneHeight };
        break;
      }
      case 'range': {
        const a = snapIn(context, gesture.tick);
        const b = snapIn(context, tick);
        const lane = Math.max(0, Math.floor(laneAtY(view, Math.max(top, y))));
        const last = Math.max(0, this.lanes.count() - 1);
        this.view.marquee.set({
          startTick: Math.min(a, b),
          endTick: Math.max(a, b),
          laneFrom: Math.min(gesture.lane < 0 ? 0 : gesture.lane, lane, last),
          laneTo: Math.min(Math.max(gesture.lane, lane), last)
        });
        break;
      }
      case 'solo':
        break;
      default:
        this.overrides = this.preview(gesture, tick, Math.floor(laneAtY(view, y)), y);
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
    const { x, y } = this.local(event);
    const view = this.currentView();
    const tick = tickAtX(view, x);

    switch (gesture.kind) {
      case 'cycle': {
        const draft = this.cycleDraft;
        this.cycleDraft = null;
        if (gesture.moved && draft && draft.end > draft.start) {
          this.transport.setCycleRange(draft.start, draft.end);
        } else if (!gesture.moved) {
          this.transport.toggleCycle();
        }
        return;
      }
      case 'punch': {
        const draft = this.punchDraft;
        if (gesture.moved && draft && draft.end > draft.start) {
          void this.project
            .execute(
              [{ type: 'setRecording', punchInTick: draft.start, punchOutTick: draft.end }],
              'Set Punch Locators'
            )
            .finally(() => (this.punchDraft = null));
        } else {
          this.punchDraft = null;
        }
        return;
      }
      case 'marquee':
      case 'locate':
      case 'range':
        this.marqueeRect = null;
        return;
      case 'solo':
        this.transport.stop();
        return;
      case 'zoom':
        this.marqueeRect = null;
        if (gesture.moved && Math.abs(x - gesture.x) > 4) {
          this.view.zoomTo(
            Math.min(tickAtX(view, gesture.x), tick),
            Math.max(tickAtX(view, gesture.x), tick),
            this.width
          );
        } else {
          this.view.zoom(gesture.alt || event.altKey ? 0.5 : 2, x);
        }
        return;
      case 'swipe':
        this.marqueeRect = null;
        this.finishSwipe(gesture, tick);
        return;
      default:
        break;
    }

    if (!gesture.moved) {
      // A click on one of several selected regions selects just it.
      if (!gesture.shift && gesture.regionId && !gesture.kind.startsWith('curve') && gesture.kind !== 'xfade') {
        this.selection.select([gesture.regionId]);
      }
      this.overrides = null;
      return;
    }

    const commands = this.commit(gesture, tick, Math.floor(laneAtY(view, y)), y, event.altKey);
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
    this.marqueeRect = null;
    this.cycleDraft = null;
    this.punchDraft = null;
  }

  /** Quick Swipe Comping: a drag on a take puts that range of it in the comp; a click selects the whole take. */
  private finishSwipe(gesture: Gesture, tick: number): void {
    const [folderId, takeId] = (gesture.regionId ?? '').split('/');
    const folder = folderId ? this.project.takeFolder(folderId) : undefined;
    if (!folder || !takeId) {
      return;
    }
    if (!gesture.moved) {
      void this.project.execute([{ type: 'selectTake', folderId: folder.id, takeId }], 'Select Take');
      return;
    }
    const context = this.context();
    const comp = swipeComp(folder.comp, takeId, snapIn(context, gesture.tick), snapIn(context, tick), folder);
    void this.project.execute([{ type: 'setComp', folderId: folder.id, segments: comp }], 'Quick Swipe Comping');
  }

  /** Where a drag puts the regions it moves or the region it trims, fades or curves. */
  private preview(gesture: Gesture, tick: number, lane: number, y: number): ItemOverride[] {
    const drawn = this.drawn;
    const index = gesture.regionId ? drawn?.indexOf.get(gesture.regionId) : undefined;
    if (!drawn || !gesture.regionId || index === undefined) {
      return [];
    }

    if (gesture.kind === 'body') {
      return this.moved(gesture, tick, lane).flatMap(m => {
        const i = drawn.indexOf.get(m.id) ?? -1;
        const delta = m.startTick - (drawn.scene.items.start[i] ?? 0);
        return [
          { index: i, start: m.startTick, lane: m.lane },
          ...(drawn.parts.get(m.id) ?? []).map(p => ({
            index: p,
            start: (drawn.scene.items.start[p] ?? 0) + delta,
            lane: m.lane
          }))
        ];
      });
    }

    const midi = this.project.midiRegions().get(gesture.regionId) ?? this.project.patternRegion(gesture.regionId);
    if (midi) {
      const context = this.context();
      const snapped = snapIn(context, tick);
      const t =
        gesture.kind === 'trim-start'
          ? trimMidiStart(midi, snapped, context.snap)
          : gesture.kind === 'trim-end'
            ? trimMidiEnd(midi, snapped, context.snap)
            : null;
      return t ? [{ index, start: t.startTick, length: t.lengthTicks, fileOffset: t.startTick - midi.startTick }] : [];
    }

    const region = this.project.regions().get(gesture.regionId);
    if (!region) {
      return [];
    }
    const context = this.context(region);

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
      case 'fade-tool':
        return gesture.edge === 'in'
          ? [{ index, fadeIn: fadeInTo(region, tick, context) }]
          : [{ index, fadeOut: fadeOutFrom(region, tick, context) }];
      case 'curve-in':
        return [{ index, fadeInCurve: curveFrom(region.fadeInCurve, gesture.y, y) }];
      case 'curve-out':
        return [{ index, fadeOutCurve: curveFrom(region.fadeOutCurve, gesture.y, y) }];
      default:
        return [];
    }
  }

  /** The command batch a finished drag sends. */
  private commit(
    gesture: Gesture,
    tick: number,
    lane: number,
    y: number,
    alt: boolean
  ): { commands: EditCommand[]; label: string } {
    if (gesture.kind === 'body') {
      return this.commitMove(gesture, tick, lane, alt);
    }

    if (gesture.kind === 'xfade') {
      const earlier = gesture.regionId ? this.project.regions().get(gesture.regionId) : undefined;
      const later = gesture.other ? this.project.regions().get(gesture.other) : undefined;
      if (!earlier || !later) {
        return { commands: [], label: '' };
      }
      const ticks = 2 * Math.abs(tick - gesture.tick);
      return { commands: crossfadeCommands(earlier, later, ticks, this.lookup()), label: 'Crossfade' };
    }

    const pattern = gesture.regionId ? this.project.patternRegion(gesture.regionId) : undefined;
    if (pattern) {
      if (gesture.kind !== 'trim-start' && gesture.kind !== 'trim-end') {
        return { commands: [], label: '' };
      }
      const context = this.context();
      const snapped = snapIn(context, tick);
      const t =
        gesture.kind === 'trim-start'
          ? trimMidiStart(pattern, snapped, context.snap)
          : trimMidiEnd(pattern, snapped, context.snap);
      const same = t.startTick === pattern.startTick && t.lengthTicks === pattern.lengthTicks;
      return {
        commands: same ? [] : [{ type: 'setPatternRegion', regionId: pattern.id, ...t }],
        label: 'Resize Region'
      };
    }

    const midi = gesture.regionId ? this.project.midiRegions().get(gesture.regionId) : undefined;
    if (midi) {
      if (gesture.kind !== 'trim-start' && gesture.kind !== 'trim-end') {
        return { commands: [], label: '' };
      }
      const context = this.context();
      const snapped = snapIn(context, tick);
      const t =
        gesture.kind === 'trim-start'
          ? trimMidiStart(midi, snapped, context.snap)
          : trimMidiEnd(midi, snapped, context.snap);
      const same = t.startTick === midi.startTick && t.lengthTicks === midi.lengthTicks;
      return {
        commands: same ? [] : [{ type: 'resizeMidiRegion', regionId: midi.id, ...t }],
        label: 'Resize Region'
      };
    }

    const region = gesture.regionId ? this.project.regions().get(gesture.regionId) : undefined;
    if (!region) {
      return { commands: [], label: '' };
    }
    const context = this.context(region);

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
      case 'fade-in':
      case 'fade-out':
      case 'fade-tool': {
        const fadeIn = gesture.kind === 'fade-in' || (gesture.kind === 'fade-tool' && gesture.edge === 'in');
        if (fadeIn) {
          const value = fadeInTo(region, tick, context);
          return {
            commands: value === region.fadeIn ? [] : [{ type: 'setRegion', regionId: region.id, fadeIn: value }],
            label: 'Fade In'
          };
        }
        const value = fadeOutFrom(region, tick, context);
        return {
          commands: value === region.fadeOut ? [] : [{ type: 'setRegion', regionId: region.id, fadeOut: value }],
          label: 'Fade Out'
        };
      }
      case 'curve-in':
      case 'curve-out': {
        const field = gesture.kind === 'curve-in' ? 'fadeInCurve' : 'fadeOutCurve';
        const value = curveFrom(region[field], gesture.y, y);
        return {
          commands: value === region[field] ? [] : [{ type: 'setRegion', regionId: region.id, [field]: value }],
          label: 'Fade Curve'
        };
      }
      default:
        return { commands: [], label: '' };
    }
  }

  /** A finished move: the moves (or ⌥ copies), then what the drag mode does around them. */
  private commitMove(
    gesture: Gesture,
    tick: number,
    lane: number,
    alt: boolean
  ): { commands: EditCommand[]; label: string } {
    const moved = this.moved(gesture, tick, lane);
    const spans = new Map<string, { startTick: number; trackId: string }>([
      ...this.project.regions(),
      ...this.project.midiRegions(),
      ...this.project.takeFolders(),
      ...this.project.patternRegions()
    ]);
    const tracks = this.project.tracks();
    const patterns = this.project.patternRegions();
    const fits = (id: string, trackId: string) => {
      const track = tracks.get(trackId);
      const kind = track?.kind;
      if (patterns.has(id)) {
        return kind === 'instrument' || !!track?.drumMachine;
      }
      return id.startsWith('mid_') ? kind === 'instrument' || kind === 'midi' : kind === 'audio';
    };
    const commands = moveCommands(moved, spans, l => this.lanes.trackAt(l), fits);
    if (commands.length === 0) {
      return { commands: [], label: '' };
    }

    if (alt) {
      // ⌥-drag copies: by the grabbed region's travel, onto the track it landed on when it changed tracks.
      const grabbed = commands.find(c => c.type === 'moveRegion' && c.regionId === gesture.regionId) ?? commands[0];
      if (grabbed?.type !== 'moveRegion') {
        return { commands: [], label: '' };
      }
      const from = spans.get(grabbed.regionId);
      const ids = moved.map(m => m.id);
      return {
        commands: [
          {
            type: 'copyRegions',
            regionIds: ids,
            offsetTicks: grabbed.startTick - (from?.startTick ?? 0),
            ...(grabbed.trackId && ids.length === 1 ? { trackId: grabbed.trackId } : {})
          }
        ],
        label: plural('Copy', ids.length)
      };
    }

    const mode = this.view.dragMode();
    if (mode === 'overlap') {
      return { commands, label: plural('Move', commands.length) };
    }
    // Where the moved regions now are, among the others on their tracks.
    const after: Span[] = commands.flatMap(c => {
      if (c.type !== 'moveRegion') {
        return [];
      }
      const span = this.span(c.regionId);
      if (!span) {
        return [];
      }
      return [
        {
          id: span.id,
          trackId: c.trackId ?? span.trackId,
          startTick: c.startTick,
          endTick: c.startTick + (span.endTick - span.startTick),
          audio: span.kind === 'audio'
        }
      ];
    });
    const movedIds = new Set(after.map(s => s.id));
    const tracksTouched = new Set(after.map(s => s.trackId));
    const others: Span[] = this.allSpans()
      .filter(s => !movedIds.has(s.id) && tracksTouched.has(s.trackId))
      .map(s => ({
        id: s.id,
        trackId: s.trackId,
        startTick: s.startTick,
        endTick: s.endTick,
        audio: s.kind === 'audio'
      }));
    const adjusted = adjustmentCommands(commands, applyDragMode(mode, after, others), this.lookup());
    return { commands: adjusted, label: plural('Move', commands.length) };
  }

  /** The selected regions where a move by the pointer's travel puts them. */
  private moved(gesture: Gesture, tick: number, lane: number) {
    const grabbed = gesture.regionId ? this.span(gesture.regionId) : undefined;
    const selected = [...this.selection.regions()]
      .map(id => this.span(id))
      .filter((r): r is RegionSpan => r !== undefined)
      .map(r => ({ id: r.id, startTick: r.startTick, lane: Math.max(0, this.lanes.laneOf(r.trackId)) }));
    return moveRegions(
      selected,
      grabbed ?? { startTick: 0 },
      tick - gesture.tick,
      lane - gesture.lane,
      this.lanes.count(),
      this.context()
    );
  }

  private fileFrames(region: { fileId: string; fileOffset: number; length: number }): number {
    return this.project.files().get(region.fileId)?.frames ?? region.fileOffset + region.length;
  }

  /** A region of any kind by id. */
  private span(id: string): RegionSpan | undefined {
    const pattern = this.project.patternRegion(id);
    if (pattern) {
      return {
        id,
        trackId: pattern.trackId,
        startTick: pattern.startTick,
        endTick: pattern.startTick + pattern.lengthTicks,
        kind: 'pattern'
      };
    }
    const midi = this.project.midiRegions().get(id);
    if (midi) {
      return {
        id,
        trackId: midi.trackId,
        startTick: midi.startTick,
        endTick: midi.startTick + midi.lengthTicks,
        kind: 'midi'
      };
    }
    const folder = this.project.takeFolders().get(id);
    if (folder) {
      return {
        id,
        trackId: folder.trackId,
        startTick: folder.startTick,
        endTick: folder.startTick + folder.lengthTicks,
        kind: 'folder'
      };
    }
    const audio = this.project.regions().get(id);
    return audio
      ? {
          id,
          trackId: audio.trackId,
          startTick: audio.startTick,
          endTick: this.project.regionEnd(audio),
          kind: 'audio'
        }
      : undefined;
  }

  /** Every region of every kind as a span. */
  allSpans(): RegionSpan[] {
    return [
      ...[...this.project.regions().keys()],
      ...[...this.project.midiRegions().keys()],
      ...[...this.project.takeFolders().keys()],
      ...[...this.project.patternRegions().keys()]
    ]
      .map(id => this.span(id))
      .filter((s): s is RegionSpan => s !== undefined);
  }

  /** Whether (x, y) is on a take folder's disclosure triangle: the start of its name bar. */
  private onDisclosure(region: RegionSpan, x: number, y: number, lane: number, view: TimelineView): boolean {
    const left = xAtTick(view, region.startTick);
    const top = yAtLane(view, lane);
    return x - left <= 16 && y - top <= 14;
  }

  /** What a right-click at (x, y) opens a menu on. */
  private menuFor(x: number, y: number, view: TimelineView): MenuTarget {
    if (y < this.lanesTop()) {
      return { kind: 'tools' };
    }
    const hit = this.hitAt(x, y, view);
    if (!hit) {
      const row = this.lanes.rows()[Math.floor(laneAtY(view, y))];
      return row?.kind === 'track'
        ? { kind: 'tools', trackId: row.track.id, tick: tickAtX(view, x) }
        : { kind: 'tools' };
    }
    if (hit.id.includes('/')) {
      const [folderId = '', takeId = null] = hit.id.split('/');
      return { kind: 'folder', folderId, takeId };
    }
    if (hit.id.startsWith('tkf_')) {
      this.selection.select([hit.id]);
      return { kind: 'folder', folderId: hit.id, takeId: null };
    }
    if (!this.selection.isSelected(hit.id)) {
      this.selection.select([hit.id]);
    }
    const trackId = this.span(hit.id)?.trackId;
    if (trackId) {
      this.selection.track.set(trackId);
    }
    return { kind: 'regions', ids: [...this.selection.regions()] };
  }

  /** An empty one-bar MIDI region at `tick` on lane `lane`, when that lane's track takes MIDI. */
  private createMidiRegion(lane: number, tick: number): void {
    const row = this.lanes.rows()[lane];
    const track = row?.kind === 'track' ? row.track : undefined;
    if (!track || (track.kind !== 'instrument' && track.kind !== 'midi')) {
      return;
    }

    const meter = this.project.meter();
    const bar = Math.floor(meter.barAt(Math.max(0, tick)));
    const region = newMidiRegion(tick, barTicks(meter.signatureAt(tick)));
    region.startTick = meter.barStart(bar);
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

  /** The Text tool's field over a region's name bar. */
  private startRename(id: string, index: number, view: TimelineView): void {
    const drawn = this.drawn;
    if (!drawn || id.includes('/')) {
      return;
    }
    const start = drawn.scene.items.start[index] ?? 0;
    const length = drawn.scene.items.length[index] ?? 0;
    const lane = drawn.scene.items.lane[index] ?? 0;
    const x = Math.max(0, xAtTick(view, start));
    this.renaming.set({
      id,
      name: drawn.scene.regions?.names[index] ?? '',
      x,
      y: yAtLane(view, lane) + 1,
      width: Math.max(80, Math.min(240, xAtTick(view, start + length) - x))
    });
  }

  protected commitRename(name: string): void {
    const renaming = this.renaming();
    this.renaming.set(null);
    const trimmed = name.trim();
    if (!renaming || !trimmed || trimmed === renaming.name) {
      return;
    }
    const command = renameCommand(renaming.id, trimmed, this.lookup());
    if (command) {
      void this.project.execute([command], 'Rename Region');
    }
  }

  private hover(x: number, y: number, event: PointerEvent): void {
    if (y < RULER_HEIGHT) {
      const punch = this.project.settings().recording.autopunch && y >= RULER_HEIGHT - 6;
      this.cursor.set(punch || y >= CYCLE_BAND_TOP ? 'col-resize' : 'pointer');
      return;
    }

    const tool = this.view.toolFor(event);
    if (tool === 'zoom') {
      this.cursor.set(event.altKey ? 'zoom-out' : 'zoom-in');
      return;
    }
    if (tool !== 'pointer') {
      this.cursor.set(ToolCursors[tool]);
      return;
    }

    const view = this.currentView();
    const hit = this.hitAt(x, y, view);
    if (hit?.id.includes('/')) {
      this.cursor.set('crosshair'); // Quick Swipe
      return;
    }
    this.cursor.set(hit ? Cursors[hit.zone] : 'default');
  }

  // ---- wheel, double-click, drop

  /** Scrolls or zooms (⌘: time, ⌥: lane height, ⇧: sideways); the automation lanes over the lanes pass theirs here. */
  wheel(event: WheelEvent): void {
    event.preventDefault();
    const { x } = this.local(event);
    if (event.metaKey || event.ctrlKey) {
      this.view.zoom(Math.exp(-event.deltaY * 0.004), x);
    } else if (event.altKey) {
      this.view.zoomLanes(Math.exp(-event.deltaY * 0.004));
    } else if (event.shiftKey) {
      this.view.scrollBy(event.deltaY || event.deltaX, 0, this.lanes.count());
    } else {
      this.view.scrollBy(event.deltaX, event.deltaY, this.lanes.count());
    }
  }

  /**
   * Below the last track, a double-click adds one (as in Logic); on an empty spot of an instrument or MIDI track it
   * creates an empty MIDI region there; on a take folder it opens or closes it.
   */
  protected doubleClick(event: MouseEvent): void {
    const { x, y } = this.local(event);
    if (y <= this.lanesTop() || !this.drawn || this.view.tool() !== 'pointer') {
      return;
    }

    const view = this.currentView();
    const lane = laneAtY(view, y);
    const hit = this.hitAt(x, y, view);
    if (lane >= this.lanes.count()) {
      void this.project.execute([{ type: 'addTrack', stereo: false }], 'New Track');
    } else if (hit?.id.startsWith('tkf_')) {
      this.view.toggleFolder(hit.id);
    } else if (!hit) {
      this.createMidiRegion(Math.floor(lane), tickAtX(view, x));
    }
  }

  protected dragOver(event: DragEvent): void {
    const types = event.dataTransfer?.types ?? [];
    if (
      !types.includes('Files') &&
      !types.includes(ServerFileType) &&
      !types.includes(ProjectFileType) &&
      !types.includes(LoopDragType)
    ) {
      return;
    }

    event.preventDefault();
    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = 'copy';
    }
    const { y } = this.local(event);
    const view = this.currentView();
    const top = this.lanesTop();
    const lane = Math.floor(laneAtY(view, Math.max(top, y)));
    const count = this.lanes.count();
    const laneY = yAtLane(view, Math.min(lane, count));
    this.dropTarget.set({ y: laneY, height: lane < count ? view.laneHeight : Math.max(8, this.height - laneY) });
  }

  protected drop(event: DragEvent): void {
    this.dropTarget.set(null);
    const { x, y } = this.local(event);
    const view = this.currentView();
    const lane = Math.floor(laneAtY(view, Math.max(this.lanesTop(), y)));
    const trackId = this.lanes.trackAt(lane) ?? null;
    const tick = snapIn(this.context(), tickAtX(view, x));
    const loopId = event.dataTransfer?.getData(LoopDragType);
    if (loopId) {
      // A loop from the Loop Browser: on the lane's track (below the last lane, a new track) at the bar under the
      // pointer, as Logic places loops.
      event.preventDefault();
      const meter = this.project.meter();
      const bar = meter.barStart(Math.floor(meter.barAt(Math.max(0, tickAtX(view, x))) + 1e-9));
      const name = event.dataTransfer?.getData('text/plain') ?? '';
      void this.loops.addAt(loopId, name, lane < this.lanes.count() ? trackId : null, bar);
      return;
    }
    const server = event.dataTransfer?.getData(ServerFileType);
    if (server) {
      event.preventDefault();
      const entry = JSON.parse(server) as { path: string; kind: 'audio' | 'midi' };
      void this.audioImport.importServerFile(entry.path, entry.kind, trackId, tick);
      return;
    }
    const projectFile = event.dataTransfer?.getData(ProjectFileType);
    if (projectFile) {
      event.preventDefault();
      void this.audioImport.placeProjectFile(projectFile, trackId, tick);
      return;
    }
    const files = [...(event.dataTransfer?.files ?? [])];
    if (files.length === 0) {
      return;
    }

    event.preventDefault();
    void this.audioImport.importFiles(files, trackId, tick);
  }
}

/** A fade curve after a vertical drag from `fromY` to `toY`: up makes the start faster, down slower. */
export function curveFrom(curve: number, fromY: number, toY: number): number {
  return Math.round(Math.min(1, Math.max(-1, curve + (fromY - toY) / 60)) * 100) / 100;
}

function sameRect(a: PixelRect | null | undefined, b: PixelRect | null | undefined): boolean {
  return a === b || (!!a && !!b && a.x === b.x && a.y === b.y && a.width === b.width && a.height === b.height);
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
    a.lanesTop === b.lanesTop &&
    a.playheadTick === b.playheadTick &&
    a.endTick === b.endTick &&
    sameRect(a.marquee, b.marquee) &&
    a.overrides === b.overrides &&
    a.cycle?.enabled === b.cycle?.enabled &&
    a.cycle?.start === b.cycle?.start &&
    a.cycle?.end === b.cycle?.end &&
    a.punch?.enabled === b.punch?.enabled &&
    a.punch?.start === b.punch?.start &&
    a.punch?.end === b.punch?.end
  );
}
