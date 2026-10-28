import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  type ElementRef,
  inject,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import type { PitchNote } from '@anode/api-client';
import { AnPitchNote, type HotspotDrag, type PitchDrag, type PitchHotspot } from '@anode/audio-ui';
import type { Waveform } from '@anode/timeline';
import { provideIcons } from '@ng-icons/core';
import { matZoomInRound, matZoomOutRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiNonIdealStateImports } from '@xui/non-ideal-state';
import { XuiSelectImports } from '@xui/select';
import { XuiTooltipImports } from '@xui/tooltip';
import {
  dragHotspot,
  dragPitch,
  type Hotspot,
  joinNotes,
  noteTrend,
  perfectPitch,
  pitchLabel,
  type PitchScale,
  playedCents,
  splitNote
} from '../flex/pitch-math';
import { keyName } from '../global/global-math';
import { noteName } from '../midi/midi-math';
import { FlexStore } from '../state/flex.store';
import { PeaksCache } from '../state/peaks';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TransportStore } from '../state/transport.store';
import { centsRange, curvePolylines, editedRegion, rowHeight, waveformPath, yAtCents } from './editor-geometry';
import { elementSize } from './element-size';

const HotspotFields: Record<PitchHotspot, Hotspot> = {
  drift: 'drift',
  vibrato: 'vibrato',
  formant: 'formantCents',
  gain: 'gainDb'
};

interface ScaleChoice {
  value: 'chromatic' | 'key';
  label: string;
}

/**
 * The Audio Track Editor (Logic's) in the Editors pane: the selected audio region (or the selected track's first) over
 * its time, with Flex Pitch: its notes as blobs at their pitches over the analysed pitch curve, a note-name gutter, and
 * the toolbar's Flex Pitch switch, "Set to perfect pitch" with a percentage and the scale quantise menu, split, join and
 * Flex Pitch to MIDI. Drag a note up or down (⌥: in cents) or nudge it with ↑/↓; drag its hotspots for drift, vibrato,
 * formant and gain; ⇧-click adds to the selection; a double-click splits a note there; J joins the selected note with
 * the next. Each gesture is one undo step, previewed here while it lasts.
 */
