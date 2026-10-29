import { CdkContextMenuTrigger } from '@angular/cdk/menu';
import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, viewChild } from '@angular/core';
import type { AutomationMode, EditCommand, TrackKind } from '@anode/api-client';
import { RULER_HEIGHT } from '@anode/timeline';
import { provideIcons } from '@ng-icons/core';
import {
  matAddRound,
  matAppsRound,
  matAutoFixNormalRound,
  matContentCutRound,
  matEditRound,
  matExpandMoreRound,
  matFileDownloadRound,
  matGraphicEqRound,
  matGridOnRound,
  matHeadphonesRound,
  matHighlightAltRound,
  matHourglassEmptyRound,
  matJoinFullRound,
  matLayersRound,
  matNearMeRound,
  matPianoRound,
  matPublicRound,
  matSettingsInputComponentRound,
  matShowChartRound,
  matTextFieldsRound,
  matTrendingUpRound,
  matViewModuleRound,
  matVolumeOffRound,
  matZoomInRound,
  matZoomOutRound
} from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiContextMenuImports } from '@xui/context-menu';
import { XuiIconImports } from '@xui/icon';
import { XuiMenuImports } from '@xui/menu';
import { XuiSpinnerImports } from '@xui/spinner';
import { XuiTooltipImports } from '@xui/tooltip';
import { AutomationHeader } from '../automation/automation-header';
import { AutomationLanes } from '../automation/automation-lanes';
import { AutomationStore } from '../automation/automation.store';
import { ConnectionService } from '../core/connection';
import { DrumMachineStore } from '../drum-machine/drum-machine.store';
import { GlobalHeaders } from '../global/global-headers';
import { GlobalTracks } from '../global/global-lanes';
import { GlobalLaneHeights } from '../global/global-math';
import { formatBinding } from '../keys/key-commands';
import { LiveLoopsGrid } from '../live-loops/live-loops-grid';
import { FlexStore } from '../state/flex.store';
import { JobsStore } from '../state/jobs.store';
import { KeyCommandsStore } from '../state/key-commands.store';
import { LanesStore } from '../state/lanes.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { type GlobalLane, GlobalLanes as GlobalLaneOrder, Tools, TracksViewStore } from '../state/tracks-view.store';
import { TransportStore } from '../state/transport.store';
import { UiStore } from '../state/ui.store';
import { AudioImport } from './audio-import';
import { canPack } from './comp';
import { DragModes } from './drag-modes';
import { plural } from './region-edit';
import { SnapModes } from './snap';
import { TakeHeader } from './take-header';
import { Timeline } from './timeline';
import { marqueeDeleteCommands, muteCommands } from './tool-edits';
import { TrackHeader } from './track-header';

/**
 * The Tracks area: a toolbar (new track, import, the left-click tool and the ⌘-click tool, snap and its mode, the drag
 * mode, the global tracks, zoom), the global tracks under the ruler, the track headers (stacks with their members, an
 * open take folder's takes) in the DOM on the left, and the canvas timeline beside them
 * (docs/01-implementation-plan.md §6.3). Headers and lanes share the rows ({@link LanesStore}), vertical scroll and
 * lane height ({@link TracksViewStore}). A right-click opens the tool menu, or the take folder's or the regions'
 * menu; T opens the tool menu where the pointer is.
 */
