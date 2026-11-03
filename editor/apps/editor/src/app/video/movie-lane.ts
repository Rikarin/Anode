import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject } from '@angular/core';
import { elementSize } from '../audio-editor/element-size';
import { ProjectStore } from '../state/project.store';
import { TracksViewStore } from '../state/tracks-view.store';
import { TransportStore } from '../state/transport.store';
import { MovieThumbnailCache } from './movie-thumbnails';
import { movieSpan, thumbnailFrames, tickAtFrame } from './movie-time';

/** The movie track's height (`GlobalLaneHeights.movie`) less its 2 px insets: the thumbnails' displayed height. */
export const MovieLaneThumbHeight = 40;

/**
 * The movie track (Logic's, a global track): the movie's span as a band and, along it, thumbnails at the density the
 * zoom allows (one per thumbnail width, each the frame at its left edge; zoomed in past a frame per thumbnail, each
 * frame once), placed at the Tracks area's zoom and scroll. Missing thumbnails are asked for in batches (the Server
 * renders and caches them); a placeholder shows until they come. A double-click locates to the frame there.
 */
@Component({
  selector: 'an-movie-lane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'absolute inset-0 block overflow-hidden',
    'data-testid': 'movie-lane',
    '(dblclick)': 'locate($event)'
  },
  template: `
    @if (span(); as s) {
      <div
        class="bg-surface-sunken border-hairline-strong absolute top-0.5 bottom-0.5 rounded-[3px] border"
        [style.left.px]="s.left"
        [style.width.px]="s.width"
        aria-hidden="true"
      ></div>
      @for (thumb of thumbs(); track thumb.frame) {
        @if (thumb.url; as url) {
          <img
            class="absolute top-1 rounded-[2px] object-cover"
            [src]="url"
            [style.left.px]="thumb.x"
            [style.width.px]="thumbWidth()"
            [style.height.px]="height"
            [attr.alt]="'Frame ' + thumb.frame"
            [attr.data-frame]="thumb.frame"
            draggable="false"
          />
        } @else {
          <div
            class="bg-surface-inset absolute top-1 rounded-[2px]"
            [style.left.px]="thumb.x"
            [style.width.px]="thumbWidth() - 1"
            [style.height.px]="height"
            [attr.data-frame]="thumb.frame"
            data-pending="true"
            aria-hidden="true"
          ></div>
        }
      }
      <span
        class="text-foreground pointer-events-none absolute top-1 truncate px-1 text-[10px] font-semibold [text-shadow:0_1px_1px_rgb(0_0_0/0.6)]"
        [style.left.px]="Math.max(0, s.left)"
        >{{ name() }}</span
      >
    }
  `
})
export class MovieLane {
  private readonly project = inject(ProjectStore);
  private readonly view = inject(TracksViewStore);
  private readonly transport = inject(TransportStore);
  protected readonly cache = inject(MovieThumbnailCache);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly size = elementSize(() => this.host);

  protected readonly Math = Math;
  protected readonly height = MovieLaneThumbHeight;
  protected readonly name = computed(() => this.project.movie()?.name ?? '');
  /** Displayed width of a thumbnail (the movie's aspect at the lane's height). */
  protected readonly thumbWidth = computed(() => {
    const movie = this.project.movie();
    return movie ? Math.max(8, Math.round((MovieLaneThumbHeight * movie.width) / movie.height)) : 64;
  });
  /** The lane's width (a wide default before it is measured, and in tests without layout). */
  private readonly width = computed(() => this.size().width || 2000);

  protected readonly span = computed(() => {
    const movie = this.project.movie();
    if (!movie) {
      return null;
    }
    const { startTick, endTick } = movieSpan(movie, this.project.tempoMap());
    const left = this.x(startTick);
    return { left, width: Math.max(1, this.x(endTick) - left) };
  });

  /** The frames along the visible part of the movie, with their positions and image URLs (null: not cached yet). */
  protected readonly frames = computed(() => {
    const movie = this.project.movie();
    if (!movie) {
      return [];
    }
    const origin = this.view.originTick();
    const ppt = this.view.pixelsPerTick();
    const width = this.width();
    return thumbnailFrames(
      movie,
      this.project.tempoMap(),
      {
        startTick: origin - this.thumbWidth() / ppt,
        endTick: origin + width / ppt,
        widthPx: width + this.thumbWidth()
      },
      this.thumbWidth()
    );
  });

  protected readonly thumbs = computed(() => {
    this.cache.cached();
    return this.frames().map(f => ({ frame: f.frame, x: this.x(f.tick), url: this.cache.url(f.frame) }));
  });

  constructor() {
    effect(() => this.cache.request(this.frames().map(f => f.frame)));
  }

  private x(tick: number): number {
    return (tick - this.view.originTick()) * this.view.pixelsPerTick();
  }

  /** Double-click: the playhead to the start of the frame under the pointer. */
  protected locate(event: MouseEvent): void {
    const movie = this.project.movie();
    if (!movie) {
      return;
    }
    const rect = this.host.nativeElement.getBoundingClientRect();
    const tick = this.view.originTick() + (event.clientX - rect.left) / this.view.pixelsPerTick();
    const frame = this.frames().reduce<number | null>((found, f) => (f.tick <= tick ? f.frame : found), null);
    if (frame !== null) {
      this.transport.locate(tickAtFrame(movie, this.project.tempoMap(), frame));
    }
  }
}
