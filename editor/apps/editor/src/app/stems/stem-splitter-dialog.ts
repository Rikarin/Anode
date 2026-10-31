import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import type { StemKind, StemSplitRequest } from '@anode/api-client';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { Notify } from '../core/notify';
import { isActive, JobsStore } from '../state/jobs.store';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';

/** The stems, in the order the Stem Splitter adds their tracks (ADR-0010). */
export const Stems: readonly { kind: StemKind; label: string }[] = [
  { kind: 'vocals', label: 'Vocals' },
  { kind: 'drums', label: 'Drums' },
  { kind: 'bass', label: 'Bass' },
  { kind: 'guitar', label: 'Guitar' },
  { kind: 'piano', label: 'Piano' },
  { kind: 'other', label: 'Other' }
];

export type StemSet = 'four' | 'six' | 'custom';

const FourStems: readonly StemKind[] = ['vocals', 'drums', 'bass', 'other'];
const SixStems: readonly StemKind[] = Stems.map(s => s.kind);

/** The stems a choice asks for, in stem order. */
export function stemsOf(set: StemSet, custom: ReadonlySet<StemKind>): StemKind[] {
  const list = set === 'four' ? FourStems : set === 'six' ? SixStems : SixStems.filter(s => custom.has(s));
  return [...list];
}

/** The request for a choice: four stems leave the list out (the Server's default). */
export function stemRequest(set: StemSet, custom: ReadonlySet<StemKind>, keepSource: boolean): StemSplitRequest {
  return set === 'four' ? { stems: null, keepSource } : { stems: stemsOf(set, custom), keepSource };
}

/**
 * The Stem Splitter (Edit ▸ Stem Splitter…, or an audio region's context menu; docs/09-phase-8.md, ADR-0010): four
 * stems (vocals, drums, bass, other), six (adding guitar and piano) or a custom selection, and whether the source region
 * keeps playing. Split runs a `stemSplit` job (the first use of a model downloads it, which its progress says); when it
 * is done the stems arrive as new tracks below the region's, one undo step.
 */
@Component({
  selector: 'an-stem-splitter-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports],
  template: `
    <xui-dialog [open]="open()" (openChange)="openChange($event)" title="Stem Splitter" [canOutsideClickClose]="false">
      <xui-dialog-body class="flex flex-col gap-4 text-xs" data-testid="stem-splitter">
        @if (region(); as r) {
          <p class="text-foreground">
            Split <strong>{{ r.name }}</strong> into stems, each on a new track below <strong>{{ trackName() }}</strong
            >.
          </p>
          <fieldset class="flex flex-col gap-2" [disabled]="running()">
            <legend class="text-foreground-muted mb-1 text-[10px] font-semibold tracking-wide uppercase">Stems</legend>
            @for (choice of sets; track choice.value) {
              <label class="text-foreground flex items-center gap-2">
                <input
                  type="radio"
                  name="stem-set"
                  class="accent-(--color-primary)"
                  [attr.data-testid]="'stem-set-' + choice.value"
                  [checked]="set() === choice.value"
                  (change)="set.set(choice.value)"
                />
                {{ choice.label }}
              </label>
            }
            @if (set() === 'custom') {
              <div class="grid grid-cols-3 gap-x-4 gap-y-1.5 ps-6" role="group" aria-label="Custom stems">
                @for (stem of stems; track stem.kind) {
                  <label class="text-foreground flex items-center gap-2">
                    <input
                      type="checkbox"
                      class="accent-(--color-primary)"
                      [attr.data-testid]="'stem-' + stem.kind"
                      [checked]="custom().has(stem.kind)"
                      (change)="toggle(stem.kind)"
                    />
                    {{ stem.label }}
                  </label>
                }
              </div>
            }
          </fieldset>
          <label class="text-foreground flex items-center gap-2">
            <input
              type="checkbox"
              class="accent-(--color-primary)"
              data-testid="stem-keep-source"
              [disabled]="running()"
              [checked]="keepSource()"
              (change)="keepSource.set(!keepSource())"
            />
            Keep the source region playing (else it is muted)
          </label>
          @if (job(); as j) {
            <div class="flex flex-col gap-1.5" data-testid="stem-progress">
              <div class="flex items-center gap-3">
                <div
                  class="well h-2 flex-1 overflow-hidden rounded-full"
                  role="progressbar"
                  aria-label="Stem Splitter progress"
                  aria-valuemin="0"
                  aria-valuemax="100"
                  [attr.aria-valuenow]="percent()"
                  [attr.aria-valuetext]="j.message ?? percent() + ' %'"
                >
                  <div class="bg-primary h-full transition-[width] duration-200" [style.width.%]="percent()"></div>
                </div>
                <span class="readout text-foreground w-20 text-end">{{
                  j.state === 'failed' ? 'Failed' : j.state === 'cancelled' ? 'Stopped' : percent() + ' %'
                }}</span>
              </div>
              <span class="text-foreground-muted" role="status" data-testid="stem-stage">{{ stage() }}</span>
            </div>
          }
        } @else {
          <p class="text-foreground-muted">Select an audio region to split into stems.</p>
        }
      </xui-dialog-body>
      <xui-dialog-footer>
        @if (running()) {
          <button xuiButton type="button" variant="outline" data-testid="stem-stop" (click)="stop()">Stop</button>
        } @else {
          <button xuiButton type="button" variant="ghost" (click)="close()">Cancel</button>
        }
        <button
          xuiButton
          type="button"
          data-testid="stem-split"
          [disabled]="running() || !region() || chosen().length === 0"
          (click)="split()"
        >
          Split
        </button>
      </xui-dialog-footer>
    </xui-dialog>
  `
})
export class StemSplitterDialog {
  protected readonly ui = inject(UiStore);
  private readonly project = inject(ProjectStore);
  private readonly jobs = inject(JobsStore);
  private readonly notify = inject(Notify);

