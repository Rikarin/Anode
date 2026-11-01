import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  Directive,
  effect,
  ElementRef,
  inject,
  input,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import type { EditCommand, ScoreSymbolKind } from '@anode/api-client';
import { AnPartBox, AnScoreToolbar, type PartBoxTool } from '@anode/audio-ui/lazy';
import {
  type Anchor,
  eventPoints,
  fromScorePoint,
  hitOf,
  measureEndX,
  noteNear,
  notePoints,
  notesInRect,
  type Point,
  type ScoreHit,
  staffAt,
  type StaffBox,
  staffBoxes,
  staffPositionAt,
  staffSpace,
  stepsOfDrag,
  tickAtX,
  toScorePoint
} from '@anode/notation';
import { PianoRollStore } from '../state/piano-roll.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TransportStore } from '../state/transport.store';
import { UiStore } from '../state/ui.store';
import { chordText, parseChord } from './chords';
import { continuesWord, lyricKey, lyricNotes } from './lyrics';
import { dragAccidental } from './notation-math';
import {
  deleteSelection,
  insertNote,
  measureAtTick,
  moveNotes,
  NoteOnlySymbols,
  regionAt,
  regionOfNote,
  snapDisplayTick,
  SpanningSymbols,
  symbolAt,
  symbolsForNotes,
  TextSymbols,
  valueTicks,
  writtenPosition
} from './score-edit';
import { ScorePrint } from './score-print';
import { ScoreStore, ZoomSteps } from './score.store';

/** Sets an element's content to a page's SVG (Verovio's, from the Server's MEI; Angular's sanitiser drops SVG). */
@Directive({ selector: '[anScoreSvg]' })
export class ScoreSvg {
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly anScoreSvg = input.required<string>();

  constructor() {
    effect(() => {
      this.element.nativeElement.innerHTML = this.anScoreSvg();
    });
  }
}

/** Where a pointer is on the score: the page, its SVG and the point in drawing units. */
interface PagePoint {
  page: number;
  element: HTMLElement;
  svg: SVGSVGElement;
  point: Point;
}

/** A drag of selected notes in progress. */
interface Drag {
  start: PagePoint;
  clientX: number;
  clientY: number;
  noteId: number;
  lines: readonly number[];
  moved: boolean;
}

/** A text field over the score: a syllable, a chord symbol, or a text symbol. */
type Entry =
  | { mode: 'lyric'; noteId: number; trackId: string; verse: number; page: number; x: number; y: number; text: string }
  | {
      mode: 'chord';
      trackId: string;
      tick: number;
      chordId: string | null;
      page: number;
      x: number;
      y: number;
      text: string;
      invalid: boolean;
    }
  | {
      mode: 'text';
      kind: ScoreSymbolKind;
      trackId: string;
      tick: number;
      staff: number;
      page: number;
      x: number;
      y: number;
      text: string;
    };

const SymbolNames: Partial<Record<ScoreSymbolKind, string>> = {
  slur: 'Slur',
  crescendo: 'Crescendo',
  diminuendo: 'Diminuendo',
  octaveUp: 'Octave Up',
  octaveDown: 'Octave Down',
  dynamic: 'Dynamic',
  text: 'Text',
  tempoText: 'Tempo Text',
  rehearsal: 'Rehearsal Mark'
};

/**
 * The Score Editor (Logic's, docs/10-phase-9.md): the score the Server derives, engraved by Verovio (ADR-0011), in the
 * linear or page view, with the Part box. In the Editors pane it shows the track of the Piano Roll's regions; in a
 * window, a score set or an extracted part.
 *
 * Editing maps the engraving back to the model through the element ids: a click selects a note (⇧ adds), a drag on the
 * empty page draws a marquee; dragging notes moves them by steps of the key vertically (⌥ forces a sharp, ⇧ a flat,
 * both a natural) and by the display grid horizontally (`moveScoreNotes`); Delete deletes. With a Part box value, a
 * click inserts a note (`addScoreNote`: the staff position from the click's height, the tick from its x measured
 * against the drawn events, snapped to the display grid); a symbol goes on the clicked note (or the selected notes when
 * chosen with a selection), at the clicked position, or between two clicks for lines; Lyrics opens a field under a
 * note (Space or Tab: next note, a hyphen: next syllable of the word); Chord symbol a field at the position ("Am7",
 * "Fmaj7/C"). Each edit is one undo step.
 */
