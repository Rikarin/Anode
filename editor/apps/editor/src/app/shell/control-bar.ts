import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { provideIcons } from '@ng-icons/core';
import {
  matFiberManualRecordRound,
  matLoopRound,
  matPlayArrowRound,
  matStopRound
} from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiTooltipImports } from '@xui/tooltip';
import { TransportStore } from '../state/transport.store';
import { EngineStatus } from './engine-status';
import { Lcd } from './lcd';

/** The control bar: transport, LCD and status (Logic's control bar; customisable later). */
@Component({
  selector: 'an-control-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [EngineStatus, Lcd, XuiButtonImports, XuiIconImports, XuiTooltipImports],
  providers: [provideIcons({ matFiberManualRecordRound, matLoopRound, matPlayArrowRound, matStopRound })],
  host: { class: 'bg-surface border-border flex h-12 shrink-0 items-center gap-4 border-b px-3' },
  template: `
    <ng-content />
    <div class="flex items-center gap-1" role="toolbar" aria-label="Transport">
      <button
        type="button"
        xuiButton
        variant="ghost"
        size="sm"
        aria-label="Stop"
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
        [attr.aria-pressed]="transport.playing()"
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
        [attr.aria-pressed]="transport.recording()"
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
        [attr.aria-pressed]="transport.cycle()"
        [class.text-warning]="transport.cycle()"
        [xuiTooltip]="'Cycle (C)'"
        (click)="transport.toggleCycle()"
      >
        <ng-icon xui name="matLoopRound" size="sm" />
      </button>
    </div>
    <an-lcd />
    <an-engine-status class="ms-auto" />
  `
})
export class ControlBar {
  protected readonly transport = inject(TransportStore);
}
