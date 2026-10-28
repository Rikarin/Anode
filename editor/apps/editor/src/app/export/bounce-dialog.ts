import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { provideIcons } from '@ng-icons/core';
import { matFolderOpenRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiNumericInputImports } from '@xui/numeric-input';
import { XuiSegmentedControlImports } from '@xui/segmented-control';
import { XuiSelectImports } from '@xui/select';
import { ConnectionService } from '../core/connection';
import { isActive, JobsStore } from '../state/jobs.store';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import {
  BitDepths,
  bounceFileNames,
  type BounceForm,
  bounceProblems,
  bounceRequest,
  defaultBounceForm,
  Dithers,
  Normalizations,
  SampleRates,
  stemTracks
} from './bounce';

/**
 * Logic's Bounce window (⌘B): the range in bars (the cycle when it is on), the mix or stems of chosen tracks, WAV or
 * AIFF at 16, 24 or 32-bit float, a sample rate, dither below 32 bits, normalisation (peak, or loudness to a LUFS
 * target, both under a true-peak ceiling), real-time or offline, and whether to add the result to the project. The bounce
 * runs as a Server job: the window shows its progress (and can cancel it), and closes when it is done; a toast then says
 * where the file is and, in the desktop app, reveals it.
 */
@Component({
  selector: 'an-bounce-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    XuiButtonImports,
    XuiDialogImports,
    XuiIconImports,
    XuiInputImports,
    XuiNumericInputImports,
    XuiSegmentedControlImports,
    XuiSelectImports
  ],
  providers: [provideIcons({ matFolderOpenRound })],
  template: `
    <xui-dialog [(open)]="ui.bounceOpen" title="Bounce" size="lg" [canOutsideClickClose]="false">
      <xui-dialog-body class="flex flex-col gap-4 text-xs" data-testid="bounce-dialog">
        <div class="grid grid-cols-[7rem_1fr] items-center gap-x-3 gap-y-2.5">
          <label class="text-foreground-muted" for="an-bounce-name">Save As</label>
          <div class="flex gap-1">
            <input
              #name
              id="an-bounce-name"
              xuiInput
              class="h-7 min-w-0 flex-1 text-xs"
              data-testid="bounce-name"
              [value]="form().fileName"
              (input)="patch({ fileName: name.value })"
            />
          </div>
          <label class="text-foreground-muted" for="an-bounce-folder">Where</label>
          <div class="flex gap-1">
            <input
              #folder
              id="an-bounce-folder"
              xuiInput
              class="h-7 min-w-0 flex-1 text-xs"
              placeholder="The project's Bounces folder"
              data-testid="bounce-folder"
              [value]="form().directory"
              (input)="patch({ directory: folder.value })"
            />
            @if (chooseFolder) {
              <button xuiButton type="button" size="sm" variant="outline" (click)="pickFolder()">
                <ng-icon xui name="matFolderOpenRound" size="sm" />
                Choose…
              </button>
            }
          </div>

          <span class="text-foreground-muted">Range</span>
          <div class="flex items-center gap-2">
            <span class="text-foreground-muted">Start bar</span>
            <xui-numeric-input
              class="w-24"
              size="sm"
              aria-label="Start bar"
              data-testid="bounce-start"
              [min]="1"
              [stepSize]="1"
              [value]="form().startBar"
              (valueChange)="patch({ startBar: $event ?? 1 })"
            />
            <span class="text-foreground-muted">End bar</span>
            <xui-numeric-input
              class="w-24"
              size="sm"
              aria-label="End bar"
              data-testid="bounce-end"
              [min]="2"
              [stepSize]="1"
              [value]="form().endBar"
              (valueChange)="patch({ endBar: $event ?? 2 })"
            />
            @if (project.settings().cycle.enabled) {
              <span class="text-foreground-muted">(the cycle)</span>
            }
          </div>

          <span class="text-foreground-muted">Source</span>
          <xui-segmented-control
            size="sm"
            aria-label="Bounce source"
            data-testid="bounce-source"
            [options]="sources"
            [value]="form().source"
            (valueChange)="patch({ source: $event ?? 'mix' })"
          />
          @if (form().source === 'stems') {
            <span class="text-foreground-muted self-start pt-1">Tracks</span>
            <div
              class="border-hairline bg-surface-inset grid max-h-32 grid-cols-2 gap-x-3 gap-y-1 overflow-y-auto rounded-md border p-2"
              role="group"
              aria-label="Stem tracks"
            >
              @for (track of tracks(); track track.id) {
                <label class="text-foreground flex min-w-0 items-center gap-2">
                  <input
                    type="checkbox"
                    class="accent-(--color-primary)"
                    [attr.data-testid]="'stem-' + track.id"
                    [checked]="form().trackIds.includes(track.id)"
                    (change)="toggleStem(track.id)"
                  />
                  <span class="truncate">{{ track.name }}</span>
                </label>
              }
            </div>
          }
        </div>

        <fieldset
          class="border-hairline grid grid-cols-[7rem_1fr] items-center gap-x-3 gap-y-2.5 rounded-md border px-3 py-2.5"
        >
          <legend class="text-foreground-muted px-1 text-[10px] font-semibold tracking-wide uppercase">PCM</legend>
          <span class="text-foreground-muted">File Format</span>
          <xui-segmented-control
            size="sm"
            aria-label="File format"
            data-testid="bounce-format"
            [options]="formats"
            [value]="form().format"
            (valueChange)="patch({ format: $event ?? 'wav' })"
          />
          <span class="text-foreground-muted">Resolution</span>
          <xui-select
            class="w-40"
            aria-label="Resolution"
            data-testid="bounce-bit-depth"
            [items]="bitDepths"
            [itemText]="labelOf"
            [filterable]="false"
            [value]="bitDepth()"
            (valueChange)="patch({ bitDepth: $event?.value ?? 24 })"
          />
          <span class="text-foreground-muted">Sample Rate</span>
          <xui-select
            class="w-40"
            aria-label="Sample rate"
            data-testid="bounce-rate"
            [items]="rates()"
            [itemText]="labelOf"
            [filterable]="false"
            [value]="rate()"
            (valueChange)="patch({ sampleRate: $event?.value ?? null })"
          />
          <span class="text-foreground-muted">Dither</span>
          <xui-select
            class="w-40"
            aria-label="Dither"
            data-testid="bounce-dither"
            [items]="dithers"
            [itemText]="labelOf"
            [filterable]="false"
            [disabled]="form().bitDepth === 32"
            [value]="dither()"
            (valueChange)="patch({ dither: $event?.value ?? 'none' })"
          />
          <span class="text-foreground-muted">Normalize</span>
          <xui-select
            class="w-48"
            aria-label="Normalize"
            data-testid="bounce-normalize"
            [items]="normalizations"
            [itemText]="labelOf"
            [filterable]="false"
            [value]="normalize()"
            (valueChange)="patch({ normalize: $event?.value ?? 'off' })"
          />
          @if (form().normalize === 'loudness') {
            <span class="text-foreground-muted">Loudness</span>
            <div class="flex items-center gap-2">
              <xui-numeric-input
                class="w-24"
                size="sm"
                aria-label="Loudness target in LUFS"
                data-testid="bounce-lufs"
                [min]="-70"
                [max]="0"
                [stepSize]="0.5"
                [value]="form().loudnessLufs"
                (valueChange)="patch({ loudnessLufs: $event ?? -14 })"
              />
              <span class="text-foreground-muted">LUFS (integrated)</span>
            </div>
          }
          @if (form().normalize !== 'off') {
            <span class="text-foreground-muted">True Peak</span>
            <div class="flex items-center gap-2">
              <xui-numeric-input
                class="w-24"
                size="sm"
                aria-label="True-peak ceiling in dB"
                data-testid="bounce-ceiling"
                [min]="-20"
                [max]="0"
                [stepSize]="0.1"
                [value]="form().peakCeilingDb"
                (valueChange)="patch({ peakCeilingDb: $event ?? -1 })"
              />
              <span class="text-foreground-muted">dBTP ceiling</span>
            </div>
          }
        </fieldset>

        <div class="grid grid-cols-[7rem_1fr] items-center gap-x-3 gap-y-2.5">
          <span class="text-foreground-muted">Mode</span>
          <xui-segmented-control
            size="sm"
            aria-label="Bounce mode"
            data-testid="bounce-mode"
            [options]="modes"
            [value]="form().realtime ? 'realtime' : 'offline'"
            (valueChange)="patch({ realtime: $event === 'realtime' })"
          />
          <span></span>
          <label class="text-foreground flex items-center gap-2">
            <input
              type="checkbox"
              class="accent-(--color-primary)"
              data-testid="bounce-add-to-project"
              [checked]="form().addToProject"
              (change)="patch({ addToProject: !form().addToProject })"
            />
            Add to Project
          </label>
        </div>

        <p class="text-foreground-muted" data-testid="bounce-files">Writes {{ files().join(', ') }}</p>
        @if (problems().length > 0 && touched()) {
          <p class="text-error" role="alert">{{ problems().join(' ') }}</p>
        }
        @if (job(); as j) {
          <div class="flex items-center gap-3" data-testid="bounce-progress">
            <div
              class="well h-2 flex-1 overflow-hidden rounded-full"
              role="progressbar"
              aria-label="Bounce progress"
              aria-valuemin="0"
              aria-valuemax="100"
              [attr.aria-valuenow]="percent()"
            >
              <div class="bg-primary h-full transition-[width] duration-200" [style.width.%]="percent()"></div>
            </div>
            <span class="readout text-foreground w-24 text-end">{{ stateText() }}</span>
          </div>
        }
      </xui-dialog-body>
      <xui-dialog-footer>
        @if (running()) {
          <button xuiButton type="button" variant="ghost" data-testid="bounce-cancel" (click)="cancel()">
            Cancel Bounce
          </button>
        } @else {
          <button xuiButton type="button" variant="ghost" (click)="ui.bounceOpen.set(false)">Cancel</button>
        }
        <button xuiButton type="button" data-testid="bounce-start-button" [disabled]="running()" (click)="bounce()">
          Bounce
        </button>
      </xui-dialog-footer>
    </xui-dialog>
  `
})
export class BounceDialog {
  protected readonly ui = inject(UiStore);
  protected readonly project = inject(ProjectStore);
  private readonly jobs = inject(JobsStore);
  private readonly dialogs = inject(ConnectionService).desktop?.dialogs;

