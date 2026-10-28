import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import type { AutomationMode, AutomationPoint, Track } from '@anode/api-client';
import { automationValueAt, deleteRange, removePoints } from '@anode/timeline';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TransportStore } from '../state/transport.store';
import { type AutomationTarget, type TargetSource, targetOf, targetsOf, unknownTarget } from './targets';

/** How often the playhead the faders follow is sampled while playing (≈15 Hz). */
export const PlayheadSampleMs = 66;

/** What is selected in the automation lanes: points of one lane (by index), or a time range of it. */
export interface AutomationSelection {
  readonly trackId: string;
  readonly target: string;
  readonly points: ReadonlySet<number>;
  readonly range: { readonly startTick: number; readonly endTick: number } | null;
}

export const ModeLabels: Readonly<Record<AutomationMode, string>> = {
  off: 'Off',
  read: 'Read',
  touch: 'Touch',
  latch: 'Latch',
  write: 'Write'
};

/**
 * Track automation in the Tracks area (docs/05-phase-4.md): whether it shows (A), which targets each track shows as
 * sub-lanes, what is selected in them, and the edits (`setAutomation`, one undo step per gesture, and
 * `setAutomationMode`). While the transport plays it samples the playhead, so controls can follow their lanes
 * ({@link playbackValue}).
 */
@Injectable({ providedIn: 'root' })
export class AutomationStore {
  private readonly project = inject(ProjectStore);
  private readonly plugins = inject(PluginsStore);
  private readonly transport = inject(TransportStore);
  private readonly regions = inject(SelectionStore);

  /** Whether the automation lanes show (Logic's A). */
  readonly shown = signal(false);
  /** Tracks whose sub-lanes the person changed, with the targets they show, in order. */
  private readonly chosen = signal<ReadonlyMap<string, readonly string[]>>(new Map());
  readonly selection = signal<AutomationSelection | null>(null);
  /** The playhead, sampled every {@link PlayheadSampleMs} while playing. */
  readonly playheadTick = signal(0);

  /** The targets each track shows as sub-lanes: volume and every target with a lane, unless the person chose. */
  readonly lanesShown = computed<ReadonlyMap<string, readonly string[]>>(() => {
    const chosen = this.chosen();
    const withLanes = new Map<string, string[]>();
    for (const lane of this.project.automationList()) {
      if (lane.trackId && lane.points.length > 0) {
        withLanes.set(lane.trackId, [...(withLanes.get(lane.trackId) ?? []), lane.target]);
      }
    }
    const result = new Map<string, readonly string[]>();
    for (const track of this.project.trackList()) {
      const own = chosen.get(track.id);
      if (own) {
        result.set(track.id, own);
      } else {
        const targets = withLanes.get(track.id) ?? [];
        result.set(track.id, ['volume', ...targets.filter(t => t !== 'volume').sort()]);
      }
    }
    return result;
  });

  constructor() {
    let timer: ReturnType<typeof setInterval> | null = null;
    const stop = () => {
      if (timer) {
        clearInterval(timer);
        timer = null;
      }
    };
    effect(() => {
      if (this.transport.playing()) {
        this.sample();
        timer ??= setInterval(() => this.sample(), PlayheadSampleMs);
      } else {
        stop();
        this.playheadTick.set(this.transport.anchorTicks());
      }
    });
    inject(DestroyRef).onDestroy(stop);
    // Selecting regions ends an automation selection (Delete then means the regions).
    effect(() => {
      if (this.regions.regions().size > 0) {
        untracked(() => this.selection.set(null));
      }
    });
  }

  private sample(): void {
    this.playheadTick.set(this.transport.positionAt(performance.now()));
  }

  toggleShown(): void {
    this.shown.update(shown => !shown);
  }

  /** The targets a track shows as sub-lanes. */
  targetsShown(trackId: string): readonly string[] {
    return this.lanesShown().get(trackId) ?? ['volume'];
  }

  /** Every target a track (or, for null, the master) can automate. */
  targets(trackId: string | null): AutomationTarget[] {
    const source = this.source(trackId);
    return source ? targetsOf(source, id => this.plugins.plugin(id)) : [];
  }

  /** One target of a track (or the master), described for drawing and readouts (a fallback for an unknown one). */
  target(trackId: string | null, target: string): AutomationTarget {
    const source = this.source(trackId);
    return (source && targetOf(source, target, id => this.plugins.plugin(id))) ?? unknownTarget(target);
  }

