import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  type ElementRef,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import { effectiveSpeed } from '../music/varispeed';
import { FloatingWindow } from '../shell/floating-window';
import { ProjectStore } from '../state/project.store';
import { TransportStore } from '../state/transport.store';
import { UiStore } from '../state/ui.store';
import { MovieMedia } from './media-urls';
import { MovieSync, type SyncStep } from './movie-sync';
import { formatFrameRate, frameStartSeconds, timecode } from './movie-time';

/** The movie window's sizes: its picture's width in CSS pixels. */
export const MovieWindowSizes = [
  { value: 'small', label: 'S', name: 'Small', width: 320 },
  { value: 'medium', label: 'M', name: 'Medium', width: 480 },
  { value: 'large', label: 'L', name: 'Large', width: 720 }
] as const;
export type MovieWindowSize = (typeof MovieWindowSizes)[number]['value'];

/**
 * The movie window (Logic's, floating; docs/11-phase-10.md, ADR-0013): the project's movie in a `<video>` (muted: the
 * sound is the project's), locked to the transport by {@link MovieSync} every animation frame, with the presented
 * frames reported by `requestVideoFrameCallback` where the browser has it. Its picture keeps the movie's aspect at one
 * of three sizes; a small LCD readout shows the playhead's frame and timecode (and whether the playhead is before or
 * after the movie). Where there is no stream to play (the fake Server) or the browser cannot play the codec (ProRes in
 * most browsers), it shows the Server's image of the frame instead.
 */
@Component({
  selector: 'an-movie-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow],
  template: `
    @if (project.movie(); as movie) {
      <an-floating-window
        [title]="movie.name"
        testId="movie-window"
        [width]="width() + 'px'"
        [origin]="{ x: 280, y: 120 }"
        (closed)="ui.movieWindowOpen.set(false)"
      >
        <div
          windowActions
          class="bezel-group text-foreground me-1 h-5.5 text-[10px] font-semibold"
          role="radiogroup"
          aria-label="Movie window size"
          (pointerdown)="$event.stopPropagation()"
        >
          @for (option of sizes; track option.value) {
            <button
              type="button"
              role="radio"
              class="w-6"
              [attr.aria-label]="option.name"
              [attr.aria-checked]="size() === option.value"
              [attr.data-testid]="'movie-size-' + option.value"
              [class.text-lit]="size() === option.value"
              (click)="size.set(option.value)"
            >
              {{ option.label }}
            </button>
          }
        </div>
        <div class="bg-surface-sunken relative w-full overflow-hidden" [style.aspect-ratio]="aspect()">
          @if (stream() && !unplayable()) {
            <video
              #video
              class="absolute inset-0 size-full object-contain"
              data-testid="movie-video"
              [src]="stream()"
              [muted]="true"
              playsinline
              preload="auto"
              disablepictureinpicture
              [attr.aria-label]="'Movie ' + movie.name"
              (error)="unplayable.set(true)"
            ></video>
          } @else if (frameImage(); as image) {
            <img
              class="absolute inset-0 size-full object-contain"
              data-testid="movie-frame-image"
              [src]="image"
              [attr.alt]="'Frame ' + (frame() ?? 0) + ' of ' + movie.name"
              draggable="false"
            />
          }
          @if (!inside()) {
            <div
              class="bg-background/70 text-foreground absolute inset-0 grid place-items-center text-xs font-semibold"
              data-testid="movie-outside"
            >
              {{ before() ? 'Before the movie' : 'After the movie' }}
            </div>
          }
          <div
            class="lcd-glass text-lcd-foreground absolute start-2 bottom-2 flex items-baseline gap-2 rounded-[5px] px-2 py-0.5 text-[11px] font-semibold"
            role="status"
            aria-label="Movie position"
            data-testid="movie-readout"
          >
            <span class="readout" data-testid="movie-frame">{{ frame() ?? '–' }}</span>
            <span class="readout text-lcd-muted" data-testid="movie-timecode">{{ timecodeText() }}</span>
          </div>
        </div>
        @if (unplayable()) {
          <p class="text-foreground-muted px-3 py-1.5 text-[11px]" role="note" data-testid="movie-unplayable">
            This browser cannot play {{ movie.codec }} video: the window shows the Server's frames instead.
          </p>
        }
        <div class="text-foreground-muted flex gap-3 px-3 py-1 text-[10px]">
          <span class="readout">{{ formatRate(movie.frameRate) }} fps</span>
          <span class="readout">{{ movie.width }} × {{ movie.height }}</span>
          <span>{{ movie.codec }}</span>
        </div>
      </an-floating-window>
    }
  `
})
export class MovieWindow {
  protected readonly ui = inject(UiStore);
  protected readonly project = inject(ProjectStore);
  private readonly transport = inject(TransportStore);
  private readonly media = inject(MovieMedia);
  private readonly video = viewChild<ElementRef<HTMLVideoElement>>('video');

