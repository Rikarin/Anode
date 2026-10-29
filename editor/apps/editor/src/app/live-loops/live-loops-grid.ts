import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  inject,
  input,
  signal
} from '@angular/core';
import type { Cell, Scene, Track } from '@anode/api-client';
import { AnLiveLoopsCell, type LiveLoopsCellActivation, type LiveLoopsCellMenuPoint } from '@anode/audio-ui';
import { RULER_HEIGHT } from '@anode/timeline';
import { provideIcons } from '@ng-icons/core';
import {
  matAddRound,
  matBlurOnRound,
  matExpandMoreRound,
  matFiberManualRecordRound,
  matPlayArrowRound,
  matStopRound,
  matViewModuleRound
} from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiContextMenuImports } from '@xui/context-menu';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiMenuImports } from '@xui/menu';
import { XuiTooltipImports } from '@xui/tooltip';
import { LanesStore } from '../state/lanes.store';
import { ProjectStore } from '../state/project.store';
import { TracksViewStore } from '../state/tracks-view.store';
import { TransportStore } from '../state/transport.store';
import { CellEditor } from './cell-editor';
import {
  CellDragType,
  CellPlayModes,
  acceptsDrag,
  canHold,
  cellProgress,
  gridRows,
  playsMidi,
  readDrop,
  slotLabel,
  slotState,
  type SlotState
} from './grid-model';
import { LaunchQuantizes, quantizeLabel, type Quantize } from './launcher';
import { LiveLoopsStore } from './live-loops.store';
import { RemixFx } from './remix-fx';

/** A cell's width in the grid, in CSS pixels. */
const CellWidth = 76;

/**
 * The Live Loops grid beside the Tracks area (Logic's; docs/07-phase-6.md): scenes as columns of cells, one row per
 * track level with the Tracks area's lanes (same rows, lane height and vertical scroll; take and automation rows are
 * blank), scrolling sideways when there are many scenes. The header launches scenes (and renames, inserts and deletes
 * them); a column at the rows' end holds each track's Live Loops activation and stop. A cell launches on click (a
 * playing one stops), selects with ⇧/⌘, takes loops from the Loop Browser and other cells by drag and drop (⌥
 * copies), and has a menu for its content, play mode and quantise. A bar at the foot holds the grid's quantise, stop,
 * performance recording and the Remix FX switch; the cell editor and Remix FX open under it.
 */
