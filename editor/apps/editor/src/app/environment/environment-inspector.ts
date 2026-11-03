import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import {
  type DelayTap,
  type EnvironmentObject,
  EnvironmentObjectKinds,
  type EnvironmentParameter,
  type TransformCondition,
  type TransformConditionOp,
  type TransformOperation,
  type TransformOperationKind
} from '@anode/api-client';
import { XuiButtonImports } from '@xui/button';
import { Band, Field, selectValue } from '../control/styles';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { EnvironmentGraphStore } from './environment-graph.store';
import { ConditionFields, EnvironmentEffectIds, FaderMessages, MaxTaps, OperationFields } from './environment-model';

const ConditionOps: readonly { op: TransformConditionOp; name: string }[] = [
  { op: 'all', name: 'All' },
  { op: 'equal', name: '=' },
  { op: 'unequal', name: '≠' },
  { op: 'lessEqual', name: '≤' },
  { op: 'greaterEqual', name: '≥' },
  { op: 'inside', name: 'Inside' },
  { op: 'outside', name: 'Outside' }
];
const OperationOps: readonly { op: TransformOperationKind; name: string }[] = [
  { op: 'thru', name: 'Thru' },
  { op: 'fix', name: 'Fix' },
  { op: 'add', name: 'Add' },
  { op: 'subtract', name: 'Sub' },
  { op: 'min', name: 'Min' },
  { op: 'max', name: 'Max' },
  { op: 'flip', name: 'Flip' },
  { op: 'multiply', name: 'Mul' },
  { op: 'divide', name: 'Div' },
  { op: 'scale', name: 'Scale' },
  { op: 'range', name: 'Range' },
  { op: 'random', name: 'Random' },
  { op: 'plusMinusRandom', name: '±Rand' }
];
const FieldNames: Record<string, string> = {
  status: 'Status',
  channel: 'Channel',
  pitch: 'Pitch',
  velocity: 'Velocity'
};

/** The control-target addresses every strip has. */
const StripAddresses = [
  { address: 'volume', name: 'Volume' },
  { address: 'pan', name: 'Pan' }
];

/** A number from a field, or null when it does not parse. */
function numberOf(event: Event): number | null {
  const text = selectValue(event).trim();
  const value = Number(text);
  return text !== '' && Number.isFinite(value) ? value : null;
}

/**
 * The Environment window's inspector (DESIGN.md "Inspector", the control windows' `Field` and `Band`): the selected
 * object's name, layer and numeric parameters (the arpeggiator's and chord memoriser's are their MIDI FX's), then its
 * kind's own settings — a port, a track, a parameter address, a transformer's conditions and operations, a delay
 * line's taps, a chord memoriser's chord — or the selected cable. Each change is one `setEnvironmentObject`.
 */
