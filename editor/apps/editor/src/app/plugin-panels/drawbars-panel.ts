import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { ParameterInfo } from '@anode/api-client';
import { AnDrawbar, DrawbarFootages } from '@anode/audio-ui';
import { PanelParameter } from './panel-parameter';
import { panelBinding } from './panel-patterns';
import type { PluginPanelHost } from './plugin-panel';

/** A drawbar parameter's value as a setting 0…8 (its range may be 0…8 or 0…1). */
export function drawbarSetting(parameter: ParameterInfo, value: number): number {
  const span = parameter.max - parameter.min;
  return span === 0 ? 0 : Math.round(((value - parameter.min) / span) * 8);
}

/** A setting 0…8 as the drawbar parameter's value. */
export function drawbarValue(parameter: ParameterInfo, setting: number): number {
  return parameter.min + (Math.min(8, Math.max(0, setting)) / 8) * (parameter.max - parameter.min);
}

/**
 * The Drawbar Organ's panel: its nine drawbars by footage (16′ … 1′, Hammond's brown, white and black tabs), then the
 * percussion, key click, scanner vibrato/chorus and rotary controls the organ has.
 */
@Component({
  selector: 'an-drawbars-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnDrawbar, PanelParameter],
  host: {
    class: 'flex flex-wrap items-start gap-4',
    role: 'group',
    'aria-label': 'Drawbars',
    'data-testid': 'drawbars-panel'
  },
  template: `
    <div class="flex gap-0.5" role="group" aria-label="Registration">
      @for (bar of bars(); track bar.parameter.id) {
        <an-drawbar
          [label]="bar.label"
          [tone]="bar.tone"
          [value]="setting(bar.parameter)"
          [attr.data-parameter]="bar.parameter.id"
          (valueChange)="set(bar.parameter, $event)"
        />
      }
    </div>
    @if (extras().length > 0) {
      <div class="flex flex-wrap items-end gap-x-1 gap-y-2">
        @for (parameter of extras(); track parameter.id) {
          <an-panel-parameter [host]="host()" [parameter]="parameter" />
        }
      </div>
    }
  `
})
export class DrawbarsPanel {
  readonly host = input.required<PluginPanelHost>();

  private readonly binding = computed(() => panelBinding(this.host().plugin, 'drawbars'));
  protected readonly bars = computed(() =>
    (this.binding()?.series['drawbar'] ?? []).map((parameter, i) => ({
      parameter,
      label: DrawbarFootages[i]?.label ?? `${i + 1}`,
      tone: DrawbarFootages[i]?.tone ?? 'white'
    }))
  );
  /** Percussion, click, scanner and rotary, in that order, where the organ has them. */
  protected readonly extras = computed(() => {
    const roles = this.binding()?.roles ?? {};
    return ['percussion', 'click', 'scanner', 'rotarySpeed', 'rotaryMix', 'drive']
      .map(role => roles[role])
      .filter((p): p is ParameterInfo => !!p);
  });

  protected setting(parameter: ParameterInfo): number {
    return drawbarSetting(parameter, this.host().value(parameter.id));
  }

  protected set(parameter: ParameterInfo, setting: number): void {
    if (setting !== this.setting(parameter)) {
      this.host().set(parameter.id, drawbarValue(parameter, setting));
    }
  }
}