@Component({
  selector: 'an-tracks-area',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AutomationHeader,
    AutomationLanes,
    GlobalHeaders,
    GlobalTracks,
    LiveLoopsGrid,
    TakeHeader,
    Timeline,
    TrackHeader,
    XuiButtonImports,
    XuiContextMenuImports,
    XuiIconImports,
    XuiMenuImports,
    XuiSpinnerImports,
    XuiTooltipImports
  ],
  providers: [
    provideIcons({
      matAddRound,
      matAppsRound,
      matAutoFixNormalRound,
      matContentCutRound,
      matEditRound,
      matExpandMoreRound,
      matFileDownloadRound,
      matGraphicEqRound,
      matGridOnRound,
      matHeadphonesRound,
      matHighlightAltRound,
      matHourglassEmptyRound,
      matJoinFullRound,
      matLayersRound,
      matNearMeRound,
      matPianoRound,
      matPublicRound,
      matShowChartRound,
      matSettingsInputComponentRound,
      matTextFieldsRound,
      matTrendingUpRound,
      matViewModuleRound,
      matVolumeOffRound,
      matZoomInRound,
      matZoomOutRound
    })
  ],
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Tracks' },
  template: `
    <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2" role="toolbar" aria-label="Tracks area">
      <button
        xuiButton
        type="button"
        size="sm"
        variant="ghost"
        aria-label="New track"
        xuiTooltip="New audio track"
        data-testid="add-track"
        (click)="addTrack('audio')"
      >
        <ng-icon xui name="matAddRound" size="sm" />
      </button>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="ghost"
        class="-ms-2 w-5 px-0"
        aria-label="New track of a kind"
        xuiTooltip="New audio, software instrument or external MIDI track"
        data-testid="add-track-menu"
        [xuiMenuTriggerFor]="kinds"
      >
        <ng-icon xui name="matExpandMoreRound" size="sm" />
      </button>
      <ng-template #drumKits>
        <xui-menu class="min-w-40" aria-label="Drum kits">
          @for (kit of drumMachines.kits(); track kit.id) {
            <button xuiMenuItem type="button" [attr.data-kit]="kit.id" (click)="drumMachines.create(kit)">
              {{ kit.name }}
            </button>
          }
          @if (drumMachines.kits().length > 0) {
            <xui-menu-divider />
          }
          <button xuiMenuItem type="button" data-testid="empty-kit" (click)="drumMachines.create(null)">
            Empty Kit
          </button>
        </xui-menu>
      </ng-template>
      <ng-template #kinds>
        <xui-menu>
          <button xuiMenuItem type="button" icon="matGraphicEqRound" (click)="addTrack('audio')">Audio Track</button>
          <button
            xuiMenuItem
            type="button"
            icon="matPianoRound"
            data-testid="add-instrument-track"
            (click)="addTrack('instrument')"
          >
            Software Instrument Track
          </button>
          <button
            xuiMenuItem
            type="button"
            icon="matSettingsInputComponentRound"
            data-testid="add-midi-track"
            (click)="addTrack('midi')"
          >
            External MIDI Track
          </button>
          <button
            xuiMenuItem
            type="button"
            icon="matAppsRound"
            data-testid="add-drum-machine"
            [xuiMenuTriggerFor]="drumKits"
          >
            Drum Machine Designer
          </button>
          <xui-menu-divider />
          <button
            xuiMenuItem
            type="button"
            icon="matLayersRound"
            data-testid="create-stack"
            [disabled]="stackable().length === 0"
            (click)="createStack()"
          >
            Create Track Stack ({{ keyOf('track.stack') }})
          </button>
        </xui-menu>
      </ng-template>
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
      <div class="bezel-group text-foreground h-6.5" role="radiogroup" aria-label="Tool" data-testid="tools">
        @for (tool of tools; track tool.value) {
          <button
            type="button"
            role="radio"
            class="grid w-6.5 place-items-center"
            [class.text-lit]="view.tool() === tool.value"
            [attr.aria-checked]="view.tool() === tool.value"
            [attr.aria-label]="tool.label"
            [xuiTooltip]="tool.label + ' tool (T ' + tool.key + '; ⌘-click: ' + secondaryLabel() + ')'"
            (click)="view.tool.set(tool.value)"
          >
            <ng-icon xui [name]="tool.icon" size="15px" />
          </button>
        }
      </div>
      <button
        type="button"
        class="bezel text-foreground flex h-6.5 items-center gap-1 px-2 text-[11px] font-semibold"
        aria-label="Command-click tool"
        data-testid="secondary-tool"
        [xuiTooltip]="'⌘-click tool: ' + secondaryLabel()"
        [xuiMenuTriggerFor]="secondaryMenu"
      >
        <span class="text-foreground-muted">⌘</span>{{ secondaryLabel() }}
        <ng-icon xui name="matExpandMoreRound" size="14px" />
      </button>
      <ng-template #secondaryMenu>
        <xui-menu>
          @for (tool of tools; track tool.value) {
            <button
              xuiMenuItem
              type="button"
              role="menuitemradio"
              [attr.aria-checked]="view.secondaryTool() === tool.value"
              [icon]="tool.icon"
              (click)="view.secondaryTool.set(tool.value)"
            >
              {{ tool.label }}
            </button>
          }
        </xui-menu>
      </ng-template>
      <div class="bezel-group text-foreground h-6.5 text-[11px] font-semibold">
        <button
          type="button"
          class="grid w-7 place-items-center"
          aria-label="Snap to grid"
          xuiTooltip="Snap"
          [attr.aria-pressed]="view.snap() ? 'true' : 'false'"
          [class.text-lit]="view.snap()"
          (click)="view.snap.set(!view.snap())"
        >
          <ng-icon xui name="matGridOnRound" size="15px" />
        </button>
        <button
          type="button"
          class="flex items-center gap-1 px-2"
          aria-label="Snap mode"
          data-testid="snap-mode"
          [xuiMenuTriggerFor]="snapMenu"
        >
          {{ snapLabel() }}
          <ng-icon xui name="matExpandMoreRound" size="14px" />
        </button>
      </div>
      <ng-template #snapMenu>
        <xui-menu>
          @for (mode of snapModes; track mode.value) {
            <button
              xuiMenuItem
              type="button"
              role="menuitemradio"
              [attr.aria-checked]="view.snapMode() === mode.value"
              (click)="view.snapMode.set(mode.value); view.snap.set(true)"
            >
              {{ mode.label }}
            </button>
          }
        </xui-menu>
      </ng-template>
      <button
        type="button"
        class="bezel text-foreground flex h-6.5 items-center gap-1 px-2 text-[11px] font-semibold"
        aria-label="Drag mode"
        data-testid="drag-mode"
        xuiTooltip="Drag: what a moved region does to those it lands on"
        [xuiMenuTriggerFor]="dragMenu"
      >
        <span class="text-foreground-muted">Drag</span>{{ dragLabel() }}
        <ng-icon xui name="matExpandMoreRound" size="14px" />
      </button>
      <ng-template #dragMenu>
        <xui-menu>
          @for (mode of dragModes; track mode.value) {
            <button
              xuiMenuItem
              type="button"
              role="menuitemradio"
              [attr.aria-checked]="view.dragMode() === mode.value"
              (click)="view.dragMode.set(mode.value)"
            >
              {{ mode.label }}
            </button>
          }
        </xui-menu>
      </ng-template>
      <button
        type="button"
        class="bezel text-foreground grid h-6.5 w-8 place-items-center"
        aria-label="Global tracks"
        data-testid="global-tracks"
        [xuiTooltip]="'Global tracks (' + keyOf('view.globalTracks') + ')'"
        [attr.aria-pressed]="view.globalTracks() ? 'true' : 'false'"
        [class.text-lit]="view.globalTracks()"
        (click)="toggleGlobal()"
      >
        <ng-icon xui name="matPublicRound" size="15px" />
      </button>
      <button
        type="button"
        class="bezel text-foreground grid h-6.5 w-8 place-items-center"
        aria-label="Automation"
        data-testid="show-automation"
        [xuiTooltip]="'Show automation (' + keyOf('view.automation') + ')'"
        [attr.aria-pressed]="automation.shown() ? 'true' : 'false'"
        [class.text-lit]="automation.shown()"
        (click)="automation.toggleShown()"
      >
        <ng-icon xui name="matShowChartRound" size="15px" />
      </button>
      <button
        type="button"
        class="bezel text-foreground grid h-6.5 w-8 place-items-center"
        aria-label="Flex"
        data-testid="flex-toggle"
        [xuiTooltip]="(flex.showFlex() ? 'Hide' : 'Show') + ' Flex (' + keyOf('view.flex') + ')'"
        [attr.aria-pressed]="flex.showFlex() ? 'true' : 'false'"
        [class.text-lit]="flex.showFlex()"
        (click)="flex.toggleShowFlex()"
      >
        <ng-icon xui name="matHourglassEmptyRound" size="15px" class="rotate-90" />
      </button>
      <button
        type="button"
        class="bezel text-foreground grid h-6.5 w-8 place-items-center"
        aria-label="Live Loops"
        data-testid="live-loops-toggle"
        [xuiTooltip]="(project.liveLoops().shown ? 'Hide' : 'Show') + ' the Live Loops grid'"
        [attr.aria-pressed]="project.liveLoops().shown ? 'true' : 'false'"
        [class.text-lit]="project.liveLoops().shown"
        (click)="toggleLiveLoops()"
      >
        <ng-icon xui name="matViewModuleRound" size="15px" />
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
      <div class="border-hairline bg-surface relative w-64 shrink-0 overflow-hidden border-e" (wheel)="wheel($event)">
        <div class="glass-bar relative z-10" [style.height.px]="ruler"></div>
        @if (view.globalTracks()) {
          <an-global-headers class="relative z-10" [style.height.px]="globalHeight()" (add)="addGlobal($event)" />
        }
        <div
          class="absolute inset-x-0"
          [style.top.px]="ruler + globalHeight()"
          [style.transform]="'translateY(' + offset() + 'px)'"
        >
          @for (row of lanes.rows(); track row.id) {
            @if (row.kind === 'track') {
              <an-track-header
                [track]="row.track"
                [index]="numberOf(row.track.id)"
                [depth]="row.depth"
                [style.height.px]="view.laneHeight()"
              />
            } @else if (row.kind === 'take') {
              <an-take-header [folder]="row.folder" [take]="row.take" [style.height.px]="view.laneHeight()" />
            } @else {
              <an-automation-header
                [track]="row.track"
                [target]="row.target"
                [depth]="row.depth"
                [style.height.px]="view.laneHeight()"
              />
            }
          }
          @if (project.trackList().length === 0) {
            <p class="text-foreground-muted p-3 text-xs">
              No tracks yet. Add one with + (its arrow for an instrument or MIDI track) or drop a WAV or MIDI file here.
            </p>
          }
        </div>
      </div>
      @if (project.liveLoops().shown) {
        <an-live-loops-grid class="border-hairline w-96 shrink-0 border-e" data-testid="live-loops-grid" />
      }
      <div class="relative flex min-w-0 flex-1" [xuiContextMenuTriggerFor]="timelineMenu">
        <an-timeline class="flex-1" [globalHeight]="globalHeight()" />
        @if (automation.shown()) {
          <an-automation-lanes
            class="absolute inset-x-0 bottom-0"
            [style.top.px]="ruler + globalHeight()"
            (scrollWheel)="timeline().wheel($event)"
          />
        }
        @if (view.globalTracks()) {
          <an-global-lanes
            #global
            class="absolute inset-x-0"
            [style.top.px]="ruler"
            [style.height.px]="globalHeight()"
          />
        }
      </div>
    </div>
    <ng-template #timelineMenu>
      @switch (timeline().menuTarget().kind) {
        @case ('folder') {
          @if (menuFolder(); as folder) {
            <xui-menu>
              <button xuiMenuItem type="button" (click)="view.toggleFolder(folder.id)">
                {{ view.expandedFolders().has(folder.id) ? 'Close Take Folder' : 'Open Take Folder' }}
              </button>
              <button xuiMenuItem type="button" [xuiMenuTriggerFor]="takesMenu">Select Take</button>
              @if (menuTake(); as take) {
                <button
                  xuiMenuItem
                  type="button"
                  color="error"
                  [disabled]="folder.takes.length < 2"
                  (click)="deleteTake(folder.id, take.id)"
                >
                  Delete {{ take.name }}
                </button>
              }
              <xui-menu-divider />
              <button xuiMenuItem type="button" data-testid="flatten-folder" (click)="flatten(folder.id)">
                Flatten
              </button>
              <button xuiMenuItem type="button" (click)="toggleFolderMute(folder.id)">
                {{ folder.muted ? 'Unmute Take Folder' : 'Mute Take Folder' }}
              </button>
            </xui-menu>
            <ng-template #takesMenu>
              <xui-menu>
                @for (take of folder.takes; track take.id) {
                  <button xuiMenuItem type="button" (click)="selectTake(folder.id, take.id)">{{ take.name }}</button>
                }
              </xui-menu>
            </ng-template>
          }
        }
        @case ('regions') {
          <xui-menu>
            <button xuiMenuItem type="button" [disabled]="!packable()" data-testid="pack-folder" (click)="pack()">
              Pack Take Folder ({{ keyOf('region.pack') }})
            </button>
            <button xuiMenuItem type="button" (click)="join()">Join Regions ({{ keyOf('region.join') }})</button>
            <button xuiMenuItem type="button" (click)="mute()">Mute/Unmute ({{ keyOf('region.mute') }})</button>
            <button xuiMenuItem type="button" (click)="splitAtPlayhead()">
              Split at Playhead ({{ keyOf('region.split') }})
            </button>
            <button xuiMenuItem type="button" (click)="repeat()">Repeat Once ({{ keyOf('region.repeat') }})</button>
            <xui-menu-divider />
            <button xuiMenuItem type="button" (click)="ui.stripSilenceOpen.set(true)">
              Strip Silence… ({{ keyOf('region.stripSilence') }})
            </button>
            <button xuiMenuItem type="button" (click)="ui.bounceInPlaceOpen.set(true)">
              Bounce in Place… ({{ keyOf('region.bounceInPlace') }})
            </button>
            <button xuiMenuItem type="button" (click)="ui.processingOpen.set(true)">
              Selection-Based Processing… ({{ keyOf('region.process') }})
            </button>
            <xui-menu-divider />
            <button xuiMenuItem type="button" color="error" (click)="deleteSelected()">Delete</button>
          </xui-menu>
        }
        @default {
          <xui-menu class="min-w-44" aria-label="Tool menu" data-testid="tool-menu" (keydown)="toolKey($event)">
            @for (tool of tools; track tool.value) {
              <button
                xuiMenuItem
                type="button"
                role="menuitemradio"
                [attr.aria-checked]="view.tool() === tool.value"
                [icon]="tool.icon"
                (click)="view.tool.set(tool.value)"
              >
                <span class="flex-1">{{ tool.label }}</span>
                <kbd class="text-foreground-muted ms-4 font-sans text-[11px]">{{ tool.key }}</kbd>
              </button>
            }
          </xui-menu>
        }
      }
    </ng-template>
  `
})
export class TracksArea {
  protected readonly project = inject(ProjectStore);
  protected readonly view = inject(TracksViewStore);
  protected readonly lanes = inject(LanesStore);
  protected readonly audioImport = inject(AudioImport);
  private readonly selection = inject(SelectionStore);
  private readonly transport = inject(TransportStore);
  private readonly keys = inject(KeyCommandsStore);
  protected readonly desktop = inject(ConnectionService).desktop;
  protected readonly automation = inject(AutomationStore);
  private readonly jobs = inject(JobsStore);
  protected readonly ui = inject(UiStore);
  protected readonly flex = inject(FlexStore);
  protected readonly drumMachines = inject(DrumMachineStore);
  protected readonly timeline = viewChild.required(Timeline);
  private readonly menuTrigger = viewChild(CdkContextMenuTrigger);
  private readonly globalLanes = viewChild<GlobalTracks>('global');