@Component({
  selector: 'an-score-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnPartBox, AnScoreToolbar, ScoreSvg],
  providers: [ScoreStore],
  host: {
    class: 'flex h-full min-h-0 flex-col',
    role: 'region',
    'aria-label': 'Score Editor',
    'data-testid': 'score-editor'
  },
  template: `
    <an-score-toolbar
      [title]="title()"
      [view]="store.view()"
      [zoom]="store.zoom()"
      [minZoom]="minZoom"
      [maxZoom]="maxZoom"
      [follow]="store.follow()"
      [partBox]="partBox()"
      [printDisabled]="!store.document() || print.printing()"
      (viewChange)="store.view.set($event)"
      (zoomBy)="store.zoomBy($event)"
      (followChange)="store.follow.set($event)"
      (partBoxChange)="partBox.set($event)"
      (staffStyles)="ui.staffStylesOpen.set(true)"
      (scoreSets)="ui.scoreSetsOpen.set(true)"
      (layout)="ui.scoreLayoutOpen.set(true)"
      (print)="printScore()"
    />
    @if (status(); as s) {
      <p
        class="border-hairline bg-primary/15 text-foreground border-b px-3 py-1 text-[11px]"
        role="status"
        data-testid="score-status"
      >
        {{ s }}
      </p>
    }
    <div class="flex min-h-0 flex-1">
      @if (partBox()) {
        <an-part-box
          class="border-hairline bg-surface w-44 shrink-0 border-e"
          [tool]="store.tool()"
          [dotted]="dotted()"
          [triplet]="triplet()"
          [disabled]="!store.document()"
          (toolChange)="chooseTool($event)"
          (dottedChange)="setDotted($event)"
          (tripletChange)="setTriplet($event)"
        />
      }
      <div
        #scroller
        class="bg-surface-sunken relative min-w-0 flex-1 overflow-auto outline-none focus-visible:ring-2 focus-visible:ring-(--focus)"
        tabindex="0"
        aria-label="Score"
        data-testid="score-scroller"
        [attr.aria-busy]="store.loading()"
        [class.cursor-crosshair]="store.tool() !== null"
        (pointerdown)="pointerDown($event)"
        (pointermove)="pointerMove($event)"
        (pointerup)="pointerUp($event)"
        (pointercancel)="cancelGesture()"
        (dblclick)="doubleClick($event)"
        (keydown)="keydown($event)"
        (scroll)="scrolled()"
      >
        @if (store.error(); as error) {
          <p class="text-error p-4 text-xs" role="alert" data-testid="score-error">{{ error }}</p>
        } @else if (!hasScore()) {
          <p class="text-foreground-muted p-4 text-xs" data-testid="score-empty">{{ emptyText() }}</p>
        }
        <div
          class="flex w-max gap-6 p-6"
          [class.flex-col]="store.view() === 'page'"
          [class.items-center]="store.view() === 'page'"
          [class.min-w-full]="store.view() === 'page'"
        >
          @for (page of store.pages(); track page.page) {
            <div
              class="an-score-page relative shrink-0"
              [style.width.px]="page.width * scale()"
              [style.height.px]="(page.height || pageHeight()) * scale()"
              [attr.data-page]="page.page"
              [attr.data-testid]="'score-page-' + page.page"
              [attr.aria-label]="'Page ' + page.page"
              role="img"
            >
              @if (page.svg) {
                <div class="h-full w-full" [anScoreSvg]="page.svg"></div>
              }
              @if (marquee(); as m) {
                @if (m.page === page.page) {
                  <div
                    class="border-primary bg-primary/15 pointer-events-none absolute border"
                    data-testid="score-marquee"
                    [style.left.px]="m.x"
                    [style.top.px]="m.y"
                    [style.width.px]="m.width"
                    [style.height.px]="m.height"
                  ></div>
                }
              }
              @if (playhead(); as p) {
                @if (p.page === page.page) {
                  <div
                    class="pointer-events-none absolute w-px bg-(--score-playhead)"
                    data-testid="score-playhead"
                    [style.left.px]="p.x"
                    [style.top.px]="p.y"
                    [style.height.px]="p.height"
                  ></div>
                }
              }
              @if (entry(); as e) {
                @if (e.page === page.page) {
                  <input
                    #entryField
                    class="border-primary bg-surface-inset text-foreground absolute h-6 w-28 rounded-[4px] border px-1.5 text-[12px] outline-none"
                    [class.border-transport-record]="e.mode === 'chord' && e.invalid"
                    [attr.aria-invalid]="e.mode === 'chord' && e.invalid"
                    [attr.aria-label]="entryLabel(e)"
                    [attr.data-testid]="'score-entry-' + e.mode"
                    [style.left.px]="e.x"
                    [style.top.px]="e.y"
                    [value]="e.text"
                    (input)="entryInput(inputValue($event))"
                    (keydown)="entryKey($event)"
                    (blur)="entryBlur()"
                    (pointerdown)="$event.stopPropagation()"
                  />
                }
              }
            </div>
          }
        </div>
      </div>
    </div>
  `
})
export class ScoreEditor {
  protected readonly store = inject(ScoreStore);
  protected readonly ui = inject(UiStore);
  protected readonly print = inject(ScorePrint);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly pianoRoll = inject(PianoRollStore);
  private readonly transport = inject(TransportStore);

  /** A score set to show (a window); with neither this nor `partTrackId`, the Piano Roll's track (the Editors pane). */
  readonly setId = input<string | null>(null);
  /** One track's part (an extracted part, in a window). */
  readonly partTrackId = input<string | null>(null);

  protected readonly minZoom = ZoomSteps[0];
  protected readonly maxZoom = ZoomSteps[ZoomSteps.length - 1] ?? 300;
  protected readonly partBox = signal(true);
  protected readonly dotted = signal(false);
  protected readonly triplet = signal(false);
  protected readonly status = signal<string | null>(null);
  protected readonly marquee = signal<{ page: number; x: number; y: number; width: number; height: number } | null>(
    null
  );
  protected readonly playhead = signal<{ page: number; x: number; y: number; height: number } | null>(null);
  protected readonly entry = signal<Entry | null>(null);
  protected readonly scale = computed(() => this.store.zoom() / 100);
  /** A page not engraved yet takes the first page's height. */
  protected readonly pageHeight = computed(() => this.store.pages().find(p => p.height > 0)?.height ?? 0);

