import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RULER_HEIGHT } from '@anode/timeline';
import { provideIcons } from '@ng-icons/core';
import {
  matAddRound,
  matContentCutRound,
  matFileDownloadRound,
  matGridOnRound,
  matNearMeRound,
  matZoomInRound,
  matZoomOutRound
} from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiSegmentedControlImports, type XuiSegmentedOption } from '@xui/segmented-control';
import { XuiSpinnerImports } from '@xui/spinner';
import { XuiTooltipImports } from '@xui/tooltip';
import { ConnectionService } from '../core/connection';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { type TracksTool, TracksViewStore } from '../state/tracks-view.store';
import { TransportStore } from '../state/transport.store';
import { AudioImport } from './audio-import';
import { Timeline } from './timeline';
import { TrackHeader } from './track-header';

/**
 * The Tracks area: a toolbar (tool, snap, zoom, new track, import), the track headers in the DOM on the left, and the
 * canvas timeline beside them (docs/01-implementation-plan.md §6.3). Headers and lanes share the vertical scroll and
 * lane height from {@link TracksViewStore}.
 */
@Component({
  selector: 'an-tracks-area',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    Timeline,
    TrackHeader,
    XuiButtonImports,
    XuiIconImports,
    XuiSegmentedControlImports,
    XuiSpinnerImports,
    XuiTooltipImports
  ],
  providers: [
    provideIcons({
      matAddRound,
      matContentCutRound,
      matFileDownloadRound,
      matGridOnRound,
      matNearMeRound,
      matZoomInRound,
      matZoomOutRound
    })
  ],
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Tracks' },
  template: `
    <div
      class="border-border flex h-9 shrink-0 items-center gap-2 border-b px-2"
      role="toolbar"
      aria-label="Tracks area"
    >
      <button
        xuiButton
        type="button"
        size="sm"
        variant="ghost"
        aria-label="New track"
        xuiTooltip="New audio track"
        data-testid="add-track"
        (click)="addTrack()"
      >
        <ng-icon xui name="matAddRound" size="sm" />
      </button>
      @if (desktop?.dialogs) {
        <button
          xuiButton
          type="button"
          size="sm"
          variant="ghost"
          aria-label="Import audio"
          xuiTooltip="Import audio files onto the selected track"
          (click)="importAudio()"
        >
          <ng-icon xui name="matFileDownloadRound" size="sm" />
        </button>
      }
      <xui-segmented-control
        size="sm"
        ariaLabel="Tool"
        [options]="tools"
        [value]="view.tool()"
        (valueChange)="setTool($event)"
      />
      <button
        xuiButton
        type="button"
        size="sm"
        variant="ghost"
        aria-label="Snap to grid"
        xuiTooltip="Snap to grid"
        [active]="view.snap()"
        [class.text-primary]="view.snap()"
        (click)="view.snap.set(!view.snap())"
      >
        <ng-icon xui name="matGridOnRound" size="sm" />
      </button>
      <div class="ms-auto flex items-center gap-1">
        @if (audioImport.importing() > 0) {
          <xui-spinner size="sm" aria-label="Importing audio" />
        }
        <button xuiButton type="button" size="sm" variant="ghost" aria-label="Zoom out" (click)="view.zoom(1 / 1.5)">
          <ng-icon xui name="matZoomOutRound" size="sm" />
        </button>
        <button xuiButton type="button" size="sm" variant="ghost" aria-label="Zoom in" (click)="view.zoom(1.5)">
          <ng-icon xui name="matZoomInRound" size="sm" />
        </button>
      </div>
    </div>
    <div class="flex min-h-0 flex-1">
      <div class="border-border relative w-64 shrink-0 overflow-hidden border-e" (wheel)="wheel($event)">
        <div class="bg-surface border-border border-b" [style.height.px]="ruler"></div>
        <div class="absolute inset-x-0" [style.top.px]="ruler" [style.transform]="'translateY(' + offset() + 'px)'">
          @for (track of project.trackList(); track track.id; let i = $index) {
            <an-track-header [track]="track" [index]="i" [style.height.px]="view.laneHeight()" />
          }
          @if (project.trackList().length === 0) {
            <p class="text-foreground-muted p-3 text-xs">No tracks yet. Add one with + or drop a WAV file here.</p>
          }
        </div>
      </div>
      <an-timeline class="flex-1" />
    </div>
  `
})
export class TracksArea {
  protected readonly project = inject(ProjectStore);
  protected readonly view = inject(TracksViewStore);
  protected readonly audioImport = inject(AudioImport);
  private readonly selection = inject(SelectionStore);
  private readonly transport = inject(TransportStore);
  protected readonly desktop = inject(ConnectionService).desktop;

  protected readonly ruler = RULER_HEIGHT;
  protected readonly tools: XuiSegmentedOption<TracksTool>[] = [
    { value: 'pointer', label: 'Pointer' },
    { value: 'scissors', label: 'Scissors' }
  ];
  protected readonly offset = computed(() => -this.view.firstLane() * this.view.laneHeight());

  protected setTool(tool: TracksTool | null): void {
    if (tool) {
      this.view.tool.set(tool);
    }
  }

  protected addTrack(): void {
    const selected = this.selection.track();
    const index = selected ? this.project.trackIds().indexOf(selected) + 1 : null;
    void this.project.execute([{ type: 'addTrack', stereo: false, ...(index ? { index } : {}) }], 'New Track');
  }

  /** The desktop app's Import: files from a native dialog, onto the selected track at the playhead. */
  protected async importAudio(): Promise<void> {
    const paths = (await this.desktop?.dialogs?.importAudio()) ?? [];
    if (paths.length > 0) {
      await this.audioImport.importPaths(paths, this.selection.track(), this.transport.positionAt(performance.now()));
    }
  }

  protected wheel(event: WheelEvent): void {
    event.preventDefault();
    this.view.scrollBy(0, event.deltaY, this.project.trackIds().length);
  }
}
