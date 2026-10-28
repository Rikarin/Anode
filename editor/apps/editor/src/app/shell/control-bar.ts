import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  viewChild,
  type ElementRef
} from '@angular/core';
import { provideIcons } from '@ng-icons/core';
import {
  matAvTimerRound,
  matDarkModeRound,
  matDashboardRound,
  matExpandMoreRound,
  matFiberManualRecordRound,
  matFolderOpenRound,
  matHistoryRound,
  matInputRound,
  matKeyboardRound,
  matLayersRound,
  matLightModeRound,
  matLockRound,
  matLooksOneRound,
  matLoopRound,
  matMergeTypeRound,
  matPlayArrowRound,
  matRedoRound,
  matSaveRound,
  matSkipPreviousRound,
  matSpeedRound,
  matStopRound,
  matSwapHorizRound,
  matUndoRound
} from '@ng-icons/material-icons/round';

import { AnVarispeedDisplay } from '@anode/audio-ui';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiMenuImports } from '@xui/menu';
import { XuiTooltipImports } from '@xui/tooltip';
import { formatBinding } from '../keys/key-commands';
import { formatSpeed, parseSpeed, VarispeedModes, VarispeedUnits, type VarispeedUnit } from '../music/varispeed';
import { HistoryStore } from '../state/history.store';
import { KeyCommandsStore } from '../state/key-commands.store';
import { ProjectSession } from '../state/project-session';
import { ProjectStore } from '../state/project.store';
import { ScreensetStore } from '../state/screenset.store';
import { SmartTempoStore } from '../state/smart-tempo.store';
import { ThemeStore } from '../state/theme.store';
import { TransportStore } from '../state/transport.store';
import { UiStore } from '../state/ui.store';
import { EngineStatus } from './engine-status';
import { Lcd } from './lcd';

/**
 * The control bar (Logic's): the project's name and save state, undo and redo, the transport (go to start, stop, play,
 * record, cycle) with the LCD, the metronome and count-in, MIDI recording's merge switch, and the engine status. Its keys (Space, R, C, Return, ⌘Z,
 * ⇧⌘Z, ⌘S) are handled by the app shell. It looks like Logic's: window chrome with grouped, gradient-bezelled buttons
 * (DESIGN.md "Control bar") around the LCD in the middle.
 */
