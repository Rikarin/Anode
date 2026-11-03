import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { type MasteringCharacter, MasteringBands } from '@anode/api-client';
import { AnKnob, AnReadingMeter } from '@anode/audio-ui';
import { AnSpectrumGraph } from '@anode/audio-ui/lazy';
import { XuiButtonImports } from '@xui/button';
import { Band, Field, selectValue } from '../control/styles';
import { FloatingWindow } from '../shell/floating-window';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import {
  type AnalysisSpan,
  type MasteringAmount,
  analysisRows,
  MasteringCharacters,
  MasteringRanges,
  signedDb
} from './mastering-math';
import { MasteringStore } from './mastering.store';

/** The amount knobs, in the window's order. */
const Amounts: readonly { key: MasteringAmount; label: string; unit: string }[] = [
  { key: 'targetLufs', label: 'Target', unit: 'LUFS' },
  { key: 'ceilingDbtp', label: 'Ceiling', unit: 'dBTP' },
  { key: 'eq', label: 'EQ', unit: '' },
  { key: 'dynamics', label: 'Dynamics', unit: '' },
  { key: 'width', label: 'Width', unit: '' }
];

/**
 * The Mastering Assistant window (docs/12-phase-11.md; Logic's): a `glass-bar` toolbar with Analyse (the whole song or
 * the cycle area, as a `masteringAnalysis` job with its progress) and the level-matched Bypass; the analysis as the
 * master's spectrum against the character's curve with its loudness, true peak, dynamics and width; the character as a
 * radiogroup and the target, ceiling and strengths as knobs (coalesced `setMastering` edits); the live loudness and the
 * compressor's and limiter's gain reduction while playing. Without an assistant on the master, Analyse adds one.
 */
@Component({
  selector: 'an-mastering-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnKnob, AnReadingMeter, AnSpectrumGraph, FloatingWindow, XuiButtonImports],
  template: `
    <an-floating-window
      title="Mastering Assistant"
      testId="mastering-window"
      width="34rem"
      [origin]="{ x: 220, y: 88 }"
      (closed)="ui.masteringOpen.set(false)"
    >
      <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2">
        <button
          xuiButton
          type="button"
          size="sm"
          data-testid="mastering-analyse"
          [disabled]="store.analysing()"
          (click)="store.analyse(span())"
        >
          Analyse
        </button>
        <div class="w-32">
          <select
            [class]="field"
            aria-label="Analyse"
            data-testid="mastering-span"
            [value]="span()"
            (change)="span.set(spanOf($event))"
          >
            <option value="song">Whole Song</option>
            <option value="cycle" [disabled]="!cycleOn()">Cycle Area</option>
          </select>
        </div>
        @if (store.job(); as job) {
          @if (store.analysing()) {
            <div
              class="well h-2 min-w-0 flex-1 overflow-hidden rounded-full"
              role="progressbar"
              aria-label="Analysis progress"
              aria-valuemin="0"
              aria-valuemax="100"
              [attr.aria-valuenow]="percent()"
            >
              <div class="bg-primary h-full transition-[width] duration-200" [style.width.%]="percent()"></div>
            </div>
          } @else if (job.state === 'failed') {
            <span class="text-error min-w-0 flex-1 truncate text-[11px]" role="status">{{ job.message }}</span>
          }
        }
        <span class="flex-1"></span>
        @if (settings(); as s) {
          <span class="text-foreground-muted readout text-[11px]" data-testid="mastering-bypass-gain">{{
            bypassText()
          }}</span>
          <button
            type="button"
            class="bezel text-foreground h-6.5 px-2 text-[11px] font-semibold"
            data-testid="mastering-bypass"
            [attr.aria-pressed]="s.matchedBypass"
            [class.text-lit]="s.matchedBypass"
            (click)="store.set('matchedBypass', !s.matchedBypass)"
          >
            Level-Matched Bypass
          </button>
        }
      </div>
      @if (!store.insert()) {
        <p class="text-foreground-muted p-4 text-[13px]" data-testid="mastering-empty">
          The master has no Mastering Assistant yet. Analyse adds one last on the master and measures the mix.
        </p>
      } @else {
        <div [class]="band">Analysis</div>
        <div class="flex flex-col gap-2 px-3 py-2">
          <an-spectrum-graph [bands]="bands" [spectrum]="spectrum()" [curve]="curve()" [label]="graphLabel()" />
          <div class="flex gap-3 text-[10px]" aria-hidden="true">
            <span class="text-foreground flex items-center gap-1"
              ><span class="bg-primary inline-block h-0.5 w-3"></span>Mix</span
            >
            <span class="text-foreground flex items-center gap-1"
              ><span class="border-foreground-muted inline-block w-3 border-t border-dashed"></span
              >{{ characterName() }} curve</span
            >
          </div>
          <dl class="grid grid-cols-3 gap-x-3 gap-y-1" data-testid="mastering-analysis">
            @for (row of rows(); track row.label) {
              <div class="flex items-baseline justify-between gap-2">
                <dt class="text-foreground-muted text-[11px]">{{ row.label }}</dt>
                <dd class="readout text-foreground text-[12px] font-medium">{{ row.value }}</dd>
              </div>
            }
          </dl>
        </div>
        @if (settings(); as s) {
          <div [class]="band">Character</div>
          <div class="px-3 py-2">
            <div
              class="bezel-group text-foreground h-6.5 text-[11px] font-semibold"
              role="radiogroup"
              aria-label="Character"
            >
              @for (c of characters; track c.value) {
                <button
                  type="button"
                  role="radio"
                  class="px-3"
                  [attr.aria-checked]="s.character === c.value"
                  [attr.tabindex]="s.character === c.value ? 0 : -1"
                  [attr.data-testid]="'mastering-character-' + c.value"
                  [class.text-lit]="s.character === c.value"
                  (click)="store.set('character', c.value)"
                  (keydown)="arrow($event, s.character)"
                >
                  {{ c.name }}
                </button>
              }
            </div>
          </div>
          <div [class]="band">Settings</div>
          <div class="flex flex-wrap items-end gap-x-1 gap-y-2 px-3 py-2">
            @for (amount of amounts; track amount.key) {
              <an-knob
                [label]="amount.label"
                [min]="ranges[amount.key].min"
                [max]="ranges[amount.key].max"
                [defaultValue]="ranges[amount.key].default"
                [unit]="amount.unit"
                [value]="s[amount.key]"
                [attr.data-parameter]="amount.key"
                (valueChange)="store.set(amount.key, $event)"
              />
            }
          </div>
        }
        <div [class]="band">Meters</div>
        <div class="grid grid-cols-2 gap-x-4 gap-y-1.5 px-3 py-2" data-testid="mastering-meters">
          <an-reading-meter
            label="Momentary"
            unit="LUFS"
            [min]="-40"
            [max]="0"
            [value]="live()?.momentaryLufs ?? null"
          />
          <an-reading-meter
            label="Compressor"
            kind="reduction"
            [min]="-12"
            [value]="live()?.compressorReductionDb ?? null"
          />
          <an-reading-meter
            label="Short-term"
            unit="LUFS"
            [min]="-40"
            [max]="0"
            [value]="live()?.shortTermLufs ?? null"
          />
          <an-reading-meter label="Limiter" kind="reduction" [min]="-12" [value]="live()?.limiterReductionDb ?? null" />
          <an-reading-meter
            label="Integrated"
            unit="LUFS"
            [min]="-40"
            [max]="0"
            [value]="live()?.integratedLufs ?? null"
          />
          <an-reading-meter
            label="True peak"
            unit="dBTP"
            [min]="-20"
            [max]="3"
            [value]="live()?.truePeakDbtp ?? null"
          />
        </div>
      }
    </an-floating-window>
  `
})
export class MasteringWindow {
  protected readonly ui = inject(UiStore);
  protected readonly store = inject(MasteringStore);
  private readonly project = inject(ProjectStore);

