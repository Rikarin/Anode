import { computed, inject, Injectable, signal } from '@angular/core';
import type {
  ControlAssignment,
  ControllerAssignments,
  ControlSurface,
  ControlSurfaceKind,
  ControlTarget,
  LearnState,
  OscSettings,
  OscStatus,
  SurfaceStatus
} from '@anode/api-client';
import { targetsOf } from '../automation/targets';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { KeyCommands } from '../keys/key-commands';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import {
  addAssignment,
  addMode,
  addZone,
  blankAssignment,
  deleteAssignment,
  deleteMode,
  deleteZone,
  findAssignment,
  moveAssignment,
  newId,
  nextName,
  renameMode,
  renameZone,
  setActiveMode,
  TrackSwitches,
  TransportActions,
  updateAssignment
} from './assignments';

const LearnOff: LearnState = { on: false, target: null, zoneId: null, modeId: null, last: null };

export const SurfaceKindNames: Record<ControlSurfaceKind, string> = {
  mackieControl: 'Mackie Control',
  mackieControlXt: 'Mackie Control XT',
  hui: 'HUI'
};

/**
 * Controller assignments, Learn, control surfaces and OSC (docs/09-phase-8.md, workstream C): the user settings the
 * Server keeps for them, fetched when a control window first opens and again on every reconnection. Assignments are
 * edited here and sent whole (`PUT /control/assignments`), optimistically; Learn follows `controlLearn` (the last
 * message, and the assignment it made, which becomes the selected one); surfaces follow `surfaces`.
 */
