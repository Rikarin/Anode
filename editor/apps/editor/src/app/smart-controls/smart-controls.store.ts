import { Injectable, inject, signal } from '@angular/core';
import type { SmartControl, SmartControls, SmartControlsLayout, SmartMapping } from '@anode/api-client';
import { AutomationStore } from '../automation/automation.store';
import { Coalescer } from '../core/coalescer';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { type MappingTarget, mappingTarget, mappingTargets } from './mapping-targets';
import { LinearGraph, graphProblem, mappedValue, smartControlTarget, smartControlsOf } from './smart-math';

/** How long after its last move a control without an end-of-gesture event still counts as held (as the Mixer's). */
const HoldMs = 1_500;

/**
 * Smart Controls (docs/07-phase-6.md) for the Smart Controls pane: a track's controls as shown (null controls show as
 * the 8-knob layout until the first command sets them up), their values and the edits. A control's drag is optimistic
 * and coalesced as a fader's is: at most one `setSmartControl` in flight, the value being sent shown at once, one undo
 * step per gesture on the Server, which records it into the control's lane during playback in touch…write. While
 * playing, a control that is not held shows its automation (`smartControl:{id}`). Mapping edits send the control's
 * whole list (`setSmartMappings`), checked as the Server checks graphs.
 */
@Injectable({ providedIn: 'root' })
export class SmartControlsStore {
  private readonly project = inject(ProjectStore);
  private readonly plugins = inject(PluginsStore);
  private readonly automation = inject(AutomationStore);
  private readonly coalescer = new Coalescer<number>((k, value) => {
    const [trackId = '', controlId = ''] = k.split('|');
    return this.project.execute(
      [{ type: 'setSmartControl', trackId, controlId, value }],
      this.control(trackId, controlId)?.name ?? 'Smart Control'
    );
  });
  private readonly held = signal<ReadonlySet<string>>(new Set());
  private readonly holdTimers = new Map<string, ReturnType<typeof setTimeout>>();

  /** Whether the Smart Controls inspector shows (Logic's "i" button in the pane). */
  readonly inspectorShown = signal(false);
  /** The control the inspector edits, by id (the same id on every track). */
  readonly selected = signal('knob1');

  /**
   * A track's Smart Controls as the pane shows them: while they are null, the 8-knob layout, with the arpeggiator
   * button as the Server will set it up (off on instrument and MIDI tracks unless their first MIDI FX slot is an
   * unbypassed Arpeggiator; null elsewhere).
   */
  controls(trackId: string): SmartControls {
    const track = this.project.track(trackId);
    if (track?.smartControls) {
      return track.smartControls;
    }
    const first = track?.strip.midiEffects[0];
    const arpeggiator =
      track?.kind === 'instrument' || track?.kind === 'midi'
        ? first?.pluginId === 'anode.arpeggiator' && !first.bypassed
        : null;
    return { ...smartControlsOf(null), arpeggiator };
  }

  control(trackId: string, controlId: string): SmartControl | undefined {
    return this.controls(trackId).controls.find(c => c.id === controlId);
  }

  /** A control's value to show: the one being sent, else its automation while playing (unless held), else stored. */
  value(trackId: string, controlId: string): number {
    const k = key(trackId, controlId);
    const pending = this.coalescer.pending(k);
    if (pending !== undefined) {
      return pending;
    }
    const stored = this.control(trackId, controlId)?.value ?? 0;
    if (this.held().has(k)) {
      return stored;
    }
    return this.automation.playbackValue(trackId, smartControlTarget(controlId)) ?? stored;
  }

  /** Sets a control during a gesture: shown at once, sent coalesced (0…1). */
  set(trackId: string, controlId: string, value: number): Promise<void> {
    const k = key(trackId, controlId);
    this.hold(k);
    return this.coalescer.set(k, Math.min(1, Math.max(0, value)));
  }

  /** A control's gesture ended: it shows its automation again. */
  release(trackId: string, controlId: string): void {
    this.unhold(key(trackId, controlId));
  }

  /**
   * A control's value as text: in its parameter's unit when it has exactly one mapping (through the graph, invert and
   * range), else as a percentage.
   */
  format(trackId: string, controlId: string, value: number): string {
    const control = this.control(trackId, controlId);
    const mapping = control?.mappings.length === 1 ? control.mappings[0] : undefined;
    const target = mapping ? this.target(trackId, mapping.target) : undefined;
    if (mapping && target) {
      return target.format(mappedValue(mapping, value));
    }
    if (control?.kind === 'switch') {
      return value >= 0.5 ? 'On' : 'Off';
    }
    return `${Math.round(value * 100)} %`;
  }

  setLayout(trackId: string, layout: SmartControlsLayout): Promise<unknown> {
    return this.project.execute(
      [{ type: 'setSmartControls', trackId, layout }],
      layout === 'knobs12' ? 'Smart Controls: 12 Knobs' : 'Smart Controls: 8 Knobs'
    );
  }

