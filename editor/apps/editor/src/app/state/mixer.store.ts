import { Injectable, inject, signal } from '@angular/core';
import type {
  ChannelStrip,
  CommandResult,
  EditCommand,
  GroupProperties,
  Insert,
  MixerGroup,
  MixerSettings,
  Send,
  Track
} from '@anode/api-client';
import { AutomationStore } from '../automation/automation.store';
import { Coalescer } from '../core/coalescer';
import { PluginsStore } from './plugins.store';
import { ProjectStore } from './project.store';

/** A channel strip's continuous controls. */
export type StripControl = 'volumeDb' | 'pan';

const Labels: Record<StripControl, string> = { volumeDb: 'Volume', pan: 'Pan' };
const Targets: Record<StripControl, string> = { volumeDb: 'volume', pan: 'pan' };

/** The strip key of the master in {@link MixerStore}'s methods. */
export const Master = null;

/** How long after its last move a control without an end-of-gesture event still counts as held. */
const HoldMs = 1_500;

/** Logic's group colours, in the order new groups take them. */
export const GroupColors = ['#4a90e2', '#f5a623', '#7ed321', '#d0021b', '#9013fe', '#50e3c2', '#f8e71c', '#bd10e0'];

/** What a new group links (Logic's defaults). */
export const DefaultGroupProperties: GroupProperties = {
  volume: true,
  mute: true,
  solo: true,
  pan: false,
  sends: false,
  recordArm: true,
  automation: false,
  selection: true
};

/**
 * Channel strip edits for the track headers and the Mixer: volume, pan, mute and solo; inserts (add, move, bypass,
 * parameters), sends (bus, level, pre/post), output routing, groups, VCA assignment, aux and VCA tracks and the mixer
 * settings. Continuous controls (faders, pan, send levels, plugin parameters) are optimistic and coalesced: each has at
 * most one edit in flight and shows the value being sent until the Server's delta confirms it
 * (docs/01-implementation-plan.md §4.4); the Server folds a gesture's batches into one undo step and, during playback in
 * touch, latch or write, records them into the track's automation (docs/05-phase-4.md). While playing, a control that
 * is not held shows its automation (read).
 */
@Injectable({ providedIn: 'root' })
export class MixerStore {
  private readonly project = inject(ProjectStore);
  private readonly plugins = inject(PluginsStore);
  private readonly automation = inject(AutomationStore);
  private readonly coalescer = new Coalescer<number>((key, value) => {
    const [owner = '', control = 'volumeDb'] = key.split('|');
    const field = control as StripControl;
    return this.project.execute([stripCommand(owner || null, { [field]: value })], Labels[field]);
  });
  private readonly sendCoalescer = new Coalescer<number>((key, levelDb) => {
    const [trackId = '', sendId = ''] = key.split('|');
    return this.project.execute([{ type: 'setSend', trackId, sendId, levelDb }], 'Send Level');
  });
  private readonly parameterCoalescer = new Coalescer<number>((key, value) => {
    const [owner = '', insertId = '', parameterId = ''] = key.split('|');
    const trackId = owner || null;
    return this.project.execute(
      [{ type: 'setInsertParameter', insertId, parameterId, value, ...(trackId ? { trackId } : {}) }],
      this.parameterName(trackId, insertId, parameterId)
    );
  });
  /** Controls held now (a drag in progress), which show what they send instead of their automation. */
  private readonly held = signal<ReadonlySet<string>>(new Set());
  private readonly holdTimers = new Map<string, ReturnType<typeof setTimeout>>();

  /** A strip as stored: the track's, or the master's for {@link Master}. */
  strip(trackId: string | null): ChannelStrip | undefined {
    return trackId === null ? this.project.master() : this.project.track(trackId)?.strip;
  }

  /** The value to show for a control: the one being sent, else its automation while playing, else the stored one. */
  value(trackId: string | null, control: StripControl): number {
    const k = key(trackId, control);
    const pending = this.coalescer.pending(k);
    if (pending !== undefined) {
      return pending;
    }
    const stored = this.strip(trackId)?.[control] ?? 0;
    if (this.held().has(k)) {
      return stored;
    }
    return this.automation.playbackValue(trackId, Targets[control]) ?? stored;
  }

  /** Sets a control during a drag: shown at once, sent at most one request at a time. */
  set(trackId: string | null, control: StripControl, value: number): Promise<void> {
    const k = key(trackId, control);
    this.hold(k);
    return this.coalescer.set(k, value);
  }

  /** A control's gesture ended: it shows its automation again (touch). */
  release(trackId: string | null, control: StripControl): void {
    this.unhold(key(trackId, control));
  }

  toggleMute(trackId: string | null): void {
    const strip = this.strip(trackId);
    if (strip) {
      void this.project.execute([stripCommand(trackId, { mute: !strip.mute })], strip.mute ? 'Unmute' : 'Mute');
    }
  }

