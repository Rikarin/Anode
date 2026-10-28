import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
  type ElementRef
} from '@angular/core';
import type { AutomationMode, FlexMode, Track, TrackAlternative } from '@anode/api-client';
import {
  AnAutomationMode,
  automationModeLabel,
  AutomationModes,
  faderDb,
  faderPosition,
  formatDb
} from '@anode/audio-ui';
import { provideIcons } from '@ng-icons/core';
import {
  matAcUnitRound,
  matAddRound,
  matCallSplitRound,
  matChevronRightRound,
  matExpandMoreRound,
  matFolderRound,
  matGraphicEqRound,
  matHourglassEmptyRound,
  matLayersRound,
  matMoreHorizRound,
  matPianoRound,
  matSettingsInputComponentRound,
  matTuneRound
} from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiMenuImports } from '@xui/menu';
import { XuiPopoverImports } from '@xui/popover';
import { XuiSliderImports } from '@xui/slider';
import { XuiTooltipImports } from '@xui/tooltip';
import { AutomationStore } from '../automation/automation.store';
import { flexModeLabel, FlexModes, FlexStore } from '../state/flex.store';
import { isActive, JobsStore } from '../state/jobs.store';
import { LiveMidiStore } from '../state/live-midi.store';
import { MixerStore } from '../state/mixer.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TrackAlternativesStore } from '../state/track-alternatives.store';
import { TrackColors } from '../state/track-colors';
import { setTrack, setTrackLabel, type TrackChange } from '../state/track-commands';
import { UiStore } from '../state/ui.store';

/**
 * A track's header in the Tracks area (Logic's track header): its colour, number, kind and name (double-click to
 * rename), mute, solo, record enable and (audio) input monitoring, and a volume slider on the fader's dB taper. An
 * instrument or MIDI track has an activity dot that lights while the engine plays or receives MIDI on it. Its menu
 * moves and deletes the track. Every change is one command batch with a label for the undo history. Audio and instrument
 * tracks have Freeze (with the job's progress while it renders); while automation shows (A), the volume slider makes
 * way for the track's automation mode and a button that adds an automation lane.
 */
