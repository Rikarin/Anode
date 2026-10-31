import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import type {
  ControlAssignment,
  ControlInput,
  ControlInputKind,
  ControlTarget,
  ControlTargetKind,
  ControlTransportAction,
  ControlValueMode,
  TrackSwitchAction
} from '@anode/api-client';
import { XuiInputImports } from '@xui/input';
import { targetsOf } from '../automation/targets';
import { KeyCommands } from '../keys/key-commands';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import {
  hasNumber,
  InputKinds,
  maxNumber,
  retarget,
  SmartKnobs,
  TargetKinds,
  TrackSwitches,
  TransportActions,
  ValueModes
} from './assignments';
import { ControlStore } from './control.store';
import { Band, Field, selectValue } from './styles';

/** What the inspector reports: a change to the assignment's fields (the store sends the whole set). */
export type AssignmentChange = Partial<Omit<ControlAssignment, 'id'>>;

const Channels = Array.from({ length: 16 }, (_, i) => i + 1);

/**
 * The Controller Assignments window's inspector (docs/09-phase-8.md, workstream C): one assignment's name and mode,
 * its input (port, kind, channel, number), its target (a parameter or Smart Control on a track or the selected one, a
 * track switch, a transport button, a key command or a mode switch), and how values map (value mode, range, pickup).
 * Each edit is reported as a change; nothing is kept here.
 */