  toggleSolo(trackId: string): void {
    const strip = this.strip(trackId);
    if (strip) {
      void this.project.execute([stripCommand(trackId, { solo: !strip.solo })], strip.solo ? 'Unsolo' : 'Solo');
    }
  }

  // ---- inserts

  /** Adds an insert at `index` (the end when left out). */
  addInsert(trackId: string | null, pluginId: string, index?: number): Promise<unknown> {
    const name = this.plugins.plugin(pluginId)?.name ?? pluginId;
    return this.project.execute(
      [{ type: 'addInsert', pluginId, ...owner(trackId), ...(index === undefined ? {} : { index }) }],
      `Insert ${name}`
    );
  }

  /** Replaces the insert at `index` with another plugin (remove, then add in its place: one undo step). */
  replaceInsert(trackId: string | null, insert: Insert, pluginId: string, index: number): Promise<unknown> {
    const name = this.plugins.plugin(pluginId)?.name ?? pluginId;
    return this.project.execute(
      [
        { type: 'removeInsert', insertId: insert.id, ...owner(trackId) },
        { type: 'addInsert', pluginId, index, ...owner(trackId) }
      ],
      `Insert ${name}`
    );
  }

  removeInsert(trackId: string | null, insert: Insert): Promise<unknown> {
    return this.project.execute([{ type: 'removeInsert', insertId: insert.id, ...owner(trackId) }], 'Remove Insert');
  }

  moveInsert(trackId: string | null, insertId: string, index: number): Promise<unknown> {
    return this.project.execute([{ type: 'moveInsert', insertId, index, ...owner(trackId) }], 'Move Insert');
  }

  toggleBypass(trackId: string | null, insert: Insert): Promise<unknown> {
    return this.project.execute(
      [{ type: 'setInsert', insertId: insert.id, bypassed: !insert.bypassed, ...owner(trackId) }],
      insert.bypassed ? 'Enable Insert' : 'Bypass Insert'
    );
  }

  /** Sets an insert's sidechain: a track id, `bus:N`, or null for none. */
  setSidechain(trackId: string | null, insert: Insert, sidechain: string | null): Promise<unknown> {
    return this.project.execute(
      [{ type: 'setInsert', insertId: insert.id, sidechain: sidechain ?? '', ...owner(trackId) }],
      'Sidechain'
    );
  }

  /** An insert parameter's value to show: being sent, else its automation while playing, else stored, else default. */
  parameterValue(trackId: string | null, insert: Insert, parameterId: string): number {
    const k = parameterKey(trackId, insert.id, parameterId);
    const pending = this.parameterCoalescer.pending(k);
    if (pending !== undefined) {
      return pending;
    }
    const stored =
      insert.parameters[parameterId] ??
      this.plugins.plugin(insert.pluginId)?.parameters.find(p => p.id === parameterId)?.default ??
      0;
    if (this.held().has(k)) {
      return stored;
    }
    return this.automation.playbackValue(trackId, `insert:${insert.id}:${parameterId}`) ?? stored;
  }

  /** Sets an insert parameter during a drag (coalesced; recorded into automation while playing in touch…write). */
  setParameter(trackId: string | null, insertId: string, parameterId: string, value: number): Promise<void> {
    const k = parameterKey(trackId, insertId, parameterId);
    this.hold(k);
    return this.parameterCoalescer.set(k, value);
  }

  // ---- sends

  /** A new send of a track to `bus` (post-fader at −∞, as Logic adds one; 0 dB when `levelDb` says so). */
  addSend(trackId: string, bus: number, levelDb?: number): Promise<unknown> {
    return this.project.execute(
      [{ type: 'addSend', trackId, bus, ...(levelDb === undefined ? {} : { levelDb }) }],
      `Send to Bus ${bus}`
    );
  }

  setSendBus(trackId: string, send: Send, bus: number): Promise<unknown> {
    return this.project.execute([{ type: 'setSend', trackId, sendId: send.id, bus }], `Send to Bus ${bus}`);
  }

  togglePreFader(trackId: string, send: Send): Promise<unknown> {
    return this.project.execute(
      [{ type: 'setSend', trackId, sendId: send.id, preFader: !send.preFader }],
      send.preFader ? 'Post-Fader Send' : 'Pre-Fader Send'
    );
  }

  removeSend(trackId: string, send: Send): Promise<unknown> {
    return this.project.execute([{ type: 'removeSend', trackId, sendId: send.id }], 'Remove Send');
  }

  /** A send level to show: being sent, else its automation while playing, else stored. */
  sendLevel(trackId: string, send: Send): number {
    const k = `${trackId}|${send.id}`;
    const pending = this.sendCoalescer.pending(k);
    if (pending !== undefined) {
      return pending;
    }
    if (this.held().has(k)) {
      return send.levelDb;
    }
    return this.automation.playbackValue(trackId, `send:${send.id}`) ?? send.levelDb;
  }

