import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { AudioRegion, EditCommand, FlexFollow, TakeFolder } from '@anode/api-client';
import { AnFadeCurve } from '@anode/audio-ui';
import { XuiNumericInputImports } from '@xui/numeric-input';
import { QuantizeGrids } from '../midi/midi-math';
import { canPitch } from '../music/region-timing';
import { FlexStore, FollowModes } from '../state/flex.store';
import { ProjectStore } from '../state/project.store';

/** A native select in the Inspector's dense grid, on the field surface. */
const Field =
  'border-border bg-surface-inset text-foreground focus-visible:outline-focus h-6 min-w-0 rounded-[5px] border px-1 text-xs focus-visible:outline-2';

const Band =
  'text-foreground border-hairline bg-surface-raised -mx-3 truncate border-y px-3 py-1 text-[11px] font-bold shadow-[inset_0_1px_0_var(--highlight)]';

/**
 * The Region inspector for an audio region (Logic's): mute; Quantize (audio: flex markers on its transients moved to the
 * grid, with strength and swing; Q); Flex & Follow, transpose and fine tune (which need Flex or Follow, as in Logic);
 * gain, and the fades: their lengths in milliseconds and their curves. Each change is one undo step.
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
      <label class="text-foreground-muted" for="quantize-audio-grid">Quantize</label>
      <div class="flex min-w-0 items-center gap-1">
        <select
          id="quantize-audio-grid"
          [class]="field + ' flex-1'"
          aria-label="Quantize audio"
          data-testid="quantize-audio-grid"
          [value]="flex.quantizeGrid()"
          (change)="setGrid($event)"
        >
          <option value="0">Off</option>
          @for (grid of grids; track grid.ticks) {
            <option [value]="grid.ticks">{{ grid.label }}</option>
          }
        </select>
        <button
          type="button"
          class="bezel text-foreground h-6 shrink-0 px-2 text-[11px] font-semibold"
          data-testid="quantize-audio"
          aria-label="Quantize audio now"
          title="Quantize the selected audio regions (Q)"
          [disabled]="flex.quantizeGrid() === 0"
          (click)="quantize()"
        >
          Q
        </button>
      </div>
      <span class="text-foreground-muted">Q-Strength</span>
      <xui-numeric-input
        size="sm"
        aria-label="Quantize strength, percent"
        data-testid="quantize-audio-strength"
        [min]="0"
        [max]="100"
        [stepSize]="5"
        [majorStepSize]="25"
        [value]="percent(flex.quantizeStrength())"
        (valueChange)="$event !== null && setStrength($event)"
      />
      <span class="text-foreground-muted">Q-Swing</span>
      <xui-numeric-input
        size="sm"
        aria-label="Quantize swing, percent"
        data-testid="quantize-audio-swing"
        [min]="0"
        [max]="100"
        [stepSize]="5"
        [majorStepSize]="25"
        [value]="percent(flex.quantizeSwing())"
        (valueChange)="$event !== null && setSwing($event)"
      />
      <label class="text-foreground-muted" for="region-follow">Flex &amp; Follow</label>
      <select
        id="region-follow"
        [class]="field"
        aria-label="Flex and Follow"
        data-testid="region-follow"
        [value]="region().follow"
        (change)="setFollow($event)"
      >
        @for (mode of follows; track mode.value) {
          <option [value]="mode.value">{{ mode.label }}</option>
        }
      </select>
      <span class="text-foreground-muted">Transpose</span>
      <xui-numeric-input
        size="sm"
        aria-label="Region transpose, semitones"
        data-testid="region-transpose"
        [min]="-36"
        [max]="36"
        [stepSize]="1"
        [majorStepSize]="12"
        [disabled]="!pitchable()"
        [value]="region().transpose"
        (valueChange)="$event !== null && $event !== region().transpose && flex.setTranspose([region().id], $event)"
      />
      <span class="text-foreground-muted">Fine Tune</span>
      <xui-numeric-input
        size="sm"
        aria-label="Region fine tune, cents"
        data-testid="region-fine-tune"
        [min]="-50"
        [max]="50"
        [stepSize]="1"
        [majorStepSize]="10"
        [disabled]="!pitchable()"
        [value]="region().fineTune"
        (valueChange)="$event !== null && $event !== region().fineTune && flex.setFineTune([region().id], $event)"
      />
      @if (!pitchable()) {
        <span></span>
        <span class="text-foreground-muted text-[11px] leading-tight"
          >Transpose and fine tune need Flex or Follow.</span
        >
      }
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
  protected readonly flex = inject(FlexStore);
  readonly region = input.required<AudioRegion>();
  protected readonly band = Band;
  protected readonly field = Field;
  protected readonly grids = QuantizeGrids;
  protected readonly follows = FollowModes;
  /** Whether transpose and fine tune apply: the track's flex on, or Flex & Follow. */
  protected readonly pitchable = computed(() =>
    canPitch(this.region(), this.project.track(this.region().trackId)?.flexOn ?? false)
  );

  protected percent(value: number): number {
    return Math.round(value * 100);
  }

  /** Quantize's grid: quantises now; Off takes the flex markers away. */
  protected setGrid(event: Event): void {
    const ticks = Number((event.target as HTMLSelectElement).value);
    this.flex.quantizeGrid.set(ticks);
    if (ticks > 0) {
      this.quantize();
    } else if (this.region().flexMarkers.length > 0) {
      void this.flex.setMarkers(this.region().id, [], 'Quantize Off');
    }
  }

  protected setStrength(percent: number): void {
    this.flex.quantizeStrength.set(Math.min(100, Math.max(0, percent)) / 100);
    this.quantize();
  }

  protected setSwing(percent: number): void {
    this.flex.quantizeSwing.set(Math.min(100, Math.max(0, percent)) / 100);
    this.quantize();
  }

  /** Quantises this region at the grid (when one is chosen). */
  protected quantize(): void {
    const grid = this.flex.quantizeGrid();
    if (grid > 0) {
      void this.flex.quantize([this.region().id], grid, this.flex.quantizeStrength(), this.flex.quantizeSwing());
    }
  }

  protected setFollow(event: Event): void {
    const follow = (event.target as HTMLSelectElement).value as FlexFollow;
    if (follow !== this.region().follow) {
      void this.flex.setFollow([this.region().id], follow);
    }
  }
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