@Component({
  selector: 'an-control-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnVarispeedDisplay, EngineStatus, Lcd, XuiIconImports, XuiInputImports, XuiMenuImports, XuiTooltipImports],
  providers: [
    provideIcons({
      matAvTimerRound,
      matDarkModeRound,
      matDashboardRound,
      matExpandMoreRound,
      matHistoryRound,
      matInputRound,
      matKeyboardRound,
      matLayersRound,
      matLooksOneRound,
      matLightModeRound,
      matLockRound,
      matMergeTypeRound,
      matFiberManualRecordRound,
      matFolderOpenRound,
      matLoopRound,
      matPlayArrowRound,
      matRedoRound,
      matSaveRound,
      matSkipPreviousRound,
      matSpeedRound,
      matStopRound,
      matSwapHorizRound,
      matUndoRound
    })
  ],
  host: { class: 'chrome-bar relative z-10 flex h-13 shrink-0 items-center gap-3 px-3 max-xl:gap-2 max-xl:px-2' },
  template: `
    <ng-content />
    <div class="flex min-w-max flex-1 items-center gap-2">
      @if (project.info(); as info) {
        <div class="flex min-w-0 items-center gap-2" role="toolbar" aria-label="Project">
          <button
            type="button"
            class="bezel text-foreground grid h-6.5 w-8 shrink-0 place-items-center"
            aria-label="Close project"
            xuiTooltip="Projects (close this one)"
            (click)="session.close()"
          >
            <ng-icon xui name="matFolderOpenRound" size="sm" />
          </button>
          <span
            class="text-foreground max-w-48 truncate text-[13px] font-semibold"
            data-testid="project-name"
            [attr.title]="info.path"
          >
            {{ info.name }}
          </span>
          @if (project.dirty()) {
            <span
              class="bg-foreground-muted size-1.5 shrink-0 rounded-full"
              data-testid="unsaved"
              aria-label="Unsaved changes"
              role="img"
            ></span>
          }
          <button
            type="button"
            class="bezel text-foreground grid h-6.5 w-6 shrink-0 place-items-center"
            aria-label="Project menu"
            data-testid="project-menu"
            xuiTooltip="Alternatives, backups and key commands"
            [xuiMenuTriggerFor]="projectMenu"
            (menuOpened)="session.refreshVersions()"
          >
            <ng-icon xui name="matExpandMoreRound" size="sm" />
          </button>
          <ng-template #projectMenu>
            <xui-menu class="min-w-56">
              <button
                xuiMenuItem
                type="button"
                data-testid="new-alternative"
                (click)="ui.saveAlternativeOpen.set(true)"
              >
                Save as Alternative…
              </button>
              <button
                xuiMenuItem
                type="button"
                data-testid="alternatives"
                [xuiMenuTriggerFor]="alternativesMenu"
                (menuOpened)="session.refreshVersions()"
              >
                Alternatives
              </button>
              <button
                xuiMenuItem
                type="button"
                data-testid="backups"
                [xuiMenuTriggerFor]="backupsMenu"
                (menuOpened)="session.refreshVersions()"
              >
                Revert to Backup
              </button>
              <xui-menu-divider />
              <button xuiMenuItem type="button" icon="matKeyboardRound" (click)="ui.keyCommandsOpen.set(true)">
                Key Commands… ({{ keyOf('view.keyCommands') }})
              </button>
              <button
                xuiMenuItem
                type="button"
                [icon]="theme.appearance() === 'dark' ? 'matLightModeRound' : 'matDarkModeRound'"
                (click)="theme.toggle()"
              >
                {{ theme.appearance() === 'dark' ? 'Light Appearance' : 'Dark Appearance' }}
              </button>
            </xui-menu>
          </ng-template>
          <ng-template #alternativesMenu>
            <xui-menu>
              @for (alternative of session.alternatives(); track alternative.id) {
                <button
                  xuiMenuItem
                  type="button"
                  role="menuitemradio"
                  [attr.aria-checked]="alternative.current"
                  [attr.data-testid]="'alternative-' + alternative.name"
                  (click)="alternative.current || session.openAlternative(alternative.id)"
                >
                  {{ alternative.current ? '✓ ' : '' }}{{ alternative.name }}
                </button>
              } @empty {
                <button xuiMenuItem type="button" disabled>No alternatives yet</button>
              }
            </xui-menu>
          </ng-template>
          <ng-template #backupsMenu>
            <xui-menu>
              @for (backup of session.backups(); track backup.id; let i = $index) {
                <button
                  xuiMenuItem
                  type="button"
                  [attr.data-testid]="'backup-' + i"
                  (click)="session.revertToBackup(backup.id)"
                >
                  {{ savedAt(backup.saved) }}
                </button>
              } @empty {
                <button xuiMenuItem type="button" disabled>No backups yet: each save keeps one</button>
              }
            </xui-menu>
          </ng-template>
          <div class="bezel-group text-foreground h-6.5 shrink-0">
            <button
              type="button"
              class="grid w-8 place-items-center disabled:opacity-35"
              aria-label="Save"
              xuiTooltip="Save (⌘S)"
              [disabled]="!project.dirty()"
              (click)="session.save()"
            >
              <ng-icon xui name="matSaveRound" size="sm" />
            </button>
            <button
              type="button"
              class="grid w-8 place-items-center disabled:opacity-35"
              aria-label="Undo"
              [xuiTooltip]="undoTip()"
              [disabled]="!history.canUndo()"
              (click)="history.undo()"
            >
              <ng-icon xui name="matUndoRound" size="sm" />
            </button>
            <button
              type="button"
              class="grid w-8 place-items-center disabled:opacity-35"
              aria-label="Redo"
              [xuiTooltip]="redoTip()"
              [disabled]="!history.canRedo()"
              (click)="history.redo()"
            >
              <ng-icon xui name="matRedoRound" size="sm" />
            </button>
          </div>
        </div>
      }
    </div>
    <div class="flex shrink-0 items-center gap-3 max-xl:gap-2">
      <div class="bezel-group text-foreground h-8" role="toolbar" aria-label="Transport">
        <button
          type="button"
          class="grid w-10 place-items-center disabled:opacity-35"
          aria-label="Go to start"
          [disabled]="!project.isOpen()"
          [xuiTooltip]="'Go to start (Return)'"
          (click)="transport.goToStart()"
        >
          <ng-icon xui name="matSkipPreviousRound" size="18px" />
        </button>
        <button
          type="button"
          class="grid w-10 place-items-center disabled:opacity-35"
          aria-label="Stop"
          [disabled]="!project.isOpen()"
          [xuiTooltip]="'Stop (Space); again: go to start'"
          (click)="transport.stop()"
        >
          <ng-icon xui name="matStopRound" size="18px" />
        </button>
        <button
          type="button"
          class="grid w-10 place-items-center disabled:opacity-35"
          aria-label="Play"
          [disabled]="!project.isOpen()"
          data-testid="play"
          [attr.aria-pressed]="transport.state() === 'playing' ? 'true' : 'false'"
          [class.text-transport-play]="transport.playing()"
          [xuiTooltip]="'Play (Space)'"
          (click)="transport.play()"
        >
          <ng-icon xui name="matPlayArrowRound" size="18px" />
        </button>
        <button
          type="button"
          class="text-transport-record grid w-10 place-items-center disabled:opacity-35"
          aria-label="Record"
          [disabled]="!project.isOpen()"
          data-testid="record"
          [attr.aria-pressed]="transport.recording() ? 'true' : 'false'"
          [xuiTooltip]="'Record (R)'"
          (click)="transport.record()"
        >
          <ng-icon xui name="matFiberManualRecordRound" size="18px" />
        </button>
        <button
          type="button"
          class="grid w-10 place-items-center disabled:opacity-35"
          aria-label="Cycle"
          [disabled]="!project.isOpen()"
          [attr.aria-pressed]="transport.cycle() ? 'true' : 'false'"
          [class.text-transport-cycle]="transport.cycle()"
          [xuiTooltip]="'Cycle (C)'"
          (click)="transport.toggleCycle()"
        >
          <ng-icon xui name="matLoopRound" size="18px" />
        </button>
      </div>
      @if (project.isOpen()) {
        <div class="bezel-group text-foreground h-6.5 text-[11px] font-semibold" role="toolbar" aria-label="Recording">
          <button
            type="button"
            class="flex items-center gap-0.5 px-2"
            aria-label="Cycle recording mode"
            data-testid="cycle-mode"
            xuiTooltip="What cycle recording makes of its passes"
            [xuiMenuTriggerFor]="cycleModeMenu"
          >
            <span class="flex xl:hidden"><ng-icon xui name="matLayersRound" size="14px" /></span>
            <span class="max-xl:sr-only">{{ recording().cycleMode === 'takeFolder' ? 'Takes' : 'Regions' }}</span>
            <ng-icon xui name="matExpandMoreRound" size="14px" />
          </button>
          <button
            type="button"
            class="flex items-center px-2.5"
            data-testid="autopunch"
            [class.text-transport-record]="recording().autopunch"
            [attr.aria-pressed]="recording().autopunch ? 'true' : 'false'"
            [xuiTooltip]="'Autopunch: record only between the punch locators (' + keyOf('transport.autopunch') + ')'"
            (click)="transport.toggleAutopunch()"
          >
            <span class="flex xl:hidden"><ng-icon xui name="matInputRound" size="14px" /></span>
            <span class="max-xl:sr-only">Punch</span>
          </button>
          <button
            type="button"
            class="flex items-center px-2.5"
            data-testid="replace"
            [class.text-lit]="recording().replace"
            [attr.aria-pressed]="recording().replace ? 'true' : 'false'"
            [xuiTooltip]="'Replace: a recording removes what it covers (' + keyOf('transport.replace') + ')'"
            (click)="transport.toggleReplace()"
          >
            <span class="flex xl:hidden"><ng-icon xui name="matSwapHorizRound" size="14px" /></span>
            <span class="max-xl:sr-only">Replace</span>
          </button>
          <button
            type="button"
            class="text-transport-record grid w-8 place-items-center"
            aria-label="Capture as recording"
            data-testid="flashback"
            [xuiTooltip]="
              'Flashback Capture: what you just played becomes a recording (' + keyOf('transport.flashback') + ')'
            "
            (click)="transport.flashbackCapture()"
          >
            <ng-icon xui name="matHistoryRound" size="16px" />
          </button>
        </div>
        <ng-template #cycleModeMenu>
          <xui-menu>
            <button
              xuiMenuItem
              type="button"
              role="menuitemradio"
              [attr.aria-checked]="recording().cycleMode === 'takeFolder'"
              (click)="transport.setCycleMode('takeFolder')"
            >
              Create Take Folders
            </button>
            <button
              xuiMenuItem
              type="button"
              role="menuitemradio"
              [attr.aria-checked]="recording().cycleMode === 'regions'"
              (click)="transport.setCycleMode('regions')"
            >
              Create Regions
            </button>
          </xui-menu>
        </ng-template>
        <an-lcd />
        <div class="bezel-group text-foreground h-6.5 text-[11px] font-semibold" role="toolbar" aria-label="Metronome">
          <button
            type="button"
            class="flex items-center px-2.5"
            [class.text-lit]="transport.metronome()"
            [attr.aria-pressed]="transport.metronome() ? 'true' : 'false'"
            xuiTooltip="Metronome click"
            (click)="transport.toggleMetronome()"
          >
            <span class="flex xl:hidden"><ng-icon xui name="matAvTimerRound" size="14px" /></span>
            <span class="max-xl:sr-only">Click</span>
          </button>
          <button
            type="button"
            class="flex items-center px-2.5"
            [class.text-lit]="transport.countIn()"
            [attr.aria-pressed]="transport.countIn() ? 'true' : 'false'"
            xuiTooltip="One bar of count-in before recording"
            (click)="transport.toggleCountIn()"
          >
            <span class="flex xl:hidden"><ng-icon xui name="matLooksOneRound" size="14px" /></span>
            <span class="max-xl:sr-only">Count-in</span>
          </button>
          <button
            type="button"
            class="flex items-center px-2.5"
            data-testid="merge-recording"
            [class.text-lit]="mergeMidi()"
            [attr.aria-pressed]="mergeMidi() ? 'true' : 'false'"
            xuiTooltip="Merge new MIDI recordings into the region under the playhead"
            (click)="toggleMerge()"
          >
            <span class="flex xl:hidden"><ng-icon xui name="matMergeTypeRound" size="14px" /></span>
            <span class="max-xl:sr-only">Merge</span>
          </button>
        </div>
        <div class="flex items-center gap-1.5" role="group" aria-label="Varispeed">
          <div
            class="bezel-group text-foreground h-6.5 text-[11px] font-semibold"
            role="toolbar"
            aria-label="Varispeed settings"
          >
            <button
              type="button"
              class="flex items-center px-2.5"
              aria-label="Varispeed"
              data-testid="varispeed-toggle"
              [class.text-lit]="varispeed().enabled"
              [attr.aria-pressed]="varispeed().enabled ? 'true' : 'false'"
              xuiTooltip="Varispeed: the whole project plays faster or slower"
              (click)="smartTempo.setVarispeedEnabled(!varispeed().enabled)"
            >
              <span class="flex xl:hidden"><ng-icon xui name="matSpeedRound" size="14px" /></span>
              <span class="max-xl:sr-only">Varispeed</span>
            </button>
            @if (varispeed().enabled) {
              <button
                type="button"
                class="flex items-center gap-0.5 px-2"
                aria-label="Varispeed mode"
                data-testid="varispeed-mode"
                [xuiTooltip]="modeLabel()"
                [xuiMenuTriggerFor]="varispeedModeMenu"
              >
                {{ modeShort() }}
                <ng-icon xui name="matExpandMoreRound" size="14px" />
              </button>
              <button
                type="button"
                class="flex items-center gap-0.5 px-2"
                aria-label="Varispeed display unit"
                data-testid="varispeed-unit"
                xuiTooltip="Show the speed as percent, resulting tempo or detune"
                [xuiMenuTriggerFor]="varispeedUnitMenu"
              >
                {{ unitShort() }}
                <ng-icon xui name="matExpandMoreRound" size="14px" />
              </button>
            }
          </div>
          @if (varispeed().enabled) {
            @if (editingSpeed()) {
              <input
                #speedInput
                xuiInput
                class="readout h-8 w-24 px-1.5 text-center text-[13px] font-semibold"
                aria-label="Varispeed speed"
                data-testid="varispeed-speed"
                [value]="speedText()"
                (keydown.enter)="commitSpeed(speedInput.value)"
                (keydown.escape)="editingSpeed.set(false)"
                (blur)="commitSpeed(speedInput.value)"
              />
            } @else {
              <an-varispeed-display
                [speed]="varispeed().speed"
                [unit]="smartTempo.varispeedUnit()"
                [bpm]="transport.tempo()"
                (committed)="smartTempo.setSpeed($event)"
                (edit)="editingSpeed.set(true)"
              />
            }
          }
        </div>
        <ng-template #varispeedModeMenu>
          <xui-menu class="min-w-60">
            @for (mode of varispeedModes; track mode.value) {
              <button
                xuiMenuItem
                type="button"
                role="menuitemradio"
                [attr.aria-checked]="varispeed().mode === mode.value"
                (click)="smartTempo.setVarispeedMode(mode.value)"
              >
                {{ varispeed().mode === mode.value ? '✓ ' : '' }}{{ mode.label }}
              </button>
            }
          </xui-menu>
        </ng-template>
        <ng-template #varispeedUnitMenu>
          <xui-menu class="min-w-52">
            @for (unit of varispeedUnits; track unit.value) {
              <button
                xuiMenuItem
                type="button"
                role="menuitemradio"
                [attr.aria-checked]="smartTempo.varispeedUnit() === unit.value"
                (click)="smartTempo.varispeedUnit.set(unit.value)"
              >
                {{ smartTempo.varispeedUnit() === unit.value ? '✓ ' : '' }}{{ unit.label }}
              </button>
            }
          </xui-menu>
        </ng-template>
      }
    </div>
    <div class="flex min-w-max flex-1 items-center justify-end gap-2">
      @if (project.isOpen()) {
        @if (renamingScreenset()) {
          <input
            #screensetName
            xuiInput
            class="h-6.5 w-36 px-1.5 text-[11px]"
            aria-label="Screenset name"
            [value]="screensets.name()"
            (keydown.enter)="renameScreenset(screensetName.value)"
            (keydown.escape)="renamingScreenset.set(false)"
            (blur)="renameScreenset(screensetName.value)"
          />
        } @else {
          <button
            type="button"
            class="bezel text-foreground flex h-6.5 max-w-44 items-center gap-1 px-2 text-[11px] font-semibold"
            aria-label="Screenset menu"
            data-testid="screenset-menu"
            [xuiTooltip]="'Screensets: keys 1–9 recall them'"
            [xuiMenuTriggerFor]="screensetMenu"
          >
            <ng-icon xui name="matDashboardRound" size="14px" class="text-foreground-muted" />
            <span class="readout" data-testid="screenset-number">{{ screensets.current() }}</span>
            <span class="truncate max-xl:hidden">{{ screensets.name() }}</span>
            @if (screensets.locked()) {
              <ng-icon xui name="matLockRound" size="12px" aria-label="Locked" />
            }
          </button>
        }
        <ng-template #screensetMenu>
          <xui-menu class="min-w-52">
            @for (n of numbers; track n) {
              <button
                xuiMenuItem
                type="button"
                role="menuitemradio"
                [attr.aria-checked]="screensets.current() === n"
                [attr.data-testid]="'screenset-' + n"
                (click)="screensets.recall(n)"
              >
                <span class="flex-1">{{ n }} {{ screensets.screensets().get(n)?.name ?? 'Screenset ' + n }}</span>
                @if (screensets.screensets().get(n)?.locked) {
                  <ng-icon xui name="matLockRound" size="12px" aria-label="Locked" />
                }
              </button>
            }
            <xui-menu-divider />
            <button xuiMenuItem type="button" (click)="screensets.saveCurrent()">Save</button>
            <button xuiMenuItem type="button" data-testid="rename-screenset" (click)="renamingScreenset.set(true)">
              Rename…
            </button>
            <button xuiMenuItem type="button" data-testid="lock-screenset" (click)="screensets.toggleLock()">
              {{ screensets.locked() ? 'Unlock' : 'Lock' }}
            </button>
          </xui-menu>
        </ng-template>
        <!-- Narrower than 1100 px the appearance is switched from the project menu (or its key command). -->
        <button
          type="button"
          class="bezel text-foreground grid h-6.5 w-8 place-items-center max-[1100px]:hidden"
          [attr.aria-label]="theme.appearance() === 'dark' ? 'Light appearance' : 'Dark appearance'"
          data-testid="appearance"
          [xuiTooltip]="
            theme.appearance() === 'dark' ? 'Switch to the light appearance' : 'Switch to the dark appearance'
          "
          (click)="theme.toggle()"
        >
          <ng-icon xui [name]="theme.appearance() === 'dark' ? 'matLightModeRound' : 'matDarkModeRound'" size="sm" />
        </button>
      }
      <an-engine-status />
    </div>
  `
})
export class ControlBar {
  protected readonly transport = inject(TransportStore);
  protected readonly project = inject(ProjectStore);
  protected readonly history = inject(HistoryStore);
  protected readonly session = inject(ProjectSession);