  setSendLevel(trackId: string, sendId: string, levelDb: number): Promise<void> {
    const k = `${trackId}|${sendId}`;
    this.hold(k);
    return this.sendCoalescer.set(k, levelDb);
  }

  releaseSend(trackId: string, sendId: string): void {
    this.unhold(`${trackId}|${sendId}`);
  }

  // ---- routing, groups, VCA

  setOutput(trackId: string, output: string): Promise<unknown> {
    return this.project.execute([{ type: 'setOutput', trackId, output }], 'Output');
  }

  /** A new aux track listening to `bus` (the lowest free one when left out). */
  addAux(bus?: number, name?: string): Promise<unknown> {
    return this.project.execute(
      [{ type: 'addAux', ...(bus === undefined ? {} : { bus }), ...(name ? { name } : {}) }],
      'New Aux'
    );
  }

  /**
   * Routes a track's send to a new aux (Logic's "New Auxiliary Channel Strip" from a send slot): the aux on the lowest
   * free bus and the send to it at 0 dB, one undo step.
   */
  async addSendToNewAux(trackId: string, bus: number): Promise<unknown> {
    return this.project.execute(
      [
        { type: 'addAux', bus },
        { type: 'addSend', trackId, bus, levelDb: 0 }
      ],
      'Send to New Aux'
    );
  }

  addVca(name?: string): Promise<CommandResult | null> {
    return this.project.execute([{ type: 'addVca', ...(name ? { name } : {}) }], 'New VCA');
  }

  assignVca(trackId: string, vcaId: string | null): Promise<unknown> {
    return this.project.execute([{ type: 'assignVca', trackId, vcaId }], vcaId ? 'Assign VCA' : 'Remove from VCA');
  }

  /** Puts a track in exactly these groups (Logic's group slot: one group, or none). */
  setTrackGroups(trackId: string, groups: readonly number[]): Promise<unknown> {
    return this.project.execute([{ type: 'setTrackGroups', trackId, groups: [...groups] }], 'Group');
  }

  /** The lowest group number no group uses, or 0 when all 32 exist. */
  nextGroupNumber(): number {
    const used = this.project.groups();
    for (let n = 1; n <= 32; n++) {
      if (!used.has(n)) {
        return n;
      }
    }
    return 0;
  }

  /** A new group with Logic's defaults, the track in it. Resolves with its number (0 when none was free). */
  async newGroup(trackId: string | null): Promise<number> {
    const number = this.nextGroupNumber();
    if (number === 0) {
      return 0;
    }
    const commands: EditCommand[] = [
      {
        type: 'setGroup',
        number,
        name: `Group ${number}`,
        color: GroupColors[(number - 1) % GroupColors.length] ?? '#4a90e2',
        enabled: true,
        properties: DefaultGroupProperties
      }
    ];
    if (trackId) {
      commands.push({ type: 'setTrackGroups', trackId, groups: [number] });
    }
    const result = await this.project.execute(commands, 'New Group');
    return result ? number : 0;
  }

  /** Changes a group's name, colour, whether it is on, or what it links. */
  setGroup(group: MixerGroup, change: Partial<Omit<MixerGroup, 'number'>>): Promise<unknown> {
    return this.project.execute(
      [
        {
          type: 'setGroup',
          number: group.number,
          name: change.name ?? null,
          color: change.color ?? null,
          enabled: change.enabled ?? null,
          properties: change.properties ?? null
        }
      ],
      'Group Settings'
    );
  }

  deleteGroup(number: number): Promise<unknown> {
    return this.project.execute([{ type: 'deleteGroup', number }], 'Delete Group');
  }

  setMixerSettings(change: Partial<MixerSettings>): Promise<unknown> {
    return this.project.execute([{ type: 'setMixerSettings', ...change }], 'Mixer Settings');
  }

  /** Tracks whose strips a group's members are (in track order). */
  groupMembers(number: number): Track[] {
    return this.project.trackList().filter(t => t.strip.groups.includes(number));
  }

  private parameterName(trackId: string | null, insertId: string, parameterId: string): string {
    const strip = this.strip(trackId);
    const insert = strip?.inserts.find(i => i.id === insertId);
    return this.plugins.plugin(insert?.pluginId)?.parameters.find(p => p.id === parameterId)?.name ?? 'Parameter';
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

function key(trackId: string | null, control: StripControl): string {
  return `${trackId ?? ''}|${control}`;
}

function parameterKey(trackId: string | null, insertId: string, parameterId: string): string {
  return `${trackId ?? ''}|${insertId}|${parameterId}`;
}

function owner(trackId: string | null): { trackId?: string } {
  return trackId === null ? {} : { trackId };
}

/** A `setStrip` command for a track's strip, or the master's when `trackId` is null. */
export function stripCommand(
  trackId: string | null,
  change: Partial<Pick<ChannelStrip, 'volumeDb' | 'pan' | 'mute' | 'solo'>>
): EditCommand {
  return { type: 'setStrip', ...(trackId === null ? {} : { trackId }), ...change };
}