  protected readonly sizes = MovieWindowSizes;
  protected readonly formatRate = formatFrameRate;
  protected readonly size = signal<MovieWindowSize>('medium');
  protected readonly width = computed(() => MovieWindowSizes.find(s => s.value === this.size())?.width ?? 480);
  protected readonly aspect = computed(() => {
    const movie = this.project.movie();
    return movie ? `${movie.width} / ${movie.height}` : '16 / 9';
  });
  /** The stream to play, or null (no Server stream: the frame images). A new movie path reloads the element. */
  protected readonly stream = computed(() => {
    const movie = this.project.movie();
    return movie ? this.media.streamUrl(movie.path) : null;
  });
  /** The element could not play the movie (its codec): the frame images instead. */
  protected readonly unplayable = signal(false);

  /** The playhead's frame (clamped to the movie), whether it is inside the movie, and before it when not. */
  protected readonly frame = signal<number | null>(null);
  protected readonly inside = signal(true);
  protected readonly before = signal(false);
  /** The last step's outcome (tests and debugging). */
  readonly lastStep = signal<SyncStep>('idle');

  protected readonly timecodeText = computed(() => {
    const movie = this.project.movie();
    const frame = this.frame();
    return movie && frame !== null ? timecode(movie, frameStartSeconds(movie, frame)) : '--:--:--:--';
  });
  /** The frame as the Server's image, at the picture's height (only when no video plays). */
  protected readonly frameImage = computed(() => {
    const movie = this.project.movie();
    const frame = this.frame();
    if (!movie || frame === null) {
      return null;
    }
    const height = Math.round((this.width() * movie.height) / movie.width);
    return this.media.frameUrl(frame, Math.min(1080, Math.max(16, height)));
  });

  private readonly sync = new MovieSync(
    () => this.project.movie(),
    () => this.project.tempoMap()
  );
  private animation = 0;
  private frameCallback: { video: HTMLVideoElement; handle: number } | null = null;

  constructor() {
    // A new movie may play where the last did not.
    effect(() => {
      this.stream();
      untracked(() => {
        this.unplayable.set(false);
        this.sync.reset();
      });
    });
    effect(() => {
      const video = this.video()?.nativeElement ?? null;
      untracked(() => this.watchFrames(video));
    });
    afterNextRender(() => {
      const loop = (now: number) => {
        this.step(now);
        this.animation = requestAnimationFrame(loop);
      };
      this.animation = requestAnimationFrame(loop);
    });
    inject(DestroyRef).onDestroy(() => {
      cancelAnimationFrame(this.animation);
      this.watchFrames(null);
    });
  }

  /** One frame of the loop: the readout follows the playhead, the video the transport. */
  step(now = performance.now()): void {
    const tick = this.transport.positionAt(now);
    const target = this.sync.target(tick);
    this.frame.set(target.frame);
    this.inside.set(target.inside);
    this.before.set(target.seconds < 0);
    const video = this.video()?.nativeElement;
    if (video && !this.unplayable()) {
      this.lastStep.set(
        this.sync.step(video, {
          tick,
          playing: this.transport.playing(),
          speed: effectiveSpeed(this.project.settings().varispeed),
          now
        })
      );
    }
  }

  /** Reports each presented frame to the sync (`requestVideoFrameCallback`), where the browser has it. */
  private watchFrames(video: HTMLVideoElement | null): void {
    if (this.frameCallback) {
      this.frameCallback.video.cancelVideoFrameCallback(this.frameCallback.handle);
      this.frameCallback = null;
    }
    // Not in every browser (nor in tests): then the sync reads currentTime.
    if (!video || typeof video.requestVideoFrameCallback !== 'function') {
      return;
    }
    const watch = () => {
      const handle = video.requestVideoFrameCallback((_now, metadata) => {
        this.sync.framePresented(metadata.mediaTime, metadata.expectedDisplayTime);
        watch();
      });
      this.frameCallback = { video, handle };
    };
    watch();
  }
}