  protected readonly band = Band;
  protected readonly field = Field;
  protected readonly bands = MasteringBands;
  protected readonly characters = MasteringCharacters;
  protected readonly amounts = Amounts;
  protected readonly ranges = MasteringRanges;

  protected readonly span = signal<AnalysisSpan>('song');
  protected readonly settings = this.store.settings;
  protected readonly live = this.store.live;
  protected readonly cycleOn = computed(() => this.project.settings().cycle.enabled);
  protected readonly percent = computed(() => Math.round((this.store.job()?.progress ?? 0) * 100));
  protected readonly rows = computed(() => analysisRows(this.store.analysis()));
  protected readonly spectrum = computed(() => this.store.analysis()?.spectrumDb ?? []);
  protected readonly curve = computed(() => this.store.analysis()?.curveDb ?? []);
  protected readonly characterName = computed(
    () => MasteringCharacters.find(c => c.value === this.settings()?.character)?.name ?? 'Clean'
  );
  protected readonly graphLabel = computed(() =>
    this.store.analysis()
      ? `The mix's spectrum against the ${this.characterName()} curve`
      : 'Spectrum (not analysed yet)'
  );
  protected readonly bypassText = computed(() => {
    const gain = this.store.bypassGain();
    return gain === null ? 'Analyse to match the bypass' : `Bypass at ${signedDb(gain)}`;
  });

  protected spanOf(event: Event): AnalysisSpan {
    return selectValue(event) === 'cycle' ? 'cycle' : 'song';
  }

  /** ←/→ move between the characters. */
  protected arrow(event: KeyboardEvent, current: MasteringCharacter): void {
    const step = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[event.key];
    if (step === undefined) {
      return;
    }
    event.preventDefault();
    const index = MasteringCharacters.findIndex(c => c.value === current);
    const next = MasteringCharacters[(index + step + MasteringCharacters.length) % MasteringCharacters.length];
    if (next) {
      void this.store.set('character', next.value);
      const group = (event.currentTarget as HTMLElement).parentElement;
      group?.querySelectorAll<HTMLElement>('[role="radio"]')[MasteringCharacters.indexOf(next)]?.focus();
    }
  }
}
