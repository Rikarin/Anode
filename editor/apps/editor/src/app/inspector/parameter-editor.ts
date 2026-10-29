import { NgComponentOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { ParameterInfo, PluginInfo } from '@anode/api-client';
import { AnChoice, AnKnob } from '@anode/audio-ui';
import { type PluginPanelHost, pluginPanel } from '../plugin-panels/plugin-panel';
import { InstrumentStore } from '../state/instrument.store';

/**
 * A parameter group: the part of its id before the first dot ("filter" of "filter.cutoff"); else, for camelCase ids
 * (the built-in instruments': "osc1Wave", "filterAttack"), its leading word when that names a known group; else "Main".
 */
export function parameterGroups(parameters: readonly ParameterInfo[]): { name: string; parameters: ParameterInfo[] }[] {
  const groups = new Map<string, ParameterInfo[]>();
  for (const parameter of parameters) {
    const name = groupOf(parameter.id);
    groups.set(name, [...(groups.get(name) ?? []), parameter]);
  }
  return [...groups].map(([name, list]) => ({ name: GroupNames[name] ?? capitalise(name), parameters: list }));
}

function groupOf(id: string): string {
  const dot = id.indexOf('.');
  if (dot > 0) {
    return id.slice(0, dot);
  }
  const word = /^[a-z]+\d*/.exec(id)?.[0] ?? '';
  const group = GroupAliases[word] ?? word;
  return group in GroupNames ? group : 'main';
}

const GroupNames: Readonly<Record<string, string>> = {
  main: 'Main',
  osc: 'Oscillators',
  osc1: 'Oscillator 1',
  osc2: 'Oscillator 2',
  filter: 'Filter',
  amp: 'Amp Envelope',
  filterEnv: 'Filter Envelope',
  lfo: 'LFO',
  unison: 'Unison'
};

/** Leading words of camelCase ids that belong to another word's group ("cutoff" is the filter's). */
const GroupAliases: Readonly<Record<string, string>> = {
  cutoff: 'filter',
  resonance: 'filter',
  key: 'filter'
};

function capitalise(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/** Whether a parameter is a choice between names (shown as a drop-down) rather than a knob. */
export function isChoice(parameter: ParameterInfo): boolean {
  return parameter.steps > 0 && (parameter.valueNames?.length ?? 0) === parameter.steps + 1;
}

/**
 * A generic editor for an instrument's parameters, from the plugin's `ParameterInfo`: a knob per continuous or
 * stepped parameter, a drop-down per named choice, grouped by their id's prefix. A knob drag shows its value at once
 * and sends coalesced `setParameter` edits (one undo step per drag), as the faders do.
 */
@Component({
  selector: 'an-parameter-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnChoice, AnKnob, NgComponentOutlet],
  host: { class: 'flex flex-col gap-3', role: 'group', '[attr.aria-label]': 'plugin().name + " parameters"' },
  template: `
    @if (panel(); as p) {
      <ng-container *ngComponentOutlet="p; inputs: { host: host() }" />
    }
    @for (group of groups(); track group.name) {
      <section class="flex flex-col gap-1" [attr.aria-label]="group.name">
        <h4 class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">{{ group.name }}</h4>
        <div class="flex flex-wrap items-end gap-x-1 gap-y-2">
          @for (parameter of group.parameters; track parameter.id) {
            @if (choice(parameter)) {
              <an-choice
                [label]="parameter.name"
                [names]="parameter.valueNames ?? []"
                [min]="parameter.min"
                [value]="value(parameter)"
                [attr.data-parameter]="parameter.id"
                (valueChange)="set(parameter, $event)"
              />
            } @else {
              <an-knob
                [label]="parameter.name"
                [min]="parameter.min"
                [max]="parameter.max"
                [steps]="parameter.steps"
                [unit]="parameter.unit"
                [defaultValue]="parameter.default"
                [value]="value(parameter)"
                [attr.data-parameter]="parameter.id"
                (valueChange)="set(parameter, $event)"
              />
            }
          }
        </div>
      </section>
    } @empty {
      <p class="text-foreground-muted text-xs">{{ plugin().name }} has no parameters.</p>
    }
  `
})
export class ParameterEditor {
  private readonly instruments = inject(InstrumentStore);
  readonly trackId = input.required<string>();
  readonly plugin = input.required<PluginInfo>();

  protected readonly groups = computed(() => parameterGroups(this.plugin().parameters));
  protected readonly choice = isChoice;
  /** A custom panel for the instrument (drawbars, the FM algorithm…) when its parameters match one; else none. */
  protected readonly panel = computed(() => pluginPanel(this.plugin()));
  protected readonly host = computed<PluginPanelHost>(() => ({
    plugin: this.plugin(),
    value: id => this.instruments.value(this.trackId(), id),
    set: (id, value) => void this.instruments.set(this.trackId(), id, value)
  }));

  protected value(parameter: ParameterInfo): number {
    return this.instruments.value(this.trackId(), parameter.id);
  }

  protected set(parameter: ParameterInfo, value: number): void {
    if (value !== this.value(parameter)) {
      void this.instruments.set(this.trackId(), parameter.id, value);
    }
  }
}