@Component({
  selector: 'an-live-loops-grid',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AnLiveLoopsCell,
    CellEditor,
    RemixFx,
    XuiButtonImports,
    XuiContextMenuImports,
    XuiIconImports,
    XuiInputImports,
    XuiMenuImports,
    XuiTooltipImports
  ],
  providers: [
    provideIcons({
      matAddRound,
      matBlurOnRound,
      matExpandMoreRound,
      matFiberManualRecordRound,
      matPlayArrowRound,
      matStopRound,
      matViewModuleRound
    })
  ],
  host: {
    class: 'relative flex h-full min-h-0 flex-col',
    role: 'region',
    'aria-label': 'Live Loops',
    '[attr.data-recording]': 'live.recordingPerformance() || null'
  },
  template: `
    <div class="flex min-h-0 flex-1">
      <div class="min-w-0 flex-1 overflow-x-auto overflow-y-hidden" (wheel)="wheel($event)">
        <div
          role="grid"
          aria-label="Live Loops"
          tabindex="-1"
          class="flex h-full flex-col outline-none"
          [style.min-width.px]="scenes().length * cellWidth"
          (keydown)="navigate($event)"
        >
          <div role="row" class="glass-bar relative z-10 flex shrink-0" [style.height.px]="ruler">
            @for (scene of scenes(); track scene.id; let i = $index) {
              <div
                role="columnheader"
                class="border-hairline flex shrink-0 items-center gap-1 border-e px-1"
                [style.width.px]="cellWidth"
                [attr.aria-label]="'Scene ' + (i + 1) + ': ' + scene.name"
                [xuiContextMenuTriggerFor]="sceneMenu"
                [xuiContextMenuTriggerData]="{ $implicit: scene, index: i }"
              >
                <button
                  type="button"
                  class="bezel text-foreground grid size-4.5 shrink-0 place-items-center"
                  data-testid="scene-launch"
                  [attr.aria-label]="'Launch scene ' + (i + 1)"
                  [xuiTooltip]="'Launch ' + scene.name"
                  (click)="live.launchScene(scene.id)"
                >
                  <ng-icon xui name="matPlayArrowRound" size="13px" />
                </button>
                @if (renaming() === scene.id) {
                  <input
                    #name
                    xuiInput
                    class="h-5 min-w-0 flex-1 px-1 text-[11px]"
                    [value]="scene.name"
                    [attr.aria-label]="'Name of scene ' + (i + 1)"
                    (keydown.enter)="rename(scene, name.value)"
                    (keydown.escape)="renaming.set(null)"
                    (blur)="rename(scene, name.value)"
                  />
                } @else {
                  <span
                    class="text-foreground min-w-0 flex-1 truncate text-[11px] font-semibold"
                    [attr.title]="scene.name + ' (double-click to rename)'"
                    (dblclick)="renaming.set(scene.id)"
                  >
                    {{ scene.name }}
                  </span>
                }
              </div>
            }
          </div>
          @if (globalHeight() > 0) {
            <div class="border-hairline bg-surface/85 shrink-0 border-b" [style.height.px]="globalHeight()"></div>
          }
          <div class="relative min-h-0 flex-1 overflow-hidden">
            <div [style.transform]="'translateY(' + offset() + 'px)'">
              @for (row of rows(); track row.id) {
                @if (row.kind === 'track') {
                  <div
                    role="row"
                    class="border-hairline flex border-b"
                    [style.height.px]="view.laneHeight()"
                    [attr.data-track]="row.track.id"
                  >
                    @for (scene of scenes(); track scene.id; let i = $index) {
                      @let cell = cellAt(row.track.id, scene.id);
                      <an-live-loops-cell
                        class="shrink-0"
                        data-testid="live-loops-cell"
                        [attr.data-track]="row.track.id"
                        [attr.data-scene]="scene.id"
                        [style.width.px]="cellWidth"
                        [name]="cell?.name ?? null"
                        [kind]="cell?.kind ?? null"
                        [state]="stateOf(cell)"
                        [progress]="progressOf(cell)"
                        [color]="row.track.color"
                        [label]="label(cell, row.track, i)"
                        [selected]="!!cell && live.selected().has(cell.id)"
                        [recording]="live.recordingCell() === row.track.id + '/' + scene.id"
                        [dropTarget]="dropAt() === row.track.id + '/' + scene.id"
                        [focusable]="isTabStop(row.track.id, scene.id)"
                        [xuiContextMenuTriggerFor]="cellMenu"
                        [xuiContextMenuTriggerData]="{ $implicit: { track: row.track, scene, index: i, cell } }"
                        (activate)="activate(row.track, scene, cell, $event)"
                        (menu)="openMenu(row.track.id, scene.id, $event)"
                        (focusin)="tabStop.set(row.track.id + '/' + scene.id)"
                        (dragstart)="dragStart(cell, $event)"
                        (dragend)="dragEnd()"
                        (dragover)="dragOver(row.track, scene, $event)"
                        (dragleave)="dropAt.set(null)"
                        (drop)="drop(row.track, scene, $event)"
                      />
                    }
                  </div>
                } @else {
                  <div class="border-hairline border-b" aria-hidden="true" [style.height.px]="view.laneHeight()"></div>
                }
              }
            </div>
            @if (scenes().length === 0 && rows().length > 0) {
              <p class="text-foreground-muted bg-surface/90 absolute inset-x-0 top-0 p-3 text-xs">
                No scenes yet. Add one with + in the grid's corner, then drop loops onto its cells.
              </p>
            }
          </div>
        </div>
      </div>
      <div class="border-hairline bg-surface relative w-14 shrink-0 overflow-hidden border-s">
        <div class="glass-bar relative z-10 grid place-items-center" [style.height.px]="ruler">
          <button
            type="button"
            class="bezel text-foreground grid h-4.5 w-7 place-items-center"
            aria-label="Add scene"
            data-testid="add-scene"
            xuiTooltip="Add a scene"
            (click)="live.addScene()"
          >
            <ng-icon xui name="matAddRound" size="14px" />
          </button>
        </div>
        <div class="absolute inset-x-0" [style.top.px]="ruler + globalHeight()">
          <div [style.transform]="'translateY(' + offset() + 'px)'">
            @for (row of rows(); track row.id) {
              @if (row.kind === 'track') {
                <div
                  class="border-hairline flex items-center justify-center gap-1 border-b"
                  [style.height.px]="view.laneHeight()"
                >
                  <button
                    type="button"
                    class="bezel grid size-5 place-items-center"
                    data-testid="track-activation"
                    [class.text-lit]="row.track.liveLoopsActive"
                    [class.text-foreground-muted]="!row.track.liveLoopsActive"
                    [attr.aria-label]="'Live Loops activation of ' + row.track.name"
                    [attr.aria-pressed]="row.track.liveLoopsActive ? 'true' : 'false'"
                    [xuiTooltip]="
                      row.track.liveLoopsActive
                        ? row.track.name + ' plays its cells: click for the Tracks area'
                        : row.track.name + ' plays the Tracks area: click for its cells'
                    "
                    (click)="live.setActivation([row.track.id], !row.track.liveLoopsActive)"
                  >
                    <ng-icon xui name="matViewModuleRound" size="13px" />
                  </button>
                  <button
                    type="button"
                    class="bezel text-foreground grid size-5 place-items-center"
                    data-testid="track-stop"
                    [attr.aria-label]="'Stop ' + row.track.name"
                    (click)="live.stop([row.track.id])"
                  >
                    <ng-icon xui name="matStopRound" size="13px" />
                  </button>
                </div>
              } @else {
                <div class="border-hairline border-b" [style.height.px]="view.laneHeight()"></div>
              }
            }
          </div>
        </div>
      </div>
    </div>
    <div
      class="glass-bar border-hairline flex h-9 shrink-0 items-center gap-2 border-t px-2"
      role="toolbar"
      aria-label="Live Loops controls"
    >
      <button
        type="button"
        class="bezel text-foreground flex h-6.5 items-center gap-1 px-2 text-[11px] font-semibold"
        aria-label="Live Loops quantize"
        data-testid="live-loops-quantize"
        xuiTooltip="Launch quantise: when launched cells start"
        [xuiMenuTriggerFor]="quantizeMenu"
      >
        <span class="text-foreground-muted">Q</span>{{ gridQuantize() }}
        <ng-icon xui name="matExpandMoreRound" size="14px" />
      </button>
      <button
        type="button"
        class="bezel text-foreground grid h-6.5 w-8 place-items-center"
        aria-label="Stop all cells"
        data-testid="live-loops-stop"
        xuiTooltip="Stop all cells"
        (click)="live.stop()"
      >
        <ng-icon xui name="matStopRound" size="15px" />
      </button>
      <button
        type="button"
        class="bezel text-transport-record grid h-6.5 w-8 place-items-center"
        aria-label="Record performance"
        data-testid="live-loops-record"
        [xuiTooltip]="
          live.recordingPerformance() ? 'Stop recording the performance' : 'Record a performance into the Tracks area'
        "
        [attr.aria-pressed]="live.recordingPerformance() ? 'true' : 'false'"
        (click)="live.recordPerformance(!live.recordingPerformance())"
      >
        <ng-icon xui name="matFiberManualRecordRound" size="15px" />
      </button>
      <button
        type="button"
        class="bezel text-foreground ms-auto flex h-6.5 items-center gap-1 px-2 text-[11px] font-semibold"
        aria-label="Remix FX"
        data-testid="remix-fx-toggle"
        [class.text-lit]="live.remixShown()"
        [attr.aria-pressed]="live.remixShown() ? 'true' : 'false'"
        (click)="live.remixShown.set(!live.remixShown())"
      >
        <ng-icon xui name="matBlurOnRound" size="15px" />
        Remix FX
      </button>
    </div>
    @if (live.current() || live.remixShown()) {
      <!-- The cell editor and Remix FX share the grid's foot, never more than most of its height. -->
      <div class="max-h-[65%] shrink-0 overflow-y-auto">
        @if (live.current()) {
          <an-cell-editor />
        }
        @if (live.remixShown()) {
          <an-remix-fx />
        }
      </div>
    }
    @if (live.recordingPerformance()) {
      <div class="border-transport-record pointer-events-none absolute inset-0 z-20 border-2" aria-hidden="true"></div>
    }

    <ng-template #quantizeMenu>
      <xui-menu>
        @for (q of quantizes; track q.value) {
          <button
            xuiMenuItem
            type="button"
            role="menuitemradio"
            [attr.aria-checked]="project.liveLoops().quantize === q.value"
            (click)="setQuantize(q.value)"
          >
            {{ q.label }}
          </button>
        }
      </xui-menu>
    </ng-template>

    <ng-template #sceneMenu let-scene let-index="index">
      <xui-menu>
        <button xuiMenuItem type="button" (click)="live.launchScene(scene.id)">Launch Scene</button>
        <button xuiMenuItem type="button" (click)="renaming.set(scene.id)">Rename Scene</button>
        <button xuiMenuItem type="button" (click)="live.addScene(index)">Insert Scene</button>
        <xui-menu-divider />
        <button xuiMenuItem type="button" color="error" (click)="live.removeScene(scene.id)">Delete Scene</button>
      </xui-menu>
    </ng-template>

    <ng-template #cellMenu let-slot>
      <xui-menu>
        @if (slot.cell; as cell) {
          <button xuiMenuItem type="button" (click)="live.trigger(cell)">
            {{ isRunning(cell) ? 'Stop' : 'Play' }}
          </button>
        }
        @if (canRecord(slot.track)) {
          @if (live.recordingCell() === slot.track.id + '/' + slot.scene.id) {
            <button xuiMenuItem type="button" (click)="live.recordCell(slot.track.id, slot.scene.id, false)">
              Stop Recording
            </button>
          } @else {
            <button
              xuiMenuItem
              type="button"
              [disabled]="live.recordingCell() !== null"
              (click)="live.recordCell(slot.track.id, slot.scene.id, true)"
            >
              Record into Cell
            </button>
          }
        }
        @if (!slot.cell) {
          @if (canHold(slot.track, 'pattern')) {
            <button xuiMenuItem type="button" (click)="live.addPattern(slot.track, slot.scene.id)">Add Pattern</button>
          }
          @if (canHold(slot.track, 'midi')) {
            <button xuiMenuItem type="button" (click)="live.addMidi(slot.track, slot.scene.id)">Add MIDI</button>
          }
        } @else {
          <xui-menu-divider />
          <button xuiMenuItem type="button" [xuiMenuTriggerFor]="playModeMenu">Play Mode</button>
          <button xuiMenuItem type="button" [xuiMenuTriggerFor]="cellQuantizeMenu">Quantize</button>
          <button xuiMenuItem type="button" [disabled]="scenes().length < 2" [xuiMenuTriggerFor]="copyMenu">
            Copy to Scene
          </button>
          <xui-menu-divider />
          <button xuiMenuItem type="button" color="error" (click)="deleteCell(slot.cell)">Delete</button>
        }
      </xui-menu>
      <ng-template #playModeMenu>
        <xui-menu>
          @for (mode of playModes; track mode.value) {
            <button
              xuiMenuItem
              type="button"
              role="menuitemradio"
              [attr.aria-checked]="slot.cell?.playMode === mode.value"
              (click)="live.setPlayMode(cellsFor(slot.cell), mode.value)"
            >
              {{ mode.label }}
            </button>
          }
        </xui-menu>
      </ng-template>
      <ng-template #cellQuantizeMenu>
        <xui-menu>
          <button
            xuiMenuItem
            type="button"
            role="menuitemradio"
            [attr.aria-checked]="!slot.cell?.quantize"
            (click)="live.setCellQuantize(cellsFor(slot.cell), null)"
          >
            Grid ({{ gridQuantize() }})
          </button>
          <xui-menu-divider />
          @for (q of quantizes; track q.value) {
            <button
              xuiMenuItem
              type="button"
              role="menuitemradio"
              [attr.aria-checked]="slot.cell?.quantize === q.value"
              (click)="live.setCellQuantize(cellsFor(slot.cell), q.value)"
            >
              {{ q.label }}
            </button>
          }
        </xui-menu>
      </ng-template>
      <ng-template #copyMenu>
        <xui-menu>
          @for (scene of scenes(); track scene.id; let i = $index) {
            @if (scene.id !== slot.scene.id) {
              <button xuiMenuItem type="button" (click)="live.moveCell(slot.cell, slot.track.id, scene.id, true)">
                {{ i + 1 }}: {{ scene.name }}
              </button>
            }
          }
        </xui-menu>
      </ng-template>
    </ng-template>
  `
})
export class LiveLoopsGrid {
  protected readonly project = inject(ProjectStore);
  protected readonly live = inject(LiveLoopsStore);
  protected readonly view = inject(TracksViewStore);
  private readonly lanes = inject(LanesStore);
  private readonly transport = inject(TransportStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** The global tracks' height under the ruler (the Tracks area's), so the rows stay level with the lanes. */
  readonly globalHeight = input(0);