@Component({
  selector: 'an-assignment-inspector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, XuiInputImports],
  host: { class: 'flex flex-col text-xs', role: 'form', 'aria-label': 'Assignment', 'data-testid': 'assignment-inspector' },
  template: `
    @let a = assignment();
    <h3 [class]="band">Assignment</h3>
    <div [class]="grid">
      <label class="text-foreground-muted" for="assignment-name">Name</label>
      <input
        #name
        id="assignment-name"
        xuiInput
        class="h-6 text-xs"
        data-testid="assignment-name"
        [value]="a.name ?? ''"
        placeholder="Untitled"
        (change)="changed.emit({ name: name.value.trim() || null })"
      />
      <label class="text-foreground-muted" for="assignment-location">Zone ▸ Mode</label>
      <select id="assignment-location" [class]="field" data-testid="assignment-location" (change)="moved.emit(value($event))">
        @for (option of locations(); track option.modeId) {
          <option [value]="option.modeId" [selected]="option.modeId === modeId()">{{ option.label }}</option>
        }
      </select>
    </div>

    <h3 [class]="band">Input</h3>
    <div [class]="grid">
      <label class="text-foreground-muted" for="assignment-port">Port</label>
      <select id="assignment-port" [class]="field" data-testid="assignment-port" (change)="setInput({ port: value($event) || null })">
        <option value="" [selected]="!a.input.port">Any</option>
        @for (port of ports(); track port.id) {
          <option [value]="port.id" [selected]="port.id === a.input.port">{{ port.name }}</option>
        }
      </select>
      <label class="text-foreground-muted" for="assignment-kind">Message</label>
      <select id="assignment-kind" [class]="field" data-testid="assignment-kind" (change)="setKind(value($event))">
        @for (kind of inputKinds; track kind.value) {
          <option [value]="kind.value" [selected]="kind.value === a.input.kind">{{ kind.label }}</option>
        }
      </select>
      <label class="text-foreground-muted" for="assignment-channel">Channel</label>
      <select
        id="assignment-channel"
        [class]="field"
        data-testid="assignment-channel"
        (change)="setInput({ channel: value($event) ? +value($event) : null })"
      >
        <option value="" [selected]="(a.input.channel ?? null) === null">Any</option>
        @for (channel of channels; track channel) {
          <option [value]="channel" [selected]="channel === a.input.channel">{{ channel }}</option>
        }
      </select>
      @if (numbered()) {
        <label class="text-foreground-muted" for="assignment-number">{{ numberLabel() }}</label>
        <input
          #number
          id="assignment-number"
          xuiInput
          type="number"
          class="readout h-6 w-24 text-xs"
          min="0"
          data-testid="assignment-number"
          [attr.max]="max()"
          [value]="a.input.number"
          [attr.aria-invalid]="invalidNumber(number.value) ? 'true' : null"
          (change)="setNumber(number.value)"
        />
      }
    </div>

    <h3 [class]="band">Target</h3>
    <div [class]="grid">
      <label class="text-foreground-muted" for="assignment-target-kind">Kind</label>
      <select
        id="assignment-target-kind"
        [class]="field"
        data-testid="assignment-target-kind"
        (change)="setTargetKind(value($event))"
      >
        @for (kind of targetKinds; track kind.value) {
          <option [value]="kind.value" [selected]="kind.value === a.target.kind">{{ kind.label }}</option>
        }
      </select>
      @switch (a.target.kind) {
        @case ('parameter') {
          <ng-container [ngTemplateOutlet]="trackField" />
          <label class="text-foreground-muted" for="assignment-target-address">Parameter</label>
          <select
            id="assignment-target-address"
            [class]="field"
            data-testid="assignment-target-address"
            (change)="setTarget({ address: value($event) })"
          >
            @for (group of parameterGroups(); track group.name) {
              <optgroup [label]="group.name">
                @for (option of group.options; track option.address) {
                  <option [value]="option.address" [selected]="option.address === a.target.address">
                    {{ option.name }}
                  </option>
                }
              </optgroup>
            }
          </select>
        }
        @case ('smartControl') {
          <span class="text-foreground-muted">Track</span>
          <span class="text-foreground">Selected Track</span>
          <label class="text-foreground-muted" for="assignment-target-knob">Control</label>
          <select
            id="assignment-target-knob"
            [class]="field"
            data-testid="assignment-target-knob"
            (change)="setTarget({ address: value($event) })"
          >
            @for (knob of knobs; track knob; let i = $index) {
              <option [value]="knob" [selected]="knob === a.target.address">Smart Control {{ i + 1 }}</option>
            }
          </select>
        }
        @case ('trackSwitch') {
          <ng-container [ngTemplateOutlet]="trackField" />
          <label class="text-foreground-muted" for="assignment-target-switch">Switch</label>
          <select
            id="assignment-target-switch"
            [class]="field"
            data-testid="assignment-target-switch"
            (change)="setTarget({ switch: switchOf(value($event)) })"
          >
            @for (s of switches; track s.value) {
              <option [value]="s.value" [selected]="s.value === a.target.switch">{{ s.label }}</option>
            }
          </select>
        }
        @case ('transport') {
          <label class="text-foreground-muted" for="assignment-target-transport">Button</label>
          <select
            id="assignment-target-transport"
            [class]="field"
            data-testid="assignment-target-transport"
            (change)="setTarget({ transport: transportOf(value($event)) })"
          >
            @for (action of transportActions; track action.value) {
              <option [value]="action.value" [selected]="action.value === a.target.transport">{{ action.label }}</option>
            }
          </select>
        }
        @case ('keyCommand') {
          <label class="text-foreground-muted" for="assignment-target-key">Command</label>
          <select
            id="assignment-target-key"
            [class]="field"
            data-testid="assignment-target-key"
            (change)="setTarget({ keyCommandId: value($event) })"
          >
            @for (command of keyCommands; track command.id) {
              <option [value]="command.id" [selected]="command.id === a.target.keyCommandId">
                {{ command.group }}: {{ command.label }}
              </option>
            }
          </select>
        }
        @case ('zoneMode') {
          <label class="text-foreground-muted" for="assignment-target-mode">Switch to</label>
          <select
            id="assignment-target-mode"
            [class]="field"
            data-testid="assignment-target-mode"
            (change)="setZoneMode(value($event))"
          >
            @for (option of locations(); track option.modeId) {
              <option [value]="option.modeId" [selected]="option.modeId === a.target.modeId">{{ option.label }}</option>
            }
          </select>
        }
      }
    </div>
    <ng-template #trackField>
      <label class="text-foreground-muted" for="assignment-target-track">Track</label>
      <select
        id="assignment-target-track"
        [class]="field"
        data-testid="assignment-target-track"
        (change)="setTrack(value($event) || null)"
      >
        <option value="" [selected]="!a.target.trackId">Selected Track</option>
        @for (track of tracks(); track track.id) {
          <option [value]="track.id" [selected]="track.id === a.target.trackId">{{ track.name }}</option>
        }
      </select>
    </ng-template>

    <h3 [class]="band">Value</h3>
    <div [class]="grid">
      <label class="text-foreground-muted" for="assignment-mode">Mode</label>
      <select id="assignment-mode" [class]="field" data-testid="assignment-value-mode" (change)="setMode(value($event))">
        @for (mode of valueModes; track mode.value) {
          <option [value]="mode.value" [selected]="mode.value === a.mode">{{ mode.label }}</option>
        }
      </select>
      <span class="text-foreground-muted">Range</span>
      <div class="flex items-center gap-1.5">
        <input
          #minField
          xuiInput
          type="number"
          class="readout h-6 w-16 text-xs"
          aria-label="Range minimum (percent)"
          data-testid="assignment-min"
          min="0"
          max="100"
          [value]="percent(a.min)"
          (change)="setRange('min', minField.value)"
        />
        <span class="text-foreground-muted">to</span>
        <input
          #maxField
          xuiInput
          type="number"
          class="readout h-6 w-16 text-xs"
          aria-label="Range maximum (percent)"
          data-testid="assignment-max"
          min="0"
          max="100"
          [value]="percent(a.max)"
          (change)="setRange('max', maxField.value)"
        />
        <span class="text-foreground-muted">%</span>
        @if (a.min > a.max) {
          <span class="text-foreground ms-1">(inverted)</span>
        }
      </div>
      <span></span>
      <label class="text-foreground flex items-center gap-2">
        <input
          type="checkbox"
          class="accent-(--color-primary)"
          data-testid="assignment-pickup"
          [checked]="a.pickup"
          (change)="changed.emit({ pickup: !a.pickup })"
        />
        Pickup: no effect until the control passes the value
      </label>
    </div>
  `
})
export class AssignmentInspector {
  private readonly control = inject(ControlStore);
  private readonly project = inject(ProjectStore);
  private readonly plugins = inject(PluginsStore);

