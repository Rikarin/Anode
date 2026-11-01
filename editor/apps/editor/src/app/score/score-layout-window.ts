import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { ScoreLayout } from '@anode/api-client';
import { Band, Field, selectValue } from '../control/styles';
import { FloatingWindow } from '../shell/floating-window';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import { instrumentNameOf, PagePresets, presetOf, withInstrumentName, withPreset } from './score-model';

/**
 * Score Layout (docs/10-phase-9.md, "layout"): the page (a preset or a custom size in millimetres, portrait or
 * landscape), margins, staff size, the title, subtitle, composer, header and footer, "Transposing score", and each
 * track's instrument names (full and short). One layout per project; each change is one `setScoreLayout`.
 */
@Component({
  selector: 'an-score-layout-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow],
  template: `
    <an-floating-window
      title="Score Layout"
      testId="score-layout-window"
      width="30rem"
      (closed)="ui.scoreLayoutOpen.set(false)"
    >
      <h3 [class]="band">Page</h3>
      <div class="grid grid-cols-[6.5rem_1fr] items-center gap-x-2 gap-y-1.5 p-3 text-xs">
        <label class="text-foreground-muted" for="an-layout-preset">Size</label>
        <div class="flex gap-2">
          <select id="an-layout-preset" [class]="field" data-testid="layout-preset" (change)="preset(value($event))">
            @for (p of presets; track p.name) {
              <option [value]="p.name" [selected]="p.name === current().name">{{ p.name }}</option>
            }
            <option value="Custom" [selected]="current().name === 'Custom'">Custom</option>
          </select>
          <select
            [class]="field"
            aria-label="Orientation"
            data-testid="layout-orientation"
            (change)="orient(value($event) === 'landscape')"
          >
            <option value="portrait" [selected]="!current().landscape">Portrait</option>
            <option value="landscape" [selected]="current().landscape">Landscape</option>
          </select>
        </div>
        <span class="text-foreground-muted">Width × height</span>
        <div class="flex items-center gap-1">
          <input
            type="number"
            min="50"
            max="2000"
            step="0.1"
            [class]="field + ' readout'"
            aria-label="Page width in millimetres"
            data-testid="layout-width"
            [value]="layout().pageWidthMm"
            (change)="number('pageWidthMm', $event, 50, 2000)"
          />
          <span class="text-foreground-muted">×</span>
          <input
            type="number"
            min="50"
            max="2000"
            step="0.1"
            [class]="field + ' readout'"
            aria-label="Page height in millimetres"
            data-testid="layout-height"
            [value]="layout().pageHeightMm"
            (change)="number('pageHeightMm', $event, 50, 2000)"
          />
          <span class="text-foreground-muted">mm</span>
        </div>
        <span class="text-foreground-muted">Margins</span>
        <div class="grid grid-cols-4 gap-1">
          @for (side of sides; track side.key) {
            <input
              type="number"
              min="0"
              max="50"
              step="0.5"
              [class]="field + ' readout'"
              [attr.aria-label]="side.label + ' margin in millimetres'"
              [title]="side.label"
              [value]="layout().margins[side.key]"
              (change)="margin(side.key, $event)"
            />
          }
        </div>
        <label class="text-foreground-muted" for="an-layout-staff">Staff size</label>
        <div class="flex items-center gap-1">
          <input
            id="an-layout-staff"
            type="number"
            min="3"
            max="12"
            step="0.1"
            [class]="field + ' readout w-20'"
            data-testid="layout-staff"
            [value]="layout().staffHeightMm"
            (change)="number('staffHeightMm', $event, 3, 12)"
          />
          <span class="text-foreground-muted">mm</span>
        </div>
      </div>
      <h3 [class]="band">Text</h3>
      <div class="grid grid-cols-[6.5rem_1fr] items-center gap-x-2 gap-y-1.5 p-3 text-xs">
        @for (text of texts; track text.key) {
          <label class="text-foreground-muted" [for]="'an-layout-' + text.key">{{ text.label }}</label>
          <input
            [id]="'an-layout-' + text.key"
            [class]="field"
            [attr.data-testid]="'layout-' + text.key"
            [value]="layout()[text.key]"
            maxlength="200"
            (change)="text_(text.key, value($event))"
          />
        }
        <span class="text-foreground-muted">Pitch</span>
        <label class="text-foreground flex items-center gap-2">
          <input
            type="checkbox"
            class="accent-(--color-primary)"
            data-testid="layout-transposing"
            [checked]="layout().transposingScore"
            (change)="patch({ transposingScore: !layout().transposingScore })"
          />
          Transposing score (off: concert pitch)
        </label>
      </div>
      <h3 [class]="band">Instrument Names</h3>
      <div class="grid grid-cols-[6.5rem_1fr_6rem] items-center gap-x-2 gap-y-1.5 p-3 text-xs">
        <span></span>
        <span class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">Full</span>
        <span class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">Short</span>
        @for (track of tracks(); track track.id) {
          <span class="text-foreground truncate">{{ track.name }}</span>
          <input
            [class]="field"
            [attr.aria-label]="'Full name of ' + track.name"
            [value]="names(track.id, track.name).full"
            (change)="rename(track.id, track.name, value($event), null)"
          />
          <input
            [class]="field"
            [attr.aria-label]="'Short name of ' + track.name"
            [value]="names(track.id, track.name).short"
            (change)="rename(track.id, track.name, null, value($event))"
          />
        }
      </div>
    </an-floating-window>
  `
})
export class ScoreLayoutWindow {
  protected readonly ui = inject(UiStore);
  private readonly project = inject(ProjectStore);

