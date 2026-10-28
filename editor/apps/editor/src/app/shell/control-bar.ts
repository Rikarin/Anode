import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { provideIcons } from '@ng-icons/core';
import {
  matFiberManualRecordRound,
  matFolderOpenRound,
  matLoopRound,
  matPlayArrowRound,
  matRedoRound,
  matSaveRound,
  matSkipPreviousRound,
  matStopRound,
  matUndoRound
} from '@ng-icons/material-icons/round';

import { XuiIconImports } from '@xui/icon';
import { XuiTooltipImports } from '@xui/tooltip';
import { HistoryStore } from '../state/history.store';
import { ProjectSession } from '../state/project-session';
import { ProjectStore } from '../state/project.store';
import { TransportStore } from '../state/transport.store';
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
  imports: [EngineStatus, Lcd, XuiIconImports, XuiTooltipImports],
  providers: [
    provideIcons({
      matFiberManualRecordRound,
      matFolderOpenRound,
      matLoopRound,
      matPlayArrowRound,
      matRedoRound,
      matSaveRound,
      matSkipPreviousRound,
      matStopRound,
      matUndoRound
    })
  ],
  host: { class: 'chrome-bar relative z-10 flex h-13 shrink-0 items-center gap-3 px-3' },
  template: `
    <ng-content />
    <div class="flex min-w-0 flex-1 items-center gap-2">
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
    <div class="flex shrink-0 items-center gap-3">
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
          [attr.aria-pressed]="transport.state() === 'playing' ? 'true' : null"
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
          [attr.aria-pressed]="transport.recording() ? 'true' : null"
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
          [attr.aria-pressed]="transport.cycle() ? 'true' : null"
          [class.text-transport-cycle]="transport.cycle()"
          [xuiTooltip]="'Cycle (C)'"
          (click)="transport.toggleCycle()"
        >
          <ng-icon xui name="matLoopRound" size="18px" />
        </button>
      </div>
      @if (project.isOpen()) {
        <an-lcd />
        <div class="bezel-group text-foreground h-6.5 text-[11px] font-semibold" role="toolbar" aria-label="Metronome">
          <button
            type="button"
            class="px-2.5"
            [class.text-lit]="transport.metronome()"
            [attr.aria-pressed]="transport.metronome() ? 'true' : null"
            xuiTooltip="Metronome click"
            (click)="transport.toggleMetronome()"
          >
            Click
          </button>
          <button
            type="button"
            class="px-2.5"
            [class.text-lit]="transport.countIn()"
            [attr.aria-pressed]="transport.countIn() ? 'true' : null"
            xuiTooltip="One bar of count-in before recording"
            (click)="transport.toggleCountIn()"
          >
            Count-in
          </button>
          <button
            type="button"
            class="px-2.5"
            data-testid="merge-recording"
            [class.text-lit]="mergeMidi()"
            [attr.aria-pressed]="mergeMidi()"
            xuiTooltip="Merge new MIDI recordings into the region under the playhead"
            (click)="toggleMerge()"
          >
            Merge
          </button>
        </div>
      }
    </div>
    <div class="flex min-w-0 flex-1 items-center justify-end">
      <an-engine-status />
    </div>
  `
})
export class ControlBar {
  protected readonly transport = inject(TransportStore);
  protected readonly project = inject(ProjectStore);
  protected readonly history = inject(HistoryStore);
  protected readonly session = inject(ProjectSession);

  protected readonly mergeMidi = computed(() => this.project.settings().mergeMidiRecording);

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
