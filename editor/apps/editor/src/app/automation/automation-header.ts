import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { Track } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import { matCloseRound, matExpandMoreRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiMenuImports } from '@xui/menu';
import { XuiTooltipImports } from '@xui/tooltip';
import { AutomationStore } from './automation.store';
import { groupTargets } from './targets';

/**
 * The header of an automation sub-lane, under its track's header: the parameter it shows (a menu of the track's
 * targets, grouped Main, Sends and per plugin; picking one shows that target in this lane), the lane's value at the
 * playhead, and a button that hides the lane (its automation stays).
 */
@Component({
  selector: 'an-automation-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiIconImports, XuiMenuImports, XuiTooltipImports],
  providers: [provideIcons({ matCloseRound, matExpandMoreRound })],
  host: {
    class: 'an-automation-header border-hairline bg-surface-raised flex items-center gap-1.5 border-b pe-2',
    role: 'group',
    '[attr.aria-label]': '"Automation of " + track().name + ": " + info().name',
    '[attr.data-target]': 'target()',
    '[style.padding-inline-start.px]': '20 + depth() * 14'
  },
  template: `
    <span class="h-3/5 w-0.5 shrink-0 rounded-full" [style.background-color]="track().color" aria-hidden="true"></span>
    <button
      type="button"
      class="bezel text-foreground flex h-5 min-w-0 flex-1 items-center gap-1 px-1.5 text-[11px] font-semibold"
      data-testid="automation-parameter"
      [attr.aria-label]="'Automation parameter of ' + track().name + ': ' + info().name"
      [xuiMenuTriggerFor]="targets"
    >
      <span class="min-w-0 flex-1 truncate text-start">{{ info().name }}</span>
      <ng-icon xui name="matExpandMoreRound" size="14px" />
    </button>
    <ng-template #targets>
      <xui-menu class="max-h-80 min-w-48 overflow-y-auto" aria-label="Automation parameters">
        @for (group of groups(); track group.group; let first = $first) {
          <xui-menu-divider [title]="group.group" [class.mt-0]="first" />
          @for (t of group.targets; track t.target) {
            <button
              xuiMenuItem
              type="button"
              role="menuitemradio"
              [attr.aria-checked]="t.target === target()"
              [selected]="t.target === target()"
              (click)="automation.setLaneTarget(track().id, target(), t.target)"
            >
              {{ t.name }}
            </button>
          }
        }
      </xui-menu>
    </ng-template>
    <span
      class="readout text-foreground w-14 shrink-0 text-end text-[11px]"
      data-testid="automation-value"
      [attr.title]="info().name + ' at the playhead'"
      >{{ value() }}</span
    >
    <button
      xuiButton
      type="button"
      variant="ghost"
      size="sm"
      class="h-5 w-5 shrink-0 px-0"
      [attr.aria-label]="'Hide ' + info().name + ' automation of ' + track().name"
      xuiTooltip="Hide this lane (its automation stays)"
      (click)="automation.removeLane(track().id, target())"
    >
      <ng-icon xui name="matCloseRound" size="14px" />
    </button>
  `
})
export class AutomationHeader {
  protected readonly automation = inject(AutomationStore);

  readonly track = input.required<Track>();
  readonly target = input.required<string>();
  readonly depth = input(0);

  protected readonly info = computed(() => this.automation.target(this.track().id, this.target()));
  protected readonly groups = computed(() => groupTargets(this.automation.targets(this.track().id)));
  protected readonly value = computed(() => {
    const value = this.automation.valueAt(this.track().id, this.target(), this.automation.playheadTick());
    return this.info().format(value ?? this.info().default);
  });
}
