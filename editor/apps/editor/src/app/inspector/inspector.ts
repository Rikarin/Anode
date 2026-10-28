import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { MidiRegion } from '@anode/api-client';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { RegionInspector } from './region-inspector';
import { TrackInspector } from './track-inspector';

/**
 * The Inspector (Logic's, left of the Tracks area): the Region inspector for the selected MIDI region, then the Track
 * inspector for the selected track (or the region's), with its instrument and MIDI routing.
 */
@Component({
  selector: 'an-inspector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RegionInspector, TrackInspector],
  host: { class: 'flex h-full flex-col gap-3 overflow-y-auto px-3 pb-3', role: 'region', 'aria-label': 'Inspector' },
  template: `
    @if (region(); as r) {
      <an-region-inspector [region]="r" />
    }
    @if (track(); as t) {
      @if (t.kind === 'audio') {
        <p class="text-foreground-muted pt-3 text-xs">
          {{ t.name }} is an audio track: its region and channel settings are in the Tracks area and the Mixer.
        </p>
      } @else {
        <an-track-inspector [track]="t" />
      }
    } @else if (!region()) {
      <p class="text-foreground-muted pt-3 text-xs">Select a track or a MIDI region to see its settings here.</p>
    }
  `
})
export class Inspector {
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);

  /** The MIDI region to inspect: the first selected one. */
  protected readonly region = computed<MidiRegion | null>(() => {
    const midi = this.project.midiRegions();
    for (const id of this.selection.regions()) {
      const region = midi.get(id);
      if (region) {
        return region;
      }
    }
    return null;
  });

  protected readonly track = computed(() => {
    const id = this.selection.track() ?? this.region()?.trackId;
    return id ? (this.project.tracks().get(id) ?? null) : null;
  });
}
