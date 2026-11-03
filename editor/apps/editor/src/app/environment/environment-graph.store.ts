import { computed, DestroyRef, inject, Injectable, signal } from '@angular/core';
import {
  type EditCommand,
  type EnvironmentObject,
  type EnvironmentObjectKind,
  EnvironmentObjectKinds,
  type EnvironmentParameter,
  type LiveMidiEvent,
  type MonitoredEvent
} from '@anode/api-client';
import type { XuiGraphConnection, XuiGraphEdge, XuiGraphNodeMove } from '@xui/node-graph';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import {
  cableProblem,
  faderEvent,
  freeSpot,
  kindLimitProblem,
  parameterSpecs,
  portId,
  portIndex
} from './environment-model';

/** What the window has selected: an object or a cable. */
export type EnvironmentSelection = { kind: 'object'; id: string } | { kind: 'cable'; id: string } | null;

/** A `setEnvironmentObject`'s changes, without its type and object. */
export type ObjectChange = Omit<Extract<EditCommand, { type: 'setEnvironmentObject' }>, 'type' | 'objectId'>;

/**
 * The Environment window's state (ADR-0015): the layer it shows, its selection, and what monitors and faders last
 * reported (`environmentMonitor`, `environmentFader` from the host's telemetry). Every edit is a Server command (one
 * undo step each); cables are checked first with the Server's own rules (environment-model.ts), so a refused one
 * never leaves the canvas. Provided by the window.
 */
@Injectable()
export class EnvironmentGraphStore {
  private readonly project = inject(ProjectStore);
  private readonly api = inject(ANODE_API);
  private readonly plugins = inject(PluginsStore);
  private readonly notify = inject(Notify);

  readonly environment = this.project.environment;
  private readonly chosenLayer = signal<string | null>(null);
  readonly selection = signal<EnvironmentSelection>(null);
  /** Each monitor's last events (oldest first). */
  readonly monitors = signal<ReadonlyMap<string, readonly MonitoredEvent[]>>(new Map());
  /** Each fader's last value (0…127): reported, or moved on screen. */
  readonly faders = signal<ReadonlyMap<string, number>>(new Map());
  /** The last refusal, shown under the toolbar. */
  readonly problem = signal<string | null>(null);

  readonly layers = computed(() => this.environment().layers);
  /** The layer shown: the one chosen, else the first. */
  readonly layerId = computed(() => {
    const layers = this.layers();
    const chosen = this.chosenLayer();
    return layers.some(l => l.id === chosen) ? chosen : (layers[0]?.id ?? null);
  });
  /** The objects on the layer shown. */
  readonly objects = computed(() => this.environment().objects.filter(o => o.layerId === this.layerId()));
  /** The cables between objects on the layer shown, as the canvas's edges. */
  readonly edges = computed<XuiGraphEdge[]>(() => {
    const here = new Set(this.objects().map(o => o.id));
    const selected = this.selection();
    return this.environment()
      .cables.filter(c => here.has(c.fromObjectId) && here.has(c.toObjectId))
      .map(c => ({
        id: c.id,
        source: { nodeId: c.fromObjectId, portId: portId('out', c.outlet) },
        target: { nodeId: c.toObjectId, portId: portId('in', c.inlet) },
        ...(selected?.kind === 'cable' && selected.id === c.id ? { width: 3 } : {})
      }));
  });
  /** Cables from or to objects on other layers, by object on this layer (Logic draws them leaving the canvas). */
  readonly crossings = computed(() => {
    const here = new Set(this.objects().map(o => o.id));
    const counts = new Map<string, number>();
    for (const c of this.environment().cables) {
      if (here.has(c.fromObjectId) !== here.has(c.toObjectId)) {
        const id = here.has(c.fromObjectId) ? c.fromObjectId : c.toObjectId;
        counts.set(id, (counts.get(id) ?? 0) + 1);
      }
    }
    return counts;
  });
  readonly selectedObject = computed(() => {
    const selection = this.selection();
    return selection?.kind === 'object' ? (this.environment().objects.find(o => o.id === selection.id) ?? null) : null;
  });

