import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { MidiRegion } from '@anode/api-client';
import { AnKnob } from '@anode/audio-ui';
import { XuiNumericInputImports } from '@xui/numeric-input';
import { XuiSelectImports } from '@xui/select';
import { gridLabel, QuantizeGrids } from '../midi/midi-math';
import { ProjectStore } from '../state/project.store';

interface GridChoice {
  ticks: number;
  label: string;
}

type PlaybackField = 'transpose' | 'velocity' | 'quantizeTicks' | 'quantizeStrength' | 'swing';

const Labels: Record<PlaybackField, string> = {
  transpose: 'Region Transpose',
  velocity: 'Region Velocity',
  quantizeTicks: 'Region Quantize',
  quantizeStrength: 'Quantize Strength',
  swing: 'Region Swing'
};

/**
 * The Region inspector for a MIDI region (Logic's): how it plays without changing its notes. Transpose in semitones,
 * a velocity offset, a quantise grid (Off or a note value) with its strength and swing. Each change is one
 * `setMidiRegion` batch.
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
        [items]="grids"
        [itemText]="gridText"
        [filterable]="false"
        [value]="grid()"
        (valueChange)="set('quantizeTicks', $event?.ticks ?? null)"
      />
    </div>
    <div class="flex justify-around">
      <an-knob
        label="Q-Strength"
        unit="%"
        [min]="0"
        [max]="100"
        [defaultValue]="100"
        [disabled]="playback().quantizeTicks === 0"
        [value]="playback().quantizeStrength * 100"
        (committed)="set('quantizeStrength', $event / 100)"
      />
      <an-knob
        label="Q-Swing"
        unit="%"
        [min]="0"
        [max]="100"
        [defaultValue]="0"
        [disabled]="playback().quantizeTicks === 0"
        [value]="playback().swing * 100"
        (committed)="set('swing', $event / 100)"
      />
    </div>
  `
})
export class RegionInspector {
  private readonly project = inject(ProjectStore);
  readonly region = input.required<MidiRegion>();

  protected readonly playback = computed(() => this.region().playback);
  /** Grids as objects: the select shows nothing for a falsy value, and Off is 0. */
  protected readonly grids: readonly GridChoice[] = [{ ticks: 0, label: 'Off' }, ...QuantizeGrids];
  protected readonly gridText = (grid: GridChoice) => grid.label;
  protected readonly grid = computed(
    () =>
      this.grids.find(g => g.ticks === this.playback().quantizeTicks) ?? {
        ticks: this.playback().quantizeTicks,
        label: gridLabel(this.playback().quantizeTicks)
      }
  );

  protected set(field: PlaybackField, value: number | null): void {
    if (value === null || !Number.isFinite(value) || this.region().playback[field] === value) {
      return;
    }
    void this.project.execute([{ type: 'setMidiRegion', regionId: this.region().id, [field]: value }], Labels[field]);
  }
}