@Component({
  selector: 'an-environment-inspector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports],
  host: { class: 'border-hairline bg-surface flex w-64 shrink-0 flex-col overflow-y-auto border-s' },
  template: `
    @if (object(); as o) {
      <h3 [class]="band">{{ kindName() }}</h3>
      <div class="grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-1.5 p-3 text-xs">
        <label class="text-foreground-muted" for="env-name">Name</label>
        <input
          id="env-name"
          [class]="field"
          data-testid="environment-name"
          [value]="o.name"
          (change)="setName(o, $event)"
          (keydown.enter)="blur($event)"
        />
        @if (store.layers().length > 1) {
          <label class="text-foreground-muted" for="env-layer">Layer</label>
          <select
            id="env-layer"
            [class]="field"
            (change)="store.setObject(o.id, { layerId: value($event) }, 'Move to Layer')"
          >
            @for (layer of store.layers(); track layer.id) {
              <option [value]="layer.id" [selected]="layer.id === o.layerId">{{ layer.name }}</option>
            }
          </select>
        }
        @for (spec of specs(); track spec.id) {
          <label class="text-foreground-muted" [for]="'env-p-' + spec.id">{{ spec.name }}</label>
          @if (choices(spec); as names) {
            <select [id]="'env-p-' + spec.id" [class]="field" (change)="setParameter(o, spec, $event)">
              @for (name of names; track $index) {
                <option [value]="spec.min + $index" [selected]="spec.min + $index === parameter(o, spec)">
                  {{ name }}
                </option>
              }
            </select>
          } @else {
            <input
              [id]="'env-p-' + spec.id"
              type="number"
              [class]="field + ' readout'"
              [attr.data-testid]="'environment-parameter-' + spec.id"
              [min]="spec.min"
              [max]="spec.max"
              [step]="spec.max - spec.min <= 1 ? 0.01 : 1"
              [value]="parameter(o, spec)"
              (change)="setParameter(o, spec, $event)"
            />
          }
        }
        @switch (o.kind) {
          @case ('physicalInput') {
            <label class="text-foreground-muted" for="env-port">Port</label>
            <select id="env-port" [class]="field" (change)="store.setObject(o.id, { port: value($event) }, 'Set Port')">
              <option value="" [selected]="!o.port">Every input</option>
              @for (port of plugins.devices().inputs; track port.id) {
                <option [value]="port.id" [selected]="port.id === o.port">{{ port.name }}</option>
              }
            </select>
          }
          @case ('midiPort') {
            <label class="text-foreground-muted" for="env-port">Port</label>
            <select id="env-port" [class]="field" (change)="store.setObject(o.id, { port: value($event) }, 'Set Port')">
              <option value="" [selected]="!o.port">None</option>
              @for (port of plugins.devices().outputs; track port.id) {
                <option [value]="port.id" [selected]="port.id === o.port">{{ port.name }}</option>
              }
            </select>
          }
          @case ('trackInput') {
            <label class="text-foreground-muted" for="env-track">Track</label>
            <select
              id="env-track"
              [class]="field"
              data-testid="environment-track"
              (change)="store.setObject(o.id, { trackId: value($event) }, 'Set Track')"
            >
              <option value="" [selected]="!o.trackId">None</option>
              @for (track of midiTracks(); track track.id) {
                <option [value]="track.id" [selected]="track.id === o.trackId">{{ track.name }}</option>
              }
            </select>
          }
          @case ('controlTarget') {
            <label class="text-foreground-muted" for="env-track">Strip</label>
            <select
              id="env-track"
              [class]="field"
              (change)="store.setObject(o.id, { trackId: value($event) }, 'Set Strip')"
            >
              <option value="" [selected]="!o.trackId">Master</option>
              @for (track of project.trackList(); track track.id) {
                <option [value]="track.id" [selected]="track.id === o.trackId">{{ track.name }}</option>
              }
            </select>
            <label class="text-foreground-muted" for="env-address">Parameter</label>
            <select
              id="env-address"
              [class]="field"
              (change)="store.setObject(o.id, { address: value($event) }, 'Set Parameter')"
            >
              <option value="" [selected]="!o.address">None</option>
              @for (target of addresses(); track target.address) {
                <option [value]="target.address" [selected]="target.address === o.address">{{ target.name }}</option>
              }
            </select>
          }
          @case ('chordMemorizer') {
            <label class="text-foreground-muted" for="env-chord">Chord</label>
            <input
              id="env-chord"
              [class]="field + ' readout'"
              placeholder="0 4 7"
              [value]="(o.chord ?? []).join(' ')"
              (change)="setChord(o, $event)"
              (keydown.enter)="blur($event)"
            />
          }
        }
      </div>
      @if (o.kind === 'transformer') {
        <h3 [class]="band">Conditions</h3>
        <div class="flex flex-col gap-1 p-2 text-xs" role="group" aria-label="Conditions">
          @for (c of o.conditions ?? []; track $index; let i = $index) {
            <div class="flex gap-1">
              <select
                [class]="field"
                aria-label="Condition field"
                (change)="setCondition(o, i, { field: fieldOf($event) })"
              >
                @for (f of conditionFields; track f) {
                  <option [value]="f" [selected]="f === c.field">{{ fieldNames[f] }}</option>
                }
              </select>
              <select [class]="field" aria-label="Condition" (change)="setCondition(o, i, { op: conditionOp($event) })">
                @for (op of conditionOps; track op.op) {
                  <option [value]="op.op" [selected]="op.op === c.op">{{ op.name }}</option>
                }
              </select>
              <input
                [class]="field + ' readout'"
                aria-label="Value"
                [value]="c.a"
                (change)="setCondition(o, i, { a: num($event, c.a) })"
              />
              <input
                [class]="field + ' readout'"
                aria-label="Second value"
                [value]="c.b"
                (change)="setCondition(o, i, { b: num($event, c.b) })"
              />
              <button
                xuiButton
                type="button"
                size="sm"
                variant="ghost"
                aria-label="Remove condition"
                (click)="removeCondition(o, i)"
              >
                −
              </button>
            </div>
          }
          <button
            xuiButton
            type="button"
            size="sm"
            variant="outline"
            data-testid="environment-add-condition"
            (click)="addCondition(o)"
          >
            Add Condition
          </button>
        </div>
        <h3 [class]="band">Operations</h3>
        <div class="flex flex-col gap-1 p-2 text-xs" role="group" aria-label="Operations">
          @for (c of o.operations ?? []; track $index; let i = $index) {
            <div class="flex gap-1">
              <select
                [class]="field"
                aria-label="Operation field"
                (change)="setOperation(o, i, { field: fieldOf($event) })"
              >
                @for (f of operationFields; track f) {
                  <option [value]="f" [selected]="f === c.field">{{ fieldNames[f] }}</option>
                }
              </select>
              <select [class]="field" aria-label="Operation" (change)="setOperation(o, i, { op: operationOp($event) })">
                @for (op of operationOps; track op.op) {
                  <option [value]="op.op" [selected]="op.op === c.op">{{ op.name }}</option>
                }
              </select>
              <input
                [class]="field + ' readout'"
                aria-label="Value"
                [value]="c.a"
                (change)="setOperation(o, i, { a: num($event, c.a) })"
              />
              <input
                [class]="field + ' readout'"
                aria-label="Second value"
                [value]="c.b"
                (change)="setOperation(o, i, { b: num($event, c.b) })"
              />
              <button
                xuiButton
                type="button"
                size="sm"
                variant="ghost"
                aria-label="Remove operation"
                (click)="removeOperation(o, i)"
              >
                −
              </button>
            </div>
          }
          <button
            xuiButton
            type="button"
            size="sm"
            variant="outline"
            data-testid="environment-add-operation"
            (click)="addOperation(o)"
          >
            Add Operation
          </button>
        </div>
      }
      @if (o.kind === 'delayLine') {
        <h3 [class]="band">Taps</h3>
        <div class="flex flex-col gap-1 p-2 text-xs" role="group" aria-label="Taps">
          <div
            class="text-foreground-muted grid grid-cols-[1fr_1fr_1fr_1.5rem] gap-1 text-[10px] font-semibold uppercase"
          >
            <span>Ticks</span><span>Transp.</span><span>Velocity</span><span></span>
          </div>
          @for (tap of o.taps ?? []; track $index; let i = $index) {
            <div class="grid grid-cols-[1fr_1fr_1fr_1.5rem] gap-1">
              <input
                [class]="field + ' readout'"
                aria-label="Delay in ticks"
                [value]="tap.delayTicks"
                (change)="setTap(o, i, { delayTicks: num($event, tap.delayTicks) })"
              />
              <input
                [class]="field + ' readout'"
                aria-label="Transpose"
                [value]="tap.transpose"
                (change)="setTap(o, i, { transpose: num($event, tap.transpose) })"
              />
              <input
                [class]="field + ' readout'"
                aria-label="Velocity offset"
                [value]="tap.velocity"
                (change)="setTap(o, i, { velocity: num($event, tap.velocity) })"
              />
              <button
                xuiButton
                type="button"
                size="sm"
                variant="ghost"
                class="px-0"
                aria-label="Remove tap"
                (click)="removeTap(o, i)"
              >
                −
              </button>
            </div>
          }
          <button
            xuiButton
            type="button"
            size="sm"
            variant="outline"
            [disabled]="(o.taps?.length ?? 0) >= maxTaps"
            (click)="addTap(o)"
          >
            Add Tap
          </button>
        </div>
      }
      <div class="border-hairline mt-auto border-t p-2">
        <button
          xuiButton
          type="button"
          size="sm"
          variant="ghost"
          color="error"
          data-testid="environment-delete"
          (click)="store.deleteSelection()"
        >
          Delete Object
        </button>
      </div>
    } @else if (cable(); as c) {
      <h3 [class]="band">Cable</h3>
      <dl class="grid grid-cols-[3rem_1fr] gap-x-2 gap-y-1 p-3 text-xs" data-testid="environment-cable">
        <dt class="text-foreground-muted">From</dt>
        <dd class="text-foreground">{{ c.from }}</dd>
        <dt class="text-foreground-muted">To</dt>
        <dd class="text-foreground">{{ c.to }}</dd>
      </dl>
      <div class="p-2">
        <button xuiButton type="button" size="sm" variant="ghost" color="error" (click)="store.deleteSelection()">
          Disconnect
        </button>
      </div>
    } @else {
      <p class="text-foreground-muted p-3 text-xs">
        Select an object to change it. Drag from an outlet to an inlet to cable objects; Delete removes the selection.
      </p>
    }
  `
})
export class EnvironmentInspector {
  protected readonly store = inject(EnvironmentGraphStore);
  protected readonly plugins = inject(PluginsStore);
  protected readonly project = inject(ProjectStore);