@Component({
  selector: 'an-track-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AnAutomationMode,
    XuiButtonImports,
    XuiIconImports,
    XuiInputImports,
    XuiMenuImports,
    XuiPopoverImports,
    XuiSliderImports,
    XuiTooltipImports
  ],
  providers: [
    provideIcons({
      matAcUnitRound,
      matAddRound,
      matCallSplitRound,
      matChevronRightRound,
      matExpandMoreRound,
      matFolderRound,
      matGraphicEqRound,
      matHourglassEmptyRound,
      matLayersRound,
      matMoreHorizRound,
      matPianoRound,
      matSettingsInputComponentRound,
      matTuneRound
    })
  ],
  host: {
    class: 'an-track-header border-hairline flex items-stretch gap-2 border-b pe-2',
    role: 'group',
    '[attr.aria-label]': '"Track " + number() + ": " + track().name',
    '[attr.data-track-id]': 'track().id',
    '[class.bg-muted]': 'selected()',
    '[class.bg-surface]': '!selected()',
    '[style.margin-inline-start.px]': 'depth() * 14',
    '[attr.data-stack]': 'track().kind === "stack" || null',
    '[attr.data-frozen]': 'frozen() || null',
    '(click)': 'select($event)'
  },
  template: `
    <button
      type="button"
      class="focus-visible:outline-focus w-1.5 shrink-0 cursor-pointer shadow-[inset_-1px_0_0_var(--hairline)] focus-visible:outline-2"
      [style.background-color]="track().color"
      [attr.aria-label]="'Colour of ' + track().name"
      [xuiPopover]="palette"
      placement="right-start"
    ></button>
    <ng-template #palette>
      <div class="grid grid-cols-6 gap-1.5 p-1.5" role="group" aria-label="Track colours">
        @for (color of colors; track color) {
          <button
            type="button"
            class="focus-visible:outline-focus aria-pressed:ring-foreground size-5 rounded-[5px] shadow-[inset_0_0_0_1px_var(--hairline)] focus-visible:outline-2 aria-pressed:ring-2 aria-pressed:ring-offset-1"
            [style.background-color]="color"
            [attr.aria-label]="'Colour ' + color"
            [attr.aria-pressed]="color === track().color"
            (click)="change({ color })"
          ></button>
        }
      </div>
    </ng-template>
    <div class="flex min-w-0 flex-1 flex-col justify-center gap-1 py-1">
      <div class="flex min-w-0 items-center gap-1.5">
        @if (track().kind === 'stack') {
          <button
            type="button"
            class="text-foreground-muted hover:text-foreground -ms-1 grid size-4 shrink-0 place-items-center"
            [attr.aria-label]="(track().collapsed ? 'Expand stack ' : 'Collapse stack ') + track().name"
            [attr.aria-expanded]="!track().collapsed"
            data-testid="stack-disclosure"
            (click)="$event.stopPropagation(); toggleCollapsed()"
          >
            <ng-icon xui size="sm" [name]="track().collapsed ? 'matChevronRightRound' : 'matExpandMoreRound'" />
          </button>
        }
        <span class="text-foreground-subtle w-4 shrink-0 text-end text-[11px] font-medium tabular-nums">{{
          number()
        }}</span>
        <ng-icon
          xui
          size="sm"
          class="text-foreground-muted shrink-0"
          [name]="kindIcon()"
          [attr.title]="kindTitle()"
          aria-hidden="true"
        />
        @if (renaming()) {
          <input
            #name
            xuiInput
            class="h-6 min-w-0 flex-1 px-1 text-xs"
            [value]="track().name"
            [attr.aria-label]="'Name of track ' + number()"
            (keydown.enter)="rename(name.value)"
            (keydown.escape)="renaming.set(false)"
            (blur)="rename(name.value)"
          />
        } @else {
          <span
            class="text-foreground min-w-0 flex-1 truncate text-[12px] font-semibold"
            [attr.title]="track().name + ' (double-click to rename)'"
            (dblclick)="startRename($event)"
          >
            {{ track().name }}
          </span>
        }
        @if (hasRegions()) {
          @if (renamingAlternative(); as alt) {
            <input
              #altName
              xuiInput
              class="h-5 w-16 shrink-0 px-1 text-[11px]"
              data-testid="track-alternative-name"
              [value]="alt.name"
              [attr.aria-label]="'Name of alternative ' + alt.name"
              (click)="$event.stopPropagation()"
              (keydown.enter)="renameAlternative(altName.value)"
              (keydown.escape)="renamingAlternative.set(null)"
              (blur)="renameAlternative(altName.value)"
            />
          } @else {
            <button
              type="button"
              class="bezel text-foreground flex h-4.5 max-w-16 shrink-0 items-center gap-0.5 px-1 text-[10px] font-semibold"
              data-testid="track-alternatives"
              aria-label="Track alternatives"
              [attr.aria-description]="activeAlternative()?.name ?? null"
              [xuiTooltip]="
                activeAlternative() ? 'Track alternative: ' + activeAlternative()?.name : 'Track alternatives'
              "
              [xuiMenuTriggerFor]="alternativesMenu"
              (click)="$event.stopPropagation()"
            >
              @if (activeAlternative(); as active) {
                <span class="truncate">{{ active.name }}</span>
              } @else {
                <ng-icon xui name="matLayersRound" size="12px" class="text-foreground-muted" />
              }
            </button>
          }
          <ng-template #alternativesMenu>
            <xui-menu aria-label="Track alternatives">
              @for (alt of track().alternatives; track alt.id) {
                <button
                  xuiMenuItem
                  type="button"
                  role="menuitemradio"
                  [attr.aria-checked]="alt.id === track().alternativeId"
                  [selected]="alt.id === track().alternativeId"
                  (click)="alternatives.select(track().id, alt.id)"
                >
                  {{ alt.name }}
                </button>
              }
              @if (track().alternatives.length > 0) {
                <xui-menu-divider />
              }
              <button xuiMenuItem type="button" (click)="alternatives.create(track().id)">New Alternative</button>
              <button xuiMenuItem type="button" (click)="alternatives.create(track().id, true)">
                Duplicate Alternative
              </button>
              <button
                xuiMenuItem
                type="button"
                [disabled]="!activeAlternative()"
                (click)="renamingAlternative.set(activeAlternative())"
              >
                Rename Alternative…
              </button>
              <button
                xuiMenuItem
                type="button"
                color="error"
                [disabled]="track().alternatives.length < 2"
                (click)="deleteAlternative()"
              >
                Delete Alternative
              </button>
            </xui-menu>
          </ng-template>
        }
        @if (track().kind !== 'audio') {
          <span
            class="size-1.5 shrink-0 rounded-full transition-colors duration-100"
            [class.bg-meter-low]="active()"
            [class.bg-border-strong]="!active()"
            role="img"
            data-testid="midi-activity"
            [attr.data-active]="active() || null"
            [attr.aria-label]="active() ? 'MIDI activity' : 'No MIDI activity'"
          ></span>
        }
        @if (freezable()) {
          @if (freezing(); as job) {
            <span
              class="text-lit readout w-7 shrink-0 text-end text-[10px] font-semibold"
              role="progressbar"
              aria-valuemin="0"
              aria-valuemax="100"
              [attr.aria-valuenow]="percent(job.progress)"
              [attr.aria-label]="'Freezing ' + track().name"
              data-testid="freeze-progress"
              >{{ percent(job.progress) }}%</span
            >
          } @else {
            <button
              type="button"
              class="hover:text-foreground focus-visible:outline-focus grid size-5 shrink-0 place-items-center rounded-[4px] focus-visible:outline-2"
              data-testid="freeze-track"
              [class.text-lit]="frozen()"
              [class.text-foreground-muted]="!frozen()"
              [attr.aria-pressed]="frozen()"
              [attr.aria-label]="'Freeze ' + track().name"
              [xuiTooltip]="frozen() ? 'Frozen: click to play it live again' : 'Freeze: render it to play from a file'"
              (click)="$event.stopPropagation(); toggleFreeze()"
            >
              <ng-icon xui name="matAcUnitRound" size="15px" />
            </button>
          }
        }
        <button
          xuiButton
          type="button"
          variant="ghost"
          size="sm"
          class="h-5 w-5 px-0"
          [attr.aria-label]="'More for ' + track().name"
          [xuiMenuTriggerFor]="menu"
        >
          <ng-icon xui name="matMoreHorizRound" size="sm" />
        </button>
        <ng-template #menu>
          <xui-menu>
            <button xuiMenuItem type="button" (click)="startRename()">Rename</button>
            <button xuiMenuItem type="button" [disabled]="index() === 0" (click)="moveBy(-1)">Move Up</button>
            <button xuiMenuItem type="button" [disabled]="index() === count() - 1" (click)="moveBy(1)">
              Move Down
            </button>
            <xui-menu-divider />
            @if (track().kind === 'stack') {
              <button xuiMenuItem type="button" (click)="unstack()">Flatten Stack</button>
            } @else {
              <button xuiMenuItem type="button" (click)="stack()">Create Track Stack</button>
              @if (track().parentId) {
                <button xuiMenuItem type="button" (click)="leaveStack()">Remove from Stack</button>
              }
            }
            @if (hasRegions()) {
              <xui-menu-divider />
              <button xuiMenuItem type="button" (click)="bounceInPlace()">Bounce Track in Place…</button>
            }
            <xui-menu-divider />
            <button xuiMenuItem type="button" color="error" (click)="remove()">Delete Track</button>
          </xui-menu>
        </ng-template>
      </div>
      <div class="flex items-center gap-1">
        <button
          type="button"
          class="state-toggle toggle-mute"
          [attr.aria-pressed]="track().strip.mute"
          [attr.aria-label]="'Mute ' + track().name"
          xuiTooltip="Mute"
          (click)="mixer.toggleMute(track().id)"
        >
          M
        </button>
        <button
          type="button"
          class="state-toggle toggle-solo"
          [attr.aria-pressed]="track().strip.solo"
          [attr.aria-label]="'Solo ' + track().name"
          xuiTooltip="Solo"
          (click)="mixer.toggleSolo(track().id)"
        >
          S
        </button>
        <button
          type="button"
          class="state-toggle toggle-arm"
          [class.invisible]="track().kind === 'stack'"
          [disabled]="track().kind === 'stack'"
          [attr.aria-pressed]="track().recordArmed"
          [attr.aria-label]="'Record enable ' + track().name"
          xuiTooltip="Record enable"
          (click)="change({ recordArmed: !track().recordArmed })"
        >
          R
        </button>
        <button
          type="button"
          class="state-toggle toggle-monitor"
          [class.invisible]="track().kind !== 'audio'"
          [disabled]="track().kind !== 'audio'"
          [attr.aria-pressed]="track().inputMonitoring"
          [attr.aria-label]="'Input monitoring ' + track().name"
          xuiTooltip="Input monitoring"
          (click)="change({ inputMonitoring: !track().inputMonitoring })"
        >
          I
        </button>
        @if (showFlex()) {
          <button
            type="button"
            class="state-toggle [--toggle-on-foreground:var(--color-primary-foreground)] [--toggle-on:var(--color-primary)]"
            data-testid="track-flex"
            aria-label="Flex"
            [attr.aria-pressed]="track().flexOn"
            [xuiTooltip]="track().flexOn ? 'Flex on: ' + flexLabel() : 'Flex off'"
            (click)="$event.stopPropagation(); flex.setTrackFlex(track().id, !track().flexOn)"
          >
            <ng-icon xui name="matHourglassEmptyRound" size="12px" class="rotate-90" />
          </button>
          <button
            type="button"
            class="bezel text-foreground flex h-4.5 min-w-0 shrink items-center px-1.5 text-[10px] font-semibold"
            data-testid="track-flex-mode"
            aria-label="Flex mode"
            [attr.aria-description]="flexLabel()"
            [xuiMenuTriggerFor]="flexModes"
            (click)="$event.stopPropagation()"
          >
            <span class="truncate">{{ flexLabel() }}</span>
          </button>
          <ng-template #flexModes>
            <xui-menu aria-label="Flex mode">
              @for (mode of flexModeList; track mode.value) {
                <button
                  xuiMenuItem
                  type="button"
                  role="menuitemradio"
                  [attr.aria-checked]="track().flexMode === mode.value"
                  [selected]="track().flexMode === mode.value"
                  (click)="setFlexMode(mode.value)"
                >
                  {{ mode.label }}
                </button>
              }
            </xui-menu>
          </ng-template>
        }
        @if (automation.shown()) {
          <div class="flex min-w-0 flex-1 items-center gap-1 ps-1">
            <button
              anAutomationMode
              type="button"
              class="min-w-0"
              [attr.aria-label]="'Automation mode of ' + track().name + ': ' + modeLabel(track().automationMode)"
              data-testid="automation-mode"
              [mode]="track().automationMode"
              [channel]="track().name"
              [xuiMenuTriggerFor]="modes"
              (click)="$event.stopPropagation()"
            ></button>
            <ng-template #modes>
              <xui-menu aria-label="Automation mode">
                @for (mode of modeList; track mode.value) {
                  <button
                    xuiMenuItem
                    type="button"
                    role="menuitemradio"
                    [attr.aria-checked]="track().automationMode === mode.value"
                    [selected]="track().automationMode === mode.value"
                    (click)="setMode(mode.value)"
                  >
                    {{ mode.label }}
                  </button>
                }
              </xui-menu>
            </ng-template>
            <button
              xuiButton
              type="button"
              variant="ghost"
              size="sm"
              class="ms-auto h-5 w-5 shrink-0 px-0"
              data-testid="add-automation-lane"
              [attr.aria-label]="'Add an automation lane to ' + track().name"
              xuiTooltip="Show another parameter's automation"
              (click)="$event.stopPropagation(); automation.addLane(track().id)"
            >
              <ng-icon xui name="matAddRound" size="sm" />
            </button>
          </div>
        } @else {
          <label class="an-track-volume flex min-w-0 flex-1 items-center ps-1" [attr.title]="'Volume ' + volumeText()">
            <span class="sr-only">Volume of {{ track().name }}</span>
            <xui-slider
              class="min-w-0 flex-1"
              color="none"
              [min]="0"
              [max]="1"
              [stepSize]="0.001"
              [labelRenderer]="false"
              [value]="volumePosition()"
              (valueChange)="setVolume($event)"
            />
          </label>
        }
      </div>
    </div>
  `
})
export class TrackHeader {
  protected readonly mixer = inject(MixerStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);