  protected readonly ruler = RULER_HEIGHT;
  protected readonly cellWidth = CellWidth;
  protected readonly quantizes = LaunchQuantizes;
  protected readonly playModes = CellPlayModes;
  protected readonly canHold = canHold;

  protected readonly rows = computed(() => gridRows(this.lanes.rows()));
  protected readonly scenes = computed(() => this.project.liveLoops().scenes);
  protected readonly offset = computed(() => -this.view.firstLane() * this.view.laneHeight());
  protected readonly gridQuantize = computed(() => quantizeLabel(this.project.liveLoops().quantize));
  /** Cells by slot (`trackId/sceneId`). */
  private readonly slots = computed(() => {
    const map = new Map<string, Cell>();
    for (const cell of this.project.liveLoops().cells) {
      map.set(`${cell.trackId}/${cell.sceneId}`, cell);
    }
    return map;
  });

  /** The scene being renamed in the header. */
  protected readonly renaming = signal<string | null>(null);
  /** The slot a drag hovers over that it would land on (`trackId/sceneId`). */
  protected readonly dropAt = signal<string | null>(null);
  /** The cell being dragged (a drag's data is unreadable until the drop). */
  private readonly dragging = signal<Cell | null>(null);
  /** The slot in the tab order (the grid's roving tab stop). */
  protected readonly tabStop = signal<string | null>(null);
  /** The transport position (ticks), refreshed every frame while cells play, for their progress. */
  private readonly position = signal(0);

