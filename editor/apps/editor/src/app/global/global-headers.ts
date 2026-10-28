import { ChangeDetectionStrategy, Component, computed, inject, output } from '@angular/core';
import { provideIcons } from '@ng-icons/core';
import { matAddRound, matMoreHorizRound } from '@ng-icons/material-icons/round';
import { XuiIconImports } from '@xui/icon';
import { XuiMenuImports } from '@xui/menu';
import { XuiTooltipImports } from '@xui/tooltip';
import { ProjectStore } from '../state/project.store';
import { type GlobalLane, GlobalLanes, TracksViewStore } from '../state/tracks-view.store';
import { GlobalLaneHeights, keyName } from './global-math';

/**
 * The global tracks' headers beside their lanes: each track's name and, for the arrangement and marker tracks, a +
 * that adds a section (after the last) or a marker (at the playhead). A menu picks which global tracks show.
 */
@Component({
  selector: 'an-global-headers',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiIconImports, XuiMenuImports, XuiTooltipImports],
  providers: [provideIcons({ matAddRound, matMoreHorizRound })],
  host: {
    class: 'border-hairline bg-surface-raised block border-b',
    role: 'group',
    'aria-label': 'Global track headers'
  },
  template: `
    @for (lane of lanes(); track lane; let first = $first) {
      <div
        class="border-hairline flex items-center gap-1 border-b ps-3 pe-1 text-[11px] font-semibold"
        [style.height.px]="heights[lane]"
      >
        <span class="text-foreground min-w-0 flex-1 truncate">{{ names[lane] }}</span>
        @if (lane === 'tempo') {
          <span class="text-foreground-muted readout text-[10px]">{{ tempo() }}</span>
        }
        @if (lane === 'key') {
          <span class="text-foreground-muted text-[10px]">{{ key() }}</span>
        }
        @if (lane === 'arrangement' || lane === 'marker') {
          <button
            type="button"
            class="bezel text-foreground grid size-4.5 place-items-center"
            [attr.aria-label]="lane === 'arrangement' ? 'Add section' : 'Add marker'"
            [attr.data-testid]="lane === 'arrangement' ? 'add-section' : 'add-marker'"
            [xuiTooltip]="
              lane === 'arrangement' ? 'Add an 8-bar section after the last' : 'Add a marker at the playhead'
            "
            (click)="add.emit(lane)"
          >
            <ng-icon xui name="matAddRound" size="12px" />
          </button>
        }
        @if (first) {
          <button
            type="button"
            class="text-foreground-muted hover:text-foreground grid size-4.5 place-items-center"
            aria-label="Choose global tracks"
            [xuiMenuTriggerFor]="choose"
          >
            <ng-icon xui name="matMoreHorizRound" size="14px" />
          </button>
        }
      </div>
    }
    <ng-template #choose>
      <xui-menu>
        @for (lane of all; track lane) {
          <button
            xuiMenuItem
            type="button"
            role="menuitemcheckbox"
            [attr.aria-checked]="view.globalLanes().has(lane)"
            (click)="view.toggleGlobalLane(lane)"
          >
            {{ view.globalLanes().has(lane) ? '✓ ' : '' }}{{ names[lane] }}
          </button>
        }
      </xui-menu>
    </ng-template>
  `
})
export class GlobalHeaders {
  protected readonly view = inject(TracksViewStore);
  private readonly project = inject(ProjectStore);
  readonly add = output<GlobalLane>();

  protected readonly all = GlobalLanes;
  protected readonly heights = GlobalLaneHeights;
  protected readonly names: Record<GlobalLane, string> = {
    arrangement: 'Arrangement',
    marker: 'Marker',
    tempo: 'Tempo',
    signature: 'Signature',
    key: 'Key'
  };
  protected readonly lanes = computed(() => GlobalLanes.filter(lane => this.view.globalLanes().has(lane)));
  protected readonly tempo = computed(() => `${this.project.settings().tempo} bpm`);
  protected readonly key = computed(() => {
    const first = this.project.settings().keys[0];
    return first ? keyName(first) : '';
  });
}