  readonly track = input.required<Track>();
  /** Position in the track list, from 0. */
  readonly index = input.required<number>();
  /** 1 for a track in a stack: its header is indented under the stack's. */
  readonly depth = input(0);

  protected readonly colors = TrackColors;
  protected readonly renaming = signal(false);
  private readonly nameInput = viewChild<ElementRef<HTMLInputElement>>('name');
  protected readonly number = computed(() => this.index() + 1);
  protected readonly count = computed(() => this.project.trackIds().length);
  protected readonly selected = computed(
    () => this.selection.track() === this.track().id || this.selection.tracks().has(this.track().id)
  );
  protected readonly volume = computed(() => this.mixer.value(this.track().id, 'volumeDb'));
  protected readonly volumePosition = computed(() => faderPosition(this.volume()));
  protected readonly volumeText = computed(() => formatDb(this.volume()));
  private readonly live = inject(LiveMidiStore);
  protected readonly automation = inject(AutomationStore);
  private readonly jobs = inject(JobsStore);
  protected readonly modeList = AutomationModes;
  protected readonly modeLabel = automationModeLabel;
  protected readonly freezable = computed(() => this.track().kind === 'audio' || this.track().kind === 'instrument');
  protected readonly frozen = computed(() => !!this.track().frozenFileId);
  /** The freeze job while it runs. */
  protected readonly freezing = computed(() => {
    const job = this.jobs.freezeOf(this.track().id);
    return isActive(job) ? (job ?? null) : null;
  });
  protected readonly active = computed(() => this.live.active().has(this.track().id));
  protected readonly flex = inject(FlexStore);
  protected readonly alternatives = inject(TrackAlternativesStore);
  private readonly ui = inject(UiStore);
  protected readonly flexModeList = FlexModes;
  /** The flex button and mode show on audio tracks while Show Flex (⌘F) is on. */
  protected readonly showFlex = computed(() => this.flex.showFlex() && this.track().kind === 'audio');
  protected readonly flexLabel = computed(() => flexModeLabel(this.track().flexMode));
  /** Tracks that hold regions: they have track alternatives and bounce in place. */
  protected readonly hasRegions = computed(() => ['audio', 'instrument', 'midi'].includes(this.track().kind));
  protected readonly activeAlternative = computed(
    () => this.track().alternatives.find(a => a.id === this.track().alternativeId) ?? null
  );
  /** The alternative being renamed in the header. */
  protected readonly renamingAlternative = signal<TrackAlternative | null>(null);
  private readonly alternativeInput = viewChild<ElementRef<HTMLInputElement>>('altName');
  protected readonly kindIcon = computed(
    () =>
      ({
        audio: 'matGraphicEqRound',
        instrument: 'matPianoRound',
        midi: 'matSettingsInputComponentRound',
        aux: 'matCallSplitRound',
        vca: 'matTuneRound',
        stack: 'matFolderRound'
      })[this.track().kind]
  );
  protected readonly kindTitle = computed(() => {
    const track = this.track();
    return track.kind === 'audio'
      ? 'Audio track'
      : track.kind === 'midi'
        ? 'External MIDI track'
        : track.kind === 'stack'
          ? 'Track stack'
          : `Software instrument: ${track.instrument?.name ?? 'none'}`;
  });