  /** Turns the arpeggiator button on or off (an Arpeggiator in the track's first MIDI FX slot). */
  toggleArpeggiator(trackId: string): Promise<unknown> {
    const on = !this.controls(trackId).arpeggiator;
    return this.project.execute(
      [{ type: 'setSmartControls', trackId, arpeggiator: on }],
      on ? 'Arpeggiator On' : 'Arpeggiator Off'
    );
  }

  /** Whether any control has a value Compare can show. */
  canCompare(trackId: string): boolean {
    const controls = this.controls(trackId);
    return controls.comparing || controls.controls.some(c => c.savedValue !== null && c.savedValue !== undefined);
  }

  /** Compare: the saved values, or the edited ones again (the Server applies it without an undo step). */
  toggleCompare(trackId: string): Promise<unknown> {
    const on = !this.controls(trackId).comparing;
    return this.project.execute([{ type: 'compareSmartControls', trackId, on }], 'Compare');
  }

  /** Automatic mapping from the plugins' macro hints. */
  autoMap(trackId: string): Promise<unknown> {
    return this.project.execute([{ type: 'autoMapSmartControls', trackId }], 'Automatic Smart Control Mapping');
  }

  rename(trackId: string, controlId: string, name: string): Promise<unknown> {
    const trimmed = name.trim();
    if (!trimmed || trimmed === this.control(trackId, controlId)?.name) {
      return Promise.resolve(null);
    }
    return this.project.execute(
      [{ type: 'setSmartControl', trackId, controlId, name: trimmed }],
      'Rename Smart Control'
    );
  }

  /** What the track's controls can map to, grouped for the picker. */
  targets(trackId: string): { group: string; targets: MappingTarget[] }[] {
    const track = this.project.track(trackId);
    return track ? mappingTargets(track, id => this.plugins.plugin(id)) : [];
  }

  /** One mapping target of a track (undefined when it is gone). */
  target(trackId: string, target: string): MappingTarget | undefined {
    const track = this.project.track(trackId);
    return track ? mappingTarget(track, target, id => this.plugins.plugin(id)) : undefined;
  }

  /** Maps a control to another parameter: its full range, not inverted, the linear graph. */
  addMapping(trackId: string, controlId: string, target: string): Promise<unknown> {
    const info = this.target(trackId, target);
    if (!info) {
      return Promise.resolve(null);
    }
    const mapping: SmartMapping = {
      target,
      min: info.min,
      max: info.max,
      invert: false,
      graph: LinearGraph.map(p => ({ ...p }))
    };
    return this.setMappings(trackId, controlId, [...this.mappings(trackId, controlId), mapping], `Map to ${info.name}`);
  }

  /** Changes mapping `index` of a control (its target, range, invert or graph). */
  updateMapping(trackId: string, controlId: string, index: number, change: Partial<SmartMapping>): Promise<unknown> {
    const list = this.mappings(trackId, controlId);
    const current = list[index];
    if (!current) {
      return Promise.resolve(null);
    }
    const next = { ...current, ...change };
    if (change.target && change.target !== current.target) {
      // A new target starts at its own full range.
      const info = this.target(trackId, change.target);
      if (info) {
        next.min = info.min;
        next.max = info.max;
      }
    }
    return this.setMappings(
      trackId,
      controlId,
      list.map((m, i) => (i === index ? next : m)),
      'Smart Control Mapping'
    );
  }

  removeMapping(trackId: string, controlId: string, index: number): Promise<unknown> {
    return this.setMappings(
      trackId,
      controlId,
      this.mappings(trackId, controlId).filter((_, i) => i !== index),
      'Remove Mapping'
    );
  }

  private mappings(trackId: string, controlId: string): SmartMapping[] {
    return [...(this.control(trackId, controlId)?.mappings ?? [])];
  }

  /** Sends a control's whole mapping list; a list the Server would refuse (a bad graph) is not sent. */
  private setMappings(trackId: string, controlId: string, mappings: SmartMapping[], label: string): Promise<unknown> {
    if (mappings.some(m => graphProblem(m.graph) !== null || !Number.isFinite(m.min) || !Number.isFinite(m.max))) {
      return Promise.resolve(null);
    }
    return this.project.execute([{ type: 'setSmartMappings', trackId, controlId, mappings }], label);
  }

  private hold(k: string): void {
    if (!this.held().has(k)) {
      this.held.update(set => new Set(set).add(k));
    }
    clearTimeout(this.holdTimers.get(k));
    this.holdTimers.set(
      k,
      setTimeout(() => this.unhold(k), HoldMs)
    );
  }

  private unhold(k: string): void {
    clearTimeout(this.holdTimers.get(k));
    this.holdTimers.delete(k);
    if (this.held().has(k)) {
      this.held.update(set => {
        const next = new Set(set);
        next.delete(k);
        return next;
      });
    }
  }
}

function key(trackId: string, controlId: string): string {
  return `${trackId}|${controlId}`;
}