  constructor() {
    const subscription = inject(EventStream).events$.subscribe(message => {
      if (message.type === 'environmentMonitor') {
        this.monitors.update(map => new Map(map).set(message.objectId, message.events));
      } else if (message.type === 'environmentFader') {
        this.faders.update(map => new Map(map).set(message.objectId, message.value));
      }
    });
    inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
  }

  /** The arpeggiator's and chord memoriser's parameters (their MIDI FX's), or a kind's own. */
  specs(kind: EnvironmentObjectKind): readonly EnvironmentParameter[] {
    return parameterSpecs(kind, id => this.plugins.plugin(id)?.parameters ?? []);
  }

  showLayer(id: string): void {
    this.chosenLayer.set(id);
    this.selection.set(null);
  }

  /**
   * The default path made visible (ADR-0015): an Environment with no objects is physical input to sequencer input, so
   * the window makes that pair (and its cable) when it first opens on one.
   */
  async ensureDefault(): Promise<void> {
    if (this.environment().objects.length > 0) {
      return;
    }
    const made = await this.run(
      [
        { type: 'addEnvironmentObject', kind: 'physicalInput', x: 40, y: 60 },
        { type: 'addEnvironmentObject', kind: 'sequencerInput', x: 320, y: 60 }
      ],
      'Create Environment'
    );
    const objects = this.environment().objects;
    const input = objects.find(o => o.kind === 'physicalInput');
    const sequencer = objects.find(o => o.kind === 'sequencerInput');
    if (made && input && sequencer) {
      await this.run(
        [{ type: 'connectEnvironment', fromObjectId: input.id, outlet: 0, toObjectId: sequencer.id, inlet: 0 }],
        'Create Environment'
      );
    }
  }

  async addLayer(): Promise<void> {
    const before = new Set(this.layers().map(l => l.id));
    if (await this.run([{ type: 'addEnvironmentLayer' }], 'New Layer')) {
      const added = this.layers().find(l => !before.has(l.id));
      if (added) {
        this.showLayer(added.id);
      }
    }
  }

  renameLayer(layerId: string, name: string): Promise<boolean> {
    return this.run([{ type: 'setEnvironmentLayer', layerId, name }], 'Rename Layer');
  }

  removeLayer(layerId: string): Promise<boolean> {
    return this.run([{ type: 'removeEnvironmentLayer', layerId }], 'Delete Layer');
  }

  /** Why an object of a kind cannot be added now (the single physical input and sequencer input), or null. */
  addProblem(kind: EnvironmentObjectKind): string | null {
    return kindLimitProblem(this.environment(), kind, null);
  }

  /** A new object of `kind` on the layer shown, at `at` or right of the others; selected once made. */
  async addObject(kind: EnvironmentObjectKind, at?: { x: number; y: number }): Promise<void> {
    const problem = this.addProblem(kind);
    if (problem) {
      this.problem.set(problem);
      return;
    }
    const spot = at ?? freeSpot(this.environment(), this.layerId());
    const before = new Set(this.environment().objects.map(o => o.id));
    const command: EditCommand = { type: 'addEnvironmentObject', kind, x: Math.round(spot.x), y: Math.round(spot.y) };
    const layerId = this.layerId();
    if (await this.run([layerId ? { ...command, layerId } : command], `New ${EnvironmentObjectKinds[kind].name}`)) {
      const added = this.environment().objects.find(o => !before.has(o.id));
      if (added) {
        this.chosenLayer.set(added.layerId);
        this.selection.set({ kind: 'object', id: added.id });
      }
    }
  }

  setObject(objectId: string, change: ObjectChange, label = 'Change Object'): Promise<boolean> {
    return this.run([{ type: 'setEnvironmentObject', objectId, ...change }], label);
  }