  constructor() {
    effect(() => {
      const input = this.nameInput()?.nativeElement;
      input?.focus();
      input?.select();
    });
    effect(() => {
      const input = this.alternativeInput()?.nativeElement;
      input?.focus();
      input?.select();
    });
  }

  /** Selects the track; ⇧ or ⌘ adds it to the selected tracks (to stack them). */
  protected select(event?: MouseEvent): void {
    this.selection.selectTrack(this.track().id, !!event && (event.shiftKey || event.metaKey));
  }

  protected toggleCollapsed(): void {
    const collapsed = !this.track().collapsed;
    void this.project.execute(
      [{ type: 'setTrack', trackId: this.track().id, input: null, collapsed }],
      collapsed ? 'Collapse Stack' : 'Expand Stack'
    );
  }

  /** A stack of the selected tracks (this one when it is not among them). */
  protected stack(): void {
    const selected = this.selection.trackSelection().filter(id => this.project.track(id)?.kind !== 'stack');
    const ids = selected.includes(this.track().id) ? selected : [this.track().id];
    void this.project.execute([{ type: 'createStack', trackIds: ids }], 'Create Track Stack');
  }

  protected unstack(): void {
    void this.project.execute([{ type: 'removeStack', stackId: this.track().id }], 'Flatten Stack');
  }

