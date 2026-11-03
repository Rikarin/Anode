import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import type { Job, MovieAudioCodec } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import { matFolderOpenRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiSegmentedControlImports } from '@xui/segmented-control';
import { ANODE_API } from '../core/api';
import { ConnectionService } from '../core/connection';
import { isActive, JobsStore } from '../state/jobs.store';
import { ProjectStore } from '../state/project.store';
import { TransportStore } from '../state/transport.store';
import { type MovieDialog, UiStore } from '../state/ui.store';

/** The Movie menu dialogs' forms. */
export interface OpenMovieForm {
  path: string;
  copyIntoProject: boolean;
  /** Where its first frame plays: the project start or the playhead. */
  at: 'start' | 'playhead';
}
export interface ExportAudioForm {
  fileName: string;
  directory: string;
  replace: boolean;
  codec: MovieAudioCodec;
}
export interface ScenesForm {
  threshold: number;
  replaceExisting: boolean;
}

/** The threshold's range (FFmpeg's scene score: lower finds softer cuts). */
export const MinThreshold = 0.1;
export const MaxThreshold = 0.9;

/** Whether a movie path is one Anode opens (MOV, MP4, M4V). */
export function isMoviePath(path: string): boolean {
  return /\.(mov|mp4|m4v)$/i.test(path.trim());
}

/** The movie's container (its extension), which Export Audio to Movie keeps. */
export function movieExtension(path: string): string {
  return (path.split('.').pop() ?? 'mov').toLowerCase();
}

const Titles: Record<MovieDialog, string> = {
  open: 'Open Movie',
  exportAudio: 'Export Audio to Movie',
  detectScenes: 'Detect Scene Cuts'
};

/**
 * The Movie menu's dialogs (docs/11-phase-10.md), one `xui-dialog` in the interchange dialog's manner: Open Movie… (a
 * file from the native dialog in the desktop app, else a path on the Server's disk — movies are not uploaded; copied
 * into the project or referenced; its first frame at the project start or the playhead), Export Audio to Movie… (a new
 * movie beside the exports with the project's bounce replacing its audio or added as another track, AAC, or PCM in a
 * MOV) and Detect Scene Cuts… (the threshold; replacing the scene markers there are). Each runs as a Server job whose
 * progress shows here; the dialog closes when it is done, and a movie that was opened shows in the movie window.
 */
