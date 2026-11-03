import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { XuiButtonImports } from '@xui/button';
import { XuiInputImports } from '@xui/input';
import { Band } from '../control/styles';
import { formatPosition, parsePosition } from '../midi-edit/position';
import { FloatingWindow } from '../shell/floating-window';
import { ProjectStore } from '../state/project.store';
import { TransportStore } from '../state/transport.store';
import { UiStore } from '../state/ui.store';
import { formatFrameRate, formatMovieOffset, movieSpan, parseMovieOffset, timecode } from './movie-time';

/**
 * File ▸ Project Settings ▸ Movie… (docs/11-phase-10.md, Logic's Movie project settings): where the movie plays — its
 * start position (bars and beats, or the playhead) and its offset (the movie time that plays there, seconds or
 * timecode), each a `setMoviePosition` edit (one undo step) — then what the probe read: the file (in the project or
 * referenced), frame rate, timecode start, duration, size, codec and audio. Lazy-loaded.
 */
@Component({
  selector: 'an-movie-settings-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow, XuiButtonImports, XuiInputImports],
  template: `
    <an-floating-window
      title="Movie Settings"
      testId="movie-settings"
      width="30rem"
      [origin]="{ x: 240, y: 110 }"
      (closed)="ui.movieSettingsOpen.set(false)"
    >
      @if (project.movie(); as movie) {
        <h3 [class]="band">Position</h3>
        <div class="grid grid-cols-[7rem_1fr] items-center gap-x-2 gap-y-1.5 p-3 text-xs">
          <label class="text-foreground-muted" for="movie-start">Movie start</label>
          <div class="flex items-center gap-2">
            <input
              #start
              id="movie-start"
              xuiInput
              class="readout h-6 w-28 text-xs"
              data-testid="movie-start"
              [value]="startText()"
              [attr.aria-invalid]="startInvalid() ? 'true' : null"
              (change)="setStart(start.value)"
              (keydown.enter)="start.blur()"
            />
            <button
              xuiButton
              type="button"
              size="sm"
              variant="outline"
              data-testid="movie-start-playhead"
              (click)="startAtPlayhead()"
            >
              Set to Playhead
            </button>
          </div>
          <label class="text-foreground-muted" for="movie-offset">Offset</label>
          <div class="flex items-center gap-2">
            <input
              #offset
              id="movie-offset"
              xuiInput
              class="readout h-6 w-28 text-xs"
              data-testid="movie-offset"
              [value]="offsetText()"
              [attr.aria-invalid]="offsetInvalid() ? 'true' : null"
              (change)="setOffset(offset.value)"
              (keydown.enter)="offset.blur()"
            />
            <span class="text-foreground-muted">s, or hh:mm:ss:ff into the movie</span>
          </div>
          <span class="text-foreground-muted">First frame at</span>
          <span class="text-foreground readout" data-testid="movie-first-frame">{{ firstFrameText() }}</span>
          @if (problem(); as p) {
            <span></span>
            <p class="text-error" role="alert">{{ p }}</p>
          }
        </div>
        <h3 [class]="band">Movie</h3>
        <dl class="grid grid-cols-[7rem_1fr] gap-x-2 gap-y-1 p-3 text-xs" data-testid="movie-info">
          <dt class="text-foreground-muted">File</dt>
          <dd class="text-foreground min-w-0 truncate" [title]="movie.path">
            {{ movie.path }}
            <span class="text-foreground-muted">({{ movie.inProject ? 'in the project' : 'referenced' }})</span>
          </dd>
          <dt class="text-foreground-muted">Frame rate</dt>
          <dd class="text-foreground readout" data-testid="movie-frame-rate">{{ rate(movie) }} fps</dd>
          <dt class="text-foreground-muted">Timecode start</dt>
          <dd class="text-foreground readout" data-testid="movie-timecode-start">{{ tc(movie, 0) }}</dd>
          <dt class="text-foreground-muted">Duration</dt>
          <dd class="text-foreground readout">
            {{ tc({ frameRate: movie.frameRate, timecodeStartSeconds: 0 }, movie.durationSeconds) }} ({{ movie.frames }}
            frames)
          </dd>
          <dt class="text-foreground-muted">Size</dt>
          <dd class="text-foreground readout">{{ movie.width }} × {{ movie.height }}, {{ movie.codec }}</dd>
          <dt class="text-foreground-muted">Audio</dt>
          <dd class="text-foreground">
            {{
              movie.audio
                ? movie.audio.codec + ', ' + movie.audio.channels + ' ch, ' + movie.audio.sampleRate / 1000 + ' kHz'
                : 'None'
            }}
          </dd>
        </dl>
      } @else {
        <p class="text-foreground-muted p-3 text-xs">The project has no movie: Movie ▸ Open Movie… adds one.</p>
      }
    </an-floating-window>
  `
})
export class MovieSettingsWindow {
  protected readonly ui = inject(UiStore);
  protected readonly project = inject(ProjectStore);
  private readonly transport = inject(TransportStore);

  protected readonly band = Band;
  protected readonly rate = (movie: { frameRate: { numerator: number; denominator: number } }) =>
    formatFrameRate(movie.frameRate);
  protected readonly tc = timecode;

  protected readonly startInvalid = signal(false);
  protected readonly offsetInvalid = signal(false);
  protected readonly problem = signal<string | null>(null);

  protected readonly startText = computed(() => {
    const movie = this.project.movie();
    return movie ? formatPosition(movie.startTick, this.project.meter()) : '';
  });
  protected readonly offsetText = computed(() => formatMovieOffset(this.project.movie()?.offsetSeconds ?? 0));
  /** Where the movie's first frame plays (before the start position when there is an offset). */
  protected readonly firstFrameText = computed(() => {
    const movie = this.project.movie();
    if (!movie) {
      return '';
    }
    const tick = Math.round(movieSpan(movie, this.project.tempoMap()).startTick);
    return formatPosition(tick, this.project.meter());
  });

  protected setStart(text: string): void {
    const tick = parsePosition(text, this.project.meter());
    this.startInvalid.set(tick === null || tick < 0);
    if (tick !== null && tick >= 0 && tick !== this.project.movie()?.startTick) {
      void this.move({ startTick: tick });
    }
  }

  protected startAtPlayhead(): void {
    void this.move({ startTick: Math.round(this.transport.positionAt(performance.now())) });
  }

  protected setOffset(text: string): void {
    const movie = this.project.movie();
    if (!movie) {
      return;
    }
    const seconds = parseMovieOffset(text, movie);
    const valid = seconds !== null && seconds >= 0 && seconds < movie.durationSeconds;
    this.offsetInvalid.set(!valid);
    this.problem.set(valid ? null : `The offset is a time inside the movie (0 to ${movie.durationSeconds} s).`);
    if (valid && seconds !== movie.offsetSeconds) {
      void this.move({ offsetSeconds: seconds });
    }
  }

  private async move(position: { startTick?: number; offsetSeconds?: number }): Promise<void> {
    this.problem.set(null);
    await this.project.execute([{ type: 'setMoviePosition', ...position }], 'Movie Position');
  }
}
