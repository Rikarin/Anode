import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  type ElementRef,
  inject,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import type { AudioEditKind, AudioEditOperation, AudioRegion, LoopSearchResult } from '@anode/api-client';
import { AnFadeCurve } from '@anode/audio-ui';
import { provideIcons } from '@ng-icons/core';
import {
  matFitScreenRound,
  matPlayArrowRound,
  matRepeatRound,
  matStopRound,
  matUndoRound,
  matZoomInRound,
  matZoomOutRound
} from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiMenuImports } from '@xui/menu';
import { XuiNonIdealStateImports } from '@xui/non-ideal-state';
import { XuiTooltipImports } from '@xui/tooltip';
import { editedRegion } from '../audio-editor/editor-geometry';
import { elementSize } from '../audio-editor/element-size';
import { ANODE_API } from '../core/api';
import { Notify } from '../core/notify';
import { isActive, JobsStore } from '../state/jobs.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { ThemeStore } from '../state/theme.store';
import { UiStore } from '../state/ui.store';
import { nearestZeroCrossing } from './audio-edit-math';
import {
  bucketFor,
  clampView,
  extendRange,
  type FileView,
  fitView,
  formatTime,
  frameAtX,
  type FrameRange,
  rangeOf,
  rulerTicks,
  tilesFor,
  viewOfRange,
  visibleRange,
  xAtFrame,
  zoomView
} from './audio-file-geometry';
import { SampleTiles } from './sample-tiles';
import { drawWaveform, RulerHeight, waveformColors } from './waveform-draw';

/** How long the view rests before the visible range is fetched (ms). */
export const FetchDebounceMs = 40;
/** Frames searched either way of a selection edge for a zero crossing. */
const ZeroCrossingRadius = 2048;
/** The width assumed before the waveform is measured (and in tests without layout). */
const UnmeasuredWidth = 800;

/** An entry of the Functions menu: what it asks for before it runs. */
interface Operation {
  kind: AudioEditKind;
  label: string;
  /** A number to enter first (dB), with its default and limits. */
  value?: { label: string; unit: string; default: number; min: number; max: number; step: number };
  curve?: boolean;
  rate?: boolean;
  /** Needs a selection (else it runs on the whole file). */
  needsSelection?: boolean;
}

export const Operations: readonly Operation[] = [
  {
    kind: 'normalize',
    label: 'Normalize…',
    value: { label: 'Peak', unit: 'dBFS', default: -1, min: -60, max: 0, step: 0.1 }
  },
  {
    kind: 'gain',
    label: 'Change Gain…',
    value: { label: 'Gain', unit: 'dB', default: 3, min: -96, max: 24, step: 0.1 }
  },
  { kind: 'fadeIn', label: 'Fade In…', curve: true },
  { kind: 'fadeOut', label: 'Fade Out…', curve: true },
  { kind: 'reverse', label: 'Reverse' },
  { kind: 'invert', label: 'Invert' },
  { kind: 'silence', label: 'Silence' },
  { kind: 'removeDc', label: 'Remove DC Offset' },
  { kind: 'trim', label: 'Trim', needsSelection: true },
  { kind: 'convertRate', label: 'Sample Rate Conversion…', rate: true }
];

/** Keeps a pointer's events on the element it went down on (where pointer capture exists). */
function capture(event: PointerEvent): void {
  const target = event.currentTarget as Partial<Pick<HTMLElement, 'setPointerCapture'>> | null;
  target?.setPointerCapture?.(event.pointerId);
}

const Rates = [22_050, 32_000, 44_100, 48_000, 88_200, 96_000, 176_400, 192_000];

/**
 * Logic's Audio File Editor in the Editors pane: the file of the selected audio region (or `UiStore.audioFileId`)
 * down to single samples, one lane per channel under a ruler in time or samples. Drag to select (⇧ extends), type
 * exact frames, snap the edges to zero crossings; the Functions menu runs destructive operations as `audioEdit` jobs
 * (each one undo step, ⌘Z or the Undo button); loop search marks the best loops; Make Groove Template extracts a
 * groove from the region; Play Selection previews the range (looped on request).
 */