@Component({
  selector: 'an-movie-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiIconImports, XuiInputImports, XuiSegmentedControlImports],
  providers: [provideIcons({ matFolderOpenRound })],
  template: `
    <xui-dialog
      [open]="kind() !== null"
      (openChange)="$event || close()"
      [title]="title()"
      size="md"
      [canOutsideClickClose]="false"
    >
      <xui-dialog-body class="flex flex-col gap-4 text-xs" data-testid="movie-dialog">
        @switch (kind()) {
          @case ('open') {
            <div class="grid grid-cols-[7rem_1fr] items-center gap-x-3 gap-y-2.5">
              <span class="text-foreground-muted">Movie</span>
              <div class="flex min-w-0 items-center gap-2">
                @if (pickFile) {
                  <button
                    xuiButton
                    type="button"
                    size="sm"
                    variant="outline"
                    data-testid="movie-choose"
                    (click)="pick()"
                  >
                    Choose…
                  </button>
                  <span class="text-foreground readout min-w-0 truncate" data-testid="movie-path">{{
                    open().path ? baseName(open().path) : 'No movie chosen'
                  }}</span>
                } @else {
                  <input
                    #path
                    xuiInput
                    class="h-7 min-w-0 flex-1 text-xs"
                    placeholder="/path/on/the/server/Picture.mov"
                    aria-label="Movie path on the Server"
                    data-testid="movie-path-field"
                    [value]="open().path"
                    (input)="patchOpen({ path: path.value })"
                  />
                }
              </div>
              <span></span>
              <label class="text-foreground flex items-center gap-2">
                <input
                  type="checkbox"
                  class="accent-(--color-primary)"
                  data-testid="movie-copy"
                  [checked]="open().copyIntoProject"
                  (change)="patchOpen({ copyIntoProject: !open().copyIntoProject })"
                />
                Copy the movie into the project
              </label>
              <span class="text-foreground-muted">First frame at</span>
              <xui-segmented-control
                size="sm"
                aria-label="Where the movie starts"
                data-testid="movie-at"
                [options]="startOptions"
                [value]="open().at"
                (valueChange)="patchOpen({ at: $event === 'playhead' ? 'playhead' : 'start' })"
              />
            </div>
            <p class="text-foreground-muted">
              MOV, MP4 or M4V. It replaces the project's movie, if it has one; the movie window shows it when it is in.
            </p>
          }
          @case ('exportAudio') {
            <div class="grid grid-cols-[7rem_1fr] items-center gap-x-3 gap-y-2.5">
              <label class="text-foreground-muted" for="an-movie-export-name">Save As</label>
              <input
                #name
                id="an-movie-export-name"
                xuiInput
                class="h-7 text-xs"
                data-testid="movie-export-name"
                [value]="exportForm().fileName"
                (input)="patchExport({ fileName: name.value })"
              />
              <label class="text-foreground-muted" for="an-movie-export-folder">Where</label>
              <div class="flex gap-1">
                <input
                  #folder
                  id="an-movie-export-folder"
                  xuiInput
                  class="h-7 min-w-0 flex-1 text-xs"
                  placeholder="The project's Exports folder"
                  data-testid="movie-export-folder"
                  [value]="exportForm().directory"
                  (input)="patchExport({ directory: folder.value })"
                />
                @if (chooseFolder) {
                  <button xuiButton type="button" size="sm" variant="outline" (click)="pickFolder()">
                    <ng-icon xui name="matFolderOpenRound" size="sm" />
                    Choose…
                  </button>
                }
              </div>
              <span class="text-foreground-muted">Audio</span>
              <xui-segmented-control
                size="sm"
                aria-label="Replace or add the audio"
                data-testid="movie-export-replace"
                [options]="replaceOptions"
                [value]="exportForm().replace ? 'replace' : 'add'"
                (valueChange)="patchExport({ replace: $event !== 'add' })"
              />
              <span class="text-foreground-muted">Codec</span>
              <xui-segmented-control
                size="sm"
                aria-label="Audio codec"
                data-testid="movie-export-codec"
                [options]="codecOptions()"
                [value]="exportForm().codec"
                (valueChange)="patchExport({ codec: $event === 'pcm' ? 'pcm' : 'aac' })"
              />
            </div>
            <p class="text-foreground-muted" data-testid="movie-export-file">
              Writes {{ exportFile() }}: the movie's picture as it is (stream copy) with the project's mix
              {{ exportForm().replace ? 'as its audio' : 'as another audio track' }}, aligned to its frames.
            </p>
            @if (touched() && exportProblem(); as p) {
              <p class="text-error" role="alert">{{ p }}</p>
            }
          }
          @case ('detectScenes') {
            <div class="grid grid-cols-[7rem_1fr] items-center gap-x-3 gap-y-2.5">
              <label class="text-foreground-muted" for="an-scene-threshold">Threshold</label>
              <div class="flex items-center gap-2">
                <input
                  #threshold
                  id="an-scene-threshold"
                  type="range"
                  class="w-40 accent-(--color-primary)"
                  data-testid="scene-threshold"
                  [min]="minThreshold"
                  [max]="maxThreshold"
                  step="0.05"
                  [value]="scenes().threshold"
                  (input)="patchScenes({ threshold: +threshold.value })"
                />
                <span class="readout text-foreground w-8">{{ scenes().threshold.toFixed(2) }}</span>
              </div>
              <span></span>
              <label class="text-foreground flex items-center gap-2">
                <input
                  type="checkbox"
                  class="accent-(--color-primary)"
                  data-testid="scene-replace"
                  [checked]="scenes().replaceExisting"
                  (change)="patchScenes({ replaceExisting: !scenes().replaceExisting })"
                />
                Replace the scene markers there are
              </label>
            </div>
            <p class="text-foreground-muted">
              A lower threshold finds softer cuts too. Each cut becomes a scene marker (Scene 1, Scene 2, …) on the
              marker track, at the frame it is on.
            </p>
          }
        }
        @if (job(); as j) {
          <div class="flex items-center gap-3" data-testid="movie-progress">
            <div
              class="well h-2 flex-1 overflow-hidden rounded-full"
              role="progressbar"
              [attr.aria-label]="title() + ' progress'"
              aria-valuemin="0"
              aria-valuemax="100"
              [attr.aria-valuenow]="percent()"
            >
              <div class="bg-primary h-full transition-[width] duration-200" [style.width.%]="percent()"></div>
            </div>
            <span class="readout text-foreground w-24 text-end">{{ stateText(j) }}</span>
          </div>
        }
      </xui-dialog-body>
      <xui-dialog-footer>
        @if (running()) {
          <button xuiButton type="button" variant="ghost" data-testid="movie-cancel" (click)="cancelJob()">Stop</button>
        } @else {
          <button xuiButton type="button" variant="ghost" (click)="close()">Cancel</button>
        }
        <button
          xuiButton
          type="button"
          data-testid="movie-start-job"
          [disabled]="running() || !ready()"
          (click)="start()"
        >
          {{ action() }}
        </button>
      </xui-dialog-footer>
    </xui-dialog>
  `
})
export class MovieDialogHost {
  protected readonly ui = inject(UiStore);
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly transport = inject(TransportStore);
  private readonly jobs = inject(JobsStore);
  private readonly desktop = inject(ConnectionService).desktop;