  /** Objects dragged on the canvas: their new positions, one step. */
  move(move: XuiGraphNodeMove): Promise<boolean> {
    const commands: EditCommand[] = move.nodes.map(n => ({
      type: 'setEnvironmentObject',
      objectId: n.nodeId,
      x: Math.round(n.position.x),
      y: Math.round(n.position.y)
    }));
    return this.run(commands, move.nodes.length > 1 ? 'Move Objects' : 'Move Object');
  }

  /** A cable from a port pair, as the canvas draws it (either end first); null when the ports are not an outlet and an inlet. */
  cableOf(connection: XuiGraphConnection): {
    fromObjectId: string;
    outlet: number;
    toObjectId: string;
    inlet: number;
  } | null {
    const outlet = portIndex(connection.source.portId, 'out');
    const inlet = portIndex(connection.target.portId, 'in');
    if (outlet !== null && inlet !== null) {
      return { fromObjectId: connection.source.nodeId, outlet, toObjectId: connection.target.nodeId, inlet };
    }
    const backOutlet = portIndex(connection.target.portId, 'out');
    const backInlet = portIndex(connection.source.portId, 'in');
    return backOutlet !== null && backInlet !== null
      ? {
          fromObjectId: connection.target.nodeId,
          outlet: backOutlet,
          toObjectId: connection.source.nodeId,
          inlet: backInlet
        }
      : null;
  }

  /** Why the canvas may not draw this cable (the Server's rules), or null: the node graph's validator. */
  connectionProblem(connection: XuiGraphConnection): string | null {
    const cable = this.cableOf(connection);
    return cable
      ? cableProblem(this.environment(), cable.fromObjectId, cable.outlet, cable.toObjectId, cable.inlet)
      : 'A cable runs from an outlet to an inlet.';
  }

  async connect(connection: XuiGraphConnection): Promise<boolean> {
    const cable = this.cableOf(connection);
    const problem = this.connectionProblem(connection);
    if (!cable || problem) {
      this.problem.set(problem);
      return false;
    }
    return this.run([{ type: 'connectEnvironment', ...cable }], 'Connect');
  }

  disconnect(cableIds: readonly string[]): Promise<boolean> {
    return this.run([{ type: 'disconnectEnvironment', cableIds: [...cableIds] }], 'Disconnect');
  }

  removeObjects(objectIds: readonly string[]): Promise<boolean> {
    return this.run(
      [{ type: 'removeEnvironmentObjects', objectIds: [...objectIds] }],
      objectIds.length > 1 ? 'Delete Objects' : 'Delete Object'
    );
  }

  /** Delete: the selected object or cable. */
  async deleteSelection(): Promise<void> {
    const selection = this.selection();
    if (!selection) {
      return;
    }
    const done =
      selection.kind === 'object' ? await this.removeObjects([selection.id]) : await this.disconnect([selection.id]);
    if (done) {
      this.selection.set(null);
    }
  }

  /** Live MIDI into an on-screen keyboard or fader (`/project/environment/objects/{id}/input`). */
  async play(objectId: string, events: LiveMidiEvent[]): Promise<void> {
    try {
      await this.api.environmentInput(objectId, { events });
    } catch (error) {
      this.notify.failure(error, 'Could not play into the Environment');
    }
  }

  /** An on-screen fader moved to `value` (0…127): shown at once, sent as its message into itself. */
  moveFader(object: EnvironmentObject, value: number): Promise<void> {
    this.faders.update(map => new Map(map).set(object.id, value));
    return this.play(object.id, [faderEvent(object.parameters, value)]);
  }

  /** A fader's value: the last reported or moved, else its stored `value`. */
  faderValue(object: EnvironmentObject): number {
    return this.faders().get(object.id) ?? object.parameters['value'] ?? 0;
  }

  private async run(commands: EditCommand[], label: string): Promise<boolean> {
    this.problem.set(null);
    return (await this.project.execute(commands, label)) !== null;
  }
}
