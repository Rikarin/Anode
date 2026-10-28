import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import type {
  BounceInPlaceDestination,
  BounceInPlaceRequest,
  BounceSourceAction,
  NormalizeMode
} from '@anode/api-client';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiInputImports } from '@xui/input';
import { XuiSegmentedControlImports } from '@xui/segmented-control';
import { XuiSelectImports } from '@xui/select';
import { Normalizations } from '../export/bounce';
import { isActive, JobsStore } from '../state/jobs.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';

interface InPlaceForm {
  destination: BounceInPlaceDestination;
  source: BounceSourceAction;
  bypassEffects: boolean;
  includeTail: boolean;
  includeVolumePan: boolean;
  normalize: NormalizeMode;
  name: string;
}

/**
 * Logic's Bounce in Place (⌃B for regions; a track's menu for the whole track): renders the selected regions (or, with
 * none selected, the selected tracks) to a new track or in place of the regions, leaving, muting or deleting the
 * source, with or without the effect plug-ins, their tail and the volume and pan. It runs as a Server job; the window
 * shows its progress and closes when the new audio is in (one undo step).
 */
@Component({
  selector: 'an-bounce-in-place-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiInputImports, XuiSegmentedControlImports, XuiSelectImports],
  template: `
    <xui-dialog [(open)]="ui.bounceInPlaceOpen" title="Bounce in Place" [canOutsideClickClose]="false">
      <xui-dialog-body class="flex flex-col gap-4 text-xs" data-testid="bounce-in-place-dialog">
        <p class="text-foreground" data-testid="bip-what">{{ what() }}</p>
        <div class="grid grid-cols-[7rem_1fr] items-center gap-x-3 gap-y-2.5">
          <label class="text-foreground-muted" for="an-bip-name">Name</label>
          <input
            #name
            id="an-bip-name"
            xuiInput
            class="h-7 min-w-0 text-xs"
            data-testid="bip-name"
            [placeholder]="defaultName()"
            [value]="form().name"
            (input)="patch({ name: name.value })"
          />
          <span class="text-foreground-muted">Destination</span>
          <xui-segmented-control
            size="sm"
            ariaLabel="Destination"
            data-testid="bip-destination"
            [options]="destinations"
            [value]="form().destination"
            (valueChange)="patch({ destination: $event ?? 'newTrack' })"
          />
          <span class="text-foreground-muted">Source</span>
          <xui-segmented-control
            size="sm"
            ariaLabel="Source"
            data-testid="bip-source"
            [options]="sources"
            [value]="form().source"
            [disabled]="form().destination === 'replace'"
            (valueChange)="patch({ source: $event ?? 'mute' })"
          />
          <span></span>
          <label class="text-foreground flex items-center gap-2">
            <input
              type="checkbox"
              class="accent-(--color-primary)"
              data-testid="bip-bypass-effects"
              [checked]="form().bypassEffects"
              (change)="patch({ bypassEffects: !form().bypassEffects })"
            />
            Bypass Effect Plug-ins
          </label>
          <span></span>
          <label class="text-foreground flex items-center gap-2">
            <input
              type="checkbox"
              class="accent-(--color-primary)"
              data-testid="bip-include-tail"
              [checked]="form().includeTail"
              [disabled]="form().bypassEffects"
              (change)="patch({ includeTail: !form().includeTail })"
            />
            Include Audio Tail
          </label>
          <span></span>
          <label class="text-foreground flex items-center gap-2">
            <input
              type="checkbox"
              class="accent-(--color-primary)"
              data-testid="bip-include-volume-pan"
              [checked]="form().includeVolumePan"
              (change)="patch({ includeVolumePan: !form().includeVolumePan })"
            />
            Include Volume/Pan
          </label>
          <span class="text-foreground-muted">Normalize</span>
          <xui-select
            class="w-48"
            aria-label="Normalize"
            data-testid="bip-normalize"
            [items]="normalizations"
            [itemText]="labelOf"
            [filterable]="false"
            [value]="normalize()"
            (valueChange)="patch({ normalize: $event?.value ?? 'off' })"
          />
        </div>
        @if (job(); as j) {
          <div class="flex items-center gap-3" data-testid="bip-progress">
            <div
              class="well h-2 flex-1 overflow-hidden rounded-full"
              role="progressbar"
              aria-label="Bounce in place progress"
              aria-valuemin="0"
              aria-valuemax="100"
              [attr.aria-valuenow]="percent()"
            >
              <div class="bg-primary h-full transition-[width] duration-200" [style.width.%]="percent()"></div>
            </div>
            <span class="readout text-foreground w-20 text-end">{{
              j.state === 'failed' ? 'Failed' : percent() + ' %'
            }}</span>
          </div>
        }
      </xui-dialog-body>
      <xui-dialog-footer>
        @if (running()) {
          <button xuiButton type="button" variant="ghost" (click)="cancel()">Cancel Bounce</button>
        } @else {
          <button xuiButton type="button" variant="ghost" (click)="ui.bounceInPlaceOpen.set(false)">Cancel</button>
        }
        <button xuiButton type="button" data-testid="bip-apply" [disabled]="running() || nothing()" (click)="bounce()">
          OK
        </button>
      </xui-dialog-footer>
    </xui-dialog>
  `
})
export class BounceInPlaceDialog {
  protected readonly ui = inject(UiStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly jobs = inject(JobsStore);

  protected readonly destinations = [
    { value: 'newTrack' as const, label: 'New Track' },
    { value: 'replace' as const, label: 'Replace Track' }
  ];
  protected readonly sources = [
    { value: 'leave' as const, label: 'Leave' },
    { value: 'mute' as const, label: 'Mute' },
    { value: 'delete' as const, label: 'Delete' }
  ];
  protected readonly normalizations = Normalizations;
  protected readonly labelOf = (item: { label: string }) => item.label;

  protected readonly form = signal<InPlaceForm>(defaults());
  protected readonly normalize = computed(() => Normalizations.find(n => n.value === this.form().normalize) ?? null);

  /** The selected regions (audio and MIDI), or else the selected tracks. */
  private readonly regionIds = computed(() => {
    const p = this.project;
    return [...this.selection.regions()].filter(id => p.regions().has(id) || p.midiRegions().has(id));
  });
  private readonly trackIds = computed(() =>
    this.regionIds().length > 0
      ? []
      : this.selection.trackSelection().filter(id => {
          const kind = this.project.track(id)?.kind;
          return kind !== undefined && kind !== 'stack' && kind !== 'vca';
        })
  );
  protected readonly nothing = computed(() => this.regionIds().length === 0 && this.trackIds().length === 0);
  protected readonly what = computed(() => {
    const regions = this.regionIds().length;
    if (regions > 0) {
      return `Bounces ${regions} selected ${regions === 1 ? 'region' : 'regions'}.`;
    }
    const tracks = this.trackIds().map(id => this.project.track(id)?.name ?? id);
    return tracks.length > 0
      ? `Bounces ${tracks.length === 1 ? 'the track' : 'the tracks'} ${tracks.join(', ')}.`
      : 'Select regions or a track to bounce in place.';
  });
  protected readonly defaultName = computed(() => {
    const first = this.regionIds()[0];
    const trackId = first
      ? (this.project.regions().get(first)?.trackId ?? this.project.midiRegion(first)?.trackId)
      : this.trackIds()[0];
    return `${(trackId && this.project.track(trackId)?.name) || 'Track'} Bounce`;
  });

  protected readonly job = this.jobs.inPlaceJob;
  protected readonly running = computed(() => isActive(this.job()));
  protected readonly percent = computed(() => Math.round((this.job()?.progress ?? 0) * 100));

  constructor() {
    effect(() => {
      if (this.ui.bounceInPlaceOpen()) {
        untracked(() => {
          if (!this.running()) {
            this.form.set(defaults());
            this.jobs.inPlaceId.set(null);
          }
        });
      }
    });
    effect(() => {
      if (this.job()?.state === 'done') {
        untracked(() => {
          this.ui.bounceInPlaceOpen.set(false);
          this.jobs.inPlaceId.set(null);
        });
      }
    });
  }

  protected patch(change: Partial<InPlaceForm>): void {
    this.form.update(form => ({ ...form, ...change }));
  }

  protected async bounce(): Promise<void> {
    const form = this.form();
    const request: BounceInPlaceRequest = {
      trackIds: this.trackIds().length > 0 ? this.trackIds() : null,
      regionIds: this.regionIds().length > 0 ? this.regionIds() : null,
      destination: form.destination,
      source: form.source,
      bypassEffects: form.bypassEffects,
      includeTail: form.includeTail && !form.bypassEffects,
      includeVolumePan: form.includeVolumePan,
      normalize: form.normalize,
      name: form.name.trim() || null
    };
    await this.jobs.bounceInPlace(request);
  }

  protected cancel(): void {
    const id = this.jobs.inPlaceId();
    if (id) {
      void this.jobs.cancel(id);
    }
  }
}

function defaults(): InPlaceForm {
  return {
    destination: 'newTrack',
    source: 'mute',
    bypassEffects: false,
    includeTail: true,
    includeVolumePan: false,
    normalize: 'off',
    name: ''
  };
}
