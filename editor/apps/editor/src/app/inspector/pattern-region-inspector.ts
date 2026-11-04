import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { PatternRegion } from '@anode/api-client';
import { LoopFields } from './loop-fields';

/** The Region inspector of a pattern region: its Loop (its steps are the Step Sequencer's). */
@Component({
  selector: 'an-pattern-region-inspector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LoopFields],
  host: { class: 'flex flex-col gap-2', role: 'group', '[attr.aria-label]': '"Region: " + region().name' },
  template: `
    <h3
      class="text-foreground border-hairline bg-surface-raised -mx-3 truncate border-y px-3 py-1 text-[11px] font-bold shadow-[inset_0_1px_0_var(--highlight)]"
      [attr.title]="region().name"
    >
      Region: {{ region().name }}
    </h3>
    <div class="grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-1.5 text-xs">
      <an-loop-fields
        kind="pattern"
        [regionId]="region().id"
        [trackId]="region().trackId"
        [startTick]="region().startTick"
        [endTick]="region().startTick + region().lengthTicks"
        [loopTicks]="region().loopTicks"
      />
    </div>
  `
})
export class PatternRegionInspector {
  readonly region = input.required<PatternRegion>();
}
