import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { Cell, Track } from '@anode/api-client';
import { AnLiveLoopsCell } from '@anode/audio-ui';
import { slotLabel } from '../live-loops/grid-model';
import { LiveLoopsStore } from '../live-loops/live-loops.store';
import { ProjectStore } from '../state/project.store';
import { Touch } from './remote-transport';

/**
 * The remote's Live Loops grid (Logic Remote's, docs/11-phase-10.md): scenes as columns with a launch button each,
 * tracks as rows with a stop button, cells at touch size; a tap launches or stops a cell at the grid's quantise, as on
 * the desktop (the Live Loops store and the Server's launcher). Stop All stops every track.
 */
@Component({
  selector: 'an-remote-live-loops',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnLiveLoopsCell],
  host: { class: 'flex min-h-0 flex-col gap-2 p-2' },
  template: `
    <div class="flex items-center gap-2">
      <span class="text-foreground text-[13px] font-semibold">Live Loops</span>
      <button type="button" [class]="touch + ' ms-auto px-3 text-[12px] font-semibold'" (click)="live.stop()">
        Stop All
      </button>
    </div>
    @if (scenes().length && rows().length) {
      <div class="overflow-auto" role="grid" aria-label="Live Loops grid" data-testid="remote-live-loops">
        <div class="flex gap-1" role="row">
          <span class="w-24 shrink-0" role="columnheader"></span>
          @for (scene of scenes(); track scene.id; let i = $index) {
            <button
              type="button"
              role="columnheader"
              [class]="touch + ' w-20 shrink-0 text-[12px] font-semibold'"
              [attr.aria-label]="'Launch scene ' + (scene.name || i + 1)"
              data-testid="remote-scene"
              (click)="live.launchScene(scene.id)"
            >
              ▶ {{ scene.name || i + 1 }}
            </button>
          }
        </div>
        @for (track of rows(); track track.id) {
          <div class="mt-1 flex gap-1" role="row">
            <button
              type="button"
              role="rowheader"
              class="bezel text-foreground flex h-14 w-24 shrink-0 items-center gap-1 overflow-hidden rounded-md px-2 text-[12px] font-semibold"
              [attr.aria-label]="'Stop ' + track.name"
              (click)="live.stop([track.id])"
            >
              <span class="h-6 w-1 shrink-0 rounded-full" [style.background]="track.color" aria-hidden="true"></span>
              <span class="truncate">■ {{ track.name }}</span>
            </button>
            @for (scene of scenes(); track scene.id; let i = $index) {
              @let cell = live.cellAt(track.id, scene.id);
              <an-live-loops-cell
                class="h-14 w-20 shrink-0"
                role="gridcell"
                data-testid="remote-cell"
                [attr.data-track]="track.id"
                [attr.data-scene]="scene.id"
                [name]="cell?.name ?? null"
                [kind]="cell?.kind ?? null"
                [state]="cell ? live.state(cell.id) : 'empty'"
                [color]="track.color"
                [label]="label(cell, track, i)"
                (activate)="trigger(cell)"
              />
            }
          </div>
        }
      </div>
    } @else {
      <p class="text-foreground-muted text-sm">The Live Loops grid is empty: add cells on the desktop.</p>
    }
  `
})
export class RemoteLiveLoops {
  protected readonly live = inject(LiveLoopsStore);
  private readonly project = inject(ProjectStore);
  protected readonly touch = Touch;
  protected readonly scenes = computed(() => this.project.liveLoops().scenes);
  /** Tracks with a cell or that can have one (not VCAs, auxes or stacks). */
  protected readonly rows = computed(() =>
    this.project.trackList().filter(t => t.kind === 'audio' || t.kind === 'instrument' || t.kind === 'midi')
  );

  protected label(cell: Cell | undefined, track: Track, index: number): string {
    return slotLabel(cell, track.name, index);
  }

  protected trigger(cell: Cell | undefined): void {
    if (cell) {
      void this.live.trigger(cell);
    }
  }
}