  protected readonly screensets = inject(ScreensetStore);
  protected readonly theme = inject(ThemeStore);
  protected readonly ui = inject(UiStore);
  private readonly keys = inject(KeyCommandsStore);

  protected readonly smartTempo = inject(SmartTempoStore);
  protected readonly varispeed = this.smartTempo.varispeed;
  protected readonly varispeedModes = VarispeedModes;
  protected readonly varispeedUnits = VarispeedUnits;
  protected readonly editingSpeed = signal(false);
  private readonly speedInput = viewChild<ElementRef<HTMLInputElement>>('speedInput');
  protected readonly modeLabel = computed(
    () => VarispeedModes.find(m => m.value === this.varispeed().mode)?.label ?? ''
  );
  protected readonly modeShort = computed(
    () => ({ speedOnly: 'Speed', speedAndPitch: 'Tape', speedAndPitchMidi: 'Tape+MIDI' })[this.varispeed().mode]
  );
  protected readonly unitShort = computed(
    () =>
      (({ percent: '%', tempo: 'bpm', detune: 'st' }) satisfies Record<VarispeedUnit, string>)[
        this.smartTempo.varispeedUnit()
      ]
  );
  /** The speed as typed: the shown value without its unit. */
  protected readonly speedText = computed(() =>
    formatSpeed(this.varispeed().speed, this.smartTempo.varispeedUnit(), this.transport.tempo())
      .replace(/\s*(%|bpm|st)$/, '')
      .replace('−', '-')
  );
  protected readonly mergeMidi = computed(() => this.project.settings().mergeMidiRecording);
  protected readonly recording = computed(() => this.project.settings().recording);
  protected readonly numbers = [1, 2, 3, 4, 5, 6, 7, 8, 9];
  protected readonly renamingScreenset = signal(false);
  private readonly screensetInput = viewChild<ElementRef<HTMLInputElement>>('screensetName');