  protected readonly ruler = RULER_HEIGHT;
  protected readonly tools = Tools;
  protected readonly snapModes = SnapModes;
  protected readonly dragModes = DragModes;
  protected readonly offset = computed(() => -this.view.firstLane() * this.view.laneHeight());
  protected readonly globalHeight = computed(() =>
    this.view.globalTracks()
      ? GlobalLaneOrder.filter(lane => this.view.globalLanes().has(lane)).reduce(
          (sum, lane) => sum + GlobalLaneHeights[lane],
          0
        )
      : 0
  );
  protected readonly secondaryLabel = computed(
    () => Tools.find(t => t.value === this.view.secondaryTool())?.label ?? ''
  );
  /** Shows or hides the Live Loops grid beside the Tracks area (Logic's). */
  protected toggleLiveLoops(): void {
    const shown = this.project.liveLoops().shown;
    void this.project.execute([{ type: 'setLiveLoops', shown: !shown }], shown ? 'Hide Live Loops' : 'Show Live Loops');
  }

  protected readonly snapLabel = computed(() => SnapModes.find(m => m.value === this.view.snapMode())?.label ?? '');
  protected readonly dragLabel = computed(() => DragModes.find(m => m.value === this.view.dragMode())?.label ?? '');
  protected readonly menuFolder = computed(() => {
    const target = this.timeline().menuTarget();
    return target.kind === 'folder' ? (this.project.takeFolder(target.folderId) ?? null) : null;
  });
  protected readonly menuTake = computed(() => {
    const target = this.timeline().menuTarget();
    const folder = this.menuFolder();
    return target.kind === 'folder' && target.takeId ? (folder?.takes.find(t => t.id === target.takeId) ?? null) : null;
  });
  /** Tracks a new stack would hold: the selected ones that are not stacks. */
  protected readonly stackable = computed(() =>
    this.selection.trackSelection().filter(id => {
      const kind = this.project.track(id)?.kind;
      return kind !== undefined && kind !== 'stack';
    })
  );
  protected readonly packable = computed(() => {
    const regions = this.project.regions();
    const spans = [...this.selection.regions()]
      .map(id => regions.get(id))
      .filter(r => r !== undefined)
      .map(r => ({ trackId: r.trackId, startTick: r.startTick, endTick: this.project.regionEnd(r) }));
    return spans.length === this.selection.regions().size && canPack(spans);
  });