  readonly object = input<EnvironmentObject | null>(null);

  protected readonly band = Band;
  protected readonly field = Field;
  protected readonly maxTaps = MaxTaps;
  protected readonly conditionFields = ConditionFields;
  protected readonly operationFields = OperationFields;
  protected readonly conditionOps = ConditionOps;
  protected readonly operationOps = OperationOps;
  protected readonly fieldNames = FieldNames;
  protected readonly value = selectValue;

  protected readonly kindName = computed(() => {
    const o = this.object();
    return o ? EnvironmentObjectKinds[o.kind].name : '';
  });
  protected readonly specs = computed(() => {
    const o = this.object();
    return o ? this.store.specs(o.kind) : [];
  });
  /** Tracks a track input can play: instrument and external MIDI tracks, Drum Machine Designers, Session Players. */
  protected readonly midiTracks = computed(() =>
    this.project.trackList().filter(t => t.kind !== 'audio' && t.kind !== 'aux' && t.kind !== 'vca')
  );
  /** A control target's addresses on its strip: volume, pan and its inserts' parameters. */
  protected readonly addresses = computed(() => {
    const o = this.object();
    const strip = o?.trackId ? this.project.track(o.trackId)?.strip : this.project.master();
    const inserts = (strip?.inserts ?? []).flatMap(insert =>
      (this.plugins.plugin(insert.pluginId)?.parameters ?? []).map(p => ({
        address: `insert:${insert.id}:${p.id}`,
        name: `${insert.name}: ${p.name}`
      }))
    );
    return [...StripAddresses, ...inserts];
  });
  /** The selected cable, described ("Transformer · Matched"). */
  protected readonly cable = computed(() => {
    const selection = this.store.selection();
    if (selection?.kind !== 'cable') {
      return null;
    }
    const env = this.store.environment();
    const cable = env.cables.find(c => c.id === selection.id);
    const from = env.objects.find(o => o.id === cable?.fromObjectId);
    const to = env.objects.find(o => o.id === cable?.toObjectId);
    if (!cable || !from || !to) {
      return null;
    }
    return {
      from: `${from.name} · ${EnvironmentObjectKinds[from.kind].outlets[cable.outlet] ?? ''}`,
      to: `${to.name} · ${EnvironmentObjectKinds[to.kind].inlets[cable.inlet] ?? ''}`
    };
  });

