import { computed, inject, Injectable, signal } from '@angular/core';
import type { CommandResult, EditCommand, Track } from '@anode/api-client';
import { sliceRange } from '@anode/timeline';
import { AutomationStore } from '../automation/automation.store';
import { Notify } from '../core/notify';
import {
  type ClipboardSpan,
  contentOf,
  cutLaneCommands,
  isEmpty,
  type LaneSlice,
  laneSlices,
  marqueeContent,
  mergedRanges,
  NoContent,
  pasteCommand,
  pasteLaneCommands,
  placeTracks,
  type RegionClipboard,
  type RegionContent,
  regionCount,
  trackNeeds,
  type TrackRange
} from '../tracks/region-clipboard';
import { plural } from '../tracks/region-edit';
import { marqueeDeleteCommands, type RegionLookup } from '../tracks/tool-edits';
import { LanesStore } from './lanes.store';
import { ProjectStore } from './project.store';
import { SelectionStore } from './selection.store';
import { type MarqueeRange, TracksViewStore } from './tracks-view.store';
import { TransportStore } from './transport.store';

/** Tracks without lanes (the Server's `RangeOf`). */
const NoAutomation: readonly Track['kind'][] = ['stack', 'drummer', 'midi'];

/**
 * Cut, copy and paste in the Tracks area (⌘X, ⌘C, ⌘V). What they act on is what Delete acts on, in its order: selected
 * automation, else the marquee tool's range, else the selected regions, with the automation under them. Paste puts the
 * earliest of it at the playhead and its topmost track on the selected track, as one undo step.
 */
@Injectable({ providedIn: 'root' })
export class RegionClipboardStore {
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly automation = inject(AutomationStore);
  private readonly view = inject(TracksViewStore);
  private readonly lanes = inject(LanesStore);
  private readonly transport = inject(TransportStore);
  private readonly notify = inject(Notify);

  private readonly stored = signal<RegionClipboard | null>(null);
  /** The clipboard, for as long as its project is the open one (a region's files are its project's). */
  readonly content = computed(() => {
    const clipboard = this.stored();
    return clipboard?.projectId === this.project.info()?.id ? clipboard : null;
  });
  readonly canPaste = computed(() => this.content() !== null);

  /** Keeps what is selected; false when nothing is. */
  copy(): boolean {
    const clipboard = this.capture();
    if (clipboard) {
      this.stored.set(clipboard);
    }
    return clipboard !== null;
  }

  /** Keeps what is selected and removes it, as one undo step. */
  cut(): void {
    if (!this.copy()) {
      return;
    }
    if (this.automation.deleteSelection()) {
      return;
    }
    const clipboard = this.stored();
    const count = clipboard ? regionCount(clipboard.content) : 0;
    const range = this.view.marquee();
    const ids = [...this.selection.regions()];
    const commands: EditCommand[] = range ? this.marqueeDelete(range) : [{ type: 'deleteRegions', regionIds: ids }];
    this.view.marquee.set(null);
    void this.project.execute(
      [...commands, ...cutLaneCommands(clipboard?.lanes ?? [], this.project.automationList())],
      plural('Cut', count)
    );
  }

  /** Puts the clipboard at the playhead on the selected track, and selects what it pasted. */
  async paste(): Promise<void> {
    const clipboard = this.content();
    if (!clipboard) {
      return;
    }
    const anchor = this.selection.track() ?? clipboard.tracks[0]?.trackId;
    const tracks = this.project.trackList();
    const places = anchor ? placeTracks(clipboard.tracks, anchor, tracks) : null;
    if (!places) {
      this.notify.error('The selected track cannot hold what is on the clipboard.');
      return;
    }
    const at = Math.max(0, Math.round(this.transport.positionAt(performance.now())));
    const offset = at - clipboard.startTick;
    const count = regionCount(clipboard.content);
    const lanes = pasteLaneCommands(clipboard.lanes, offset, places, this.project.automationList(), (trackId, slice) =>
      this.accepts(trackId, slice)
    );
    const commands = [...(count > 0 ? [pasteCommand(clipboard.content, offset, places)] : []), ...lanes.commands];
    if (commands.length === 0) {
      this.notify.error('The selected track has no such automation target.');
      return;
    }
    const result = await this.project.execute(commands, count > 0 ? plural('Paste', count) : 'Paste Automation');
    if (result) {
      if (count > 0) {
        this.selection.select(pastedIds(result));
      }
      if (lanes.skipped > 0) {
        this.notify.info(plural('Left out', lanes.skipped, 'automation lane') + ' its track does not have.');
      }
    }
  }

  /** What Delete does to a marquee range: regions inside go, those across an edge are trimmed to it. */
  marqueeDelete(range: MarqueeRange): EditCommand[] {
    return marqueeDeleteCommands(range.startTick, range.endTick, this.marqueeSpans(range), this.lookup());
  }

  private capture(): RegionClipboard | null {
    const selected = this.automation.selection();
    if (selected && (selected.range || selected.points.size > 0)) {
      return this.automationClipboard(selected.trackId, selected.target);
    }
    const range = this.view.marquee();
    return range ? this.marqueeClipboard(range) : this.regionsClipboard();
  }

  private regionsClipboard(): RegionClipboard | null {
    const ids = this.selection.regions();
    const spans = this.spans().filter(s => ids.has(s.id));
    return this.clipboardOf(contentOf([...ids], this.lookup()), spans, spans);
  }