  constructor() {
    let frame = 0;
    const draw = () => {
      if (this.live.states().size) {
        this.position.set(this.transport.positionAt(performance.now()));
      }
      frame = requestAnimationFrame(draw);
    };
    if (typeof requestAnimationFrame === 'function') {
      frame = requestAnimationFrame(draw);
    }
    inject(DestroyRef).onDestroy(() => {
      if (typeof cancelAnimationFrame === 'function') {
        cancelAnimationFrame(frame);
      }
    });
  }

  protected cellAt(trackId: string, sceneId: string): Cell | undefined {
    return this.slots().get(`${trackId}/${sceneId}`);
  }

  protected stateOf(cell: Cell | undefined): SlotState {
    return slotState(cell, id => this.live.state(id));
  }

  protected isRunning(cell: Cell): boolean {
    const state = this.live.state(cell.id);
    return state === 'playing' || state === 'queued';
  }

  protected progressOf(cell: Cell | undefined): number | null {
    if (!cell) {
      return null;
    }
    const status = this.live.status(cell.id);
    return cellProgress(
      status.state,
      status.started,
      cell.lengthTicks,
      cell.playMode,
      this.position(),
      this.project.tempoMap(),
      this.project.info()?.sampleRate ?? 48_000
    );
  }

  protected label(cell: Cell | undefined, track: Track, index: number): string {
    return slotLabel(cell, track.name, index);
  }