  /** A parameter's value names: the fader's messages, a MIDI FX's choices; null for a number. */
  protected choices(spec: EnvironmentParameter): readonly string[] | null {
    const o = this.object();
    if (o?.kind === 'fader' && spec.id === 'message') {
      return FaderMessages;
    }
    const effect = o ? EnvironmentEffectIds[o.kind] : undefined;
    const info = effect ? this.plugins.plugin(effect)?.parameters.find(p => p.id === spec.id) : undefined;
    return info?.valueNames ?? null;
  }

  protected parameter(object: EnvironmentObject, spec: EnvironmentParameter): number {
    return object.parameters[spec.id] ?? spec.default;
  }

  protected setName(object: EnvironmentObject, event: Event): void {
    const name = selectValue(event).trim();
    if (name && name !== object.name) {
      void this.store.setObject(object.id, { name }, 'Rename Object');
    }
  }

  protected setParameter(object: EnvironmentObject, spec: EnvironmentParameter, event: Event): void {
    const value = numberOf(event);
    if (value !== null) {
      void this.store.setObject(
        object.id,
        { parameters: { [spec.id]: Math.min(spec.max, Math.max(spec.min, value)) } },
        `Set ${spec.name}`
      );
    }
  }

  protected setChord(object: EnvironmentObject, event: Event): void {
    const notes = selectValue(event)
      .split(/[\s,]+/)
      .filter(Boolean)
      .map(Number);
    if (notes.every(Number.isInteger)) {
      void this.store.setObject(object.id, { chord: notes }, 'Set Chord');
    }
  }

