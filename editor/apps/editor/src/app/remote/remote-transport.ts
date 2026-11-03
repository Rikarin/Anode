import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { ListenIn } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import {
  matFiberManualRecordRound,
  matHeadphonesRound,
  matLoopRound,
  matPlayArrowRound,
  matSkipPreviousRound,
  matStopRound
} from '@ng-icons/material-icons/round';
import { XuiIconImports } from '@xui/icon';
import { ProjectStore } from '../state/project.store';
import { TransportStore, clockTime } from '../state/transport.store';
import { ListenInStore } from './listen-in/listen-in.store';

/** A remote touch target: at least 44 px square (Apple's minimum), a bezel. */
export const Touch = 'bezel text-foreground flex min-h-11 min-w-11 items-center justify-center rounded-md';

/**
 * The remote's control bar (docs/11-phase-10.md): Go to Start, Play/Stop, Record and Cycle as 44 px bezels; the LCD
 * (bars and beats over clock time, tempo); and listen-in — a toggle with the stream's state, how far behind the
 * Server's transport the remote hears it, and the listener count.
 */
@Component({
  selector: 'an-remote-transport',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiIconImports],
  providers: [
    provideIcons({
      matFiberManualRecordRound,
      matHeadphonesRound,
      matLoopRound,
      matPlayArrowRound,
      matSkipPreviousRound,
      matStopRound
    })
  ],
  host: { class: 'chrome-bar flex flex-wrap items-center gap-2 px-2 py-1.5' },
  template: `
    <div class="flex gap-1.5" role="group" aria-label="Transport">
      <button type="button" [class]="touch" aria-label="Go to Beginning" (click)="transport.goToStart()">
        <ng-icon xui name="matSkipPreviousRound" size="22px" />
      </button>
      <button
        type="button"
        [class]="touch"
        data-testid="remote-play"
        [attr.aria-label]="transport.playing() ? 'Stop' : 'Play'"
        [attr.aria-pressed]="transport.playing()"
        [class.text-transport-play]="transport.playing()"
        (click)="transport.togglePlay()"
      >
        <ng-icon xui [name]="transport.playing() ? 'matStopRound' : 'matPlayArrowRound'" size="24px" />
      </button>
      <button
        type="button"
        [class]="touch + ' text-transport-record'"
        data-testid="remote-record"
        aria-label="Record"
        [attr.aria-pressed]="transport.recording()"
        (click)="transport.record()"
      >
        <ng-icon xui name="matFiberManualRecordRound" size="22px" />
      </button>
      <button
        type="button"
        [class]="touch"
        aria-label="Cycle"
        [attr.aria-pressed]="transport.cycle()"
        [class.text-transport-cycle]="transport.cycle()"
        (click)="transport.toggleCycle()"
      >
        <ng-icon xui name="matLoopRound" size="22px" />
      </button>
    </div>
    <div
      class="lcd-glass flex h-11 min-w-0 flex-1 items-center justify-center gap-3 rounded-md px-3 leading-none"
      role="group"
      aria-label="Position"
    >
      <div class="flex flex-col items-center">
        <span class="readout text-[19px] font-semibold whitespace-pre" data-testid="remote-bars">{{ bars() }}</span>
        <span class="readout text-lcd-muted mt-1 text-[10px]">{{ time() }}</span>
      </div>
      <div class="flex flex-col items-center border-s border-(--lcd-divider) ps-3">
        <span class="readout text-[15px] font-semibold">{{ transport.tempo() }}</span>
        <span class="text-lcd-muted mt-1 text-[9px] font-semibold tracking-[0.08em] uppercase">bpm</span>
      </div>
    </div>
    <div class="flex items-center gap-2">
      <button
        type="button"
        [class]="touch + ' gap-1.5 px-3 text-[12px] font-semibold'"
        data-testid="listen-in"
        [attr.aria-pressed]="listen.on()"
        [class.text-lit]="listen.on()"
        (click)="toggleListen()"
      >
        <ng-icon xui name="matHeadphonesRound" size="20px" />
        Listen
      </button>
      <div
        class="text-foreground flex min-w-24 flex-col text-[11px] leading-tight"
        role="status"
        data-testid="listen-in-status"
      >
        <span class="font-semibold">{{ statusText() }}</span>
        @if (listen.on()) {
          <span class="readout text-foreground-muted" data-testid="listen-in-latency">{{ latencyText() }}</span>
        }
      </div>
    </div>
  `
})
export class RemoteTransport {
  protected readonly transport = inject(TransportStore);
  private readonly project = inject(ProjectStore);
  protected readonly listen = inject(ListenInStore);
  protected readonly touch = Touch;

  protected readonly bars = signal('1 1 1   1');
  protected readonly time = signal(clockTime(0));

  protected readonly statusText = computed(() => {
    const listeners = this.listen.server().listeners;
    switch (this.listen.status()) {
      case 'connecting':
        return 'Connecting…';
      case 'buffering':
        return 'Buffering…';
      case 'playing':
        return `Listening · ${listeners} of ${ListenIn.maxListeners}`;
      case 'failed':
        return this.listen.error() ?? 'Listen-in stopped';
      default:
        return listeners > 0 ? `${listeners} listening` : 'Listen-in off';
    }
  });

  protected readonly latencyText = computed(() => {
    const behind = this.listen.latencyMs();
    const server = this.listen.server().latencyMs;
    const buffer = this.listen.stats()?.bufferedMs;
    const parts = [
      behind === null ? 'Latency —' : `${Math.round(behind)} ms behind`,
      buffer === undefined ? null : `buffer ${Math.round(buffer)} ms`,
      server == null ? null : `server ${Math.round(server)} ms`
    ];
    return parts.filter(p => p !== null).join(' · ');
  });

  protected toggleListen(): void {
    void this.listen.toggle();
  }

  constructor() {
    let frame = 0;
    const draw = () => {
      const now = performance.now();
      const ticks = this.transport.positionAt(now);
      const { bar, beat, division, tick } = this.project.meter().position(ticks);
      this.bars.set(`${bar} ${beat} ${division} ${String(tick).padStart(3, ' ')}`);
      this.time.set(clockTime(this.transport.secondsAt(now)));
      frame = requestAnimationFrame(draw);
    };
    if (typeof requestAnimationFrame === 'function') {
      frame = requestAnimationFrame(draw);
    }
    inject(DestroyRef).onDestroy(() => {
      if (typeof cancelAnimationFrame === 'function') {
        cancelAnimationFrame(frame);
      }
    });
  }
}