  protected readonly minThreshold = MinThreshold;
  protected readonly maxThreshold = MaxThreshold;
  protected readonly pickFile = typeof this.desktop?.dialogs?.importFile === 'function';
  protected readonly chooseFolder = typeof this.desktop?.dialogs?.chooseFolder === 'function';
  protected readonly startOptions = [
    { value: 'start', label: 'Project start' },
    { value: 'playhead', label: 'Playhead' }
  ];
  protected readonly replaceOptions = [
    { value: 'replace', label: 'Replace' },
    { value: 'add', label: 'Add a track' }
  ];

  protected readonly kind = this.ui.movieDialog;
  protected readonly title = computed(() => {
    const kind = this.kind();
    return kind ? Titles[kind] : '';
  });
  protected readonly action = computed(() =>
    this.kind() === 'open' ? 'Open' : this.kind() === 'exportAudio' ? 'Export' : 'Detect'
  );

  protected readonly open = signal<OpenMovieForm>({ path: '', copyIntoProject: true, at: 'start' });
  protected readonly exportForm = signal<ExportAudioForm>({ fileName: '', directory: '', replace: true, codec: 'aac' });
  protected readonly scenes = signal<ScenesForm>({ threshold: 0.4, replaceExisting: true });
  protected readonly touched = signal(false);

  /** The container the export keeps (the movie's): PCM goes in a MOV only. */
  private readonly extension = computed(() => movieExtension(this.project.movie()?.path ?? 'movie.mov'));
  protected readonly codecOptions = computed(() =>
    this.extension() === 'mov'
      ? [
          { value: 'aac', label: 'AAC' },
          { value: 'pcm', label: 'PCM' }
        ]
      : [{ value: 'aac', label: 'AAC' }]
  );
  protected readonly exportFile = computed(() => {
    const name = this.exportForm().fileName.trim() || 'Untitled';
    const extension = this.extension();
    return name.toLowerCase().endsWith(`.${extension}`) ? name : `${name}.${extension}`;
  });
  protected readonly exportProblem = computed(() => {
    const form = this.exportForm();
    if (!form.fileName.trim()) {
      return 'Give the movie a name.';
    }
    if (/[\\/]/.test(form.fileName)) {
      return 'The name cannot contain folders; choose the folder under Where.';
    }
    return form.codec === 'pcm' && this.extension() !== 'mov' ? 'PCM audio goes in a MOV only.' : null;
  });
  protected readonly ready = computed(() => {
    switch (this.kind()) {
      case 'open':
        return isMoviePath(this.open().path);
      case 'exportAudio':
        return this.exportProblem() === null;
      default:
        return this.project.movie() !== null;
    }
  });