  private marqueeClipboard(range: MarqueeRange): RegionClipboard | null {
    const spans = this.marqueeSpans(range);
    const content = marqueeContent(range.startTick, range.endTick, spans, this.lookup());
    const ranges = this.marqueeTracks(range).map(trackId => ({
      trackId,
      startTick: range.startTick,
      endTick: range.endTick
    }));
    return this.clipboardOf(content, ranges, this.placedSpans(content));
  }

  /** Selected points of one lane, or its selected range with the values at the range's edges. */
  private automationClipboard(trackId: string, target: string): RegionClipboard | null {
    const selection = this.automation.selection();
    const points = this.automation.points(trackId, target);
    const stepped = this.automation.target(trackId, target).stepped;
    const range = selection?.range;
    const chosen = range
      ? sliceRange(points, range.startTick, range.endTick, stepped)
      : points.filter((_, i) => selection?.points.has(i));
    const first = chosen[0];
    const last = chosen.at(-1);
    const projectId = this.project.info()?.id;
    if (!first || !last || projectId === undefined) {
      return null;
    }
    const slice: LaneSlice = { trackId, target, stepped, startTick: first.tick, endTick: last.tick, points: chosen };
    return {
      projectId,
      startTick: first.tick,
      content: NoContent,
      lanes: [slice],
      tracks: [{ trackId, row: 0, needs: [] }]
    };
  }

  /** A clipboard of `content` with the automation under `ranges`; `spans` say where the content starts. */
  private clipboardOf(
    content: RegionContent,
    ranges: readonly TrackRange[],
    spans: readonly { trackId: string; startTick: number }[]
  ): RegionClipboard | null {
    const projectId = this.project.info()?.id;
    const lanes = laneSlices(
      mergedRanges(ranges),
      this.project.automationList(),
      (trackId, target) => this.automation.target(trackId, target).stepped
    );
    if (projectId === undefined || isEmpty(content, lanes)) {
      return null;
    }
    const order = this.project.trackList().map(t => t.id);
    const needs = trackNeeds(content, id => this.project.track(id)?.kind);
    const ids = [...new Set([...spans.map(s => s.trackId), ...lanes.map(l => l.trackId)])].sort(
      (a, b) => order.indexOf(a) - order.indexOf(b)
    );
    const top = order.indexOf(ids[0] ?? '');
    return {
      projectId,
      startTick: Math.min(...spans.map(s => s.startTick), ...lanes.map(l => l.startTick)),
      content,
      lanes,
      tracks: ids.map(trackId => ({
        trackId,
        row: order.indexOf(trackId) - top,
        needs: [...(needs.get(trackId) ?? [])]
      }))
    };
  }

  private placedSpans(content: RegionContent): { trackId: string; startTick: number }[] {
    return [
      ...content.regions,
      ...content.midiRegions,
      ...content.takeFolders,
      ...content.patternRegions,
      ...content.sessionPlayerRegions
    ];
  }

  /** Whether a slice's target is on the track it lands on: its own always; another's only what that strip has. */
  private accepts(trackId: string, slice: LaneSlice): boolean {
    if (trackId === slice.trackId) {
      return true;
    }
    const kind = this.project.track(trackId)?.kind;
    return (
      kind !== undefined &&
      !NoAutomation.includes(kind) &&
      this.automation.targets(trackId).some(t => t.target === slice.target)
    );
  }

  private marqueeTracks(range: MarqueeRange): string[] {
    return this.lanes
      .rows()
      .slice(range.laneFrom, range.laneTo + 1)
      .filter(row => row.kind === 'track')
      .map(row => row.track.id);
  }

  private marqueeSpans(range: MarqueeRange): ClipboardSpan[] {
    const tracks = new Set(this.marqueeTracks(range));
    return this.spans().filter(s => tracks.has(s.trackId));
  }

  /** Every region of every kind as a span. */
  private spans(): ClipboardSpan[] {
    const project = this.project;
    const lengthed = [
      ...project.midiRegions().values(),
      ...project.takeFolders().values(),
      ...project.patternRegions().values(),
      ...project.sessionPlayerRegions().values()
    ];
    return [
      ...[...project.regions().values()].map(r => ({
        id: r.id,
        trackId: r.trackId,
        startTick: r.startTick,
        endTick: project.regionEnd(r)
      })),
      ...lengthed.map(r => ({
        id: r.id,
        trackId: r.trackId,
        startTick: r.startTick,
        endTick: r.startTick + r.lengthTicks
      }))
    ];
  }

  private lookup(): RegionLookup {
    const project = this.project;
    const tempo = project.tempoMap();
    const rate = project.info()?.sampleRate ?? 48_000;
    return {
      regions: project.regions(),
      midiRegions: project.midiRegions(),
      takeFolders: project.takeFolders(),
      patternRegions: project.patternRegions(),
      sessionPlayerRegions: project.sessionPlayerRegions(),
      endOf: r => project.regionEnd(r),
      frames: (from, to) => Math.round(tempo.framesBetween(from, to - from, rate)),
      fileFrames: fileId => project.files().get(fileId)?.frames ?? 0
    };
  }
}

/** The regions an edit added or changed, of every kind. */
function pastedIds(result: CommandResult): string[] {
  return result.delta.ops.flatMap(op => {
    switch (op.op) {
      case 'regionUpserted':
      case 'midiRegionUpserted':
      case 'patternRegionUpserted':
      case 'sessionPlayerRegionUpserted':
        return [op.region.id];
      case 'takeFolderUpserted':
        return [op.folder.id];
      default:
        return [];
    }
  });
}
