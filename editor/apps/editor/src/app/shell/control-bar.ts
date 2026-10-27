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
import { XuiButtonImports } from '@xui/button';
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
 * ⇧⌘Z, ⌘S) are handled by the app shell.
 */
@Component({
  selector: 'an-control-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [EngineStatus, Lcd, XuiButtonImports, XuiIconImports, XuiTooltipImports],
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
  host: { class: 'bg-surface border-border flex h-12 shrink-0 items-center gap-4 border-b px-3' },
  template: `
    <ng-content />
    @if (project.info(); as info) {
      <div class="flex min-w-0 items-center gap-1" role="toolbar" aria-label="Project">
        <button
          xuiButton
          type="button"
          variant="ghost"
          size="sm"
          aria-label="Close project"
          xuiTooltip="Projects (close this one)"
          (click)="session.close()"
        >
          <ng-icon xui name="matFolderOpenRound" size="sm" />
        </button>
        <span class="max-w-48 truncate text-sm font-medium" data-testid="project-name" [attr.title]="info.path">
          {{ info.name }}
        </span>
        @if (project.dirty()) {
          <span
            class="bg-warning size-1.5 rounded-full"
            data-testid="unsaved"
            aria-label="Unsaved changes"
            role="img"
          ></span>
        }
        <button
          xuiButton
          type="button"
          variant="ghost"
          size="sm"
          aria-label="Save"
          xuiTooltip="Save (⌘S)"
          [disabled]="!project.dirty()"
          (click)="session.save()"
        >
          <ng-icon xui name="matSaveRound" size="sm" />
        </button>
        <button
          xuiButton
          type="button"
          variant="ghost"
          size="sm"
          aria-label="Undo"
          [xuiTooltip]="undoTip()"
          [disabled]="!history.canUndo()"
          (click)="history.undo()"
        >
          <ng-icon xui name="matUndoRound" size="sm" />
        </button>
        <button
          xuiButton
          type="button"
          variant="ghost"
          size="sm"
          aria-label="Redo"
          [xuiTooltip]="redoTip()"
          [disabled]="!history.canRedo()"
          (click)="history.redo()"
        >
          <ng-icon xui name="matRedoRound" size="sm" />
        </button>
      </div>
    }
    <div class="flex items-center gap-1" role="toolbar" aria-label="Transport">
      <button
        type="button"
        xuiButton
        variant="ghost"
        size="sm"
        aria-label="Go to start"
        [disabled]="!project.isOpen()"
        [xuiTooltip]="'Go to start (Return)'"
        (click)="transport.goToStart()"
      >
        <ng-icon xui name="matSkipPreviousRound" size="sm" />
      </button>
      <button
        type="button"
        xuiButton
        variant="ghost"
        size="sm"
        aria-label="Stop"
        [disabled]="!project.isOpen()"
        [xuiTooltip]="'Stop (Space); again: go to start'"
        (click)="transport.stop()"
      >
        <ng-icon xui name="matStopRound" size="sm" />
      </button>
      <button
        type="button"
        xuiButton
        variant="ghost"
        size="sm"
        aria-label="Play"
        [disabled]="!project.isOpen()"
        data-testid="play"
        [active]="transport.state() === 'playing'"
        [class.text-success]="transport.playing()"
        [xuiTooltip]="'Play (Space)'"
        (click)="transport.play()"
      >
        <ng-icon xui name="matPlayArrowRound" size="sm" />
      </button>
      <button
        type="button"
        xuiButton
        variant="ghost"
        size="sm"
        aria-label="Record"
        [disabled]="!project.isOpen()"
        data-testid="record"
        [active]="transport.recording()"
        [class.text-error]="transport.recording()"
        [xuiTooltip]="'Record (R)'"
        (click)="transport.record()"
      >
        <ng-icon xui name="matFiberManualRecordRound" size="sm" />
      </button>
      <button
        type="button"
        xuiButton
        variant="ghost"
        size="sm"
        aria-label="Cycle"
        [disabled]="!project.isOpen()"
        [active]="transport.cycle()"
        [class.text-warning]="transport.cycle()"
        [xuiTooltip]="'Cycle (C)'"
        (click)="transport.toggleCycle()"
      >
        <ng-icon xui name="matLoopRound" size="sm" />
      </button>
    </div>
    @if (project.isOpen()) {
      <an-lcd />
      <div class="flex items-center gap-1" role="toolbar" aria-label="Metronome">
        <button
          xuiButton
          type="button"
          size="sm"
          class="px-2 text-xs"
          [variant]="transport.metronome() ? 'default' : 'ghost'"
          [active]="transport.metronome()"
          xuiTooltip="Metronome click"
          (click)="transport.toggleMetronome()"
        >
          Click
        </button>
        <button
          xuiButton
          type="button"
          size="sm"
          class="px-2 text-xs"
          [variant]="transport.countIn() ? 'default' : 'ghost'"
          [active]="transport.countIn()"
          xuiTooltip="One bar of count-in before recording"
          (click)="transport.toggleCountIn()"
        >
          Count-in
        </button>
        <button
          xuiButton
          type="button"
          size="sm"
          class="px-2 text-xs"
          data-testid="merge-recording"
          [variant]="mergeMidi() ? 'default' : 'ghost'"
          [active]="mergeMidi()"
          [attr.aria-pressed]="mergeMidi()"
          xuiTooltip="Merge new MIDI recordings into the region under the playhead"
          (click)="toggleMerge()"
        >
          Merge
        </button>
      </div>
    }
    <an-engine-status class="ms-auto" />
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