@Component({
  selector: 'an-audio-track-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AnPitchNote,
    XuiButtonImports,
    XuiIconImports,
    XuiInputImports,
    XuiNonIdealStateImports,
    XuiSelectImports,
    XuiTooltipImports
  ],
  providers: [provideIcons({ matZoomInRound, matZoomOutRound })],
  host: {
    class: 'flex h-full min-h-0 flex-col outline-none',
    role: 'region',
    'aria-label': 'Audio Track Editor',
    tabindex: '-1',
    '(keydown)': 'key($event)'
  },
  template: `
    <div
      class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2"
      role="toolbar"
      aria-label="Audio Track Editor tools"
    >
      <button
        xuiButton
        type="button"
        size="sm"
        variant="outline"
        data-testid="flex-pitch-enable"
        [active]="pitchOn()"
        [class.text-lit]="pitchOn()"
        [attr.aria-pressed]="pitchOn()"
        [disabled]="!region() || analysing()"
        xuiTooltip="Flex Pitch: edit the region's notes"
        (click)="enablePitch()"
      >
        Flex Pitch
      </button>
      @if (analysing()) {
        <span class="text-foreground-muted text-xs" role="status" data-testid="flex-pitch-progress"
          >Analysing pitch…</span
        >
      }
      <div class="bg-hairline h-5 w-px"></div>
      <label class="text-foreground-muted flex items-center gap-1 text-xs">
        Perfect pitch
        <input
          xuiInput
          type="number"
          min="0"
          max="100"
          step="1"
          class="readout h-6 w-14 px-1 text-xs"
          aria-label="Perfect pitch percentage"
          data-testid="perfect-pitch-percent"
          [value]="percent()"
          #percentInput
          (change)="setPercent(percentInput.value)"
        />
        %
      </label>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="outline"
        data-testid="perfect-pitch-apply"
        [disabled]="notes().length === 0"
        [xuiTooltip]="'Set ' + (selected().size ? 'the selected notes' : 'every note') + ' to perfect pitch'"
        (click)="applyPerfectPitch(percent())"
      >
        Set
      </button>
      <xui-select
        class="an-compact-select w-36"
        aria-label="Scale quantize"
        data-testid="scale-quantize"
        [items]="scales()"
        [itemText]="scaleText"
        [filterable]="false"
        [value]="scaleChoice()"
        (valueChange)="scale.set($event?.value ?? 'chromatic')"
      />
      <button
        xuiButton
        type="button"
        size="sm"
        variant="outline"
        data-testid="scale-quantize-apply"
        [disabled]="notes().length === 0"
        xuiTooltip="Quantise the pitches to the scale"
        (click)="applyPerfectPitch(100)"
      >
        Q
      </button>
      <div class="bg-hairline h-5 w-px"></div>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="ghost"
        data-testid="pitch-split"
        [disabled]="selected().size === 0"
        xuiTooltip="Split the selected note at the playhead (or in half)"
        (click)="splitSelected()"
      >
        Split
      </button>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="ghost"
        data-testid="pitch-join"
        [disabled]="!canJoin()"
        xuiTooltip="Join the selected note with the next (J)"
        (click)="join()"
      >
        Join
      </button>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="outline"
        data-testid="flex-pitch-to-midi"
        [disabled]="notes().length === 0"
        xuiTooltip="Make a MIDI region of the notes, on a new instrument track"
        (click)="toMidi()"
      >
        To MIDI
      </button>
      <span class="text-foreground-muted min-w-0 truncate text-xs" data-testid="audio-editor-title">{{ title() }}</span>
      <div class="ms-auto flex items-center gap-1">
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
        description="Select an audio region, or an audio track, to see it here. Turn on Flex Pitch to edit its notes."
      />
    } @else {
      <div class="flex min-h-0 flex-1">
        <div
          class="border-hairline bg-surface relative w-14 shrink-0 overflow-hidden border-e"
          aria-hidden="true"
          data-testid="pitch-gutter"
        >
          @for (row of rows(); track row.cents) {
            <span
              class="text-foreground readout absolute right-1 -translate-y-1/2 text-[9px] leading-none"
              [class.font-semibold]="row.c"
              [style.top.px]="row.y"
              >{{ row.name }}</span
            >
          }
        </div>
        <div #viewport class="bg-surface-sunken relative min-w-0 flex-1 overflow-x-auto overflow-y-hidden">
          <div class="relative h-full" [style.width.px]="contentWidth()" data-testid="pitch-view">
            <svg class="pointer-events-none absolute inset-0 size-full" aria-hidden="true">
              @for (row of rows(); track row.cents) {
                <line
                  x1="0"
                  [attr.x2]="contentWidth()"
                  [attr.y1]="row.y"
                  [attr.y2]="row.y"
                  [class]="row.c ? 'stroke-hairline-strong' : 'stroke-hairline'"
                  stroke-width="1"
                />
              }
              <path
                class="fill-foreground"
                [attr.fill-opacity]="pitchOn() ? 0.12 : 0.35"
                [attr.transform]="'translate(0 ' + waveTop() + ')'"
                [attr.d]="wavePath()"
              />
              @for (line of curve(); track $index) {
                <polyline class="stroke-foreground-muted fill-none" stroke-width="1" [attr.points]="line" />
              }
            </svg>
            @for (n of placed(); track n.index) {
              <an-pitch-note
                [style.left.px]="n.left"
                [style.top.px]="n.top"
                [width]="n.width"
                [height]="n.height"
                [cents]="n.note.targetCents"
                [label]="n.label"
                [color]="color()"
                [selected]="selected().has(n.index)"
                [curve]="n.curve"
                [centsPerPixel]="centsPerPixel()"
                [drift]="n.note.drift"
                [vibrato]="n.note.vibrato"
                [formantCents]="n.note.formantCents"
                [gainDb]="n.note.gainDb"
                (pressed)="press(n.index, $event.shift)"
                (pitchDrag)="pitchDrag(n.index, $event)"
                (nudge)="nudge(n.index, $event)"
                (hotspotDrag)="hotspotDrag(n.index, $event)"
                (split)="split(n.index, $event)"
              />
            }
            @if (!pitchOn() && notes().length === 0) {
              <p class="text-foreground-muted absolute bottom-2 left-2 text-xs">
                Turn on Flex Pitch to find and edit the notes.
              </p>
            }
          </div>
        </div>
      </div>
    }
  `
})
export class AudioTrackEditor {
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly flex = inject(FlexStore);
  private readonly peaks = inject(PeaksCache);
  private readonly transport = inject(TransportStore);
  private readonly viewport = viewChild<ElementRef<HTMLElement>>('viewport');
  private readonly size = elementSize(() => this.viewport());

