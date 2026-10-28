import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { AudioRegion, MidiRegion, TakeFolder } from '@anode/api-client';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { AudioRegionInspector, TakeFolderInspector } from './audio-region-inspector';
import { RegionInspector } from './region-inspector';
import { TrackDetails } from './track-details';
import { TrackInspector } from './track-inspector';

const Band =
  'text-foreground border-hairline bg-surface-raised -mx-3 truncate border-y px-3 py-1 text-[11px] font-bold shadow-[inset_0_1px_0_var(--highlight)]';

/**
 * The Inspector (Logic's, left of the Tracks area): the Region inspector for the first selected region (MIDI playback;
 * audio mute, gain, fades and curves; a take folder's mute and crossfade), then the Track inspector for the selected
 * track (or the region's): its stack and notes, and an instrument or MIDI track's instrument and MIDI routing.
 */
@Component({
  selector: 'an-inspector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AudioRegionInspector, RegionInspector, TakeFolderInspector, TrackDetails, TrackInspector],
  host: { class: 'flex h-full flex-col gap-3 overflow-y-auto px-3 pb-3', role: 'region', 'aria-label': 'Inspector' },
  template: `
    @if (region(); as r) {
      @switch (r.kind) {
        @case ('midi') {
          <an-region-inspector [region]="r.midi" />
        }
        @case ('audio') {
          <an-audio-region-inspector [region]="r.audio" />
        }
        @case ('folder') {
          <an-take-folder-inspector [folder]="r.folder" />
        }
      }
    }
    @if (track(); as t) {
      @if (t.kind === 'instrument' || t.kind === 'midi') {
        <an-track-inspector [track]="t" />
      } @else {
        <h3 [class]="band" [attr.title]="t.name">Track: {{ t.name }}</h3>
      }
      <an-track-details [track]="t" />
    } @else if (!region()) {
      <p class="text-foreground-muted pt-3 text-xs">Select a track or a region to see its settings here.</p>
    }
  `
})
export class Inspector {
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  protected readonly band = Band;

  /** The region to inspect: the first selected one, of any kind. */
  protected readonly region = computed<
    | { kind: 'midi'; midi: MidiRegion; trackId: string }
    | { kind: 'audio'; audio: AudioRegion; trackId: string }
    | { kind: 'folder'; folder: TakeFolder; trackId: string }
    | null
  >(() => {
    for (const id of this.selection.regions()) {
      const midi = this.project.midiRegions().get(id);
      if (midi) {
        return { kind: 'midi', midi, trackId: midi.trackId };
      }
      const audio = this.project.regions().get(id);
      if (audio) {
        return { kind: 'audio', audio, trackId: audio.trackId };
      }
      const folder = this.project.takeFolders().get(id);
      if (folder) {
        return { kind: 'folder', folder, trackId: folder.trackId };
      }
    }
    return null;
  });

  protected readonly track = computed(() => {
    const id = this.selection.track() ?? this.region()?.trackId;
    return id ? (this.project.tracks().get(id) ?? null) : null;
  });
}
