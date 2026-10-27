import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { AnChannelStrip, type MeterSource } from '@anode/audio-ui';
import { MasterMeter, MeterBus } from '../state/meters';
import { Master, MixerStore } from '../state/mixer.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';

/**
 * The Mixer (docs/02-phase-1.md: a basic one): a channel strip per track in track order, then the master. Fader and
 * pan drags are optimistic and coalesced through {@link MixerStore}; meters read the Server's `meters` frames from
 * their own animation frames.
 */
@Component({
  selector: 'an-mixer-pane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnChannelStrip],
  host: { class: 'flex h-full min-h-0 gap-2 overflow-x-auto p-2', role: 'region', 'aria-label': 'Mixer' },
  template: `
    @for (track of project.trackList(); track track.id) {
      <an-channel-strip
        [name]="track.name"
        [color]="track.color"
        [volumeDb]="mixer.value(track.id, 'volumeDb')"
        [pan]="mixer.value(track.id, 'pan')"
        [mute]="track.strip.mute"
        [solo]="track.strip.solo"
        [mono]="!track.input.stereo"
        [selected]="selection.track() === track.id"
        [meter]="meter(track.id)"
        (volumeChange)="mixer.set(track.id, 'volumeDb', $event)"
        (panChange)="mixer.set(track.id, 'pan', $event)"
        (muteToggle)="mixer.toggleMute(track.id)"
        (soloToggle)="mixer.toggleSolo(track.id)"
        (click)="selection.track.set(track.id)"
      />
    }
    <div class="ms-auto"></div>
    <an-channel-strip
      name="Stereo Out"
      master
      color="var(--color-foreground-muted)"
      [volumeDb]="mixer.value(master, 'volumeDb')"
      [pan]="mixer.value(master, 'pan')"
      [mute]="project.master().mute"
      [meter]="meter(masterMeter)"
      (volumeChange)="mixer.set(master, 'volumeDb', $event)"
      (panChange)="mixer.set(master, 'pan', $event)"
      (muteToggle)="mixer.toggleMute(master)"
    />
  `
})
export class MixerPane {
  protected readonly project = inject(ProjectStore);
  protected readonly mixer = inject(MixerStore);
  protected readonly selection = inject(SelectionStore);
  private readonly meters = inject(MeterBus);
  private readonly sources = new Map<string, MeterSource>();

  protected readonly master = Master;
  protected readonly masterMeter = MasterMeter;

  /** A stable meter source per strip, so the meter keeps its ballistics across change detection. */
  protected meter(key: string): MeterSource {
    let source = this.sources.get(key);
    if (!source) {
      source = () => this.meters.level(key);
      this.sources.set(key, source);
    }
    return source;
  }
}
