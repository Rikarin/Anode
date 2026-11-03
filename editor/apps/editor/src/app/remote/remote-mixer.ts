import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import type { Send, Track } from '@anode/api-client';
import { AnFader, AnKnob, AnPanKnob } from '@anode/audio-ui';
import { provideIcons } from '@ng-icons/core';
import { matChevronLeftRound, matChevronRightRound } from '@ng-icons/material-icons/round';
import { XuiIconImports } from '@xui/icon';
import { MixerStore } from '../state/mixer.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { setTrack, setTrackLabel } from '../state/track-commands';
import { bankCount, bankOf, bankOfIndex, mixerTracks } from './remote-model';
import { Touch } from './remote-transport';

/**
 * The remote's mixer (docs/11-phase-10.md): the tracks in banks of 8 with bank buttons, each strip a 44 px wide column
 * with the track's colour bar and name (tap to select), the pan knob, the fader, Mute, Solo and Record keys at touch
 * size, and its sends as knobs. Moves go through the desktop mixer's store (`setStrip`, `setSend`, coalesced while a
 * finger drags), so the desktop shows them at once. The bank follows the selected track.
 */
@Component({
  selector: 'an-remote-mixer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnFader, AnKnob, AnPanKnob, XuiIconImports],
  providers: [provideIcons({ matChevronLeftRound, matChevronRightRound })],
  host: { class: 'flex min-h-0 flex-col' },
  template: `
    <div class="glass-bar flex h-13 shrink-0 items-center gap-2 px-2">
      <button
        type="button"
        [class]="touch"
        aria-label="Previous bank"
        data-testid="bank-previous"
        [attr.disabled]="view().bank === 0 ? '' : null"
        (click)="bank.set(view().bank - 1)"
      >
        <ng-icon xui name="matChevronLeftRound" size="22px" />
      </button>
      <span class="readout text-foreground text-[12px] font-semibold" data-testid="bank-label">
        {{ tracks().length ? 'Tracks ' + view().first + '–' + view().last + ' of ' + tracks().length : 'No tracks' }}
      </span>
      <button
        type="button"
        [class]="touch"
        aria-label="Next bank"
        data-testid="bank-next"
        [attr.disabled]="view().bank >= banks() - 1 ? '' : null"
        (click)="bank.set(view().bank + 1)"
      >
        <ng-icon xui name="matChevronRightRound" size="22px" />
      </button>
    </div>
    <div
      class="bg-surface-sunken flex min-h-0 flex-1 overflow-x-auto overscroll-x-contain"
      role="list"
      aria-label="Mixer"
    >
      @for (track of view().items; track track.id) {
        <div
          class="strip-body border-hairline flex w-24 shrink-0 flex-col items-center gap-2 border-e px-1 py-2"
          role="listitem"
          data-testid="remote-strip"
          [attr.data-track]="track.id"
          [class.strip-selected]="selection.track() === track.id"
        >
          <button
            type="button"
            class="bg-surface-inset border-hairline text-foreground flex min-h-11 w-full flex-col justify-end overflow-hidden rounded border text-[11px] font-semibold"
            [attr.aria-label]="'Select ' + track.name"
            [attr.aria-pressed]="selection.track() === track.id"
            (click)="selection.selectTrack(track.id)"
          >
            <span class="h-1 w-full shrink-0" [style.background]="track.color" aria-hidden="true"></span>
            <span class="truncate px-1 py-1">{{ track.name }}</span>
          </button>
          <an-pan-knob
            [label]="'Pan ' + track.name"
            [value]="mixer.value(track.id, 'pan')"
            (valueChange)="mixer.set(track.id, 'pan', $event)"
            (committed)="mixer.release(track.id, 'pan')"
          />
          <an-fader
            class="min-h-40 flex-1"
            [label]="track.name + ' volume'"
            [value]="mixer.value(track.id, 'volumeDb')"
            (valueChange)="mixer.set(track.id, 'volumeDb', $event)"
            (committed)="mixer.release(track.id, 'volumeDb')"
          />
          <div class="grid grid-cols-2 gap-1">
            <button
              type="button"
              class="state-toggle toggle-mute size-11"
              [attr.aria-pressed]="track.strip.mute"
              [attr.aria-label]="'Mute ' + track.name"
              (click)="mixer.toggleMute(track.id)"
            >
              M
            </button>
            <button
              type="button"
              class="state-toggle toggle-solo size-11"
              [attr.aria-pressed]="track.strip.solo"
              [attr.aria-label]="'Solo ' + track.name"
              (click)="mixer.toggleSolo(track.id)"
            >
              S
            </button>
            @if (armable(track)) {
              <button
                type="button"
                class="state-toggle toggle-arm col-span-2 h-11"
                [attr.aria-pressed]="track.recordArmed"
                [attr.aria-label]="'Record enable ' + track.name"
                (click)="toggleArm(track)"
              >
                R
              </button>
            }
          </div>
          @for (send of track.strip.sends; track send.id) {
            <an-knob
              [label]="busName(send)"
              unit="dB"
              [min]="-60"
              [max]="6"
              [value]="sendLevel(track.id, send)"
              (valueChange)="setSend(track.id, send, $event)"
              (committed)="mixer.releaseSend(track.id, send.id)"
            />
          }
        </div>
      } @empty {
        <p class="text-foreground-muted p-4 text-sm">The project has no tracks yet.</p>
      }
    </div>
  `
})
export class RemoteMixer {
  protected readonly mixer = inject(MixerStore);
  private readonly project = inject(ProjectStore);
  protected readonly selection = inject(SelectionStore);
  protected readonly touch = Touch;

  protected readonly tracks = computed(() => mixerTracks(this.project.trackList()));
  protected readonly banks = computed(() => bankCount(this.tracks().length));
  readonly bank = signal(0);
  protected readonly view = computed(() => bankOf(this.tracks(), this.bank()));

  constructor() {
    // The bank follows the selected track (from here, the desktop or a control surface).
    effect(() => {
      const id = this.selection.track();
      const index = this.tracks().findIndex(t => t.id === id);
      if (index >= 0) {
        untracked(() => this.bank.set(bankOfIndex(index)));
      }
    });
  }

  protected armable(track: Track): boolean {
    return track.kind === 'audio' || track.kind === 'instrument' || track.kind === 'midi';
  }

  protected toggleArm(track: Track): void {
    const change = { recordArmed: !track.recordArmed };
    void this.project.execute([setTrack(track.id, change)], setTrackLabel(change));
  }

  protected busName(send: Send): string {
    const aux = this.project.trackList().find(t => t.kind === 'aux' && t.inputBus === send.bus);
    return aux ? aux.name : `Bus ${send.bus}`;
  }

  /** A send's level for the knob (−∞ shows as its bottom). */
  protected sendLevel(trackId: string, send: Send): number {
    return Math.max(-60, this.mixer.sendLevel(trackId, send));
  }

  protected setSend(trackId: string, send: Send, levelDb: number): void {
    void this.mixer.setSendLevel(trackId, send.id, levelDb <= -60 ? -144 : Math.round(levelDb * 10) / 10);
  }
}
