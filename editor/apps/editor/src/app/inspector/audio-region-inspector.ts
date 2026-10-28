import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { AudioRegion, EditCommand, TakeFolder } from '@anode/api-client';
import { AnFadeCurve } from '@anode/audio-ui';
import { XuiNumericInputImports } from '@xui/numeric-input';
import { ProjectStore } from '../state/project.store';

const Band =
  'text-foreground border-hairline bg-surface-raised -mx-3 truncate border-y px-3 py-1 text-[11px] font-bold shadow-[inset_0_1px_0_var(--highlight)]';

/**
 * The Region inspector for an audio region (Logic's): mute, gain, and the fades: their lengths in milliseconds and their
 * curves. Each change is one `setRegion` batch.
 */
@Component({
  selector: 'an-audio-region-inspector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnFadeCurve, XuiNumericInputImports],
  host: { class: 'flex flex-col gap-2', role: 'group', '[attr.aria-label]': '"Region: " + region().name' },
  template: `
    <h3 [class]="band" [attr.title]="region().name">Region: {{ region().name }}</h3>
    <div class="grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-1.5 text-xs">
      <span class="text-foreground-muted">Mute</span>
      <button
        type="button"
        class="state-toggle toggle-mute justify-self-start"
        aria-label="Mute region"
        data-testid="region-mute"
        [attr.aria-pressed]="region().muted"
        (click)="set({ muted: !region().muted }, region().muted ? 'Unmute Region' : 'Mute Region')"
      >
        M
      </button>
      <span class="text-foreground-muted">Gain</span>
      <xui-numeric-input
        size="sm"
        aria-label="Region gain in dB"
        data-testid="region-gain"
        [min]="-96"
        [max]="30"
        [stepSize]="0.5"
        [majorStepSize]="6"
        [value]="region().gainDb"
        (valueChange)="$event !== null && set({ gainDb: $event }, 'Region Gain')"
      />
      <span class="text-foreground-muted">Fade In</span>
      <xui-numeric-input
        size="sm"
        aria-label="Fade in, ms"
        [min]="0"
        [stepSize]="5"
        [majorStepSize]="50"
        [value]="ms(region().fadeIn)"
        (valueChange)="$event !== null && set({ fadeIn: frames($event) }, 'Fade In')"
      />
      <span class="text-foreground-muted">Fade Out</span>
      <xui-numeric-input
        size="sm"
        aria-label="Fade out, ms"
        [min]="0"
        [stepSize]="5"
        [majorStepSize]="50"
        [value]="ms(region().fadeOut)"
        (valueChange)="$event !== null && set({ fadeOut: frames($event) }, 'Fade Out')"
      />
    </div>
    <div class="flex justify-around pt-1">
      <an-fade-curve
        label="In Curve"
        data-testid="fade-in-curve"
        [disabled]="region().fadeIn === 0"
        [value]="region().fadeInCurve"
        (committed)="set({ fadeInCurve: $event }, 'Fade Curve')"
      />
      <an-fade-curve
        label="Out Curve"
        out
        data-testid="fade-out-curve"
        [disabled]="region().fadeOut === 0"
        [value]="region().fadeOutCurve"
        (committed)="set({ fadeOutCurve: $event }, 'Fade Curve')"
      />
    </div>
  `
})
export class AudioRegionInspector {
  private readonly project = inject(ProjectStore);
  readonly region = input.required<AudioRegion>();
  protected readonly band = Band;
  private readonly rate = computed(() => this.project.info()?.sampleRate ?? 48_000);

  protected ms(frames: number): number {
    return Math.round((frames / this.rate()) * 1000);
  }

  protected frames(ms: number): number {
    return Math.max(0, Math.round((ms / 1000) * this.rate()));
  }

  protected set(change: Partial<Extract<EditCommand, { type: 'setRegion' }>>, label: string): void {
    const region = this.region();
    const changed = Object.entries(change).some(([key, value]) => region[key as keyof AudioRegion] !== value);
    if (changed) {
      void this.project.execute([{ type: 'setRegion', regionId: region.id, ...change }], label);
    }
  }
}

/** The Region inspector for a take folder: its name, mute, the crossfade at comp boundaries, and its takes. */
@Component({
  selector: 'an-take-folder-inspector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiNumericInputImports],
  host: { class: 'flex flex-col gap-2', role: 'group', '[attr.aria-label]': '"Take folder: " + folder().name' },
  template: `
    <h3 [class]="band" [attr.title]="folder().name">Take Folder: {{ folder().name }}</h3>
    <div class="grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-1.5 text-xs">
      <span class="text-foreground-muted">Mute</span>
      <button
        type="button"
        class="state-toggle toggle-mute justify-self-start"
        aria-label="Mute take folder"
        [attr.aria-pressed]="folder().muted"
        (click)="setFolder({ muted: !folder().muted }, folder().muted ? 'Unmute Take Folder' : 'Mute Take Folder')"
      >
        M
      </button>
      <span class="text-foreground-muted">Crossfade</span>
      <xui-numeric-input
        size="sm"
        aria-label="Comp crossfade, ms"
        [min]="0"
        [max]="500"
        [stepSize]="1"
        [majorStepSize]="10"
        [value]="ms(folder().crossfade)"
        (valueChange)="$event !== null && setFolder({ crossfade: frames($event) }, 'Comp Crossfade')"
      />
      <span class="text-foreground-muted">Takes</span>
      <span class="text-foreground readout">{{ folder().takes.length }}</span>
    </div>
  `
})
export class TakeFolderInspector {
  private readonly project = inject(ProjectStore);
  readonly folder = input.required<TakeFolder>();
  protected readonly band = Band;
  private readonly rate = computed(() => this.project.info()?.sampleRate ?? 48_000);

  protected ms(frames: number): number {
    return Math.round((frames / this.rate()) * 1000);
  }

  protected frames(ms: number): number {
    return Math.max(0, Math.round((ms / 1000) * this.rate()));
  }

  protected setFolder(change: { muted?: boolean; crossfade?: number }, label: string): void {
    const folder = this.folder();
    const differs =
      (change.muted !== undefined && change.muted !== folder.muted) ||
      (change.crossfade !== undefined && change.crossfade !== folder.crossfade);
    if (differs) {
      void this.project.execute([{ type: 'setTakeFolder', folderId: folder.id, ...change }], label);
    }
  }
}