  constructor() {
    const register: [string, () => void][] = [
      ['tools.menu', () => this.openToolMenu()],
      ['view.globalTracks', () => this.toggleGlobal()],
      ['view.zoomIn', () => this.view.zoom(1.5)],
      ['view.zoomOut', () => this.view.zoom(1 / 1.5)],
      ['region.pack', () => this.pack()],
      ['region.flatten', () => this.flattenSelected()],
      ['region.toggleTakes', () => this.toggleSelectedFolders()],
      ['region.join', () => this.join()],
      ['region.mute', () => this.mute()],
      ['region.split', () => this.splitAtPlayhead()],
      ['region.repeat', () => this.repeat()],
      ['track.stack', () => this.createStack()],
      ['track.marker', () => this.addGlobal('marker')],
      ['track.newAudio', () => this.addTrack('audio')],
      ['track.newInstrument', () => this.addTrack('instrument')],
      ['edit.selectAll', () => this.selectAll()],
      ['view.automation', () => this.automation.toggleShown()],
      ['view.flex', () => this.flex.toggleShowFlex()],
      ['region.quantizeAudio', () => this.quantizeAudio()],
      ['track.freeze', () => this.toggleFreeze()],
      ['automation.off', () => this.setMode('off')],
      ['automation.read', () => this.setMode('read')],
      ['automation.touch', () => this.setMode('touch')],
      ['automation.latch', () => this.setMode('latch')],
      ['automation.write', () => this.setMode('write')],
      ['edit.delete', () => this.deleteSelected()]
    ];
    const unregister = register.map(([id, action]) => this.keys.register(id, action));
    inject(DestroyRef).onDestroy(() => unregister.forEach(off => off()));
  }