  protected canRecord(track: Track): boolean {
    return playsMidi(track);
  }

  /** The tab stop: the slot last focused, else the first. */
  protected isTabStop(trackId: string, sceneId: string): boolean {
    const stop = this.tabStop();
    if (stop !== null && this.hasSlot(stop)) {
      return stop === `${trackId}/${sceneId}`;
    }
    const first = this.rows().find(r => r.kind === 'track');
    return first?.kind === 'track' && first.track.id === trackId && this.scenes()[0]?.id === sceneId;
  }

  private hasSlot(key: string): boolean {
    const [trackId, sceneId] = key.split('/');
    return this.rows().some(r => r.kind === 'track' && r.id === trackId) && this.scenes().some(s => s.id === sceneId);
  }

  /** The selected cells a menu command applies to: the selection when the cell is in it, else the cell. */
  protected cellsFor(cell: Cell): Cell[] {
    const selected = this.live.selected();
    if (!selected.has(cell.id)) {
      return [cell];
    }
    return this.project.liveLoops().cells.filter(c => selected.has(c.id));
  }

  protected activate(track: Track, scene: Scene, cell: Cell | undefined, event: LiveLoopsCellActivation): void {
    this.tabStop.set(`${track.id}/${scene.id}`);
    if (!cell) {
      this.live.clearSelection();
    } else if (event.extend) {
      this.live.select(cell.id, true);
    } else {
      void this.live.trigger(cell);
    }
  }