  readonly assignment = input.required<ControlAssignment>();
  /** The mode it is in. */
  readonly modeId = input.required<string>();
  readonly changed = output<AssignmentChange>();
  /** Moved to another mode. */
  readonly moved = output<string>();

  protected readonly band = Band;
  protected readonly field = Field;
  protected readonly grid = 'grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-1.5 p-3';
  protected readonly value = selectValue;
  protected readonly inputKinds = InputKinds;
  protected readonly targetKinds = TargetKinds;
  protected readonly valueModes = ValueModes;
  protected readonly switches = TrackSwitches;
  protected readonly transportActions = TransportActions;
  protected readonly keyCommands = KeyCommands;
  protected readonly knobs = SmartKnobs;
  protected readonly channels = Channels;

  protected readonly ports = this.control.inputs;
  protected readonly tracks = computed(() => this.project.trackList().filter(t => t.kind !== 'stack'));
  protected readonly numbered = computed(() => hasNumber(this.assignment().input.kind));
  protected readonly max = computed(() => maxNumber(this.assignment().input.kind));
  protected readonly numberLabel = computed(() => {
    const kind = this.assignment().input.kind;
    return kind === 'note' ? 'Key' : kind === 'nrpn' ? 'Parameter' : 'Controller';
  });
  protected readonly locations = computed(() =>
    this.control
      .assignments()
      .zones.flatMap(zone => zone.modes.map(mode => ({ modeId: mode.id, zoneId: zone.id, label: `${zone.name} ▸ ${mode.name}` })))
  );
  /**
   * The parameters a target can name: a fixed track's (its main controls, sends and plug-in parameters, as automation
   * lists them), or for the selected track, the controls every track has (and the one set, if it is another).
   */
  protected readonly parameterGroups = computed(() => {
    const target = this.assignment().target;
    const track = target.trackId ? this.project.track(target.trackId) : undefined;
    const list = track
      ? targetsOf(track, id => this.plugins.plugin(id)).filter(t => !t.target.startsWith('smartControl:'))
      : [
          { target: 'volume', name: 'Volume', group: 'Main' },
          { target: 'pan', name: 'Pan', group: 'Main' },
          { target: 'mute', name: 'Mute', group: 'Main' }
        ];
    const groups = new Map<string, { address: string; name: string }[]>();
    for (const t of list) {
      groups.set(t.group, [...(groups.get(t.group) ?? []), { address: t.target, name: t.name }]);
    }
    const address = target.address;
    if (address && !list.some(t => t.target === address)) {
      groups.set('Other', [{ address, name: this.control.parameterName(target.trackId, address) }]);
    }
    return [...groups].map(([name, options]) => ({ name, options }));
  });

  protected percent(value: number): number {
    return Math.round(value * 1000) / 10;
  }

  protected invalidNumber(text: string): boolean {
    const n = Number(text);
    return !(Number.isInteger(n) && n >= 0 && n <= this.max());
  }

  protected setInput(change: Partial<ControlInput>): void {
    this.changed.emit({ input: { ...this.assignment().input, ...change } });
  }

  protected setKind(kind: string): void {
    const k = kind as ControlInputKind;
    const number = Math.min(this.assignment().input.number, maxNumber(k));
    this.setInput({ kind: k, number });
  }

  protected setNumber(text: string): void {
    if (!this.invalidNumber(text)) {
      this.setInput({ number: Number(text) });
    }
  }

  protected setTarget(change: Partial<ControlTarget>): void {
    this.changed.emit({ target: { ...this.assignment().target, ...change } });
  }

  protected setTargetKind(kind: string): void {
    this.changed.emit({ target: retarget(this.assignment().target, kind as ControlTargetKind, this.control.assignments()) });
  }

  /** Another track: a parameter keeps its address only when that track has it (volume, pan and mute always). */
  protected setTrack(trackId: string | null): void {
    const target = this.assignment().target;
    if (target.kind !== 'parameter') {
      this.setTarget({ trackId });
      return;
    }
    const keeps = ['volume', 'pan', 'mute'].includes(target.address ?? '');
    this.setTarget({ trackId, address: keeps ? (target.address ?? 'volume') : 'volume' });
  }

  protected setZoneMode(modeId: string): void {
    const location = this.locations().find(l => l.modeId === modeId);
    if (location) {
      this.setTarget({ zoneId: location.zoneId, modeId });
    }
  }

  protected setMode(mode: string): void {
    this.changed.emit({ mode: mode as ControlValueMode });
  }

  protected setRange(edge: 'min' | 'max', text: string): void {
    const n = Number(text);
    if (Number.isFinite(n)) {
      this.changed.emit({ [edge]: Math.min(1, Math.max(0, n / 100)) });
    }
  }

  protected switchOf(value: string): TrackSwitchAction {
    return value as TrackSwitchAction;
  }

  protected transportOf(value: string): ControlTransportAction {
    return value as ControlTransportAction;
  }
}