  /** Quantises the selected audio regions at the Region inspector's grid (Q). */
  private quantizeAudio(): void {
    const ids = [...this.selection.regions()].filter(id => this.project.regions().has(id));
    const grid = this.flex.quantizeGrid();
    if (ids.length > 0 && grid > 0) {
      void this.flex.quantize(ids, grid, this.flex.quantizeStrength(), this.flex.quantizeSwing());
    }
  }

  /** Freezes the selected track, or plays it live again when it is frozen. */
  private toggleFreeze(): void {
    const track = this.project.track(this.selection.track() ?? '');
    if (track?.kind !== 'audio' && track?.kind !== 'instrument') {
      return;
    }
    if (track.frozenFileId) {
      void this.jobs.unfreeze(track.id);
    } else {
      void this.jobs.freeze(track.id);
    }
  }

  /** The selected tracks' automation mode. */
  private setMode(mode: AutomationMode): void {
    for (const id of this.selection.trackSelection()) {
      if (this.project.track(id)) {
        void this.automation.setMode(id, mode);
      }
    }
  }

  protected keyOf(id: string): string {
    const binding = this.keys.bindingOf(id);
    return binding ? formatBinding(binding) : '—';
  }

  protected numberOf(trackId: string): number {
    return this.project.trackIds().indexOf(trackId);
  }

