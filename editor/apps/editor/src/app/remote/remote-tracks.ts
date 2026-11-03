import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import type { Track } from '@anode/api-client';
import { LiveMidiStore } from '../state/live-midi.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';

const KindNames: Record<Track['kind'], string> = {
  audio: 'Audio',
  instrument: 'Instrument',
  midi: 'MIDI',
  aux: 'Aux',
  vca: 'VCA',
  stack: 'Stack',
  drummer: 'Drummer',
  bassPlayer: 'Bass Player',
  keyboardPlayer: 'Keyboard Player'
};

/**
 * The remote's track list (docs/11-phase-10.md): 48 px rows with the track's colour, number, name and kind; a tap
 * selects it (the desktop follows through `PUT /project/selection`, as Logic Remote's selection does), and the
 * selected one is what the keyboard, drum pads, Smart Controls and Step Sequencer act on.
 */
@Component({
  selector: 'an-remote-tracks',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex min-h-0 flex-col overflow-y-auto' },
  template: `
    <ul class="flex flex-col" role="listbox" aria-label="Tracks" data-testid="remote-tracks">
      @for (track of project.trackList(); track track.id; let i = $index) {
        <li
          role="option"
          class="border-hairline flex min-h-12 cursor-default items-center gap-2 border-b px-2"
          [class.bg-muted]="selection.track() === track.id"
          [class.bg-surface]="selection.track() !== track.id"
          [attr.aria-selected]="selection.track() === track.id"
          [attr.data-track]="track.id"
          tabindex="0"
          (click)="selection.selectTrack(track.id)"
          (keydown.enter)="selection.selectTrack(track.id)"
          (keydown.space)="$event.preventDefault(); selection.selectTrack(track.id)"
        >
          <span class="h-8 w-1 shrink-0 rounded-full" [style.background]="track.color" aria-hidden="true"></span>
          <span class="readout text-foreground-muted w-5 text-end text-[11px]">{{ i + 1 }}</span>
          <span class="text-foreground min-w-0 flex-1 truncate text-[13px] font-semibold">{{ track.name }}</span>
          @if (live.isActive(track.id)) {
            <span class="bg-meter-low size-2 rounded-full" role="img" aria-label="MIDI activity"></span>
          }
          <span class="text-foreground-muted text-[11px]">{{ kinds[track.kind] }}</span>
        </li>
      } @empty {
        <li class="text-foreground-muted p-3 text-sm">No tracks.</li>
      }
    </ul>
  `
})
export class RemoteTracks {
  protected readonly project = inject(ProjectStore);
  protected readonly selection = inject(SelectionStore);
  protected readonly live = inject(LiveMidiStore);
  protected readonly kinds = KindNames;
}