  protected readonly stems = Stems;
  protected readonly sets: readonly { value: StemSet; label: string }[] = [
    { value: 'four', label: 'Four stems: vocals, drums, bass, other' },
    { value: 'six', label: 'Six stems: adds guitar and piano' },
    { value: 'custom', label: 'Custom' }
  ];
  protected readonly set = signal<StemSet>('four');
  protected readonly custom = signal<ReadonlySet<StemKind>>(new Set(['vocals', 'drums', 'bass', 'other']));
  protected readonly keepSource = signal(false);
  protected readonly chosen = computed(() => stemsOf(this.set(), this.custom()));

  protected readonly open = computed(() => this.ui.stemSplitter() !== null);
  protected readonly region = computed(() => {
    const id = this.ui.stemSplitter()?.regionId;
    return id ? (this.project.regions().get(id) ?? null) : null;
  });
  protected readonly trackName = computed(() => {
    const region = this.region();
    return region ? (this.project.track(region.trackId)?.name ?? '') : '';
  });
  protected readonly job = this.jobs.stemJob;
  protected readonly running = computed(() => isActive(this.job()));
  protected readonly percent = computed(() => Math.round((this.job()?.progress ?? 0) * 100));
  protected readonly stage = computed(() => {
    const job = this.job();
    if (!job) {
      return '';
    }
    switch (job.state) {
      case 'queued':
        return 'Waiting to start…';
      case 'running':
        return job.message ?? 'Separating stems…';
      case 'failed':
        return job.message ?? 'The split failed.';
      case 'cancelled':
        return 'Stopped: no tracks were added.';
      case 'done':
        return 'Done.';
    }
  });

  constructor() {
    effect(() => {
      if (this.open()) {
        untracked(() => {
          if (!this.running()) {
            this.jobs.stemId.set(null);
          }
        });
      }
    });
    effect(() => {
      const job = this.job();
      if (job?.state === 'done') {
        untracked(() => {
          const n = job.files.length;
          this.notify.success(`Split into ${n} ${n === 1 ? 'stem' : 'stems'}`);
          this.jobs.stemId.set(null);
          this.ui.stemSplitter.set(null);
        });
      }
    });
  }

  protected toggle(kind: StemKind): void {
    this.custom.update(set => {
      const next = new Set(set);
      if (!next.delete(kind)) {
        next.add(kind);
      }
      return next;
    });
  }

  protected async split(): Promise<void> {
    const region = this.region();
    if (region) {
      await this.jobs.splitStems(region.id, stemRequest(this.set(), this.custom(), this.keepSource()));
    }
  }

  protected stop(): void {
    const job = this.job();
    if (job) {
      void this.jobs.cancel(job.id);
    }
  }

  protected openChange(open: boolean): void {
    if (!open) {
      this.close();
    }
  }

  /** Closing leaves a running split running (its tracks still arrive); the jobs UI follows it. */
  protected close(): void {
    this.ui.stemSplitter.set(null);
  }
}