  protected toggleGlobal(): void {
    this.view.globalTracks.set(!this.view.globalTracks());
    if (this.view.globalTracks() && this.view.globalLanes().size === 0) {
      this.view.globalLanes.set(new Set(['arrangement', 'marker', 'tempo', 'signature']));
    }
  }

  protected addGlobal(lane: GlobalLane): void {
    if (!this.view.globalTracks()) {
      this.view.globalTracks.set(true);
    }
    const lanes = this.globalLanes();
    if (lane === 'arrangement') {
      lanes?.addSection();
    } else if (lane === 'marker') {
      lanes?.addMarkerAtPlayhead();
    }
  }

  /** The tool menu where the pointer is (T), or over the middle of the timeline. */
  openToolMenu(): void {
    const timeline = this.timeline();
    timeline.menuTarget.set({ kind: 'tools' });
    const at = timeline.pointerAt();
    const host = document.querySelector('an-timeline')?.getBoundingClientRect();
    this.menuTrigger()?.open(
      at ?? { x: (host?.left ?? 0) + (host?.width ?? 0) / 2, y: (host?.top ?? 0) + (host?.height ?? 0) / 3 }
    );
    // Opened from the keyboard: the menu takes the keys (a tool's letter picks it).
    setTimeout(() =>
      document
        .querySelector<HTMLElement>('[data-testid="tool-menu"] [role="menuitemradio"][aria-checked="true"]')
        ?.focus()
    );
  }