  /** The region shown. */
  readonly region = computed(() =>
    editedRegion(this.project.regions(), this.selection.regions(), this.selection.track())
  );
  private readonly regionId = computed(() => this.region()?.id ?? null);
  private readonly track = computed(() => {
    const region = this.region();
    return region ? this.project.track(region.trackId) : undefined;
  });
  private readonly timing = computed(() => {
    const region = this.region();
    return region ? this.project.timing(region) : null;
  });
  protected readonly pitchOn = computed(() => {
    const track = this.track();
    return !!track?.flexOn && track.flexMode === 'pitch';
  });
  protected readonly analysing = computed(() => {
    const region = this.region();
    return !!region && this.flex.analysing().has(region.fileId);
  });
  private readonly analysis = computed(() => {
    const region = this.region();
    return region ? this.flex.analysis(region.fileId) : undefined;
  });
  protected readonly color = computed(() => this.track()?.color ?? '#30a0e0');
  protected readonly title = computed(() => {
    const region = this.region();
    const track = this.track();
    return region ? `${track ? `${track.name}: ` : ''}${region.name}` : '';
  });

  /** A gesture's preview: the notes as they are dragged. */
  private readonly draft = signal<PitchNote[] | null>(null);
  private originals: PitchNote[] | null = null;
  /**
   * The last committed notes until the Server answers, so a quick run of edits (key presses) builds on each other
   * instead of each on the notes from before the run.
   */
  private readonly pending = signal<PitchNote[] | null>(null);
  readonly notes = computed(() => this.draft() ?? this.pending() ?? this.region()?.pitchNotes ?? []);
  /** Selected notes, by index. */
  readonly selected = signal<ReadonlySet<number>>(new Set());
  protected readonly percent = signal(100);
  protected readonly scale = signal<ScaleChoice['value']>('chromatic');
  private readonly zoom = signal(1);
  private readonly waveform = signal<Waveform | null>(null);

  protected readonly scales = computed<ScaleChoice[]>(() => {
    const key = this.project.settings().keys[0];
    return [
      { value: 'chromatic', label: 'Chromatic' },
      ...(key ? [{ value: 'key' as const, label: keyName(key) }] : [])
    ];
  });
  protected readonly scaleText = (choice: ScaleChoice) => choice.label;
  protected readonly scaleChoice = computed(() => this.scales().find(s => s.value === this.scale()) ?? null);
  private readonly pitchScale = computed<PitchScale>(() => {
    const key = this.project.settings().keys[0];
    return this.scale() === 'key' && key ? { tonic: key.tonic, mode: key.mode } : 'chromatic';
  });

  protected readonly contentWidth = computed(() => Math.max(1, this.size().width) * this.zoom());
  private readonly height = computed(() => Math.max(1, this.size().height));
  private readonly range = computed(() => centsRange(this.notes(), this.regionCurve()));
  private readonly row = computed(() => rowHeight(this.range(), this.height()));
  protected readonly centsPerPixel = computed(() => 100 / Math.max(1, this.row()));

  /** The analysed curve over the region's frames. */
  private readonly regionCurve = computed(() => {
    const region = this.region();
    const analysis = this.analysis();
    if (!region || !analysis || analysis.pitch.length === 0) {
      return [];
    }
    const first = Math.ceil(region.fileOffset / analysis.hop);
    const last = Math.floor((region.fileOffset + region.length) / analysis.hop);
    return analysis.pitch.slice(first, last + 1);
  });

