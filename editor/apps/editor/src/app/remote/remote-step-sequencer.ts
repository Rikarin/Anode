import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { PatternRow } from '@anode/api-client';
import { pitchName } from '@anode/audio-ui';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TransportStore } from '../state/transport.store';
import { StepSequencerStore } from '../step-sequencer/step-sequencer.store';
import { Touch } from './remote-transport';

/**
 * The remote's Step Sequencer (docs/11-phase-10.md): the selected track's pattern regions to choose from (or a new one
 * at the playhead), then the pattern's rows, each a name and its steps as 44 px toggles, beats marked, through the Step
 * Sequencer store (`setPattern`) as the desktop's. Velocity, gate and the other step values stay on the desktop.
 */
@Component({
  selector: 'an-remote-step-sequencer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex min-h-0 flex-col gap-2 p-2' },
  template: `
    <div class="flex flex-wrap items-center gap-2">
      <span class="text-foreground text-[13px] font-semibold">Step Sequencer</span>
      @for (region of regions(); track region.id; let i = $index) {
        <button
          type="button"
          [class]="touch + ' px-3 text-[12px]'"
          data-testid="remote-pattern-region"
          [attr.aria-pressed]="steps.region()?.id === region.id"
          [class.text-lit]="steps.region()?.id === region.id"
          (click)="selection.select([region.id])"
        >
          {{ region.name || 'Pattern ' + (i + 1) }}
        </button>
      }
      @if (steps.targetTrack(); as track) {
        <button type="button" [class]="touch + ' ms-auto px-3 text-[12px] font-semibold'" (click)="newRegion(track.id)">
          New Pattern
        </button>
      }
    </div>
    @if (steps.pattern(); as pattern) {
      <div class="overflow-auto" role="grid" aria-label="Pattern" data-testid="remote-pattern">
        @for (row of pattern.rows; track row.id) {
          <div class="mb-1 flex items-center gap-1" role="row">
            <span
              class="text-foreground bg-surface border-hairline flex h-11 w-24 shrink-0 items-center truncate rounded-md border px-2 text-[12px] font-semibold"
              role="rowheader"
              >{{ rowName(row) }}</span
            >
            @for (step of row.steps.slice(0, row.length); track $index; let i = $index) {
              <button
                type="button"
                role="gridcell"
                class="well size-11 shrink-0 rounded-md"
                [class.bg-primary]="step.on"
                [class.opacity-60]="row.muted"
                [class.ms-1]="i > 0 && i % 4 === 0"
                [attr.aria-pressed]="step.on"
                [attr.aria-label]="rowName(row) + ' step ' + (i + 1)"
                (click)="steps.toggleStep(row.id, i)"
              ></button>
            }
          </div>
        }
      </div>
    } @else {
      <p class="text-foreground-muted text-sm">
        {{
          steps.targetTrack()
            ? 'Choose a pattern region of the track, or make a new one.'
            : 'Select a track that plays patterns (an instrument, MIDI or Drum Machine Designer track).'
        }}
      </p>
    }
  `
})
export class RemoteStepSequencer {
  protected readonly steps = inject(StepSequencerStore);
  protected readonly selection = inject(SelectionStore);
  private readonly project = inject(ProjectStore);
  private readonly transport = inject(TransportStore);
  protected readonly touch = Touch;

  /** The selected track's pattern regions, in time order. */
  protected readonly regions = computed(() => {
    const id = this.selection.track();
    return [...this.project.patternRegions().values()]
      .filter(r => r.trackId === id)
      .sort((a, b) => a.startTick - b.startTick);
  });

  protected rowName(row: PatternRow): string {
    const pad = this.steps.pads()?.find(p => p.note === row.note);
    return pad?.name ?? (row.name || pitchName(row.note));
  }

  protected async newRegion(trackId: string): Promise<void> {
    const id = await this.steps.newRegion(trackId, Math.round(this.transport.positionAt(performance.now())));
    if (id) {
      this.selection.select([id]);
    }
  }
}