  protected readonly chooseFolder = typeof this.dialogs?.chooseFolder === 'function';
  protected readonly sources = [
    { value: 'mix' as const, label: 'Mix' },
    { value: 'stems' as const, label: 'Stems' }
  ];
  protected readonly formats = [
    { value: 'wav' as const, label: 'WAV' },
    { value: 'aiff' as const, label: 'AIFF' }
  ];
  protected readonly modes = [
    { value: 'offline', label: 'Offline' },
    { value: 'realtime', label: 'Realtime' }
  ];
  protected readonly bitDepths = BitDepths;
  protected readonly dithers = Dithers;
  protected readonly normalizations = Normalizations;
  protected readonly labelOf = (item: { label: string }) => item.label;

  protected readonly form = signal<BounceForm>(this.defaults());
  protected readonly touched = signal(false);
  protected readonly tracks = computed(() => stemTracks(this.project.trackList()));
  protected readonly problems = computed(() => bounceProblems(this.form()));
  protected readonly files = computed(() => bounceFileNames(this.form(), this.project.trackList()));
  protected readonly rates = computed(() => {
    const own = this.project.info()?.sampleRate ?? 48_000;
    return [
      { value: null, label: `Project (${kHz(own)})` },
      ...SampleRates.map(rate => ({ value: rate, label: kHz(rate) }))
    ];
  });
  protected readonly bitDepth = computed(() => BitDepths.find(b => b.value === this.form().bitDepth) ?? null);
  protected readonly rate = computed(() => this.rates().find(r => r.value === this.form().sampleRate) ?? null);
  protected readonly dither = computed(() => Dithers.find(d => d.value === this.form().dither) ?? null);
  protected readonly normalize = computed(() => Normalizations.find(n => n.value === this.form().normalize) ?? null);