  /** x of a file frame (through the region's timing, fitted to the view). */
  private readonly xOf = computed(() => {
    const timing = this.timing();
    const width = this.contentWidth();
    if (!timing) {
      return (_: number) => 0;
    }
    const length = Math.max(1, timing.lengthTicks);
    return (frame: number) => ((timing.tickAt(frame) - timing.startTick) / length) * width;
  });

  protected readonly rows = computed(() => {
    const [lo, hi] = this.range();
    const row = this.row();
    const out: { cents: number; y: number; name: string; c: boolean }[] = [];
    for (let c = lo; c <= hi; c += 100) {
      const pitch = Math.round(c / 100);
      const isC = pitch % 12 === 0;
      if (row >= 9 || isC) {
        out.push({ cents: c, y: yAtCents(c, this.range(), this.height()), name: noteName(pitch), c: isC });
      }
    }
    return out;
  });

  protected readonly waveTop = computed(() => this.height() * 0.15);
  protected readonly wavePath = computed(() => {
    const waveform = this.waveform();
    const timing = this.timing();
    if (!waveform || !timing) {
      return '';
    }
    const width = Math.round(this.contentWidth());
    const length = Math.max(1, timing.lengthTicks);
    return waveformPath(
      waveform,
      x => timing.frameAt(timing.startTick + (x / width) * length),
      width,
      this.height() * 0.7
    );
  });

  protected readonly curve = computed(() => {
    const region = this.region();
    const analysis = this.analysis();
    if (!region || !analysis || analysis.pitch.length === 0) {
      return [];
    }
    const x = this.xOf();
    return curvePolylines(analysis.pitch, analysis.hop, region.fileOffset, region.fileOffset + region.length, x, c =>
      yAtCents(c, this.range(), this.height())
    );
  });

  protected readonly placed = computed(() => {
    const x = this.xOf();
    const range = this.range();
    const height = this.height();
    const row = this.row();
    const analysis = this.analysis();
    return this.notes().map((note, index) => {
      const left = x(note.startFrame);
      const width = Math.max(4, x(note.endFrame) - left);
      const h = Math.max(10, row);
      const top = yAtCents(note.targetCents, range, height) - h / 2;
      let curve = '';
      if (analysis && analysis.pitch.length > 0) {
        const trend = noteTrend(note, analysis.pitch, analysis.hop);
        const points: string[] = [];
        for (let hop = Math.ceil(note.startFrame / analysis.hop); hop * analysis.hop < note.endFrame; hop++) {
          const played = playedCents(note, analysis.pitch[hop] ?? null, trend);
          if (played !== null) {
            points.push(
              `${(x(hop * analysis.hop) - left).toFixed(1)},${(yAtCents(played, range, height) - top).toFixed(1)}`
            );
          }
        }
        curve = points.join(' ');
      }
      return { index, note, left, width, top, height: h, label: pitchLabel(note.targetCents), curve };
    });
  });

  protected readonly canJoin = computed(() => {
    const first = Math.min(...this.selected());
    return Number.isFinite(first) && first + 1 < this.notes().length;
  });

  constructor() {
    effect(() => {
      // A different region: nothing selected, no gesture.
      this.regionId();
      untracked(() => {
        this.selected.set(new Set());
        this.draft.set(null);
        this.pending.set(null);
        this.originals = null;
      });
    });
    effect(() => {
      const region = this.region();
      const width = this.contentWidth();
      if (!region) {
        this.waveform.set(null);
        return;
      }
      const level = this.peaks.levelFor(region.length / Math.max(1, width));
      const fileId = region.fileId;
      void this.peaks.get(fileId, level).then(waveform => {
        if (this.region()?.fileId === fileId) {
          this.waveform.set(waveform);
        }
      });
    });
  }

  protected zoomBy(factor: number): void {
    this.zoom.update(z => Math.min(32, Math.max(1, z * factor)));
  }

