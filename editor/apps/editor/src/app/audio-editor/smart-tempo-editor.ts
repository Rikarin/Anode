import {
  ChangeDetectionStrategy,
  Component,
  type ElementRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import type { FileTempo } from '@anode/api-client';
import type { Waveform } from '@anode/timeline';
import { provideIcons } from '@ng-icons/core';
import { matZoomInRound, matZoomOutRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiNonIdealStateImports } from '@xui/non-ideal-state';
import { XuiTooltipImports } from '@xui/tooltip';
import { FlexStore } from '../state/flex.store';
import { PeaksCache } from '../state/peaks';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { SmartTempoStore } from '../state/smart-tempo.store';
import { addBeat, doubleTempo, halveTempo, moveBeat, removeBeat, toggleDownbeat } from './beat-draft';
import { editedRegion, waveformPath } from './editor-geometry';
import { elementSize } from './element-size';

/** Seconds a beat's ←/→ nudges it (⇧: a millisecond). */
const NudgeSeconds = 0.01;

/**
 * The Smart Tempo Editor (Logic's): the selected audio region's file over its frames, with its file tempo's beats and
 * downbeats (the file's own, else its analysis) in a strip above the waveform. Edit a draft, previewed over the waveform:
 * drag a beat (or ←/→ it; ⇧ finely), click the strip to add one, double-click a beat to make it a downbeat (or a plain
 * beat again), Delete removes it; double or halve the tempo. Keep stores the draft as the file's tempo; Apply also
 * adapts the project tempo to the region; Revert drops the draft.
 */
@Component({
  selector: 'an-smart-tempo-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiIconImports, XuiNonIdealStateImports, XuiTooltipImports],
  providers: [provideIcons({ matZoomInRound, matZoomOutRound })],
  host: {
    class: 'flex h-full min-h-0 flex-col outline-none',
    role: 'region',
    'aria-label': 'Smart Tempo Editor',
    tabindex: '-1'
  },
  template: `
    <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2" role="toolbar" aria-label="Smart Tempo tools">
      <span class="text-foreground-muted text-xs">File tempo</span>
      <span class="readout text-foreground text-sm font-semibold" data-testid="smart-tempo-bpm">{{ bpmText() }}</span>
      @if (shown()?.variable) {
        <span class="text-foreground-muted text-[10px] font-semibold uppercase">Variable</span>
      }
      @if (dirty()) {
        <span class="text-lit text-[10px] font-semibold uppercase" data-testid="smart-tempo-edited">Edited</span>
      }
      <div class="bg-hairline h-5 w-px"></div>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="outline"
        data-testid="smart-tempo-double"
        aria-label="Double tempo"
        xuiTooltip="Double the tempo: a beat between every two"
        [disabled]="!shown()"
        (click)="double()"
      >
        ×2
      </button>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="outline"
        data-testid="smart-tempo-halve"
        aria-label="Halve tempo"
        xuiTooltip="Halve the tempo: every other beat"
        [disabled]="!shown()"
        (click)="halve()"
      >
        ÷2
      </button>
      <div class="ms-auto flex items-center gap-1">
        <button
          xuiButton
          type="button"
          size="sm"
          variant="ghost"
          data-testid="smart-tempo-revert"
          [disabled]="!dirty()"
          (click)="draft.set(null)"
        >
          Revert
        </button>
        <button
          xuiButton
          type="button"
          size="sm"
          variant="outline"
          data-testid="smart-tempo-keep"
          xuiTooltip="Keep the project tempo: store these beats as the file's tempo"
          [disabled]="!shown()"
          (click)="keep()"
        >
          Keep
        </button>
        <button
          xuiButton
          type="button"
          size="sm"
          data-testid="smart-tempo-apply"
          xuiTooltip="Store these beats and adapt the project tempo to the region"
          [disabled]="!shown()"
          (click)="apply()"
        >
          Apply
        </button>
        <button xuiButton type="button" size="sm" variant="ghost" aria-label="Zoom out" (click)="zoomBy(1 / 1.5)">
          <ng-icon xui name="matZoomOutRound" size="sm" />
        </button>
        <button xuiButton type="button" size="sm" variant="ghost" aria-label="Zoom in" (click)="zoomBy(1.5)">
          <ng-icon xui name="matZoomInRound" size="sm" />
        </button>
      </div>
    </div>
    @if (!region()) {
      <xui-non-ideal-state
        class="min-h-0 flex-1"
        title="No audio region"
        description="Select an audio region to see its file's beats and downbeats here."
      />
    } @else if (!shown()) {
      <xui-non-ideal-state class="min-h-0 flex-1" title="Analysing the beats…" [description]="region()?.name ?? ''" />
    } @else {
      <div #viewport class="bg-surface-sunken relative min-h-0 flex-1 overflow-x-auto overflow-y-hidden">
        <div class="relative flex h-full flex-col" [style.width.px]="contentWidth()">
          <div
            class="bg-surface-raised border-hairline relative h-7 shrink-0 cursor-copy border-b"
            data-testid="smart-tempo-strip"
            (pointerdown)="stripPress($event)"
          >
            @for (b of placed(); track b.frame) {
              <div
                class="focus-visible:outline-focus absolute top-0 flex h-full w-3 -translate-x-1/2 cursor-ew-resize touch-none justify-center focus-visible:outline-2"
                role="slider"
                tabindex="0"
                data-testid="smart-tempo-beat"
                [attr.data-downbeat]="b.downbeat || null"
                [style.left.px]="b.x"
                [attr.aria-label]="b.downbeat ? 'Downbeat, bar ' + b.bar : 'Beat ' + (b.index + 1)"
                aria-valuemin="0"
                [attr.aria-valuemax]="fileFrames()"
                [attr.aria-valuenow]="b.frame"
                [attr.aria-valuetext]="b.seconds"
                (pointerdown)="down($event, b.index)"
                (pointermove)="move($event)"
                (pointerup)="up($event)"
                (pointercancel)="up($event)"
                (dblclick)="toggleAt(b.index)"
                (keydown)="key($event, b.index)"
              >
                <span
                  class="h-full"
                  [class]="b.downbeat ? 'bg-primary w-0.5' : 'bg-foreground-muted w-px'"
                  aria-hidden="true"
                ></span>
                @if (b.downbeat) {
                  <span
                    class="text-foreground readout absolute top-0.5 left-2 text-[10px] font-semibold"
                    aria-hidden="true"
                    >{{ b.bar }}</span
                  >
                }
              </div>
            }
          </div>
          <svg class="pointer-events-none min-h-0 w-full flex-1" aria-hidden="true" #wave>
            <path class="fill-foreground" fill-opacity="0.35" [attr.d]="wavePath()" />
            @for (b of placed(); track b.frame) {
              <line
                [attr.x1]="b.x"
                [attr.x2]="b.x"
                y1="0"
                y2="100%"
                [class]="b.downbeat ? 'stroke-primary' : 'stroke-foreground-muted'"
                [attr.stroke-opacity]="b.downbeat ? 0.9 : 0.45"
                stroke-width="1"
              />
            }
          </svg>
        </div>
      </div>
    }
  `
})
export class SmartTempoEditor {
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly flex = inject(FlexStore);
  private readonly smart = inject(SmartTempoStore);
  private readonly peaks = inject(PeaksCache);
  private readonly viewport = viewChild<ElementRef<HTMLElement>>('viewport');
  private readonly waveSvg = viewChild<ElementRef<HTMLElement>>('wave');
  private readonly size = elementSize(() => this.viewport());
  private readonly waveSize = elementSize(() => this.waveSvg());

  readonly region = computed(() =>
    editedRegion(this.project.regions(), this.selection.regions(), this.selection.track())
  );
  private readonly file = computed(() => {
    const region = this.region();
    return region ? this.project.files().get(region.fileId) : undefined;
  });
  private readonly fileId = computed(() => this.file()?.id ?? null);
  protected readonly sampleRate = computed(() => this.file()?.sampleRate ?? this.project.info()?.sampleRate ?? 48_000);
  protected readonly fileFrames = computed(() => this.file()?.frames ?? 0);
  /** The file's tempo as stored, else as analysed. */
  private readonly original = computed<FileTempo | null>(
    () => {
      const file = this.file();
      if (!file) {
        return null;
      }
      return file.tempo ?? this.flex.analysis(file.id)?.tempo ?? null;
      // A refetched analysis with the same beats is the same tempo (it must not drop the draft).
    },
    { equal: (a, b) => JSON.stringify(a) === JSON.stringify(b) }
  );
  /** The draft being edited (null: none, the original shows). */
  readonly draft = signal<FileTempo | null>(null);
  readonly shown = computed(() => this.draft() ?? this.original());
  protected readonly dirty = computed(() => this.draft() !== null);
  protected readonly bpmText = computed(() => {
    const tempo = this.shown();
    return tempo ? `${tempo.bpm.toFixed(2)} bpm` : '—';
  });

  private readonly zoom = signal(1);
  private readonly waveform = signal<Waveform | null>(null);
  protected readonly contentWidth = computed(() => Math.max(1, this.size().width) * this.zoom());

  /** The file frames shown: the region's. */
  private readonly span = computed(() => {
    const region = this.region();
    return region ? { from: region.fileOffset, to: region.fileOffset + region.length } : { from: 0, to: 1 };
  });

  private xOf(frame: number): number {
    const { from, to } = this.span();
    return ((frame - from) / Math.max(1, to - from)) * this.contentWidth();
  }

  private frameOf(x: number): number {
    const { from, to } = this.span();
    return from + (x / this.contentWidth()) * (to - from);
  }

  protected readonly placed = computed(() => {
    const tempo = this.shown();
    const { from, to } = this.span();
    if (!tempo) {
      return [];
    }
    const downbeats = new Set(tempo.downbeats);
    const sr = this.sampleRate();
    let bar = 0;
    const out: { index: number; frame: number; x: number; downbeat: boolean; bar: number; seconds: string }[] = [];
    tempo.beats.forEach((frame, index) => {
      const downbeat = downbeats.has(frame);
      if (downbeat) {
        bar++;
      }
      if (frame >= from && frame <= to) {
        out.push({
          index,
          frame,
          x: this.xOf(frame),
          downbeat,
          bar,
          seconds: `${(frame / sr).toFixed(3)} s`
        });
      }
    });
    return out;
  });

  protected readonly wavePath = computed(() => {
    const waveform = this.waveform();
    const width = Math.round(this.contentWidth());
    const height = this.waveSize().height;
    return waveform && height > 0 ? waveformPath(waveform, x => this.frameOf(x), width, height) : '';
  });

  private dragging: { index: number; pointer: number; left: number } | null = null;

  constructor() {
    effect(() => {
      // Another file, or its tempo changed: the draft goes.
      this.fileId();
      this.original();
      untracked(() => this.draft.set(null));
    });
    effect(() => {
      const region = this.region();
      const width = this.contentWidth();
      if (!region) {
        this.waveform.set(null);
        return;
      }
      const fileId = region.fileId;
      void this.peaks.get(fileId, this.peaks.levelFor(region.length / Math.max(1, width))).then(waveform => {
        if (this.region()?.fileId === fileId) {
          this.waveform.set(waveform);
        }
      });
    });
  }

  /** Applies an edit to the draft (started from the original). */
  edit(change: (tempo: FileTempo) => FileTempo): void {
    const tempo = this.shown();
    if (tempo) {
      this.draft.set(change(tempo));
    }
  }

  double(): void {
    this.edit(t => doubleTempo(t, this.sampleRate()));
  }

  halve(): void {
    this.edit(t => halveTempo(t, this.sampleRate()));
  }

  /** A double-click: the beat becomes a downbeat, or a plain beat again. */
  toggleAt(index: number): void {
    this.edit(t => toggleDownbeat(t, index, this.sampleRate()));
  }

  protected zoomBy(factor: number): void {
    this.zoom.update(z => Math.min(64, Math.max(1, z * factor)));
  }

  protected stripPress(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const frame = this.frameOf(event.clientX - rect.left);
    this.edit(t => addBeat(t, frame, this.sampleRate()));
  }

  protected down(event: PointerEvent, index: number): void {
    if (event.button !== 0) {
      return;
    }
    event.stopPropagation();
    const strip = (event.currentTarget as HTMLElement).parentElement;
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    this.dragging = { index, pointer: event.pointerId, left: strip?.getBoundingClientRect().left ?? 0 };
  }

  protected move(event: PointerEvent): void {
    const d = this.dragging;
    if (d?.pointer === event.pointerId) {
      const frame = this.frameOf(event.clientX - d.left);
      this.edit(t => moveBeat(t, d.index, frame, this.sampleRate()));
    }
  }

  protected up(event: PointerEvent): void {
    if (this.dragging?.pointer === event.pointerId) {
      this.dragging = null;
    }
  }

  protected key(event: KeyboardEvent, index: number): void {
    const sr = this.sampleRate();
    const step = Math.round((event.shiftKey ? 0.001 : NudgeSeconds) * sr);
    const tempo = this.shown();
    const frame = tempo?.beats[index];
    if (frame === undefined) {
      return;
    }
    if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
      event.preventDefault();
      event.stopPropagation();
      this.edit(t => moveBeat(t, index, frame + (event.key === 'ArrowLeft' ? -step : step), sr));
    } else if (event.key === 'Delete' || event.key === 'Backspace') {
      event.preventDefault();
      event.stopPropagation();
      this.edit(t => removeBeat(t, index, sr));
    } else if (event.key === 'Enter') {
      event.preventDefault();
      event.stopPropagation();
      this.edit(t => toggleDownbeat(t, index, sr));
    }
  }

  /** Keep the project tempo: the draft becomes the file's tempo. */
  async keep(): Promise<void> {
    const file = this.file();
    const tempo = this.shown();
    if (file && tempo) {
      await this.smart.setFileTempo(file.id, tempo);
      this.draft.set(null);
    }
  }

  /** The draft becomes the file's tempo, and the project tempo adapts to the region. */
  async apply(): Promise<void> {
    const region = this.region();
    const file = this.file();
    const tempo = this.shown();
    if (region && file && tempo) {
      if (this.dirty() || !file.tempo) {
        await this.smart.setFileTempo(file.id, tempo);
      }
      await this.smart.adaptTempo(region.id);
      this.draft.set(null);
    }
  }
}