  /** The job this window started (it follows only its own). */
  private readonly jobId = signal<string | null>(null);
  protected readonly job = computed(() => {
    const id = this.jobId();
    return id ? (this.jobs.jobs().get(id) ?? null) : null;
  });
  protected readonly running = computed(() => isActive(this.job()));
  protected readonly percent = computed(() => Math.round((this.job()?.progress ?? 0) * 100));
  protected readonly stateText = computed(() => {
    const job = this.job();
    switch (job?.state) {
      case 'queued':
        return 'Waiting…';
      case 'running':
        return `${this.percent()} %`;
      case 'done':
        return 'Done';
      case 'failed':
        return 'Failed';
      case 'cancelled':
        return 'Cancelled';
      default:
        return '';
    }
  });

  constructor() {
    // Each time the window opens it starts from the project as it is now (its name, the cycle).
    effect(() => {
      if (this.ui.bounceOpen()) {
        untracked(() => {
          if (!this.running()) {
            this.form.set(this.defaults());
            this.jobId.set(null);
            this.touched.set(false);
          }
        });
      }
    });
    // Done: the window closes (the toast says where the file went).
    effect(() => {
      if (this.job()?.state === 'done') {
        untracked(() => this.ui.bounceOpen.set(false));
      }
    });
  }

  protected patch(change: Partial<BounceForm>): void {
    this.form.update(form => ({ ...form, ...change }));
  }

  protected toggleStem(id: string): void {
    const ids = this.form().trackIds;
    this.patch({ trackIds: ids.includes(id) ? ids.filter(t => t !== id) : [...ids, id] });
  }

  protected async pickFolder(): Promise<void> {
    const folder = await this.dialogs?.chooseFolder();
    if (folder) {
      this.patch({ directory: folder });
    }
  }

  protected async bounce(): Promise<void> {
    this.touched.set(true);
    if (this.problems().length > 0) {
      return;
    }
    const job = await this.jobs.bounce(bounceRequest(this.form(), this.project.meter()));
    if (job) {
      this.jobId.set(job.id);
    }
  }

  protected cancel(): void {
    const id = this.jobId();
    if (id) {
      void this.jobs.cancel(id);
    }
  }

  private defaults(): BounceForm {
    return defaultBounceForm(
      this.project.info()?.name ?? 'Bounce',
      this.project.settings(),
      this.project.trackList(),
      this.project.meter()
    );
  }
}

function kHz(rate: number): string {
  return `${(rate / 1000).toLocaleString('en', { maximumFractionDigits: 1 })} kHz`;
}
