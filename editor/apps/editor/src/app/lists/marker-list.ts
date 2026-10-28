import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import type { Marker } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import { matAddRound, matDeleteRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { ProjectStore } from '../state/project.store';
import { TransportStore } from '../state/transport.store';

/**
 * The Marker List (Logic's): every marker with its position (bars and beats), name, length and text, in time order.
 * Click a position to go there; edit a name or text in place; + adds a marker at the playhead.
 */
@Component({
  selector: 'an-marker-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiIconImports, XuiInputImports],
  providers: [provideIcons({ matAddRound, matDeleteRound })],
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Marker List' },
  template: `
    <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2">
      <span class="text-foreground flex-1 text-[11px] font-semibold">Marker List</span>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="ghost"
        aria-label="New marker at the playhead"
        data-testid="marker-list-add"
        (click)="add()"
      >
        <ng-icon xui name="matAddRound" size="sm" />
      </button>
    </div>
    <div class="min-h-0 flex-1 overflow-y-auto" role="list" aria-label="Markers">
      <div
        class="border-hairline text-foreground-muted grid grid-cols-[4.5rem_1fr_1fr_1.5rem] gap-2 border-b px-2 py-1 text-[10px] font-semibold tracking-wide uppercase"
        aria-hidden="true"
      >
        <span>Position</span>
        <span>Name</span>
        <span>Text</span>
        <span></span>
      </div>
      @for (marker of project.markers(); track marker.id) {
        <div
          class="border-hairline grid grid-cols-[4.5rem_1fr_1fr_1.5rem] items-center gap-2 border-b px-2 py-1 text-xs"
          role="listitem"
          [attr.data-testid]="'marker-row-' + marker.name"
        >
          <button
            type="button"
            class="text-foreground readout hover:text-primary text-start"
            [attr.aria-label]="'Go to ' + marker.name"
            (click)="transport.locate(marker.tick)"
          >
            <span
              class="me-1 inline-block size-2 rounded-full"
              [style.background-color]="marker.color || 'var(--color-primary)'"
            ></span
            >{{ position(marker.tick) }}
          </button>
          <input
            #name
            xuiInput
            class="h-6 px-1 text-xs"
            [attr.aria-label]="'Name of marker ' + marker.name"
            [value]="marker.name"
            (keydown.enter)="name.blur()"
            (blur)="rename(marker, name.value)"
          />
          <input
            #text
            xuiInput
            class="h-6 px-1 text-xs"
            [attr.aria-label]="'Text of marker ' + marker.name"
            [value]="marker.text"
            (keydown.enter)="text.blur()"
            (blur)="setText(marker, text.value)"
          />
          <button
            type="button"
            class="text-foreground-muted hover:text-error grid size-5 place-items-center"
            [attr.aria-label]="'Delete marker ' + marker.name"
            (click)="remove(marker)"
          >
            <ng-icon xui name="matDeleteRound" size="14px" />
          </button>
        </div>
      } @empty {
        <p class="text-foreground-muted p-3 text-xs">
          No markers yet. Add one with + here, ⌥' or the marker track's +.
        </p>
      }
    </div>
  `
})
export class MarkerList {
  protected readonly project = inject(ProjectStore);
  protected readonly transport = inject(TransportStore);

  protected position(tick: number): string {
    const p = this.project.meter().position(tick);
    return `${p.bar} ${p.beat} ${p.division} ${p.tick}`;
  }

  protected add(): void {
    const tick = this.project.meter().snapBar(this.transport.positionAt(performance.now()));
    void this.project.execute(
      [{ type: 'addMarker', name: `Marker ${this.project.markers().length + 1}`, tick, lengthTicks: 0 }],
      'Create Marker'
    );
  }

  protected rename(marker: Marker, name: string): void {
    const trimmed = name.trim();
    if (trimmed && trimmed !== marker.name) {
      void this.project.execute([{ type: 'setMarker', markerId: marker.id, name: trimmed }], 'Rename Marker');
    }
  }

  protected setText(marker: Marker, text: string): void {
    if (text !== marker.text) {
      void this.project.execute([{ type: 'setMarker', markerId: marker.id, text }], 'Marker Text');
    }
  }

  protected remove(marker: Marker): void {
    void this.project.execute([{ type: 'deleteMarkers', markerIds: [marker.id] }], 'Delete Marker');
  }
}