  protected num(event: Event, fallback: number): number {
    return numberOf(event) ?? fallback;
  }

  protected fieldOf(event: Event): TransformCondition['field'] {
    return selectValue(event) as TransformCondition['field'];
  }

  protected conditionOp(event: Event): TransformConditionOp {
    return selectValue(event) as TransformConditionOp;
  }

  protected operationOp(event: Event): TransformOperationKind {
    return selectValue(event) as TransformOperationKind;
  }

  protected addCondition(object: EnvironmentObject): void {
    const conditions = [...(object.conditions ?? []), { field: 'status', op: 'equal', a: 144, b: 0 } as const];
    void this.store.setObject(object.id, { conditions }, 'Add Condition');
  }

  protected setCondition(object: EnvironmentObject, index: number, change: Partial<TransformCondition>): void {
    const conditions = (object.conditions ?? []).map((c, i) => (i === index ? { ...c, ...change } : c));
    void this.store.setObject(object.id, { conditions }, 'Change Condition');
  }

  protected removeCondition(object: EnvironmentObject, index: number): void {
    const conditions = (object.conditions ?? []).filter((_, i) => i !== index);
    void this.store.setObject(object.id, { conditions }, 'Delete Condition');
  }

  protected addOperation(object: EnvironmentObject): void {
    const operations = [...(object.operations ?? []), { field: 'pitch', op: 'add', a: 12, b: 0 } as const];
    void this.store.setObject(object.id, { operations }, 'Add Operation');
  }

  protected setOperation(object: EnvironmentObject, index: number, change: Partial<TransformOperation>): void {
    const operations = (object.operations ?? []).map((c, i) => (i === index ? { ...c, ...change } : c));
    void this.store.setObject(object.id, { operations }, 'Change Operation');
  }

  protected removeOperation(object: EnvironmentObject, index: number): void {
    const operations = (object.operations ?? []).filter((_, i) => i !== index);
    void this.store.setObject(object.id, { operations }, 'Delete Operation');
  }

  protected addTap(object: EnvironmentObject): void {
    const taps = object.taps ?? [];
    const last = taps.at(-1);
    const tap: DelayTap = { delayTicks: Math.min(3840, (last?.delayTicks ?? 0) + 240), transpose: 0, velocity: 0 };
    void this.store.setObject(object.id, { taps: [...taps, tap] }, 'Add Tap');
  }

  protected setTap(object: EnvironmentObject, index: number, change: Partial<DelayTap>): void {
    const taps = (object.taps ?? []).map((t, i) => (i === index ? { ...t, ...change } : t));
    void this.store.setObject(object.id, { taps }, 'Change Tap');
  }

  protected removeTap(object: EnvironmentObject, index: number): void {
    const taps = (object.taps ?? []).filter((_, i) => i !== index);
    void this.store.setObject(object.id, { taps }, 'Delete Tap');
  }

  protected blur(event: Event): void {
    (event.target as HTMLElement).blur();
  }
}