  /** In the tool menu, a tool's key picks it (Logic's T then the key). */
  protected toolKey(event: KeyboardEvent): void {
    const tool = Tools.find(t => t.key === event.key.toUpperCase());
    if (tool && !event.metaKey && !event.ctrlKey && !event.altKey) {
      event.preventDefault();
      event.stopPropagation();
      this.view.tool.set(tool.value);
      this.menuTrigger()?.close();
    }
  }

  /** A new track after the selected one: audio (mono), or a software instrument (stereo) or external MIDI track. */
  protected addTrack(kind: TrackKind): void {
    const selected = this.selection.track();
    const index = selected ? this.project.trackIds().indexOf(selected) + 1 : null;
    const before = new Set(this.project.trackIds());
    void this.project
      .execute(
        [{ type: 'addTrack', stereo: kind === 'instrument', kind, ...(index ? { index } : {}) }],
        kind === 'audio' ? 'New Track' : kind === 'instrument' ? 'New Software Instrument Track' : 'New MIDI Track'
      )
      .then(result => {
        // Select the new track, so the keyboard plays it and the inspector shows it.
        const added = result?.delta.ops.find(op => op.op === 'trackUpserted' && !before.has(op.track.id));
        if (kind !== 'audio' && added?.op === 'trackUpserted') {
          this.selection.track.set(added.track.id);
        }
      });
  }

  /** A track stack of the selected tracks (⇧⌘D). */
  protected createStack(): void {
    const ids = this.stackable();
    if (ids.length > 0) {
      void this.project.execute([{ type: 'createStack', trackIds: ids }], 'Create Track Stack');
    }
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
    this.view.scrollBy(0, event.deltaY, this.lanes.count());
  }

  // ---- region commands (menus and keys)

  private selected(): string[] {
    return [...this.selection.regions()];
  }

  protected pack(): void {
    if (this.packable()) {
      void this.project.execute([{ type: 'packTakeFolder', regionIds: this.selected() }], 'Pack Take Folder');
    }
  }

  protected join(): void {
    const ids = this.selected().filter(id => !id.startsWith('tkf_'));
    if (ids.length > 1) {
      void this.project.execute([{ type: 'joinRegions', regionIds: ids }], 'Join Regions');
    }
  }

  protected mute(): void {
    const commands = muteCommands(this.selected(), {
      regions: this.project.regions(),
      midiRegions: this.project.midiRegions(),
      takeFolders: this.project.takeFolders(),
      endOf: r => this.project.regionEnd(r),
      frames: () => 0,
      fileFrames: () => 0
    });
    void this.project.execute(commands, plural('Mute', commands.length));
  }