  protected deleteCell(cell: Cell): void {
    void this.live.deleteCells(this.cellsFor(cell).map(c => c.id));
  }

  protected setQuantize(quantize: Quantize): void {
    void this.live.setQuantize(quantize);
  }

  protected rename(scene: Scene, name: string): void {
    if (this.renaming() !== scene.id) {
      return;
    }
    this.renaming.set(null);
    const trimmed = name.trim();
    if (trimmed && trimmed !== scene.name) {
      void this.live.renameScene(scene.id, trimmed);
    }
  }

  /** Opens a cell's context menu from the keyboard, under the cell. */
  protected openMenu(trackId: string, sceneId: string, at: LiveLoopsCellMenuPoint): void {
    this.slotElement(trackId, sceneId)?.dispatchEvent(
      new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: at.x, clientY: at.y })
    );
  }

  private slotElement(trackId: string, sceneId: string): HTMLElement | null {
    return this.host.nativeElement.querySelector<HTMLElement>(
      `[data-testid="live-loops-cell"][data-track="${CSS.escape(trackId)}"][data-scene="${CSS.escape(sceneId)}"]`
    );
  }

  /** Arrow keys move between cells (rows are tracks, columns scenes); Home and End go to a row's ends. */
  protected navigate(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    const slot = target.closest<HTMLElement>('[data-testid="live-loops-cell"]');
    if (!slot || target.tagName === 'INPUT') {
      return;
    }
    const tracks = this.rows().flatMap(r => (r.kind === 'track' ? [r.track.id] : []));
    const scenes = this.scenes().map(s => s.id);
    let row = tracks.indexOf(slot.dataset['track'] ?? '');
    let column = scenes.indexOf(slot.dataset['scene'] ?? '');
    const moves: Record<string, () => void> = {
      ArrowUp: () => (row = Math.max(0, row - 1)),
      ArrowDown: () => (row = Math.min(tracks.length - 1, row + 1)),
      ArrowLeft: () => (column = Math.max(0, column - 1)),
      ArrowRight: () => (column = Math.min(scenes.length - 1, column + 1)),
      Home: () => (column = 0),
      End: () => (column = scenes.length - 1)
    };
    const move = moves[event.key];
    if (!move || row < 0 || column < 0) {
      return;
    }
    event.preventDefault();
    move();
    const trackId = tracks[row] ?? '';
    const sceneId = scenes[column] ?? '';
    this.tabStop.set(`${trackId}/${sceneId}`);
    this.slotElement(trackId, sceneId)?.querySelector('button')?.focus();
  }

  protected wheel(event: WheelEvent): void {
    // Up and down scroll the rows with the Tracks area; sideways scrolls the scenes natively.
    if (Math.abs(event.deltaY) > Math.abs(event.deltaX) && !event.shiftKey) {
      event.preventDefault();
      this.view.scrollBy(0, event.deltaY, this.lanes.count());
    }
  }

  // ---- drag and drop

  protected dragStart(cell: Cell | undefined, event: DragEvent): void {
    if (!cell || !event.dataTransfer) {
      event.preventDefault();
      return;
    }
    this.dragging.set(cell);
    event.dataTransfer.setData(CellDragType, cell.id);
    event.dataTransfer.effectAllowed = 'copyMove';
  }

  protected dragEnd(): void {
    this.dragging.set(null);
    this.dropAt.set(null);
  }

  protected dragOver(track: Track, scene: Scene, event: DragEvent): void {
    const data = event.dataTransfer;
    if (!data || !acceptsDrag([...data.types])) {
      return;
    }
    const dragged = this.dragging();
    if (dragged && !canHold(track, dragged.kind)) {
      return;
    }
    event.preventDefault();
    data.dropEffect = dragged ? (event.altKey ? 'copy' : 'move') : 'copy';
    this.dropAt.set(`${track.id}/${scene.id}`);
  }

  protected drop(track: Track, scene: Scene, event: DragEvent): void {
    this.dropAt.set(null);
    const drop = event.dataTransfer ? readDrop(event.dataTransfer) : null;
    if (!drop) {
      return;
    }
    event.preventDefault();
    if (drop.kind === 'loop') {
      void this.live.dropLoop(drop.loopId, track.id, scene.id);
      return;
    }
    const cell = this.project.liveLoops().cells.find(c => c.id === drop.cellId);
    if (cell) {
      void this.live.moveCell(cell, track.id, scene.id, event.altKey);
    }
    this.dragging.set(null);
  }
}