  constructor() {
    effect(() => {
      const input = this.screensetInput()?.nativeElement;
      input?.focus();
      input?.select();
    });
    effect(() => {
      const input = this.speedInput()?.nativeElement;
      input?.focus();
      input?.select();
    });
  }

  /** A typed speed in the shown unit (Return); a value it cannot read leaves the speed. */
  protected commitSpeed(text: string): void {
    if (!this.editingSpeed()) {
      return;
    }
    this.editingSpeed.set(false);
    const speed = parseSpeed(text, this.smartTempo.varispeedUnit(), this.transport.tempo());
    if (speed !== null && speed !== this.varispeed().speed) {
      void this.smartTempo.setSpeed(speed);
    }
  }

  protected keyOf(id: string): string {
    const binding = this.keys.bindingOf(id);
    return binding ? formatBinding(binding) : 'no key';
  }

  protected savedAt(iso: string): string {
    const date = new Date(iso);
    return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
  }

  protected renameScreenset(name: string): void {
    if (this.renamingScreenset()) {
      this.renamingScreenset.set(false);
      void this.screensets.rename(name);
    }
  }

  protected toggleMerge(): void {
    const merge = !this.mergeMidi();
    void this.project.execute(
      [{ type: 'setMidiRecording', merge }],
      merge ? 'Merge MIDI Recordings' : 'Separate MIDI Recordings'
    );
  }

  protected readonly undoTip = computed(() => {
    const label = this.history.undoLabel();
    return label ? `Undo ${label} (⌘Z)` : 'Nothing to undo';
  });
  protected readonly redoTip = computed(() => {
    const label = this.history.redoLabel();
    return label ? `Redo ${label} (⇧⌘Z)` : 'Nothing to redo';
  });
}