  protected leaveStack(): void {
    void this.project.execute(
      [{ type: 'setTrackParent', trackId: this.track().id, parentId: null }],
      'Remove from Stack'
    );
  }

  protected startRename(event?: Event): void {
    event?.stopPropagation();
    this.renaming.set(true);
  }

  protected rename(name: string): void {
    if (!this.renaming()) {
      return;
    }

    this.renaming.set(false);
    const trimmed = name.trim();
    if (trimmed && trimmed !== this.track().name) {
      this.change({ name: trimmed });
    }
  }

  protected change(change: TrackChange): void {
    void this.project.execute([setTrack(this.track().id, change)], setTrackLabel(change));
  }

  protected setVolume(position: number): void {
    void this.mixer.set(this.track().id, 'volumeDb', Math.round(faderDb(position) * 10) / 10);
  }

  protected moveBy(offset: number): void {
    const index = this.index() + offset;
    if (index >= 0 && index < this.count()) {
      void this.project.execute([{ type: 'moveTrack', trackId: this.track().id, index }], 'Move Track');
    }
  }

  protected percent(progress: number): number {
    return Math.round(Math.min(1, Math.max(0, progress)) * 100);
  }

  /** Freezes the track, or plays a frozen one live again. */
  protected toggleFreeze(): void {
    if (this.frozen()) {
      void this.jobs.unfreeze(this.track().id);
    } else {
      void this.jobs.freeze(this.track().id);
    }
  }

  protected setMode(mode: AutomationMode): void {
    void this.automation.setMode(this.track().id, mode);
  }

  protected setFlexMode(mode: FlexMode): void {
    void this.flex.setFlexMode(this.track().id, mode);
  }

  protected renameAlternative(name: string): void {
    const alt = this.renamingAlternative();
    if (!alt) {
      return;
    }
    this.renamingAlternative.set(null);
    const trimmed = name.trim();
    if (trimmed && trimmed !== alt.name) {
      void this.alternatives.rename(this.track().id, alt.id, trimmed);
    }
  }

  protected deleteAlternative(): void {
    const id = this.track().alternativeId;
    if (id) {
      void this.alternatives.delete(this.track().id, id);
    }
  }

  /** Bounce in Place of this whole track: it alone is selected, without regions, and the window opens. */
  protected bounceInPlace(): void {
    this.selection.clear();
    this.selection.selectTrack(this.track().id);
    this.ui.bounceInPlaceOpen.set(true);
  }

  protected remove(): void {
    void this.project.execute([{ type: 'removeTrack', trackId: this.track().id }], 'Delete Track');
  }
}