  protected setPercent(text: string): void {
    const value = Number(text);
    if (Number.isFinite(value)) {
      this.percent.set(Math.min(100, Math.max(0, Math.round(value))));
    }
  }

  protected enablePitch(): void {
    const region = this.region();
    if (region) {
      void this.flex.enableFlexPitch(region);
    }
  }

  protected press(index: number, shift: boolean): void {
    this.selected.update(set => {
      if (!shift) {
        return set.has(index) ? set : new Set([index]);
      }
      const next = new Set(set);
      if (!next.delete(index)) {
        next.add(index);
      }
      return next;
    });
  }

  /** The notes a gesture on note `index` changes: the selection when it holds the note, else just it. */
  private targets(index: number): ReadonlySet<number> {
    const set = this.selected();
    return set.has(index) ? set : new Set([index]);
  }

  private gesture(index: number, done: boolean, change: (note: PitchNote) => PitchNote, label: string): void {
    const base = (this.originals ??= [...this.notes()]);
    const targets = this.targets(index);
    const next = base.map((note, i) => (targets.has(i) ? change(note) : note));
    if (done) {
      this.originals = null;
      this.draft.set(null);
      this.commit(next, label);
    } else {
      this.draft.set(next);
    }
  }

  protected pitchDrag(index: number, drag: PitchDrag): void {
    this.gesture(index, drag.done, note => dragPitch(note, drag.cents, drag.fine), 'Move Pitch');
  }

  protected nudge(index: number, cents: number): void {
    this.gesture(index, true, note => dragPitch(note, cents, Math.abs(cents) < 100), 'Move Pitch');
  }

  protected hotspotDrag(index: number, drag: HotspotDrag): void {
    const field = HotspotFields[drag.hotspot];
    this.gesture(index, drag.done, note => dragHotspot(note, field, drag.dy), `Flex Pitch ${drag.hotspot}`);
  }

  protected split(index: number, fraction: number): void {
    const note = this.notes()[index];
    if (note) {
      const frame = note.startFrame + fraction * (note.endFrame - note.startFrame);
      this.commit(splitNote(this.notes(), index, frame), 'Split Note');
    }
  }

  /** Splits the first selected note at the playhead when it is inside it, else in half. */
  protected splitSelected(): void {
    const index = Math.min(...this.selected());
    const note = this.notes()[index];
    const timing = this.timing();
    if (!note || !timing) {
      return;
    }
    const playhead = timing.frameAt(this.transport.anchorTicks());
    const inside = playhead > note.startFrame && playhead < note.endFrame;
    this.split(index, inside ? (playhead - note.startFrame) / (note.endFrame - note.startFrame) : 0.5);
  }

  protected join(): void {
    const index = Math.min(...this.selected());
    if (this.canJoin()) {
      this.selected.set(new Set([index]));
      this.commit(joinNotes(this.notes(), index), 'Join Notes');
    }
  }

  protected applyPerfectPitch(percent: number): void {
    const selected = this.selected();
    this.commit(
      perfectPitch(this.notes(), percent, this.pitchScale(), selected.size > 0 ? selected : undefined),
      percent === 100 && this.scale() === 'key' ? 'Quantize Pitch to Scale' : 'Set to Perfect Pitch'
    );
  }

  protected toMidi(): void {
    const region = this.region();
    if (region) {
      void this.flex.flexPitchToMidi(region.id);
    }
  }

  protected key(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    if (target?.closest('input, textarea, [role="combobox"], [role="listbox"]')) {
      return;
    }
    if ((event.key === 'j' || event.key === 'J') && !event.metaKey && !event.ctrlKey) {
      this.join();
      event.preventDefault();
      event.stopPropagation();
    } else if (event.key === 'Escape') {
      this.selected.set(new Set());
    }
  }

  private commit(notes: PitchNote[], label: string): void {
    const region = this.region();
    if (region) {
      this.pending.set(notes);
      void this.flex.setPitchNotes(region.id, notes, label).finally(() => {
        if (this.pending() === notes) {
          this.pending.set(null);
        }
      });
    }
  }
}
