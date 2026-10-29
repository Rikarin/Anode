import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { MidiRegion } from '@anode/api-client';
import { AnKnob } from '@anode/audio-ui';
import { XuiNumericInputImports } from '@xui/numeric-input';
import { XuiSelectImports } from '@xui/select';
import { type QuantizeChoice, quantizeChoices, regionQuantizeCommand } from '../grooves/groove-menu';
import { gridLabel, QuantizeGrids } from '../midi/midi-math';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';

type PlaybackField = 'transpose' | 'velocity' | 'quantizeStrength' | 'swing';

const Labels: Record<PlaybackField, string> = {
  transpose: 'Region Transpose',
  velocity: 'Region Velocity',
  quantizeStrength: 'Quantize Strength',
  swing: 'Region Swing'
};

/**
 * The Region inspector for a MIDI region (Logic's): how it plays without changing its notes. Transpose in semitones,
 * a velocity offset, a quantise grid (Off, a note value, or Phase 7's groove templates of the project) with its strength
 * and swing, and Make Groove Template. Each change is one `setMidiRegion` batch.
 */
@Component({
  selector: 'an-region-inspector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnKnob, XuiNumericInputImports, XuiSelectImports],
  host: { class: 'flex flex-col gap-2', role: 'group', '[attr.aria-label]': '"Region: " + region().name' },
  template: `
    <h3
      class="text-foreground border-hairline bg-surface-raised -mx-3 truncate border-y px-3 py-1 text-[11px] font-bold shadow-[inset_0_1px_0_var(--highlight)]"
      [attr.title]="region().name"
    >
      Region: {{ region().name }}
    </h3>
    <div class="grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-1.5 text-xs">
      <span class="text-foreground-muted">Transpose</span>
      <xui-numeric-input
        size="sm"
        aria-label="Transpose"
        data-testid="region-transpose"
        [min]="-96"
        [max]="96"
        [stepSize]="1"
        [majorStepSize]="12"
        [value]="playback().transpose"
        (valueChange)="set('transpose', $event)"
      />
      <span class="text-foreground-muted">Velocity</span>
      <xui-numeric-input
        size="sm"
        aria-label="Velocity offset"
        [min]="-127"
        [max]="127"
        [stepSize]="1"
        [majorStepSize]="10"
        [value]="playback().velocity"
        (valueChange)="set('velocity', $event)"
      />
      <span class="text-foreground-muted">Quantize</span>
      <xui-select
        aria-label="Quantize"
        data-testid="region-quantize"
        [items]="grids()"
        [itemText]="gridText"
        [filterable]="false"
        [value]="grid()"
        (valueChange)="$event && quantize($event)"
      />
      <span class="text-foreground-muted">Groove</span>
      <button
        type="button"
        class="bezel text-foreground h-6 justify-self-start px-2 text-[11px] font-semibold"
        data-testid="region-make-groove"
        (click)="ui.grooves.set({ regionId: region().id })"
      >
        Make Groove Template…
      </button>
    </div>
    <div class="flex justify-around">
      <an-knob
        label="Q-Strength"
        unit="%"
        [min]="0"
        [max]="100"
        [defaultValue]="100"
        [disabled]="quantizeOff()"
        [value]="playback().quantizeStrength * 100"
        (committed)="set('quantizeStrength', $event / 100)"
      />
      <an-knob
        label="Q-Swing"
        unit="%"
        [min]="0"
        [max]="100"
        [defaultValue]="0"
        [disabled]="playback().quantizeTicks === 0 || !!playback().grooveId"
        [value]="playback().swing * 100"
        (committed)="set('swing', $event / 100)"
      />
    </div>
  `
})
export class RegionInspector {
  private readonly project = inject(ProjectStore);
  protected readonly ui = inject(UiStore);
  readonly region = input.required<MidiRegion>();

  protected readonly playback = computed(() => this.region().playback);
  /** Grids as objects (the select shows nothing for a falsy value, and Off is 0), then the project's grooves. */
  protected readonly grids = computed(() =>
    quantizeChoices([{ ticks: 0, label: 'Off' }, ...QuantizeGrids], this.project.grooveList())
  );
  protected readonly gridText = (grid: QuantizeChoice) => grid.label;
  protected readonly grid = computed(() => {
    const groove = this.playback().grooveId;
    const choices = this.grids();
    if (groove) {
      return choices.find(g => g.grooveId === groove) ?? null;
    }
    return (
      choices.find(g => !g.grooveId && g.ticks === this.playback().quantizeTicks) ?? {
        ticks: this.playback().quantizeTicks,
        label: gridLabel(this.playback().quantizeTicks)
      }
    );
  });
  protected readonly quantizeOff = computed(() => this.playback().quantizeTicks === 0 && !this.playback().grooveId);

  /** A grid value or a groove (Phase 7: `grooveId`; a grid value returns a grooved region to the grid). */
  protected quantize(choice: QuantizeChoice): void {
    const command = regionQuantizeCommand(this.region(), choice);
    if (command) {
      void this.project.execute([command], choice.grooveId ? 'Region Groove' : 'Region Quantize');
    }
  }

  protected set(field: PlaybackField, value: number | null): void {
    if (value === null || !Number.isFinite(value) || this.region().playback[field] === value) {
      return;
    }
    void this.project.execute([{ type: 'setMidiRegion', regionId: this.region().id, [field]: value }], Labels[field]);
  }
}