@Component({
  selector: 'an-audio-file-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AnFadeCurve,
    XuiButtonImports,
    XuiIconImports,
    XuiInputImports,
    XuiMenuImports,
    XuiNonIdealStateImports,
    XuiTooltipImports
  ],
  providers: [
    provideIcons({
      matFitScreenRound,
      matPlayArrowRound,
      matRepeatRound,
      matStopRound,
      matUndoRound,
      matZoomInRound,
      matZoomOutRound
    })
  ],
  host: {
    class: 'flex h-full min-h-0 flex-col outline-none',
    role: 'region',
    'aria-label': 'Audio File Editor',
    'data-testid': 'audio-file-editor'
  },
  template: `
    <div
      class="glass-bar flex h-9 shrink-0 items-center gap-2 overflow-x-auto px-2"
      role="toolbar"
      aria-label="Audio File Editor tools"
    >
      <button
        xuiButton
        type="button"
        size="sm"
        variant="ghost"
        aria-label="Undo"
        data-testid="afe-undo"
        xuiTooltip="Undo (⌘Z)"
        [attr.disabled]="busy() || null"
        (click)="undo()"
      >
        <ng-icon xui name="matUndoRound" size="sm" />
      </button>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="outline"
        data-testid="afe-functions"
        [disabled]="!file() || busy()"
        [xuiMenuTriggerFor]="functionsMenu"
      >
        Functions
      </button>
      <ng-template #functionsMenu>
        <xui-menu class="min-w-52" aria-label="Audio file functions">
          @for (op of operations; track op.kind) {
            <button
              xuiMenuItem
              type="button"
              [attr.data-testid]="'afe-op-' + op.kind"
              [disabled]="!!op.needsSelection && !selection()"
              (click)="choose(op)"
            >
              {{ op.label }}
            </button>
          }
        </xui-menu>
      </ng-template>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="outline"
        data-testid="afe-loop-search"
        xuiTooltip="Find seamless loops in the selection (or the file)"
        [attr.disabled]="!file() || searching() || null"
        (click)="searchLoops()"
      >
        Loop Search
      </button>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="outline"
        data-testid="afe-make-groove"
        xuiTooltip="Make a groove template from the region's transients"
        [attr.aria-expanded]="grooveOpen()"
        [attr.disabled]="!region() || null"
        (click)="grooveOpen.set(!grooveOpen())"
      >
        Make Groove Template
      </button>
      <div class="bg-hairline h-5 w-px shrink-0"></div>
      <div class="bezel-group flex h-6.5 shrink-0 items-stretch" role="group" aria-label="Preview">
        <button
          type="button"
          class="text-foreground flex items-center px-1.5 disabled:opacity-35"
          aria-label="Play selection"
          data-testid="afe-play"
          [class.text-transport-play]="previewing()"
          [disabled]="!file()"
          (click)="play()"
        >
          <ng-icon xui name="matPlayArrowRound" size="sm" />
        </button>
        <button
          type="button"
          class="text-foreground flex items-center px-1.5 disabled:opacity-35"
          aria-label="Loop preview"
          data-testid="afe-preview-loop"
          [class.text-transport-cycle]="loopPreview()"
          [attr.aria-pressed]="loopPreview()"
          (click)="loopPreview.set(!loopPreview())"
        >
          <ng-icon xui name="matRepeatRound" size="sm" />
        </button>
        <button
          type="button"
          class="text-foreground flex items-center px-1.5 disabled:opacity-35"
          aria-label="Stop preview"
          data-testid="afe-stop"
          [disabled]="!file()"
          (click)="stop()"
        >
          <ng-icon xui name="matStopRound" size="sm" />
        </button>
      </div>
      <button
        type="button"
        class="bezel h-6.5 shrink-0 px-2 text-[11px] font-semibold"
        data-testid="afe-snap-zero"
        xuiTooltip="Snap the selection's edges to zero crossings"
        [class.text-lit]="snapZero()"
        [class.text-foreground]="!snapZero()"
        [attr.aria-pressed]="snapZero()"
        (click)="toggleSnap()"
      >
        Snap to Zero Crossings
      </button>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="ghost"
        data-testid="afe-select-all"
        [disabled]="!file()"
        (click)="selectAll()"
      >
        Select All
      </button>
      <span class="text-foreground-muted min-w-0 truncate text-xs" data-testid="afe-title">{{ title() }}</span>
      <div class="ms-auto flex shrink-0 items-center gap-1">
        <button
          type="button"
          class="bezel h-6.5 px-2 text-[11px] font-semibold"
          data-testid="afe-ruler-unit"
          xuiTooltip="Show the ruler in samples"
          [class.text-lit]="unit() === 'samples'"
          [class.text-foreground]="unit() !== 'samples'"
          [attr.aria-pressed]="unit() === 'samples'"
          (click)="unit.set(unit() === 'samples' ? 'time' : 'samples')"
        >
          Samples
        </button>
        <button
          xuiButton
          type="button"
          size="sm"
          variant="ghost"
          aria-label="Zoom to selection"
          data-testid="afe-zoom-selection"
          [disabled]="!selection()"
          (click)="zoomToSelection()"
        >
          <ng-icon xui name="matFitScreenRound" size="sm" />
        </button>
        <button
          xuiButton
          type="button"
          size="sm"
          variant="ghost"
          data-testid="afe-zoom-fit"
          [disabled]="!file()"
          (click)="view.set(null)"
        >
          Whole File
        </button>
        <button
          xuiButton
          type="button"
          size="sm"
          variant="ghost"
          aria-label="Zoom out"
          data-testid="afe-zoom-out"
          (click)="zoomBy(1 / 2)"
        >
          <ng-icon xui name="matZoomOutRound" size="sm" />
        </button>
        <button
          xuiButton
          type="button"
          size="sm"
          variant="ghost"
          aria-label="Zoom in"
          data-testid="afe-zoom-in"
          (click)="zoomBy(2)"
        >
          <ng-icon xui name="matZoomInRound" size="sm" />
        </button>
      </div>
    </div>

    @if (file(); as f) {
      <div
        class="border-hairline bg-surface flex h-8 shrink-0 items-center gap-3 overflow-x-auto border-b px-2 text-xs"
        role="group"
        aria-label="Selection"
      >
        <label class="text-foreground-muted flex items-center gap-1.5">
          Start
          <input
            #startInput
            xuiInput
            type="number"
            min="0"
            step="1"
            class="readout h-6 w-24 px-1 text-xs"
            aria-label="Selection start (samples)"
            data-testid="afe-start"
            [max]="f.frames"
            [value]="selection()?.start ?? ''"
            (change)="setEdge('start', startInput.valueAsNumber)"
          />
        </label>
        <label class="text-foreground-muted flex items-center gap-1.5">
          End
          <input
            #endInput
            xuiInput
            type="number"
            min="0"
            step="1"
            class="readout h-6 w-24 px-1 text-xs"
            aria-label="Selection end (samples)"
            data-testid="afe-end"
            [max]="f.frames"
            [value]="selection()?.end ?? ''"
            (change)="setEdge('end', endInput.valueAsNumber)"
          />
        </label>
        <span
          class="text-foreground readout whitespace-nowrap"
          data-testid="afe-selection-readout"
          aria-live="polite"
          >{{ selectionText() }}</span
        >
        <span class="text-foreground-muted readout ms-auto whitespace-nowrap" data-testid="afe-file-readout">{{
          fileText()
        }}</span>
      </div>

      @if (pending(); as op) {
        <div
          class="border-hairline bg-surface-raised flex shrink-0 items-center gap-3 border-b px-3 py-1.5 text-xs"
          role="group"
          data-testid="afe-op-panel"
          [attr.aria-label]="opTitle(op)"
        >
          <span class="text-foreground text-[11px] font-bold">{{ opTitle(op) }}</span>
          @if (op.value; as v) {
            <label class="text-foreground-muted flex items-center gap-1.5">
              {{ v.label }}
              <input
                #valueInput
                xuiInput
                type="number"
                class="readout h-6 w-20 px-1 text-xs"
                data-testid="afe-op-value"
                [attr.aria-label]="v.label + ' (' + v.unit + ')'"
                [min]="v.min"
                [max]="v.max"
                [step]="v.step"
                [value]="opValue()"
                (change)="opValue.set(valueInput.valueAsNumber)"
              />
              {{ v.unit }}
            </label>
          }
          @if (op.curve) {
            <an-fade-curve
              data-testid="afe-op-curve"
              label="Curve"
              [out]="op.kind === 'fadeOut'"
              [value]="opCurve()"
              (valueChange)="opCurve.set($event)"
            />
          }
          @if (op.rate) {
            <label class="text-foreground-muted flex items-center gap-1.5">
              Target rate
              <select
                #rateSelect
                class="border-border bg-surface-inset text-foreground readout h-6 rounded-[5px] border px-1 text-xs"
                aria-label="Target sample rate"
                data-testid="afe-op-rate"
                (change)="opRate.set(+rateSelect.value)"
              >
                @for (rate of rates; track rate) {
                  <option [value]="rate" [selected]="rate === opRate()">{{ rate }} Hz</option>
                }
              </select>
            </label>
          }
          <span class="text-foreground-muted">{{ opScope(op) }}</span>
          <div class="ms-auto flex items-center gap-2">
            <button
              xuiButton
              type="button"
              size="sm"
              variant="ghost"
              data-testid="afe-op-cancel"
              (click)="pending.set(null)"
            >
              Cancel
            </button>
            <button
              xuiButton
              type="button"
              size="sm"
              data-testid="afe-op-apply"
              [disabled]="busy() || !valid(op)"
              (click)="apply(op)"
            >
              Apply
            </button>
          </div>
        </div>
      }

      @if (grooveOpen()) {
        <div
          class="border-hairline bg-surface-raised flex shrink-0 items-center gap-3 border-b px-3 py-1.5 text-xs"
          role="group"
          aria-label="Make Groove Template"
          data-testid="afe-groove-panel"
        >
          <span class="text-foreground text-[11px] font-bold">Make Groove Template</span>
          <label class="text-foreground-muted flex items-center gap-1.5">
            Name
            <input
              #grooveNameInput
              xuiInput
              class="h-6 w-40 px-1 text-xs"
              aria-label="Groove name"
              data-testid="afe-groove-name"
              [value]="grooveName()"
              (input)="grooveName.set(grooveNameInput.value)"
            />
          </label>
          <label class="text-foreground-muted flex items-center gap-1.5">
            Bars
            <input
              #grooveBarsInput
              xuiInput
              type="number"
              min="1"
              max="4"
              step="1"
              class="readout h-6 w-14 px-1 text-xs"
              aria-label="Groove length (bars)"
              data-testid="afe-groove-bars"
              [value]="grooveBars()"
              (change)="grooveBars.set(grooveBarsInput.valueAsNumber)"
            />
          </label>
          <div class="ms-auto flex items-center gap-2">
            <button xuiButton type="button" size="sm" variant="ghost" (click)="grooveOpen.set(false)">Cancel</button>
            <button xuiButton type="button" size="sm" data-testid="afe-groove-make" (click)="makeGroove()">Make</button>
          </div>
        </div>
      }

      @if (job(); as j) {
        @if (busy() || j.state === 'failed') {
          <div
            class="border-hairline flex shrink-0 items-center gap-3 border-b px-3 py-1.5 text-xs"
            data-testid="afe-progress"
          >
            <span class="text-foreground-muted">{{ jobLabel() }}</span>
            <div
              class="well h-2 flex-1 overflow-hidden rounded-full"
              role="progressbar"
              aria-label="Audio file edit progress"
              aria-valuemin="0"
              aria-valuemax="100"
              [attr.aria-valuenow]="percent()"
            >
              <div class="bg-primary h-full transition-[width] duration-200" [style.width.%]="percent()"></div>
            </div>
            <span class="readout text-foreground w-14 text-end">{{
              j.state === 'failed' ? 'Failed' : percent() + ' %'
            }}</span>
          </div>
        }
      }

      <div class="flex min-h-0 flex-1">
        <div class="flex min-w-0 flex-1 flex-col">
          <div
            #viewport
            class="focus-visible:outline-focus relative min-h-0 flex-1 cursor-text touch-none overflow-hidden outline-none focus-visible:outline-2 focus-visible:-outline-offset-2"
            tabindex="0"
            role="application"
            aria-roledescription="waveform"
            data-testid="afe-waveform"
            [id]="waveformId"
            [attr.aria-label]="waveformLabel()"
            [attr.data-frames-per-pixel]="shownView().framesPerPixel"
            [attr.data-start-frame]="shownView().start"
            [attr.data-bucket]="bucket()"
            [attr.data-version]="f.version"
            (pointerdown)="down($event)"
            (pointermove)="move($event)"
            (pointerup)="up($event)"
            (pointercancel)="up($event)"
            (wheel)="wheel($event)"
            (keydown)="key($event)"
          >
            <canvas #canvas class="pointer-events-none absolute inset-0 size-full" aria-hidden="true"></canvas>
            @if (selectionBox(); as box) {
              <div
                class="bg-primary/20 border-primary pointer-events-none absolute bottom-0 border-x"
                data-testid="afe-selection"
                [style.top.px]="rulerHeight"
                [style.left.px]="box.left"
                [style.width.px]="box.width"
              ></div>
            }
            @for (marker of markers(); track $index) {
              <div
                class="pointer-events-none absolute bottom-0 border-x border-dashed"
                [class.border-transport-cycle]="marker.active"
                [class.border-foreground-muted]="!marker.active"
                data-testid="afe-loop-marker"
                aria-hidden="true"
                [attr.data-start-frame]="marker.loop.startFrame"
                [attr.data-end-frame]="marker.loop.endFrame"
                [style.top.px]="rulerHeight"
                [style.left.px]="marker.left"
                [style.width.px]="marker.width"
              >
                @if (marker.active) {
                  <div class="bg-transport-cycle absolute inset-0 opacity-10"></div>
                }
                <span
                  class="bg-surface-raised text-foreground readout absolute top-0 right-0 rounded-bl-[4px] px-1 text-[10px] font-semibold"
                  >{{ marker.number }}</span
                >
              </div>
            }
          </div>
          <div
            #scrollbar
            class="well relative mx-2 my-1 h-2.5 shrink-0 cursor-pointer touch-none rounded-full outline-none focus-visible:outline-2 focus-visible:outline-(--color-focus)"
            role="scrollbar"
            tabindex="0"
            aria-label="Scroll the waveform"
            aria-orientation="horizontal"
            aria-valuemin="0"
            aria-valuemax="100"
            data-testid="afe-scrollbar"
            [attr.aria-controls]="waveformId"
            [attr.aria-valuenow]="scrollPercent()"
            (pointerdown)="scrollDown($event)"
            (pointermove)="scrollMove($event)"
            (pointerup)="scrollUp($event)"
            (pointercancel)="scrollUp($event)"
            (keydown)="scrollKey($event)"
          >
            <div
              class="bg-foreground-muted/50 absolute inset-y-0 rounded-full"
              [style.left.%]="thumb().left"
              [style.width.%]="thumb().width"
            ></div>
          </div>
        </div>
        @if (loops().length > 0) {
          <div class="border-hairline bg-surface flex w-56 shrink-0 flex-col border-s">
            <div class="border-hairline bg-surface-raised border-y px-3 py-1 text-[11px] font-bold">Loops</div>
            <ol class="min-h-0 flex-1 overflow-y-auto" aria-label="Loops found" data-testid="afe-loops">
              @for (loop of loops(); track $index; let i = $index) {
                <li>
                  <button
                    type="button"
                    class="hover:bg-surface-raised flex h-7 w-full items-center gap-2 px-3 text-start text-xs"
                    data-testid="afe-loop-result"
                    [class.bg-muted]="activeLoop() === i"
                    [attr.aria-pressed]="activeLoop() === i"
                    [attr.aria-label]="loopLabel(loop, i)"
                    (click)="selectLoop(i)"
                  >
                    <span class="text-foreground readout w-4 font-semibold">{{ i + 1 }}</span>
                    <span class="text-foreground readout flex-1 truncate">{{ loopLength(loop) }}</span>
                    <span class="text-foreground-muted readout">{{ loop.score.toFixed(3) }}</span>
                  </button>
                </li>
              }
            </ol>
          </div>
        }
      </div>
    } @else {
      <xui-non-ideal-state
        class="min-h-0 flex-1"
        title="No audio file"
        description="Select an audio region to edit its file at the sample level."
      />
    }
  `
})
export class AudioFileEditor {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);
  private readonly project = inject(ProjectStore);
  private readonly selectionStore = inject(SelectionStore);
  private readonly ui = inject(UiStore);
  private readonly jobs = inject(JobsStore);
  private readonly theme = inject(ThemeStore);
  private readonly viewport = viewChild<ElementRef<HTMLElement>>('viewport');
  private readonly canvas = viewChild<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly scrollbar = viewChild<ElementRef<HTMLElement>>('scrollbar');
  private readonly size = elementSize(() => this.viewport());

  protected readonly operations = Operations;
  protected readonly rates = Rates;
  protected readonly rulerHeight = RulerHeight;
  protected readonly waveformId = `an-afe-waveform-${Math.random().toString(36).slice(2, 8)}`;

  /** The selected audio region (or the selected track's first). */
  private readonly selectedRegion = computed(() =>
    editedRegion(this.project.regions(), this.selectionStore.regions(), this.selectionStore.track())
  );
  readonly fileId = computed(() => this.ui.audioFileId() ?? this.selectedRegion()?.fileId ?? null);
  readonly file = computed(() => {
    const id = this.fileId();
    return id ? (this.project.files().get(id) ?? null) : null;
  });
  /** The region whose groove Make Groove Template extracts: the selected one, else the first that plays the file. */
  readonly region = computed<AudioRegion | null>(() => {
    const id = this.fileId();
    const selected = this.selectedRegion();
    if (!id) {
      return null;
    }
    if (selected?.fileId === id) {
      return selected;
    }
    return this.project.regionList().find(r => r.fileId === id) ?? null;
  });
  protected readonly title = computed(() => {
    const file = this.file();
    return file ? `${file.name}${file.version > 0 ? ` (version ${file.version})` : ''}` : '';
  });
  private readonly total = computed(() => this.file()?.frames ?? 0);
  private readonly width = computed(() => this.size().width || UnmeasuredWidth);
  private readonly height = computed(() => this.size().height || 240);

  /** The view as set (null: the whole file). */
  readonly view = signal<FileView | null>(null);
  /** The view shown: kept inside the file and the zoom limits. */
  readonly shownView = computed(() => {
    const total = Math.max(1, this.total());
    const view = this.view();
    return view ? clampView(view, total, this.width()) : fitView(total, this.width());
  });
  readonly bucket = computed(() => bucketFor(this.shownView().framesPerPixel));
  readonly selection = signal<FrameRange | null>(null);
  readonly unit = signal<'time' | 'samples'>('time');
  readonly snapZero = signal(false);
  readonly loops = signal<readonly LoopSearchResult[]>([]);
  readonly activeLoop = signal<number | null>(null);
  protected readonly searching = signal(false);
  protected readonly previewing = signal(false);
  readonly loopPreview = signal(false);

  /** The operation whose settings show, before Apply. */
  readonly pending = signal<Operation | null>(null);
  protected readonly opValue = signal(0);
  protected readonly opCurve = signal(0);
  protected readonly opRate = signal(44_100);
  readonly grooveOpen = signal(false);
  protected readonly grooveName = signal('');
  protected readonly grooveBars = signal(1);

  private readonly jobId = signal<string | null>(null);
  readonly job = computed(() => {
    const id = this.jobId();
    return id ? (this.jobs.jobs().get(id) ?? null) : null;
  });
  readonly busy = computed(() => isActive(this.job()));
  protected readonly percent = computed(() => Math.round((this.job()?.progress ?? 0) * 100));
  private readonly jobKind = signal<AudioEditKind | null>(null);
  protected readonly jobLabel = computed(() => {
    const kind = this.jobKind();
    const label = Operations.find(o => o.kind === kind)?.label.replace('…', '') ?? 'Edit';
    return `${label}…`;
  });

  readonly tiles = new SampleTiles((id, start, frames, bucket) => this.api.fileSamples(id, start, frames, bucket));
  private fetchTimer: ReturnType<typeof setTimeout> | null = null;
  private drag: { anchor: number; pointer: number } | null = null;
  private scrolling: number | null = null;

  protected readonly selectionBox = computed(() => {
    const range = this.selection();
    if (!range) {
      return null;
    }
    const view = this.shownView();
    const left = xAtFrame(range.start, view);
    const right = xAtFrame(range.end, view);
    const width = this.width();
    if (right < 0 || left > width) {
      return null;
    }
    const l = Math.max(-1, left);
    return { left: l, width: Math.max(1, Math.min(width + 1, right) - l) };
  });

  protected readonly markers = computed(() => {
    const view = this.shownView();
    const active = this.activeLoop();
    return this.loops().map((loop, i) => {
      const left = xAtFrame(loop.startFrame, view);
      return {
        loop,
        number: i + 1,
        active: active === i,
        left,
        width: Math.max(1, xAtFrame(loop.endFrame, view) - left)
      };
    });
  });

  protected readonly selectionText = computed(() => {
    const range = this.selection();
    const rate = this.file()?.sampleRate ?? 48_000;
    if (!range) {
      return 'No selection';
    }
    const length = range.end - range.start;
    return `${formatTime(range.start, rate, 1)} – ${formatTime(range.end, rate, 1)} · length ${length} samples (${formatTime(length, rate, 1)})`;
  });

  protected readonly fileText = computed(() => {
    const file = this.file();
    if (!file) {
      return '';
    }
    return `${file.channels === 1 ? 'Mono' : file.channels === 2 ? 'Stereo' : `${file.channels} ch`} · ${file.sampleRate} Hz · ${file.frames} samples (${formatTime(file.frames, file.sampleRate)})`;
  });

  protected readonly waveformLabel = computed(() => {
    const file = this.file();
    const view = this.shownView();
    if (!file) {
      return 'Waveform';
    }
    const shown = visibleRange(view, this.width(), file.frames);
    return `Waveform of ${file.name}: samples ${shown.start} to ${shown.end} of ${file.frames}. Drag to select, shift-click to extend, arrows scroll, plus and minus zoom.`;
  });

  protected readonly thumb = computed(() => {
    const total = Math.max(1, this.total());
    const view = this.shownView();
    const visible = Math.min(total, view.framesPerPixel * this.width());
    return { left: (view.start / total) * 100, width: Math.max(2, (visible / total) * 100) };
  });
  protected readonly scrollPercent = computed(() => {
    const total = Math.max(1, this.total());
    const view = this.shownView();
    const visible = Math.min(total, view.framesPerPixel * this.width());
    return total > visible ? Math.round((view.start / (total - visible)) * 100) : 0;
  });

  constructor() {
    // A different file starts from the whole file, without a selection or loops.
    effect(() => {
      this.fileId();
      untracked(() => {
        this.view.set(null);
        this.selection.set(null);
        this.loops.set([]);
        this.activeLoop.set(null);
        this.pending.set(null);
      });
    });
    // A new version (an operation, its undo): drop loops found on the old one; a new length drops the selection.
    let last: { id: string; version: number; frames: number } | null = null;
    effect(() => {
      const file = this.file();
      untracked(() => {
        if (file && last?.id === file.id && (last.version !== file.version || last.frames !== file.frames)) {
          this.loops.set([]);
          this.activeLoop.set(null);
          if (last.frames !== file.frames) {
            this.selection.set(null);
          }
        }
        last = file ? { id: file.id, version: file.version, frames: file.frames } : null;
      });
    });
    // Fetch the visible range at the zoom's bucket, once the view rests.
    effect(() => {
      const file = this.file();
      if (!file) {
        return;
      }
      this.tiles.setSource(file.id, file.version, file.frames);
      const bucket = this.bucket();
      const range = visibleRange(this.shownView(), this.width(), file.frames);
      const tiles = tilesFor(range, bucket);
      if (this.fetchTimer) {
        clearTimeout(this.fetchTimer);
      }
      this.fetchTimer = setTimeout(() => {
        this.fetchTimer = null;
        void this.tiles.ensure(file.id, bucket, tiles, file.frames);
      }, FetchDebounceMs);
    });
    effect(() => this.draw());
    inject(DestroyRef).onDestroy(() => {
      if (this.fetchTimer) {
        clearTimeout(this.fetchTimer);
      }
    });
  }

  private draw(): void {
    const file = this.file();
    const canvas = this.canvas()?.nativeElement;
    this.tiles.revision();
    this.theme.appearance();
    if (!file || !canvas) {
      return;
    }
    const width = this.width();
    const height = this.height();
    const view = this.shownView();
    const bucket = this.bucket();
    const ticks = rulerTicks(view, width, file.sampleRate, this.unit());
    const context = canvas.getContext('2d');
    if (!context) {
      return;
    }
    const dpr = devicePixelRatio || 1;
    canvas.width = Math.round(width * dpr);
    canvas.height = Math.round(height * dpr);
    context.setTransform(dpr, 0, 0, dpr, 0, 0);
    drawWaveform(
      context,
      { width, height, view, totalFrames: file.frames, channels: file.channels, bucket, ticks },
      this.tiles,
      waveformColors(canvas)
    );
  }

  // ---- View

  zoomBy(factor: number, anchorX = this.width() / 2): void {
    this.view.set(zoomView(this.shownView(), factor, anchorX, Math.max(1, this.total()), this.width()));
  }

  scrollBy(pixels: number): void {
    const view = this.shownView();
    this.view.set(
      clampView(
        { start: view.start + pixels * view.framesPerPixel, framesPerPixel: view.framesPerPixel },
        Math.max(1, this.total()),
        this.width()
      )
    );
  }

  zoomToSelection(): void {
    const range = this.selection();
    if (range) {
      this.view.set(viewOfRange(range, Math.max(1, this.total()), this.width()));
    }
  }

  protected wheel(event: WheelEvent): void {
    event.preventDefault();
    if (event.ctrlKey || event.metaKey) {
      this.zoomBy(Math.exp(-event.deltaY / 200), this.localX(event));
    } else {
      this.scrollBy(event.deltaX !== 0 ? event.deltaX : event.deltaY);
    }
  }

  protected key(event: KeyboardEvent): void {
    const page = this.width() / 8;
    switch (event.key) {
      case 'ArrowLeft':
        this.scrollBy(-page);
        break;
      case 'ArrowRight':
        this.scrollBy(page);
        break;
      case 'Home':
        this.view.set({ ...this.shownView(), start: 0 });
        break;
      case 'End':
        this.view.set({ ...this.shownView(), start: this.total() });
        break;
      case '+':
      case '=':
        this.zoomBy(2);
        break;
      case '-':
        this.zoomBy(1 / 2);
        break;
      default:
        return;
    }
    event.preventDefault();
  }

  private scrollTo(event: PointerEvent): void {
    const element = this.scrollbar()?.nativeElement;
    if (!element) {
      return;
    }
    const rect = element.getBoundingClientRect();
    const total = Math.max(1, this.total());
    const view = this.shownView();
    const visible = view.framesPerPixel * this.width();
    const at = ((event.clientX - rect.left) / Math.max(1, rect.width)) * total;
    this.view.set(clampView({ start: at - visible / 2, framesPerPixel: view.framesPerPixel }, total, this.width()));
  }

  protected scrollDown(event: PointerEvent): void {
    this.scrolling = event.pointerId;
    capture(event);
    this.scrollTo(event);
  }

  protected scrollMove(event: PointerEvent): void {
    if (this.scrolling === event.pointerId) {
      this.scrollTo(event);
    }
  }

  protected scrollUp(event: PointerEvent): void {
    if (this.scrolling === event.pointerId) {
      this.scrolling = null;
    }
  }

  protected scrollKey(event: KeyboardEvent): void {
    if (event.key === 'ArrowLeft' || event.key === 'ArrowRight' || event.key === 'Home' || event.key === 'End') {
      this.key(event);
    } else if (event.key === 'PageUp' || event.key === 'PageDown') {
      event.preventDefault();
      this.scrollBy((event.key === 'PageUp' ? -1 : 1) * this.width());
    }
  }

  // ---- Selection

  private localX(event: MouseEvent): number {
    const rect = this.viewport()?.nativeElement.getBoundingClientRect();
    return event.clientX - (rect?.left ?? 0);
  }

  private frameAt(event: MouseEvent): number {
    return Math.max(0, Math.min(this.total(), Math.round(frameAtX(this.localX(event), this.shownView()))));
  }

  protected down(event: PointerEvent): void {
    if (event.button !== 0 || !this.file()) {
      return;
    }
    (event.currentTarget as HTMLElement).focus();
    capture(event);
    const frame = this.frameAt(event);
    const current = this.selection();
    if (event.shiftKey && current) {
      const extended = extendRange(current, frame, this.total());
      // The edge that did not move stays the anchor while the drag goes on.
      const anchor = extended?.start === current.start ? current.start : current.end;
      this.selection.set(extended);
      this.drag = { anchor, pointer: event.pointerId };
    } else {
      this.selection.set(null);
      this.drag = { anchor: frame, pointer: event.pointerId };
    }
    this.activeLoop.set(null);
  }

  protected move(event: PointerEvent): void {
    if (this.drag?.pointer === event.pointerId) {
      this.selection.set(rangeOf(this.drag.anchor, this.frameAt(event), this.total()));
    }
  }

  protected up(event: PointerEvent): void {
    if (this.drag?.pointer !== event.pointerId) {
      return;
    }
    this.drag = null;
    const range = this.selection();
    if (range && this.snapZero()) {
      void this.snapSelection(range);
    }
  }

  selectAll(): void {
    const total = this.total();
    this.selection.set(total > 0 ? { start: 0, end: total } : null);
    this.activeLoop.set(null);
  }

  /** Sets one edge from its field (the other: the current one, or the file's other end). */
  setEdge(edge: 'start' | 'end', value: number): void {
    if (!Number.isFinite(value)) {
      return;
    }
    const total = this.total();
    const current = this.selection() ?? { start: 0, end: total };
    const range = edge === 'start' ? rangeOf(value, current.end, total) : rangeOf(current.start, value, total);
    this.selection.set(range);
    this.activeLoop.set(null);
    if (range && this.snapZero()) {
      void this.snapSelection(range);
    }
  }

  toggleSnap(): void {
    this.snapZero.set(!this.snapZero());
    const range = this.selection();
    if (this.snapZero() && range) {
      void this.snapSelection(range);
    }
  }

  /** Moves the selection's edges to the nearest zero crossings (of the first channel's raw samples). */
  async snapSelection(range: FrameRange): Promise<void> {
    const [start, end] = await Promise.all([this.zeroCrossing(range.start), this.zeroCrossing(range.end)]);
    const snapped = rangeOf(start, end, this.total());
    // Only when nothing changed the selection meanwhile.
    if (snapped && this.selection() === range) {
      this.selection.set(snapped);
    }
  }

  private async zeroCrossing(frame: number): Promise<number> {
    const id = this.fileId();
    const total = this.total();
    if (!id || frame <= 0 || frame >= total) {
      return frame;
    }
    const from = Math.max(0, frame - ZeroCrossingRadius);
    const frames = Math.min(total - from, ZeroCrossingRadius * 2);
    try {
      const range = await this.api.fileSamples(id, from, frames, 1);
      return nearestZeroCrossing(range.samples?.[0] ?? [], frame, range.startFrame, ZeroCrossingRadius);
    } catch {
      return frame;
    }
  }

  // ---- Operations

  /** A Functions menu entry: those with settings show them first; the rest run at once. */
  choose(op: Operation): void {
    this.grooveOpen.set(false);
    if (op.value || op.curve || op.rate) {
      this.opValue.set(op.value?.default ?? 0);
      this.opCurve.set(0);
      const rate = this.file()?.sampleRate ?? 48_000;
      this.opRate.set(rate === 44_100 ? 48_000 : 44_100);
      this.pending.set(op);
    } else {
      this.pending.set(null);
      void this.run(op.kind, {});
    }
  }

  /** An operation's name without the menu's ellipsis. */
  protected opTitle(op: Operation): string {
    return op.label.replace('…', '');
  }

  protected opScope(op: Operation): string {
    if (op.rate) {
      return 'The whole file';
    }
    return this.selection() ? 'The selection' : 'The whole file';
  }

  protected valid(op: Operation): boolean {
    const v = op.value;
    return !v || (Number.isFinite(this.opValue()) && this.opValue() >= v.min && this.opValue() <= v.max);
  }

  apply(op: Operation): void {
    if (!this.valid(op)) {
      return;
    }
    const extra: Partial<AudioEditOperation> = op.value
      ? { value: this.opValue() }
      : op.curve
        ? { curve: this.opCurve() }
        : op.rate
          ? { targetRate: this.opRate() }
          : {};
    this.pending.set(null);
    void this.run(op.kind, extra);
  }

  /** Posts an operation over the selection (or the whole file) as an `audioEdit` job and follows it. */
  async run(kind: AudioEditKind, extra: Partial<AudioEditOperation>): Promise<void> {
    const id = this.fileId();
    const total = this.total();
    if (!id || total <= 0 || this.busy()) {
      return;
    }
    const range = kind === 'convertRate' ? { start: 0, end: total } : (this.selection() ?? { start: 0, end: total });
    if (kind === 'trim' && !this.selection()) {
      return;
    }
    const operation: AudioEditOperation = { kind, startFrame: range.start, endFrame: range.end, ...extra };
    const job = await this.notify.run(() => this.api.editFile(id, operation), 'Could not edit the audio file');
    if (job) {
      this.jobKind.set(kind);
      this.jobId.set(job.id);
      this.jobs.update(job);
    }
  }

  undo(): void {
    void this.project.undo();
  }

  // ---- Loop search, groove, preview

  async searchLoops(): Promise<void> {
    const id = this.fileId();
    const total = this.total();
    if (!id || total <= 0) {
      return;
    }
    const range = this.selection() ?? { start: 0, end: total };
    this.searching.set(true);
    try {
      const found = await this.notify.run(
        () => this.api.searchLoops(id, { startFrame: range.start, endFrame: range.end, count: 5 }),
        'Could not search for loops'
      );
      if (found) {
        this.loops.set(found);
        this.activeLoop.set(null);
        if (found.length === 0) {
          this.notify.info('No loops found in the range.');
        }
      }
    } finally {
      this.searching.set(false);
    }
  }

  selectLoop(index: number): void {
    const loop = this.loops()[index];
    if (loop) {
      this.selection.set({ start: loop.startFrame, end: loop.endFrame });
      this.activeLoop.set(index);
    }
  }

  protected loopLabel(loop: LoopSearchResult, index: number): string {
    return `Loop ${index + 1}, score ${loop.score.toFixed(3)}: samples ${loop.startFrame} to ${loop.endFrame}`;
  }

  protected loopLength(loop: LoopSearchResult): string {
    const rate = this.file()?.sampleRate ?? 48_000;
    return `${loop.endFrame - loop.startFrame} (${formatTime(loop.endFrame - loop.startFrame, rate, 1)})`;
  }

  async makeGroove(): Promise<void> {
    const region = this.region();
    if (!region) {
      return;
    }
    const bars = Math.max(1, Math.min(4, Math.round(this.grooveBars() || 1)));
    const name = this.grooveName().trim() || `${region.name} Groove`;
    const result = await this.project.execute(
      [{ type: 'makeGroove', regionId: region.id, name, lengthBars: bars }],
      'Make Groove Template'
    );
    if (result) {
      this.grooveOpen.set(false);
      this.notify.success(`Made the groove template ${name}`);
    }
  }

  async play(): Promise<void> {
    const id = this.fileId();
    const total = this.total();
    if (!id || total <= 0) {
      return;
    }
    const range = this.selection() ?? { start: 0, end: total };
    const done = await this.notify.run(
      () => this.api.previewFileRange(id, { startFrame: range.start, endFrame: range.end, loop: this.loopPreview() }),
      'Could not play the selection'
    );
    this.previewing.set(done !== null);
  }

  async stop(): Promise<void> {
    await this.notify.run(() => this.api.previewFile(null), 'Could not stop the preview');
    this.previewing.set(false);
  }
}