@Injectable({ providedIn: 'root' })
export class ControlStore {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);
  private readonly ui = inject(UiStore);
  private readonly project = inject(ProjectStore);
  private readonly plugins = inject(PluginsStore);

  readonly assignments = signal<ControllerAssignments>({ zones: [] });
  readonly learnState = signal<LearnState>(LearnOff);
  readonly learning = computed(() => this.learnState().on);
  /** The assignment the window's inspector shows. */
  readonly selectedId = signal<string | null>(null);
  readonly selected = computed(() => findAssignment(this.assignments(), this.selectedId()));
  /** The assignment Learn made last (the list marks it). */
  readonly learnedId = signal<string | null>(null);
  readonly surfaces = signal<readonly ControlSurface[]>([]);
  readonly surfaceStatus = signal<ReadonlyMap<string, SurfaceStatus>>(new Map());
  readonly osc = signal<OscStatus | null>(null);
  readonly loaded = signal(false);
  /** MIDI ports by id, for names (Anode's virtual ports are among them on macOS). */
  readonly inputs = computed(() => this.plugins.devices().inputs);
  readonly outputs = computed(() => this.plugins.devices().outputs);

  constructor() {
    const events = inject(EventStream);
    events.events$.subscribe(message => {
      if (message.type === 'controlLearn') {
        this.learnState.update(state => ({ ...state, last: message.message }));
        if (message.assignmentId) {
          void this.learned(message.assignmentId);
        }
      } else if (message.type === 'surfaces') {
        this.surfaceStatus.set(new Map(message.surfaces.map(s => [s.id, s])));
      }
    });
    events.connected$.subscribe(() => {
      if (this.loaded()) {
        void this.load();
      }
    });
  }

  /** Fetches everything (the windows call it when they open). */
  async load(): Promise<void> {
    await this.notify.run(async () => {
      const [assignments, learn, surfaces, status, osc] = await Promise.all([
        this.api.controllerAssignments(),
        this.api.learn(),
        this.api.surfaces(),
        this.api.surfaceStatus(),
        this.api.osc()
      ]);
      this.assignments.set(assignments);
      this.learnState.set(learn);
      this.surfaces.set(surfaces);
      this.surfaceStatus.set(new Map(status.map(s => [s.id, s])));
      this.osc.set(osc);
      this.loaded.set(true);
    }, 'Could not read the control settings');
  }

  // ---- assignments

  /** Sends a whole new set of assignments; the shown ones go back if the Server refuses it. */
  async save(next: ControllerAssignments): Promise<boolean> {
    const previous = this.assignments();
    this.assignments.set(next);
    const saved = await this.notify.run(
      () => this.api.setControllerAssignments(next),
      'Could not save the assignments'
    );
    if (!saved) {
      this.assignments.set(previous);
      return false;
    }
    this.assignments.set(saved);
    return true;
  }

  /** A new assignment in the selected one's mode (or the first zone's active mode), selected. */
  async addAssignment(): Promise<void> {
    const current = this.assignments();
    const id = newId('cas', current);
    const modeId = this.selected()?.mode.id ?? null;
    const taken = current.zones.flatMap(z => z.modes.flatMap(m => m.assignments.map(a => a.name ?? '')));
    if (await this.save(addAssignment(current, blankAssignment(id, nextName('Assignment', taken)), modeId))) {
      this.selectedId.set(id);
    }
  }

  async deleteAssignment(id: string): Promise<void> {
    if ((await this.save(deleteAssignment(this.assignments(), id))) && this.selectedId() === id) {
      this.selectedId.set(null);
    }
  }

  updateAssignment(id: string, change: Partial<Omit<ControlAssignment, 'id'>>): Promise<boolean> {
    return this.save(updateAssignment(this.assignments(), id, change));
  }

  moveAssignment(id: string, modeId: string): Promise<boolean> {
    return this.save(moveAssignment(this.assignments(), id, modeId));
  }

  addZone(): Promise<boolean> {
    const current = this.assignments();
    return this.save(
      addZone(
        current,
        nextName(
          'Zone',
          current.zones.map(z => z.name)
        )
      )
    );
  }

  renameZone(zoneId: string, name: string): Promise<boolean> {
    return this.save(renameZone(this.assignments(), zoneId, name));
  }

  deleteZone(zoneId: string): Promise<boolean> {
    return this.save(deleteZone(this.assignments(), zoneId));
  }

  addMode(zoneId: string): Promise<boolean> {
    const zone = this.assignments().zones.find(z => z.id === zoneId);
    return this.save(addMode(this.assignments(), zoneId, nextName('Mode', zone?.modes.map(m => m.name) ?? [])));
  }

  renameMode(modeId: string, name: string): Promise<boolean> {
    return this.save(renameMode(this.assignments(), modeId, name));
  }

  deleteMode(modeId: string): Promise<boolean> {
    return this.save(deleteMode(this.assignments(), modeId));
  }

  setActiveMode(zoneId: string, modeId: string): Promise<boolean> {
    return this.save(setActiveMode(this.assignments(), zoneId, modeId));
  }

  // ---- Learn

  /** Turns Learn on (or changes what it learns for), in the selected assignment's zone and mode. */
  async startLearn(target: ControlTarget | null = this.learnState().target): Promise<void> {
    const where = this.selected();
    const state = await this.notify.run(
      () => this.api.startLearn({ target, zoneId: where?.zone.id ?? null, modeId: where?.mode.id ?? null }),
      'Could not start Learn'
    );
    if (state) {
      this.learnState.set(state);
    }
  }

  async stopLearn(): Promise<void> {
    const state = await this.notify.run(() => this.api.stopLearn(), 'Could not stop Learn');
    if (state) {
      this.learnState.set(state);
    }
  }

  toggleLearn(): Promise<void> {
    return this.learning() ? this.stopLearn() : this.startLearn(null);
  }

  /** While learning, a control clicked in the editor becomes what the next moved control is assigned to. */
  setLearnTarget(target: ControlTarget): Promise<void> {
    return this.startLearn(target);
  }

  /** "Learn Assignment for …": opens Controller Assignments in Learn with this target. */
  async learnFor(target: ControlTarget): Promise<void> {
    this.ui.controllerAssignmentsOpen.set(true);
    if (!this.loaded()) {
      await this.load();
    }
    await this.startLearn(target);
  }

  /** Learn made (or replaced) an assignment: fetch it and select it. */
  private async learned(id: string): Promise<void> {
    const assignments = await this.notify.run(() => this.api.controllerAssignments(), 'Could not read the assignments');
    if (assignments) {
      this.assignments.set(assignments);
      this.selectedId.set(id);
      this.learnedId.set(id);
    }
  }

  // ---- surfaces

  async addSurface(kind: ControlSurfaceKind, inputPort: string, outputPort: string): Promise<void> {
    const surface = await this.notify.run(
      () => this.api.addSurface({ kind, inputPort, outputPort }),
      `Could not add the ${SurfaceKindNames[kind]}`
    );
    if (surface) {
      this.surfaces.update(list => [...list, surface]);
      await this.refreshStatus();
    }
  }

  async removeSurface(id: string): Promise<void> {
    const done = await this.notify.run(async () => {
      await this.api.removeSurface(id);
      return true;
    }, 'Could not remove the control surface');
    if (done) {
      this.surfaces.update(list => list.filter(s => s.id !== id).map((s, order) => ({ ...s, order })));
    }
  }

  /** Moves a surface up (−1) or down (+1) in the order extenders follow. */
  async moveSurface(id: string, delta: -1 | 1): Promise<void> {
    const ids = this.surfaces().map(s => s.id);
    const from = ids.indexOf(id);
    const to = from + delta;
    if (from < 0 || to < 0 || to >= ids.length) {
      return;
    }
    ids.splice(to, 0, ...ids.splice(from, 1));
    const surfaces = await this.notify.run(() => this.api.reorderSurfaces(ids), 'Could not reorder the surfaces');
    if (surfaces) {
      this.surfaces.set(surfaces);
    }
  }

  private async refreshStatus(): Promise<void> {
    const status = await this.notify.run(() => this.api.surfaceStatus(), 'Could not read the surfaces’ status');
    if (status) {
      this.surfaceStatus.set(new Map(status.map(s => [s.id, s])));
    }
  }

  // ---- OSC

  async setOsc(settings: OscSettings): Promise<void> {
    const status = await this.notify.run(() => this.api.setOsc(settings), 'Could not change the OSC settings');
    if (status) {
      this.osc.set(status);
    }
  }

  async refreshOsc(): Promise<void> {
    const status = await this.notify.run(() => this.api.osc(), 'Could not read the OSC status');
    if (status) {
      this.osc.set(status);
    }
  }

  // ---- names

  portName(id: string | null | undefined): string {
    if (!id) {
      return 'Any';
    }
    return [...this.inputs(), ...this.outputs()].find(p => p.id === id)?.name ?? id;
  }

  trackName(trackId: string | null | undefined): string {
    if (!trackId) {
      return 'Selected Track';
    }
    return this.project.track(trackId)?.name ?? 'Missing Track';
  }

  /** A parameter address's name on a track (on the selected track, from its kind only). */
  parameterName(trackId: string | null | undefined, address: string | null | undefined): string {
    if (!address) {
      return '—';
    }
    const track = trackId ? this.project.track(trackId) : undefined;
    const found = track ? targetsOf(track, id => this.plugins.plugin(id)).find(t => t.target === address) : undefined;
    if (found) {
      return found.name;
    }
    const plain: Record<string, string> = { volume: 'Volume', pan: 'Pan', mute: 'Mute' };
    return plain[address] ?? address;
  }

  /** A target as the list and the Learn banner show it: `Bass: Volume`, `Selected Track: Mute`, `Transport: Play`. */
  targetLabel(target: ControlTarget | null | undefined): string {
    if (!target) {
      return 'None';
    }
    switch (target.kind) {
      case 'parameter':
        return `${this.trackName(target.trackId)}: ${this.parameterName(target.trackId, target.address)}`;
      case 'smartControl':
        return `${this.trackName(target.trackId)}: Smart Control ${(target.address ?? '').replace(/^knob/, '')}`;
      case 'trackSwitch':
        return `${this.trackName(target.trackId)}: ${TrackSwitches.find(s => s.value === target.switch)?.label ?? '—'}`;
      case 'transport':
        return `Transport: ${TransportActions.find(a => a.value === target.transport)?.label ?? '—'}`;
      case 'keyCommand':
        return `Key Command: ${KeyCommands.find(c => c.id === target.keyCommandId)?.label ?? target.keyCommandId ?? '—'}`;
      case 'zoneMode': {
        const zone = this.assignments().zones.find(z => z.id === target.zoneId);
        const mode = zone?.modes.find(m => m.id === target.modeId);
        return `Switch to ${zone?.name ?? '—'} ▸ ${mode?.name ?? '—'}`;
      }
    }
  }
}
