import { ChangeDetectionStrategy, Component, computed, inject, type OnInit } from '@angular/core';
import { XuiNumericInputImports } from '@xui/numeric-input';
import { XuiPopoverImports } from '@xui/popover';
import { MonitoringStore } from '../surround/monitoring.store';

/**
 * The monitor section, in the Mixer's toolbar: Dim, Mono and the reference level at a press, the levels behind them in a
 * popover. They act on what the speakers get, never on a bounce or the meters, and belong to the user, not the project.
 */
@Component({
  selector: 'an-monitor-section',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiNumericInputImports, XuiPopoverImports],
  host: { class: 'flex items-center gap-1' },
  template: `
    <div
      class="bezel-group text-foreground h-6.5 text-[11px] font-semibold"
      role="group"
      aria-label="Monitor section"
      data-testid="monitor-section"
    >
      <button
        type="button"
        class="px-2"
        data-testid="monitor-dim"
        [attr.aria-pressed]="section().dim"
        [class.text-lit]="section().dim"
        (click)="monitoring.setSection({ dim: !section().dim })"
      >
        Dim
      </button>
      <button
        type="button"
        class="px-2"
        data-testid="monitor-mono"
        [attr.aria-pressed]="section().mono"
        [class.text-lit]="section().mono"
        (click)="monitoring.setSection({ mono: !section().mono })"
      >
        Mono
      </button>
      <button
        type="button"
        class="px-2"
        data-testid="monitor-reference"
        [attr.aria-pressed]="section().reference"
        [class.text-lit]="section().reference"
        (click)="monitoring.setSection({ reference: !section().reference })"
      >
        Ref
      </button>
      <button
        type="button"
        class="readout px-2"
        data-testid="monitor-level"
        placement="bottom-end"
        [attr.aria-label]="'Monitor level: ' + levelText()"
        [xuiPopover]="levels"
      >
        {{ levelText() }}
      </button>
    </div>
    <ng-template #levels>
      <div class="grid w-52 grid-cols-[1fr_6rem] items-center gap-x-3 gap-y-2 p-2 text-xs" data-testid="monitor-levels">
        <label class="text-foreground-muted" for="an-monitor-level">Level (dB)</label>
        <xui-numeric-input
          size="sm"
          id="an-monitor-level"
          aria-label="Monitor level in dB"
          data-testid="monitor-level-db"
          [min]="-96"
          [max]="12"
          [stepSize]="1"
          [majorStepSize]="6"
          [value]="section().levelDb"
          (valueChange)="monitoring.setSection({ levelDb: $event ?? 0 })"
        />
        <label class="text-foreground-muted" for="an-monitor-reference">Reference (dB)</label>
        <xui-numeric-input
          size="sm"
          id="an-monitor-reference"
          aria-label="Reference level in dB"
          data-testid="monitor-reference-db"
          [min]="-96"
          [max]="12"
          [stepSize]="1"
          [majorStepSize]="6"
          [value]="section().referenceDb"
          (valueChange)="monitoring.setSection({ referenceDb: $event ?? 0 })"
        />
        <label class="text-foreground-muted" for="an-monitor-dim">Dim (dB)</label>
        <xui-numeric-input
          size="sm"
          id="an-monitor-dim"
          aria-label="Dim in dB"
          data-testid="monitor-dim-db"
          [min]="-96"
          [max]="0"
          [stepSize]="1"
          [majorStepSize]="6"
          [value]="section().dimDb"
          (valueChange)="monitoring.setSection({ dimDb: $event ?? 0 })"
        />
      </div>
    </ng-template>
  `
})
export class MonitorSectionBar implements OnInit {
  protected readonly monitoring = inject(MonitoringStore);

  protected readonly section = this.monitoring.section;
  /** The level the speakers are at now: the reference or the level, less the dim. */
  protected readonly levelText = computed(() => {
    const section = this.section();
    const db = (section.reference ? section.referenceDb : section.levelDb) + (section.dim ? section.dimDb : 0);
    return `${db > 0 ? '+' : ''}${db.toFixed(0)} dB`;
  });

  ngOnInit(): void {
    void this.monitoring.load();
  }
}