  /** A sub-lane for the next target the track does not show yet. */
  addLane(trackId: string): void {
    const shown = this.targetsShown(trackId);
    const next = this.targets(trackId).find(t => !shown.includes(t.target));
    if (next) {
      this.choose(trackId, [...shown, next.target]);
    }
    this.shown.set(true);
  }

  /** Hides a sub-lane (its automation stays). */
  removeLane(trackId: string, target: string): void {
    this.choose(
      trackId,
      this.targetsShown(trackId).filter(t => t !== target)
    );
    if (this.selection()?.trackId === trackId && this.selection()?.target === target) {
      this.selection.set(null);
    }
  }

  /** Shows another target in a sub-lane (a target shown elsewhere moves here). */
  setLaneTarget(trackId: string, from: string, to: string): void {
    const shown = this.targetsShown(trackId).filter(t => t !== to);
    const index = shown.indexOf(from);
    if (index < 0) {
      this.choose(trackId, [...shown, to]);
    } else {
      this.choose(
        trackId,
        shown.map(t => (t === from ? to : t))
      );
    }
  }

  private choose(trackId: string, targets: readonly string[]): void {
    this.chosen.update(map => new Map(map).set(trackId, targets));
  }

  /** A lane's points (empty when it has none). */
  points(trackId: string | null, target: string): readonly AutomationPoint[] {
    return this.project.automationLane(trackId, target)?.points ?? [];
  }

  /** A track's automation mode (the master's is always read). */
  mode(trackId: string | null): AutomationMode {
    return trackId === null ? 'read' : (this.project.track(trackId)?.automationMode ?? 'read');
  }

  setMode(trackId: string, mode: AutomationMode): Promise<unknown> {
    return this.project.execute([{ type: 'setAutomationMode', trackId, mode }], `Automation ${ModeLabels[mode]}`);
  }

  /** Replaces a lane's points: one undo step ("Automation: Volume"). An empty list removes the lane. */
  commit(trackId: string | null, target: string, points: readonly AutomationPoint[]): Promise<unknown> {
    const name = this.target(trackId, target).name;
    return this.project.execute(
      [
        {
          type: 'setAutomation',
          target,
          points: points.map(p => ({ tick: Math.max(0, Math.round(p.tick)), value: p.value, curve: p.curve })),
          ...(trackId === null ? {} : { trackId })
        }
      ],
      `Automation: ${name}`
    );
  }

  selectPoints(trackId: string, target: string, points: Iterable<number>): void {
    this.regions.clear();
    this.selection.set({ trackId, target, points: new Set(points), range: null });
  }

  selectRange(trackId: string, target: string, startTick: number, endTick: number, points: Iterable<number>): void {
    this.regions.clear();
    this.selection.set({
      trackId,
      target,
      points: new Set(points),
      range: { startTick: Math.min(startTick, endTick), endTick: Math.max(startTick, endTick) }
    });
  }

  clearSelection(): void {
    this.selection.set(null);
  }

  /**
   * Deletes what is selected: the time range (keeping the values at its edges) or the selected points. False when
   * nothing is selected, so Delete falls through to regions.
   */
  deleteSelection(): boolean {
    const selection = this.selection();
    if (!selection || (!selection.range && selection.points.size === 0)) {
      return false;
    }
    const points = this.points(selection.trackId, selection.target);
    const stepped = this.target(selection.trackId, selection.target).stepped;
    const next = selection.range
      ? deleteRange(points, selection.range.startTick, selection.range.endTick, stepped)
      : removePoints(points, selection.points);
    this.selection.set(null);
    void this.commit(selection.trackId, selection.target, next);
    return true;
  }

  /**
   * A lane's value at the playhead while the transport plays and the track's mode plays automation (not off); else
   * undefined, and the control shows its own value. The Mixer's faders and knobs follow it.
   */
  playbackValue(trackId: string | null, target: string): number | undefined {
    if (!this.transport.playing() || this.mode(trackId) === 'off') {
      return undefined;
    }
    const points = this.project.automationLane(trackId, target)?.points;
    if (!points?.length) {
      return undefined;
    }
    return automationValueAt(points, this.playheadTick(), target === 'mute');
  }

  /** A lane's value at a tick (its first or last point beyond its ends), or undefined when it has no points. */
  valueAt(trackId: string | null, target: string, tick: number): number | undefined {
    return automationValueAt(this.points(trackId, target), tick, this.target(trackId, target).stepped);
  }

  private source(trackId: string | null): TargetSource | undefined {
    if (trackId === null) {
      return { strip: this.project.master() };
    }
    const track: Track | undefined = this.project.track(trackId);
    return track;
  }
}
