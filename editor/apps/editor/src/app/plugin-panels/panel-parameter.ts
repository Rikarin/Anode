import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { ParameterInfo } from '@anode/api-client';
import { AnChoice, AnKnob } from '@anode/audio-ui';
import type { PluginPanelHost } from './plugin-panel';

/** A parameter with a name for each value (the generic editor's test; not imported, which would make a cycle). */
export function isChoice(parameter: ParameterInfo): boolean {
  return parameter.steps > 0 && (parameter.valueNames?.length ?? 0) === parameter.steps + 1;
}

/**
 * One parameter in a custom panel, as the generic editor draws it (a drop-down for named choices, else a knob), read
 * and written through the panel's host. `label` overrides the parameter's name ("Ratio" in an operator's row).
 */
@Component({
  selector: 'an-panel-parameter',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnChoice, AnKnob],
  host: { class: 'contents' },
  template: `
    @if (choice()) {
      <an-choice
        [label]="label() || parameter().name"
        [names]="parameter().valueNames ?? []"
        [min]="parameter().min"
        [value]="host().value(parameter().id)"
        [attr.data-parameter]="parameter().id"
        (valueChange)="set($event)"
      />
    } @else {
      <an-knob
        [label]="label() || parameter().name"
        [min]="parameter().min"
        [max]="parameter().max"
        [steps]="parameter().steps"
        [unit]="parameter().unit"
        [defaultValue]="parameter().default"
        [value]="host().value(parameter().id)"
        [attr.data-parameter]="parameter().id"
        (valueChange)="set($event)"
      />
    }
  `
})
export class PanelParameter {
  readonly host = input.required<PluginPanelHost>();
  readonly parameter = input.required<ParameterInfo>();
  readonly label = input('');

  protected readonly choice = computed(() => isChoice(this.parameter()));

  protected set(value: number): void {
    if (value !== this.host().value(this.parameter().id)) {
      this.host().set(this.parameter().id, value);
    }
  }
}