  private readonly jobId = signal<string | null>(null);
  protected readonly job = computed(() => {
    const id = this.jobId();
    return id ? (this.jobs.jobs().get(id) ?? null) : null;
  });
  protected readonly running = computed(() => isActive(this.job()));
  protected readonly percent = computed(() => Math.round((this.job()?.progress ?? 0) * 100));

  constructor() {
    // Each time it opens, it starts afresh from the project as it is.
    effect(() => {
      if (this.kind()) {
        untracked(() => {
          if (!this.running()) {
            const movie = this.project.movie();
            this.open.set({ path: '', copyIntoProject: true, at: 'start' });
            this.exportForm.set({
              fileName: `${movie?.name ?? this.project.info()?.name ?? 'Movie'} with Mix`,
              directory: '',
              replace: true,
              codec: 'aac'
            });
            this.scenes.set({ threshold: 0.4, replaceExisting: true });
            this.jobId.set(null);
            this.touched.set(false);
          }
        });
      }
    });
    effect(() => {
      const job = this.job();
      if (job?.state === 'done') {
        untracked(() => {
          // A movie that came in shows at once.
          if (job.kind === 'movieImport') {
            this.ui.movieWindowOpen.set(true);
          }
          this.ui.movieDialog.set(null);
        });
      }
    });
  }

  protected baseName(path: string): string {
    return path.split(/[\\/]/).pop() ?? path;
  }

  protected patchOpen(change: Partial<OpenMovieForm>): void {
    this.open.update(form => ({ ...form, ...change }));
  }

  protected patchExport(change: Partial<ExportAudioForm>): void {
    this.exportForm.update(form => ({ ...form, ...change }));
  }

  protected patchScenes(change: Partial<ScenesForm>): void {
    const threshold = change.threshold;
    this.scenes.update(form => ({
      ...form,
      ...change,
      threshold: threshold === undefined ? form.threshold : Math.min(MaxThreshold, Math.max(MinThreshold, threshold))
    }));
  }

  protected async pick(): Promise<void> {
    const path = await this.desktop?.dialogs?.importFile?.('movie');
    if (path) {
      this.patchOpen({ path });
    }
  }

  protected async pickFolder(): Promise<void> {
    const folder = await this.desktop?.dialogs?.chooseFolder();
    if (folder) {
      this.patchExport({ directory: folder });
    }
  }

  protected stateText(job: Job): string {
    switch (job.state) {
      case 'queued':
        return 'Waiting…';
      case 'running':
        return `${this.percent()} %`;
      case 'done':
        return 'Done';
      case 'failed':
        return 'Failed';
      default:
        return 'Stopped';
    }
  }

  protected close(): void {
    if (!this.running()) {
      this.ui.movieDialog.set(null);
    }
  }

  protected async cancelJob(): Promise<void> {
    const id = this.jobId();
    if (id) {
      await this.jobs.cancel(id);
    }
  }

  protected async start(): Promise<void> {
    this.touched.set(true);
    if (!this.ready()) {
      return;
    }
    let job: Job | null = null;
    switch (this.kind()) {
      case 'open': {
        const form = this.open();
        const startTick = form.at === 'playhead' ? Math.round(this.transport.positionAt(performance.now())) : 0;
        job = await this.jobs.interchange(
          () => this.api.importMovie({ path: form.path.trim(), copyIntoProject: form.copyIntoProject, startTick }),
          'Could not open the movie'
        );
        break;
      }
      case 'exportAudio': {
        const form = this.exportForm();
        job = await this.jobs.interchange(
          () =>
            this.api.exportMovieAudio({
              fileName: form.fileName.trim(),
              directory: form.directory.trim() || null,
              replace: form.replace,
              codec: form.codec
            }),
          'Could not export the audio to the movie'
        );
        break;
      }
      case 'detectScenes': {
        const form = this.scenes();
        job = await this.jobs.interchange(
          () => this.api.detectMovieScenes({ threshold: form.threshold, replaceExisting: form.replaceExisting }),
          'Could not detect the scene cuts'
        );
        break;
      }
      default:
        break;
    }
    if (job) {
      this.jobId.set(job.id);
    }
  }
}