  protected readonly band = Band;
  protected readonly field = Field;
  protected readonly value = selectValue;
  protected readonly presets = PagePresets;
  protected readonly sides: readonly { key: keyof ScoreLayout['margins']; label: string }[] = [
    { key: 'top', label: 'Top' },
    { key: 'bottom', label: 'Bottom' },
    { key: 'left', label: 'Left' },
    { key: 'right', label: 'Right' }
  ];
  protected readonly texts: readonly { key: 'title' | 'subtitle' | 'composer' | 'header' | 'footer'; label: string }[] =
    [
      { key: 'title', label: 'Title' },
      { key: 'subtitle', label: 'Subtitle' },
      { key: 'composer', label: 'Composer' },
      { key: 'header', label: 'Header' },
      { key: 'footer', label: 'Footer' }
    ];
  protected readonly layout = computed(() => this.project.score().layout);
  protected readonly current = computed(() => presetOf(this.layout()));
  protected readonly tracks = computed(() =>
    this.project.trackList().filter(t => t.kind === 'instrument' || t.kind === 'midi')
  );

  protected names(trackId: string, trackName: string): { full: string; short: string } {
    return instrumentNameOf(this.layout(), trackId, trackName);
  }

  protected patch(change: Partial<ScoreLayout>): void {
    this.save({ ...this.layout(), ...change });
  }

  private save(layout: ScoreLayout): void {
    void this.project.execute([{ type: 'setScoreLayout', layout }], 'Score Layout');
  }

  protected preset(name: string): void {
    if (name !== 'Custom') {
      this.save(withPreset(this.layout(), name, this.current().landscape));
    }
  }

  protected orient(landscape: boolean): void {
    const layout = this.layout();
    if (landscape !== layout.pageWidthMm > layout.pageHeightMm) {
      this.save({ ...layout, pageWidthMm: layout.pageHeightMm, pageHeightMm: layout.pageWidthMm });
    }
  }

  protected number(
    key: 'pageWidthMm' | 'pageHeightMm' | 'staffHeightMm',
    event: Event,
    min: number,
    max: number
  ): void {
    const value = Number(selectValue(event));
    if (Number.isFinite(value)) {
      this.patch({ [key]: Math.min(max, Math.max(min, value)) });
    }
  }

  protected margin(side: keyof ScoreLayout['margins'], event: Event): void {
    const value = Number(selectValue(event));
    if (Number.isFinite(value)) {
      this.patch({ margins: { ...this.layout().margins, [side]: Math.min(50, Math.max(0, value)) } });
    }
  }

  protected text_(key: 'title' | 'subtitle' | 'composer' | 'header' | 'footer', value: string): void {
    this.patch({ [key]: value.slice(0, 200) });
  }

  protected rename(trackId: string, trackName: string, full: string | null, short: string | null): void {
    const names = this.names(trackId, trackName);
    this.save(
      withInstrumentName(this.layout(), trackId, trackName, { full: full ?? names.full, short: short ?? names.short })
    );
  }
}