  private readonly scroller = viewChild.required<ElementRef<HTMLElement>>('scroller');
  private readonly entryField = viewChild<ElementRef<HTMLInputElement>>('entryField');
  private drag: Drag | null = null;
  private marqueeStart: PagePoint | null = null;
  /** The first click of a two-click symbol (a slur's first note, a line's start). */
  private pending: {
    kind: ScoreSymbolKind;
    trackId: string;
    staff: number;
    noteId: number | null;
    tick: number;
  } | null = null;
  private lastMeasure = 0;

  /** The track the pane shows: the Piano Roll's. */
  private readonly paneTrack = computed(() => this.pianoRoll.track()?.id ?? null);

  protected readonly title = computed(() => {
    const set = this.setId();
    if (set) {
      return `Score set: ${this.project.score().sets.find(s => s.id === set)?.name ?? ''}`;
    }
    const part = this.partTrackId() ?? this.paneTrack();
    return part ? (this.project.track(part)?.name ?? '') : '';
  });
  protected readonly hasScore = computed(() => this.store.pages().length > 0);
  protected readonly emptyText = computed(() =>
    this.store.loading()
      ? 'Engraving…'
      : this.setId() || this.partTrackId() || this.paneTrack()
        ? 'No score yet.'
        : 'Select a MIDI region or an instrument track to see its score.'
  );

