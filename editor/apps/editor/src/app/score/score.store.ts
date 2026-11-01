import { computed, DestroyRef, effect, inject, Injectable, InjectionToken, signal, untracked } from '@angular/core';
import type { ScoreDocument, ScoreViewMode, StaffStyle } from '@anode/api-client';
import { type NotationRenderer, type RenderedPage, RenderSuperseded, VerovioNotationRenderer } from '@anode/notation';
import { ANODE_API, type ScoreQuery } from '../core/api';
import { describeError } from '../core/notify';
import { ProjectStore } from '../state/project.store';
import { indexView, type PartTool, type ScoreIndex } from './score-edit';
import { StaffStyleLibrary } from './staff-style-library';

/** Makes the Score Editor's renderer: Verovio in a worker (tests provide one that does not engrave). */
export const NOTATION_RENDERER = new InjectionToken<() => NotationRenderer>('NotationRenderer', {
  providedIn: 'root',
  factory: () => () => new VerovioNotationRenderer()
});

/** How long the score waits after the last change before it refetches (typing and dragging change a lot at once). */
export const RefetchDelayMs = 60;

/** The zoom steps (percent). */
export const ZoomSteps = [50, 67, 75, 85, 100, 115, 130, 150, 175, 200, 250, 300] as const;

/**
 * One Score Editor's state (each editor, the pane's and a score set's window, has its own): which score it shows (a
 * part or a score set; linear or page view), the score the Server derives (`GET /project/score`, refetched shortly
 * after the changes that can affect it), its engraving (Verovio in a worker), the zoom, the Part box tool, the
 * selected symbols, and the playhead follow. Selected notes are the shared selection (the Piano Roll shows them).
 */
@Injectable()
export class ScoreStore {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly renderer = inject(NOTATION_RENDERER)();
  private readonly library = inject(StaffStyleLibrary);

  /** A part (a track) or a score set; neither: every MIDI track. */
  readonly query = signal<Pick<ScoreQuery, 'set' | 'part'>>({});
  readonly view = signal<ScoreViewMode>('linear');
  readonly zoom = signal(100);
  readonly tool = signal<PartTool | null>(null);
  readonly follow = signal(true);
  readonly selectedSymbols = signal<ReadonlySet<string>>(new Set());
  readonly selectedChords = signal<ReadonlySet<string>>(new Set());
  readonly selectedLyrics = signal<ReadonlySet<string>>(new Set());
  /** The pages the viewport shows (page view), rendered first after an edit. */
  readonly visiblePages = signal<readonly number[]>([1, 2]);

  readonly document = signal<ScoreDocument | null>(null);
  readonly pages = signal<readonly RenderedPage[]>([]);
  readonly error = signal<string | null>(null);
  readonly loading = signal(false);

  readonly index = computed<ScoreIndex | null>(() => {
    const document = this.document();
    return document ? indexView(document.view) : null;
  });

  private timer: ReturnType<typeof setTimeout> | null = null;
  private generation = 0;

  constructor() {
    // Anything the score is derived from: notes, tracks (styles), score content, meter, key, tempo, markers.
    effect(() => {
      this.project.midiRegions();
      this.project.tracks();
      this.project.score();
      this.project.settings();
      this.project.markers();
      const query = this.query();
      const view = this.view();
      untracked(() => {
        if (this.project.isOpen() && (query.part || query.set || this.project.trackList().length > 0)) {
          this.schedule();
        }
      });
    });
    void this.library.load();
    inject(DestroyRef).onDestroy(() => {
      if (this.timer) {
        clearTimeout(this.timer);
      }
      this.renderer.dispose();
    });
  }

  /** The style a track uses: the project's own, else a factory one; null or unknown: piano. */
  styleOf(trackId: string): StaffStyle | null {
    return this.library.styleOf(this.project.track(trackId)?.staffStyleId);
  }

  /** Refetches and re-engraves soon (debounced). */
  schedule(): void {
    if (this.timer) {
      clearTimeout(this.timer);
    }
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.refresh();
    }, RefetchDelayMs);
  }

  /** Fetches the score now and engraves it: the visible pages first, then the rest. */
  async refresh(): Promise<void> {
    const generation = ++this.generation;
    const query = this.query();
    const view = this.view();
    this.loading.set(true);
    try {
      const document = await this.api.score({ ...query, view });
      if (generation !== this.generation) {
        return;
      }
      const options = { view, layout: document.view.layout };
      const first = await this.renderer.render(
        document.mei,
        view === 'page' ? { ...options, pages: this.visiblePages() } : options
      );
      if (generation !== this.generation) {
        return;
      }
      this.document.set(document);
      this.pages.set(first);
      this.error.set(null);
      if (view === 'page' && first.some(p => !p.svg)) {
        const all = await this.renderer.render(document.mei, options);
        if (generation === this.generation) {
          this.pages.set(all);
        }
      }
    } catch (error) {
      if (!(error instanceof RenderSuperseded) && generation === this.generation) {
        this.error.set(describeError(error));
      }
    } finally {
      if (generation === this.generation) {
        this.loading.set(false);
      }
    }
  }

  zoomBy(direction: 1 | -1): void {
    const current = this.zoom();
    const steps = [...ZoomSteps];
    const next = direction > 0 ? steps.find(z => z > current) : [...steps].reverse().find(z => z < current);
    if (next) {
      this.zoom.set(next);
    }
  }

  clearScoreSelection(): void {
    this.selectedSymbols.set(new Set());
    this.selectedChords.set(new Set());
    this.selectedLyrics.set(new Set());
  }
}