  protected splitAtPlayhead(): void {
    const at = Math.round(this.transport.positionAt(performance.now()));
    const commands: EditCommand[] = this.selected().flatMap((id): EditCommand[] => {
      const audio = this.project.regions().get(id);
      const midi = this.project.midiRegions().get(id);
      const start = audio?.startTick ?? midi?.startTick;
      const end = audio ? this.project.regionEnd(audio) : midi ? midi.startTick + midi.lengthTicks : undefined;
      return start !== undefined && end !== undefined && at > start && at < end
        ? [{ type: 'splitRegion', regionId: id, atTick: at }]
        : [];
    });
    void this.project.execute(commands, plural('Split', commands.length));
  }

  /** Repeat Once: a copy right after the selection's end (⌘R). */
  protected repeat(): void {
    const ids = this.selected();
    const spans = this.timeline()
      .allSpans()
      .filter(s => ids.includes(s.id));
    if (spans.length === 0) {
      return;
    }
    const offset = Math.max(...spans.map(s => s.endTick)) - Math.min(...spans.map(s => s.startTick));
    void this.project.execute(
      [{ type: 'copyRegions', regionIds: ids, offsetTicks: Math.round(offset) }],
      plural('Repeat', ids.length)
    );
  }

  protected selectAll(): void {
    this.selection.select([
      ...this.project.regions().keys(),
      ...this.project.midiRegions().keys(),
      ...this.project.takeFolders().keys()
    ]);
  }

  /** Delete: selected automation (points or a range), else the marquee tool's range, else the selected regions. */
  deleteSelected(): void {
    if (this.automation.deleteSelection()) {
      return;
    }
    const range = this.view.marquee();
    if (range) {
      const tracks = new Set(
        this.lanes
          .rows()
          .slice(range.laneFrom, range.laneTo + 1)
          .filter(row => row.kind === 'track')
          .map(row => row.track.id)
      );
      const spans = this.timeline()
        .allSpans()
        .filter(s => tracks.has(s.trackId));
      const tempo = this.project.tempoMap();
      const rate = this.project.info()?.sampleRate ?? 48_000;
      const commands = marqueeDeleteCommands(range.startTick, range.endTick, spans, {
        regions: this.project.regions(),
        midiRegions: this.project.midiRegions(),
        takeFolders: this.project.takeFolders(),
        endOf: r => this.project.regionEnd(r),
        frames: (from, to) => Math.round(tempo.framesBetween(from, to - from, rate)),
        fileFrames: fileId => this.project.files().get(fileId)?.frames ?? 0
      });
      this.view.marquee.set(null);
      void this.project.execute(commands, 'Delete Marquee Selection');
      return;
    }
    const ids = this.selected();
    if (ids.length > 0) {
      void this.project.execute([{ type: 'deleteRegions', regionIds: ids }], plural('Delete', ids.length));
    }
  }

  protected flatten(folderId: string): void {
    void this.project.execute([{ type: 'flattenTakeFolder', folderId }], 'Flatten Take Folder');
  }

  private flattenSelected(): void {
    const folders = this.selected().filter(id => id.startsWith('tkf_'));
    void this.project.execute(
      folders.map(folderId => ({ type: 'flattenTakeFolder', folderId })),
      'Flatten Take Folder'
    );
  }

  private toggleSelectedFolders(): void {
    for (const id of this.selected().filter(i => i.startsWith('tkf_'))) {
      this.view.toggleFolder(id);
    }
  }

  protected selectTake(folderId: string, takeId: string): void {
    void this.project.execute([{ type: 'selectTake', folderId, takeId }], 'Select Take');
  }

  protected deleteTake(folderId: string, takeId: string): void {
    void this.project.execute([{ type: 'deleteTake', folderId, takeId }], 'Delete Take');
  }

  protected toggleFolderMute(folderId: string): void {
    const folder = this.project.takeFolder(folderId);
    if (folder) {
      void this.project.execute(
        [{ type: 'setTakeFolder', folderId, muted: !folder.muted }],
        folder.muted ? 'Unmute Take Folder' : 'Mute Take Folder'
      );
    }
  }
}