  constructor() {
    // What the score shows.
    effect(() => {
      const set = this.setId();
      const part = this.partTrackId() ?? (set ? null : this.paneTrack());
      untracked(() => this.store.query.set(set ? { set } : part ? { part } : {}));
    });
    // The selection drawn in the engraving.
    effect(() => {
      const notes = this.selection.notes();
      const symbols = this.store.selectedSymbols();
      const chords = this.store.selectedChords();
      const lyrics = this.store.selectedLyrics();
      this.store.pages();
      untracked(() => queueMicrotask(() => this.paintSelection(notes, symbols, chords, lyrics)));
    });
    // A field gets the focus when it opens.
    effect(() => {
      const field = this.entryField();
      if (field) {
        queueMicrotask(() => {
          field.nativeElement.focus();
          field.nativeElement.select();
        });
      }
    });
    // A field moved to the next note shows that note's text: set here, since the value binding may not see a change
    // when typing and the move fall between two change detections.
    effect(() => {
      const entry = this.entry();
      const field = this.entryField()?.nativeElement;
      if (entry && field && field.value !== entry.text) {
        field.value = entry.text;
        field.select();
      }
    });
    // Follows the playhead while playing.
    const timer = setInterval(() => this.followPlayhead(), 100);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  // ---- toolbar and Part box

  protected chooseTool(tool: PartBoxTool | null): void {
    this.pending = null;
    this.status.set(null);
    const selected = this.selectedNotes();
    // A symbol chosen with notes selected goes on them at once (Logic's Part box with a selection).
    if (
      tool?.kind === 'symbol' &&
      selected.length > 0 &&
      !(TextSymbols.has(tool.symbol) && tool.symbol !== 'dynamic')
    ) {
      const symbols = symbolsForNotes(tool.symbol, selected, tool.text ?? null);
      if (symbols) {
        void this.project.execute(
          [{ type: 'addScoreSymbols', symbols }],
          `Add ${SymbolNames[tool.symbol] ?? 'Symbol'}`
        );
        return;
      }
    }
    this.store.tool.set(tool);
    if (tool?.kind === 'rest') {
      this.status.set('Rests follow from the notes: shorten, move or delete notes to change them.');
    } else if (tool?.kind === 'lyric') {
      this.status.set('Click a note to enter its syllable: Space or Tab moves on, a hyphen joins the next syllable.');
    } else if (tool?.kind === 'chord') {
      this.status.set('Click where the chord symbol goes and type it, such as Am7 or Fmaj7/C.');
    } else if (tool?.kind === 'symbol' && SpanningSymbols.has(tool.symbol)) {
      this.status.set(
        tool.symbol === 'slur' ? 'Click the first note, then the last.' : 'Click where it starts, then where it ends.'
      );
    }
  }

  protected setDotted(on: boolean): void {
    this.dotted.set(on);
    const tool = this.store.tool();
    if (tool?.kind === 'note' || tool?.kind === 'rest') {
      this.store.tool.set({ ...tool, dots: on ? 1 : 0 });
    }
  }

  protected setTriplet(on: boolean): void {
    this.triplet.set(on);
    const tool = this.store.tool();
    if (tool?.kind === 'note' || tool?.kind === 'rest') {
      this.store.tool.set({ ...tool, triplet: on });
    }
  }

  protected printScore(): void {
    const set = this.setId();
    const part = this.partTrackId() ?? (set ? null : this.paneTrack());
    void this.print.print(set ? { set } : part ? { part } : {}, this.title() || this.project.info()?.name || 'Score');
  }

  // ---- pointer

  private pagePoint(event: MouseEvent): PagePoint | null {
    const element = (event.target as Element | null)?.closest<HTMLElement>('[data-page]');
    const svg = element?.querySelector('svg');
    if (!element || !svg) {
      return null;
    }
    return {
      page: Number(element.dataset['page']),
      element,
      svg,
      point: toScorePoint(svg, event.clientX, event.clientY)
    };
  }

  protected pointerDown(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    this.scroller().nativeElement.focus({ preventScroll: true });
    const at = this.pagePoint(event);
    if (!at) {
      this.clearSelection();
      return;
    }
    const hit = this.hitAt(event.target as Element, at);
    const tool = this.store.tool();
    if (tool) {
      event.preventDefault();
      this.useTool(tool, hit, at);
      return;
    }
    if (hit?.kind === 'note') {
      this.selectNote(hit.noteId, event.shiftKey || event.metaKey);
      const note = this.store.index()?.notes.get(hit.noteId);
      const box = note ? this.boxOfNote(at, note.noteId) : null;
      this.drag = {
        start: at,
        clientX: event.clientX,
        clientY: event.clientY,
        noteId: hit.noteId,
        lines: box?.lines ?? [],
        moved: false
      };
    } else if (hit?.kind === 'symbol' || hit?.kind === 'lyric' || hit?.kind === 'chordSymbol') {
      this.selectScoreItem(hit, event.shiftKey || event.metaKey);
      return;
    } else {
      if (!event.shiftKey) {
        this.clearSelection();
      }
      this.marqueeStart = at;
    }
    this.scroller().nativeElement.setPointerCapture(event.pointerId);
  }

  protected pointerMove(event: PointerEvent): void {
    const drag = this.drag;
    if (drag) {
      const dx = event.clientX - drag.clientX;
      const dy = event.clientY - drag.clientY;
      if (!drag.moved && Math.hypot(dx, dy) < 4) {
        return;
      }
      drag.moved = true;
      this.previewDrag(drag, event);
      return;
    }
    const start = this.marqueeStart;
    if (start) {
      const point = toScorePoint(start.svg, event.clientX, event.clientY);
      const page = this.store.pages().find(p => p.page === start.page);
      if (!page) {
        return;
      }
      const width = page.width * this.scale();
      const height = (page.height || this.pageHeight()) * this.scale();
      const a = fromScorePoint(start.svg, start.point, width, height);
      const b = fromScorePoint(start.svg, point, width, height);
      this.marquee.set({
        page: start.page,
        x: Math.min(a.x, b.x),
        y: Math.min(a.y, b.y),
        width: Math.abs(a.x - b.x),
        height: Math.abs(a.y - b.y)
      });
    }
  }

  protected pointerUp(event: PointerEvent): void {
    const drag = this.drag;
    this.drag = null;
    if (drag?.moved) {
      this.clearDragPreview();
      void this.finishDrag(drag, event);
      return;
    }
    const start = this.marqueeStart;
    this.marqueeStart = null;
    if (start && this.marquee()) {
      const end = toScorePoint(start.svg, event.clientX, event.clientY);
      const rect = {
        x: Math.min(start.point.x, end.x),
        y: Math.min(start.point.y, end.y),
        width: Math.abs(start.point.x - end.x),
        height: Math.abs(start.point.y - end.y)
      };
      const ids = notesInRect(start.svg, rect);
      if (event.shiftKey) {
        this.selection.selectNotes([...this.selection.notes(), ...ids]);
      } else {
        this.selection.selectNotes(ids);
      }
    }
    this.marquee.set(null);
  }

  protected cancelGesture(): void {
    this.drag = null;
    this.marqueeStart = null;
    this.marquee.set(null);
    this.clearDragPreview();
  }

  /** What a pointer is on: the element under it, else a note head near it (a head on a line can lose to the line). */
  private hitAt(target: Element, at: PagePoint): ScoreHit | null {
    const hit = hitOf(target, at.element);
    if (hit && hit.kind !== 'measure') {
      return hit;
    }
    const box = staffAt(staffBoxes(at.svg), at.point);
    const noteId = box ? noteNear(at.svg, at.point, staffSpace(box)) : null;
    return noteId !== null ? { kind: 'note', noteId, elementId: `n${noteId}` } : hit;
  }

  protected doubleClick(event: MouseEvent): void {
    const at = this.pagePoint(event);
    if (!at) {
      return;
    }
    const hit = this.hitAt(event.target as Element, at);
    if (hit?.kind === 'lyric') {
      const lyric = this.project.score().lyrics.find(l => l.id === hit.lyricId);
      const note = lyric ? this.store.index()?.notes.get(lyric.noteId) : undefined;
      if (lyric && note) {
        this.openLyric(note.noteId, note.trackId, lyric.verse, at);
      }
    } else if (hit?.kind === 'chordSymbol') {
      const chord = this.project.score().chords.find(c => c.id === hit.chordId);
      if (chord) {
        this.entry.set({
          mode: 'chord',
          trackId: chord.trackId,
          tick: chord.tick,
          chordId: chord.id,
          page: at.page,
          ...this.cssOf(at, at.point, -36),
          text: chordText(chord),
          invalid: false
        });
      }
    } else if (hit?.kind === 'note' && this.store.tool() === null) {
      const note = this.store.index()?.notes.get(hit.noteId);
      if (note) {
        this.openLyric(note.noteId, note.trackId, 1, at);
      }
    }
  }

  // ---- keys

  protected keydown(event: KeyboardEvent): void {
    if ((event.target as Element | null)?.closest('input')) {
      return;
    }
    if (event.key === 'Backspace' || event.key === 'Delete') {
      event.preventDefault();
      event.stopPropagation();
      void this.deleteSelected();
    } else if (event.key === 'Escape') {
      this.store.tool.set(null);
      this.pending = null;
      this.status.set(null);
      this.clearSelection();
    } else if ((event.metaKey || event.ctrlKey) && (event.key === '=' || event.key === '+' || event.key === '-')) {
      event.preventDefault();
      this.store.zoomBy(event.key === '-' ? -1 : 1);
    }
  }

  protected scrolled(): void {
    const scroller = this.scroller().nativeElement;
    if (this.store.view() !== 'page') {
      return;
    }
    const top = scroller.scrollTop;
    const bottom = top + scroller.clientHeight;
    const visible: number[] = [];
    for (const page of Array.from(scroller.querySelectorAll<HTMLElement>('[data-page]'))) {
      if (page.offsetTop < bottom && page.offsetTop + page.offsetHeight > top) {
        visible.push(Number(page.dataset['page']));
      }
    }
    if (visible.length) {
      this.store.visiblePages.set(visible);
    }
  }

  // ---- selection

  private selectNote(noteId: number, add: boolean): void {
    this.store.clearScoreSelection();
    if (add) {
      this.selection.toggleNote(noteId);
    } else if (!this.selection.notes().has(noteId)) {
      this.selection.selectNotes([noteId]);
    }
  }

  private selectScoreItem(hit: ScoreHit, add: boolean): void {
    if (!add) {
      this.selection.clearNotes();
      this.store.clearScoreSelection();
    }
    const toggle = (set: ReadonlySet<string>, id: string) => {
      const next = new Set(set);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    };
    if (hit.kind === 'symbol') {
      this.store.selectedSymbols.update(s => toggle(s, hit.symbolId));
    } else if (hit.kind === 'lyric') {
      this.store.selectedLyrics.update(s => toggle(s, hit.lyricId));
    } else if (hit.kind === 'chordSymbol') {
      this.store.selectedChords.update(s => toggle(s, hit.chordId));
    }
  }

  private clearSelection(): void {
    this.selection.clearNotes();
    this.store.clearScoreSelection();
  }

  /** The selected notes the score draws. */
  private selectedNotes() {
    const index = this.store.index();
    return [...this.selection.notes()]
      .map(id => index?.notes.get(id))
      .filter((n): n is NonNullable<typeof n> => n !== undefined);
  }

  private paintSelection(
    notes: ReadonlySet<number>,
    symbols: ReadonlySet<string>,
    chords: ReadonlySet<string>,
    lyrics: ReadonlySet<string>
  ): void {
    const root = this.scroller().nativeElement;
    for (const element of Array.from(root.querySelectorAll('.an-selected'))) {
      element.classList.remove('an-selected');
    }
    const ids = [
      ...[...symbols].map(id => `s${id}`),
      ...[...chords].map(id => `h${id}`),
      ...[...lyrics].map(id => `l${id}`)
    ];
    for (const element of Array.from(root.querySelectorAll('g.note[id]'))) {
      const match = /^n([1-9][0-9]*)(?:-[0-9]+)?$/.exec(element.id);
      if (match?.[1] && notes.has(Number(match[1]))) {
        element.classList.add('an-selected');
      }
    }
    for (const id of ids) {
      root.querySelector(`[id="${CSS.escape(id)}"]`)?.classList.add('an-selected');
    }
  }

  // ---- geometry

  private boxOfNote(at: PagePoint, noteId: number): StaffBox | null {
    const point = notePoints(at.svg).get(noteId);
    return point ? staffAt(staffBoxes(at.svg), point) : null;
  }

  /** The display tick at a point of a staff box: measured against the drawn events of that staff and measure. */
  private tickAt(boxes: readonly StaffBox[], box: StaffBox, x: number): number | null {
    const index = this.store.index();
    const measure = index?.measures.find(m => m.number === box.measure);
    if (!index || !measure) {
      return null;
    }
    const anchors: Anchor[] = [];
    for (const event of eventPoints(box.element)) {
      const tick = index.ticks.get(event.elementId);
      if (tick !== undefined) {
        anchors.push({ x: event.point.x, tick });
      }
    }
    return Math.round(
      tickAtX(x, anchors, {
        startTick: measure.startTick,
        endTick: measure.startTick + measure.lengthTicks,
        startX: box.left,
        endX: measureEndX(boxes, box.measure)
      })
    );
  }

  /** A point on the page as CSS pixels for a field, `dy` pixels below (negative: above). */
  private cssOf(at: PagePoint, point: Point, dy: number): { x: number; y: number } {
    const page = this.store.pages().find(p => p.page === at.page);
    const width = (page?.width ?? 0) * this.scale();
    const height = (page && page.height > 0 ? page.height : this.pageHeight()) * this.scale();
    const css = fromScorePoint(at.svg, point, width, height);
    return { x: Math.max(0, css.x - 12), y: Math.max(0, css.y + dy) };
  }

  /** Where a click is in the model: the staff (track, staff of its part), display tick and staff position. */
  private placeOf(at: PagePoint) {
    const index = this.store.index();
    const boxes = staffBoxes(at.svg);
    const box = staffAt(boxes, at.point);
    if (!index || !box) {
      return null;
    }
    const staff = index.staves[box.staff];
    const measure = index.measures.find(m => m.number === box.measure);
    const tick = this.tickAt(boxes, box, at.point.x);
    if (!staff || !measure || tick === null) {
      return null;
    }
    return { box, staff, measure, tick, position: staffPositionAt(at.point.y, box.lines) };
  }

  // ---- tools

  private useTool(tool: PartBoxTool, hit: ScoreHit | null, at: PagePoint): void {
    switch (tool.kind) {
      case 'note':
        void this.insertAt(tool, at);
        return;
      case 'rest':
        this.status.set('Rests follow from the notes: shorten, move or delete notes to change them.');
        return;
      case 'lyric':
        if (hit?.kind === 'note') {
          const note = this.store.index()?.notes.get(hit.noteId);
          if (note) {
            this.openLyric(note.noteId, note.trackId, 1, at);
          }
        }
        return;
      case 'chord':
        this.openChord(at);
        return;
      case 'symbol':
        void this.symbolAtClick(tool.symbol, tool.text ?? null, hit, at);
        return;
    }
  }

  private async insertAt(tool: Extract<PartBoxTool, { kind: 'note' }>, at: PagePoint): Promise<void> {
    const place = this.placeOf(at);
    if (!place) {
      return;
    }
    const { staff, measure } = place;
    const style = this.store.styleOf(staff.trackId);
    if (!style) {
      return;
    }
    const region = regionAt(this.project.midiRegions().values(), staff.trackId, place.tick);
    const tick = snapDisplayTick(
      place.tick,
      measure,
      region,
      style,
      tool.triplet ? valueTicks(tool.value, 0, true) : 0
    );
    const styleTranspose = style.staves[staff.staff]?.transpose ?? 0;
    const staffPosition = style.drumMap?.length
      ? place.position
      : writtenPosition(place.position, staff.clef, staff.transpose, styleTranspose, measure.key.fifths);
    await this.project.execute(
      [
        insertNote({
          tool,
          trackId: staff.trackId,
          tick,
          staffPosition,
          staff: staff.staff,
          regionId: region?.id ?? null
        })
      ],
      'Insert Note'
    );
  }

  private async symbolAtClick(
    kind: ScoreSymbolKind,
    text: string | null,
    hit: ScoreHit | null,
    at: PagePoint
  ): Promise<void> {
    const index = this.store.index();
    const note = hit?.kind === 'note' ? index?.notes.get(hit.noteId) : undefined;
    const label = `Add ${SymbolNames[kind] ?? 'Symbol'}`;
    if (NoteOnlySymbols.has(kind)) {
      if (!note) {
        this.status.set('Click a note for this symbol.');
        return;
      }
      await this.project.execute(
        [{ type: 'addScoreSymbols', symbols: [{ kind, trackId: note.trackId, noteId: note.noteId }] }],
        label
      );
      return;
    }
    const place = this.placeOf(at);
    const trackId = note?.trackId ?? place?.staff.trackId;
    if (!trackId) {
      return;
    }
    const style = this.store.styleOf(trackId);
    const region = place ? regionAt(this.project.midiRegions().values(), trackId, place.tick) : null;
    const tick = place && style ? snapDisplayTick(place.tick, place.measure, region, style) : (note?.tick ?? 0);
    const staff = place?.staff.staff ?? 0;
    if (SpanningSymbols.has(kind)) {
      if (kind === 'slur' && !note) {
        this.status.set('A slur goes from one note to another: click a note.');
        return;
      }
      const pending = this.pending;
      if (pending?.kind !== kind) {
        this.pending = { kind, trackId, staff, noteId: note?.noteId ?? null, tick: note?.tick ?? tick };
        this.status.set(kind === 'slur' ? 'Now click the last note.' : 'Now click where it ends.');
        return;
      }
      this.pending = null;
      this.status.set(null);
      const command: EditCommand =
        kind === 'slur' || (pending.noteId !== null && note)
          ? {
              type: 'addScoreSymbols',
              symbols: [{ kind, trackId: pending.trackId, noteId: pending.noteId, endNoteId: note?.noteId ?? null }]
            }
          : {
              type: 'addScoreSymbols',
              symbols: [
                symbolAt(kind, pending.trackId, pending.tick, pending.staff, null, Math.max(tick, pending.tick + 1))
              ]
            };
      await this.project.execute([command], label);
      return;
    }
    if (kind === 'dynamic') {
      await this.project.execute(
        [
          {
            type: 'addScoreSymbols',
            symbols: [note ? { kind, trackId, noteId: note.noteId, text } : symbolAt(kind, trackId, tick, staff, text)]
          }
        ],
        label
      );
      return;
    }
    if (TextSymbols.has(kind)) {
      this.entry.set({
        mode: 'text',
        kind,
        trackId,
        tick,
        staff,
        page: at.page,
        ...this.cssOf(at, at.point, -28),
        text: ''
      });
      return;
    }
    await this.project.execute([{ type: 'addScoreSymbols', symbols: [symbolAt(kind, trackId, tick, staff)] }], label);
  }

  // ---- drag

  private previewDrag(drag: Drag, event: PointerEvent): void {
    const point = toScorePoint(drag.start.svg, event.clientX, event.clientY);
    const dx = point.x - drag.start.point.x;
    const steps = stepsOfDrag(point.y - drag.start.point.y, drag.lines);
    const halfSpace =
      drag.lines.length > 1 ? ((drag.lines.at(-1) ?? 0) - (drag.lines[0] ?? 0)) / (drag.lines.length - 1) / 2 : 0;
    const notes = this.selection.notes();
    for (const element of Array.from(drag.start.element.querySelectorAll<SVGGElement>('g.note[id]'))) {
      const match = /^n([1-9][0-9]*)/.exec(element.id);
      if (match?.[1] && notes.has(Number(match[1]))) {
        element.setAttribute('transform', `translate(${dx}, ${-steps * halfSpace})`);
        element.classList.add('an-dragging');
      }
    }
  }

  private clearDragPreview(): void {
    for (const element of Array.from(this.scroller().nativeElement.querySelectorAll('.an-dragging'))) {
      element.removeAttribute('transform');
      element.classList.remove('an-dragging');
    }
  }

  private async finishDrag(drag: Drag, event: PointerEvent): Promise<void> {
    const end = toScorePoint(drag.start.svg, event.clientX, event.clientY);
    const steps = stepsOfDrag(end.y - drag.start.point.y, drag.lines);
    const index = this.store.index();
    const grabbed = index?.notes.get(drag.noteId);
    const regions = this.project.midiRegions();
    const region = regionOfNote(regions.values(), drag.noteId);
    let ticks = 0;
    const boxes = staffBoxes(drag.start.svg);
    const box = staffAt(boxes, { x: end.x, y: drag.start.point.y });
    if (grabbed && region && box && Math.abs(end.x - drag.start.point.x) > 0) {
      const style = this.store.styleOf(region.trackId);
      const target = this.tickAt(boxes, box, end.x);
      const measure = target !== null && index ? measureAtTick(index.measures, target) : null;
      if (style && target !== null && measure) {
        ticks = snapDisplayTick(target, measure, region, style) - grabbed.tick;
      }
    }
    const notes = this.selectedNotes()
      .map(n => ({ noteId: n.noteId, regionId: regionOfNote(regions.values(), n.noteId)?.id ?? '' }))
      .filter(n => n.regionId);
    const commands = moveNotes(notes, steps, ticks, dragAccidental(event));
    if (commands.length) {
      await this.project.execute(commands, 'Move Notes');
    }
  }

  // ---- delete

  private async deleteSelected(): Promise<void> {
    const regions = this.project.midiRegions();
    const lyrics = this.project.score().lyrics;
    const commands = deleteSelection({
      notes: this.selectedNotes()
        .map(n => ({ noteId: n.noteId, regionId: regionOfNote(regions.values(), n.noteId)?.id ?? '' }))
        .filter(n => n.regionId),
      symbols: [...this.store.selectedSymbols()],
      lyrics: lyrics
        .filter(l => this.store.selectedLyrics().has(l.id))
        .map(l => ({ noteId: l.noteId, verse: l.verse })),
      chords: [...this.store.selectedChords()]
    });
    if (commands.length) {
      this.clearSelection();
      await this.project.execute(commands, 'Delete');
    }
  }

  // ---- lyrics, chords and text

  private openLyric(noteId: number, trackId: string, verse: number, at: PagePoint): void {
    const point = notePoints(at.svg).get(noteId);
    const box = point ? staffAt(staffBoxes(at.svg), point) : null;
    if (!point || !box) {
      return;
    }
    const existing = this.project.score().lyrics.find(l => l.noteId === noteId && l.verse === verse);
    const below = { x: point.x, y: box.lines.at(-1) ?? point.y };
    this.selection.selectNotes([noteId]);
    this.entry.set({
      mode: 'lyric',
      noteId,
      trackId,
      verse,
      page: at.page,
      ...this.cssOf(at, below, 18),
      text: existing?.text ?? ''
    });
  }

  private openChord(at: PagePoint): void {
    const place = this.placeOf(at);
    if (!place) {
      return;
    }
    const style = this.store.styleOf(place.staff.trackId);
    const region = regionAt(this.project.midiRegions().values(), place.staff.trackId, place.tick);
    // Chord symbols snap to beats.
    const beat = 3840 / place.measure.time.denominator;
    const tick = style
      ? Math.min(
          place.measure.startTick +
            Math.round((snapDisplayTick(place.tick, place.measure, region, style) - place.measure.startTick) / beat) *
              beat,
          place.measure.startTick + place.measure.lengthTicks - beat
        )
      : place.tick;
    const top = { x: at.point.x, y: place.box.lines[0] ?? at.point.y };
    this.entry.set({
      mode: 'chord',
      trackId: place.staff.trackId,
      tick,
      chordId: null,
      page: at.page,
      ...this.cssOf(at, top, -40),
      text: '',
      invalid: false
    });
  }

  protected entryLabel(entry: Entry): string {
    return entry.mode === 'lyric'
      ? 'Syllable'
      : entry.mode === 'chord'
        ? 'Chord symbol'
        : (SymbolNames[entry.kind] ?? 'Text');
  }

  protected inputValue(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected entryInput(text: string): void {
    this.entry.update(e => (e ? { ...e, text, ...(e.mode === 'chord' ? { invalid: false } : {}) } : e));
  }

  protected entryBlur(): void {
    // Leaving the field enters what it holds (Logic keeps typed text), then closes it.
    const entry = this.entry();
    if (!entry) {
      return;
    }
    if (entry.mode === 'lyric') {
      void this.lyricStep({ key: 'Enter', shiftKey: false });
    } else if (entry.mode === 'chord') {
      void this.enterChord(false);
    } else {
      void this.enterText();
    }
  }

  protected entryKey(event: KeyboardEvent): void {
    event.stopPropagation();
    const entry = this.entry();
    if (!entry) {
      return;
    }
    if (entry.mode === 'lyric') {
      void this.lyricStep(event, event);
      return;
    }
    if (event.key === 'Escape') {
      event.preventDefault();
      this.entry.set(null);
      this.status.set(null);
    } else if (event.key === 'Enter' || event.key === 'Tab') {
      event.preventDefault();
      if (entry.mode === 'chord') {
        void this.enterChord(event.key === 'Tab');
      } else {
        void this.enterText();
      }
    }
  }

  private async lyricStep(key: { key: string; shiftKey: boolean }, event?: KeyboardEvent): Promise<void> {
    const entry = this.entry();
    const document = this.store.document();
    if (entry?.mode !== 'lyric' || !document) {
      return;
    }
    const lyrics = this.project.score().lyrics;
    const order = lyricNotes(document.view, entry.trackId);
    const existing = lyrics.find(l => l.noteId === entry.noteId && l.verse === entry.verse) ?? null;
    const result = lyricKey(
      key,
      entry.noteId,
      entry.text,
      continuesWord(lyrics, order, entry.noteId, entry.verse),
      existing
    );
    if (!result) {
      return;
    }
    event?.preventDefault();
    const index = order.indexOf(entry.noteId);
    const next = result.move === 0 ? undefined : order[index + result.move];
    // Move first, so the field follows at once; the syllable's command runs behind it.
    if (result.done || next === undefined) {
      this.entry.set(null);
      if (!result.done && result.move !== 0) {
        this.scroller().nativeElement.focus({ preventScroll: true });
      }
    } else {
      const page = this.scroller().nativeElement.querySelector<HTMLElement>(`[data-page="${entry.page}"]`);
      const svg = page?.querySelector('svg');
      if (page && svg) {
        this.openLyric(next, entry.trackId, entry.verse, {
          page: entry.page,
          element: page,
          svg,
          point: { x: 0, y: 0 }
        });
      }
    }
    if (result.change) {
      await this.project.execute([{ type: 'setLyrics', verse: entry.verse, syllables: [result.change] }], 'Lyrics');
    }
  }

  private async enterChord(next: boolean): Promise<void> {
    const entry = this.entry();
    if (entry?.mode !== 'chord') {
      return;
    }
    const text = entry.text.trim();
    if (!text) {
      this.entry.set(null);
      if (entry.chordId) {
        await this.project.execute([{ type: 'deleteChordSymbols', chordIds: [entry.chordId] }], 'Delete Chord Symbol');
      }
      return;
    }
    const chord = parseChord(text);
    if (!chord) {
      this.entry.set({ ...entry, invalid: true });
      this.status.set(
        `“${text}” is not a chord symbol: a root (C, F♯, B♭), a quality (m7, maj7, dim…) and an optional /bass.`
      );
      return;
    }
    this.status.set(null);
    const beat = this.beatAt(entry.tick);
    this.entry.set(
      next
        ? { ...entry, tick: entry.tick + beat, chordId: null, text: '', x: entry.x + 48 * this.scale(), invalid: false }
        : null
    );
    const command: EditCommand = entry.chordId
      ? {
          type: 'editChordSymbols',
          chords: [{ id: entry.chordId, trackId: entry.trackId, tick: entry.tick, ...chord, showGrid: false }]
        }
      : { type: 'addChordSymbols', chords: [{ trackId: entry.trackId, tick: entry.tick, ...chord, showGrid: false }] };
    await this.project.execute([command], 'Chord Symbol');
  }

  private beatAt(tick: number): number {
    const measure = measureAtTick(this.store.index()?.measures ?? [], tick);
    return measure ? 3840 / measure.time.denominator : 960;
  }

  private async enterText(): Promise<void> {
    const entry = this.entry();
    this.entry.set(null);
    if (entry?.mode !== 'text' || !entry.text.trim()) {
      return;
    }
    await this.project.execute(
      [
        {
          type: 'addScoreSymbols',
          symbols: [symbolAt(entry.kind, entry.trackId, entry.tick, entry.staff, entry.text.trim())]
        }
      ],
      `Add ${SymbolNames[entry.kind] ?? 'Text'}`
    );
  }

  // ---- playhead

  private followPlayhead(): void {
    if (!this.transport.playing()) {
      if (this.playhead()) {
        this.playhead.set(null);
      }
      return;
    }
    const index = this.store.index();
    if (!index) {
      return;
    }
    const tick = this.transport.positionAt(performance.now());
    const measure = measureAtTick(index.measures, tick);
    if (!measure) {
      return;
    }
    const root = this.scroller().nativeElement;
    const element = root.querySelector<SVGGElement>(`g#m${measure.number}`);
    const pageElement = element?.closest<HTMLElement>('[data-page]');
    const svg = pageElement?.querySelector('svg');
    if (!element || !pageElement || !svg) {
      return;
    }
    const boxes = staffBoxes(svg).filter(b => b.measure === measure.number);
    const first = boxes[0];
    const last = boxes.at(-1);
    const page = this.store.pages().find(p => p.page === Number(pageElement.dataset['page']));
    if (!first || !last || !page) {
      return;
    }
    const x = first.left + ((tick - measure.startTick) / measure.lengthTicks) * (first.right - first.left);
    const width = page.width * this.scale();
    const height = (page.height || this.pageHeight()) * this.scale();
    const top = fromScorePoint(svg, { x, y: (first.lines[0] ?? 0) - 400 }, width, height);
    const bottom = fromScorePoint(svg, { x, y: (last.lines.at(-1) ?? 0) + 400 }, width, height);
    this.playhead.set({ page: page.page, x: top.x, y: top.y, height: Math.max(0, bottom.y - top.y) });
    if (this.store.follow() && measure.number !== this.lastMeasure) {
      this.lastMeasure = measure.number;
      element.scrollIntoView({ block: 'nearest', inline: 'center' });
    }
  }
}
