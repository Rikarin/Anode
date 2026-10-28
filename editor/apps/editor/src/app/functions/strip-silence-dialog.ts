import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import type { AudioRegion } from '@anode/api-client';
import type { Waveform } from '@anode/timeline';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiInputImports } from '@xui/input';
import { PeaksCache } from '../state/peaks';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';
import { DefaultSilence, envelopeOf, type SilenceSettings, silenceStretches } from './strip-silence';

/** How many regions the window previews (it strips them all). */
const PreviewRegions = 4;
/** The preview's width in SVG units (one per envelope block). */
const PreviewBlocks = 400;

interface Preview {
  region: AudioRegion;
  /** The envelope as an SVG path (0…1 tall, mirrored about the middle). */
  path: string;
  /** Kept stretches, as fractions of the region. */
  keep: { x: number; width: number }[];
  count: number;
}

/**
 * Logic's Strip Silence (⌃X): splits the selected audio regions at their silences, by a threshold, a minimum silence
 * time, and a pre-attack and post-release that widen what is kept. The window previews the result on each region's
 * waveform from its peaks as the values change; OK sends one StripSilenceCommand (one undo step), and the Server
 * decides on its own analysis.
 */
@Component({
  selector: 'an-strip-silence-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiInputImports],
  template: `
    <xui-dialog [(open)]="ui.stripSilenceOpen" title="Strip Silence" size="lg" [canOutsideClickClose]="false">
      <xui-dialog-body class="flex flex-col gap-4 text-xs" data-testid="strip-silence-dialog">
        @if (regions().length === 0) {
          <p class="text-foreground-muted">Select audio regions to strip their silence.</p>
        } @else {
          <div class="flex flex-col gap-2" role="list" aria-label="Preview">
            @for (p of previews(); track p.region.id) {
              <div class="flex flex-col gap-1" role="listitem">
                <div class="flex items-baseline justify-between gap-2">
                  <span class="text-foreground truncate text-[11px] font-semibold">{{ p.region.name }}</span>
                  <span class="text-foreground-muted readout"
                    >{{ p.count }} {{ p.count === 1 ? 'region' : 'regions' }}</span
                  >
                </div>
                <svg
                  class="well h-12 w-full rounded-md"
                  viewBox="0 0 1 1"
                  preserveAspectRatio="none"
                  role="img"
                  [attr.aria-label]="p.region.name + ': ' + p.count + ' regions kept'"
                >
                  @for (k of p.keep; track k.x) {
                    <rect class="fill-primary/25" [attr.x]="k.x" y="0" [attr.width]="k.width" height="1" />
                  }
                  <path class="fill-foreground-muted" [attr.d]="p.path" />
                  @for (k of p.keep; track k.x) {
                    <rect class="fill-primary" [attr.x]="k.x" y="0" [attr.width]="k.width" height="0.06" />
                  }
                </svg>
              </div>
            }
            @if (regions().length > previews().length) {
              <p class="text-foreground-muted">and {{ regions().length - previews().length }} more</p>
            }
          </div>
        }

        <div class="grid grid-cols-[11rem_6rem_auto] items-center gap-x-3 gap-y-2.5">
          <label class="text-foreground-muted" for="an-silence-threshold">Threshold</label>
          <input
            #threshold
            id="an-silence-threshold"
            xuiInput
            type="number"
            class="readout h-7 text-xs"
            min="-96"
            max="0"
            step="1"
            data-testid="strip-silence-threshold"
            [value]="settings().thresholdDb"
            (input)="patch('thresholdDb', threshold.valueAsNumber)"
          />
          <span class="text-foreground-muted">dB</span>
          <label class="text-foreground-muted" for="an-silence-min">Minimum Time to Accept as Silence</label>
          <input
            #min
            id="an-silence-min"
            xuiInput
            type="number"
            class="readout h-7 text-xs"
            min="0"
            step="10"
            data-testid="strip-silence-min-time"
            [value]="settings().minTimeMs"
            (input)="patch('minTimeMs', min.valueAsNumber)"
          />
          <span class="text-foreground-muted">ms</span>
          <label class="text-foreground-muted" for="an-silence-pre">Pre Attack-Time</label>
          <input
            #pre
            id="an-silence-pre"
            xuiInput
            type="number"
            class="readout h-7 text-xs"
            min="0"
            step="5"
            data-testid="strip-silence-pre-attack"
            [value]="settings().preAttackMs"
            (input)="patch('preAttackMs', pre.valueAsNumber)"
          />
          <span class="text-foreground-muted">ms</span>
          <label class="text-foreground-muted" for="an-silence-post">Post Release-Time</label>
          <input
            #post
            id="an-silence-post"
            xuiInput
            type="number"
            class="readout h-7 text-xs"
            min="0"
            step="5"
            data-testid="strip-silence-post-release"
            [value]="settings().postReleaseMs"
            (input)="patch('postReleaseMs', post.valueAsNumber)"
          />
          <span class="text-foreground-muted">ms</span>
        </div>
        <p class="text-foreground-muted" data-testid="strip-silence-count" aria-live="polite">{{ summary() }}</p>
      </xui-dialog-body>
      <xui-dialog-footer>
        <button xuiButton type="button" variant="ghost" (click)="ui.stripSilenceOpen.set(false)">Cancel</button>
        <button
          xuiButton
          type="button"
          data-testid="strip-silence-apply"
          [disabled]="regions().length === 0"
          (click)="apply()"
        >
          OK
        </button>
      </xui-dialog-footer>
    </xui-dialog>
  `
})
export class StripSilenceDialog {
  protected readonly ui = inject(UiStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly peaks = inject(PeaksCache);

  protected readonly settings = signal<SilenceSettings>(DefaultSilence);
  /** Waveforms by file id, as they arrive. */
  private readonly waveforms = signal<ReadonlyMap<string, Waveform>>(new Map());

  /** The selected audio regions. */
  protected readonly regions = computed(() => {
    const regions = this.project.regions();
    return [...this.selection.regions()].flatMap(id => regions.get(id) ?? []);
  });

  protected readonly previews = computed<Preview[]>(() => {
    const settings = this.settings();
    const rate = this.project.info()?.sampleRate ?? 48_000;
    return this.regions()
      .slice(0, PreviewRegions)
      .map(region => {
        const from = region.fileOffset;
        const to = region.fileOffset + region.length;
        const waveform = this.waveforms().get(region.fileId);
        if (!waveform) {
          return { region, path: '', keep: [], count: 0 };
        }
        const envelope = envelopeOf(waveform, from, to, PreviewBlocks);
        const stretches = silenceStretches(envelope, settings, rate, from, to);
        const n = envelope.levels.length;
        const top = envelope.levels.map((l, i) => `${(i / n).toFixed(4)},${(0.5 - Math.min(1, l) * 0.47).toFixed(3)}`);
        const bottom = envelope.levels
          .map((l, i) => `${(i / n).toFixed(4)},${(0.5 + Math.min(1, l) * 0.47).toFixed(3)}`)
          .reverse();
        return {
          region,
          path: n > 0 ? `M${top.join('L')}L${bottom.join('L')}Z` : '',
          keep: stretches.map(([a, b]) => ({ x: (a - from) / region.length, width: (b - a) / region.length })),
          count: stretches.length
        };
      });
  });

  protected readonly summary = computed(() => {
    const regions = this.regions().length;
    if (regions === 0) {
      return '';
    }
    const previews = this.previews();
    const kept = previews.reduce((sum, p) => sum + p.count, 0);
    const shown = previews.length === regions ? '' : ` (the first ${previews.length} previewed)`;
    return `${regions} ${regions === 1 ? 'region becomes' : 'regions become'} ${kept} ${kept === 1 ? 'region' : 'regions'}${shown}.`;
  });

  constructor() {
    // Each opening loads the peaks of what it previews.
    effect(() => {
      if (!this.ui.stripSilenceOpen()) {
        return;
      }
      const regions = this.regions().slice(0, PreviewRegions);
      untracked(() => regions.forEach(region => void this.load(region)));
    });
  }

  protected patch(key: keyof SilenceSettings, value: number): void {
    if (Number.isFinite(value)) {
      this.settings.update(settings => ({ ...settings, [key]: value }));
    }
  }

  protected async apply(): Promise<void> {
    const ids = this.regions().map(r => r.id);
    if (ids.length === 0) {
      return;
    }
    const { thresholdDb, minTimeMs, preAttackMs, postReleaseMs } = this.settings();
    this.ui.stripSilenceOpen.set(false);
    await this.project.execute(
      [{ type: 'stripSilence', regionIds: ids, thresholdDb, minTimeMs, preAttackMs, postReleaseMs }],
      'Strip Silence'
    );
  }

  private async load(region: AudioRegion): Promise<void> {
    if (this.waveforms().has(region.fileId)) {
      return;
    }
    const level = this.peaks.levelFor(region.length / PreviewBlocks);
    const waveform = await this.peaks.get(region.fileId, level);
    if (waveform) {
      this.waveforms.update(map => new Map(map).set(region.fileId, waveform));
    }
  }
}
