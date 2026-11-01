import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { ScoreSet } from '@anode/api-client';
import { XuiButtonImports } from '@xui/button';
import { Band, Field, selectValue } from '../control/styles';
import { FloatingWindow } from '../shell/floating-window';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import { newScoreId } from './score-model';

/**
 * Score Sets (Logic's Score Sets window, docs/10-phase-9.md): named sets of the project's MIDI tracks, made and
 * edited here (`setScoreSet`), opened as a full score in a Score Editor window, and a part extracted from a set (the
 * part of one track, transposed when its style transposes).
 */
@Component({
  selector: 'an-score-sets-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow, XuiButtonImports],
  template: `
    <an-floating-window
      title="Score Sets"
      testId="score-sets-window"
      width="34rem"
      (closed)="ui.scoreSetsOpen.set(false)"
    >
      <div class="flex min-h-0 text-xs">
        <div
          class="border-hairline bg-surface w-44 shrink-0 overflow-y-auto border-e"
          role="listbox"
          aria-label="Score sets"
        >
          @for (set of sets(); track set.id) {
            <button
              type="button"
              role="option"
              class="text-foreground flex h-7 w-full items-center px-3 text-start"
              [class.bg-primary/25]="selectedId() === set.id"
              [attr.aria-selected]="selectedId() === set.id"
              [attr.data-testid]="'score-set-' + set.name"
              (click)="selectedId.set(set.id)"
            >
              <span class="truncate">{{ set.name }}</span>
            </button>
          } @empty {
            <p class="text-foreground-muted px-3 py-2">No score sets yet.</p>
          }
        </div>
        <div class="flex min-w-0 flex-1 flex-col gap-3 p-3">
          <div class="flex gap-2">
            <button xuiButton type="button" size="sm" variant="outline" data-testid="score-set-new" (click)="create()">
              New Set
            </button>
            <button
              xuiButton
              type="button"
              size="sm"
              variant="ghost"
              data-testid="score-set-delete"
              [disabled]="!selected()"
              (click)="remove()"
            >
              Delete
            </button>
          </div>
          @if (selected(); as set) {
            <div class="grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-1.5">
              <label class="text-foreground-muted" for="an-set-name">Name</label>
              <input
                id="an-set-name"
                [class]="field"
                data-testid="score-set-name"
                [value]="set.name"
                (change)="rename(set, value($event))"
              />
            </div>
            <h3 [class]="band + ' -mx-3'">Tracks</h3>
            <div class="flex flex-col gap-1" role="group" aria-label="Tracks in the set">
              @for (track of tracks(); track track.id) {
                <label class="text-foreground flex h-6 items-center gap-2">
                  <input
                    type="checkbox"
                    class="accent-(--color-primary)"
                    [attr.data-testid]="'score-set-track-' + track.name"
                    [checked]="set.trackIds.includes(track.id)"
                    (change)="toggle(set, track.id)"
                  />
                  <span class="truncate">{{ track.name }}</span>
                </label>
              } @empty {
                <p class="text-foreground-muted">The project has no MIDI tracks.</p>
              }
            </div>
            <div class="border-hairline flex flex-wrap items-center gap-2 border-t pt-3">
              <button
                xuiButton
                type="button"
                size="sm"
                data-testid="score-set-open"
                [disabled]="set.trackIds.length === 0"
                (click)="open(set)"
              >
                Open Score
              </button>
              <span class="flex-1"></span>
              <select
                [class]="field + ' w-40'"
                aria-label="Part to extract"
                data-testid="score-set-part"
                (change)="part.set(value($event))"
              >
                @for (id of set.trackIds; track id) {
                  <option [value]="id" [selected]="id === partId()">{{ nameOf(id) }}</option>
                }
              </select>
              <button
                xuiButton
                type="button"
                size="sm"
                variant="outline"
                data-testid="score-set-extract"
                [disabled]="!partId()"
                (click)="extract()"
              >
                Extract Part
              </button>
            </div>
          } @else {
            <p class="text-foreground-muted">Make a set of tracks to open as a full score, print and export.</p>
          }
        </div>
      </div>
    </an-floating-window>
  `
})
export class ScoreSetsWindow {
  protected readonly ui = inject(UiStore);
  private readonly project = inject(ProjectStore);

  protected readonly band = Band;
  protected readonly field = Field;
  protected readonly value = selectValue;
  protected readonly sets = computed(() => this.project.score().sets);
  protected readonly selectedId = signal<string | null>(null);
  protected readonly part = signal<string | null>(null);
  protected readonly selected = computed(() => {
    const sets = this.sets();
    return sets.find(s => s.id === this.selectedId()) ?? sets[0] ?? null;
  });
  protected readonly tracks = computed(() =>
    this.project.trackList().filter(t => t.kind === 'instrument' || t.kind === 'midi')
  );
  protected readonly partId = computed(() => {
    const set = this.selected();
    const part = this.part();
    return set ? (set.trackIds.find(id => id === part) ?? set.trackIds[0] ?? null) : null;
  });

  protected nameOf(trackId: string): string {
    return this.project.track(trackId)?.name ?? trackId;
  }

  protected create(): void {
    const set: ScoreSet = {
      id: newScoreId('scs'),
      name: this.sets().length ? `Score Set ${this.sets().length + 1}` : 'Full Score',
      trackIds: this.tracks().map(t => t.id)
    };
    void this.project.execute([{ type: 'setScoreSet', set }], 'New Score Set');
    this.selectedId.set(set.id);
  }

  protected rename(set: ScoreSet, name: string): void {
    if (name.trim() && name !== set.name) {
      void this.project.execute([{ type: 'setScoreSet', set: { ...set, name: name.trim() } }], 'Rename Score Set');
    }
  }

  protected toggle(set: ScoreSet, trackId: string): void {
    // Kept in track order.
    const chosen = new Set(set.trackIds);
    if (chosen.has(trackId)) {
      chosen.delete(trackId);
    } else {
      chosen.add(trackId);
    }
    const trackIds = this.tracks()
      .map(t => t.id)
      .filter(id => chosen.has(id));
    void this.project.execute([{ type: 'setScoreSet', set: { ...set, trackIds } }], 'Edit Score Set');
  }

  protected remove(): void {
    const set = this.selected();
    if (set) {
      void this.project.execute([{ type: 'deleteScoreSet', setId: set.id }], 'Delete Score Set');
      this.selectedId.set(null);
    }
  }

  protected open(set: ScoreSet): void {
    this.ui.scoreWindow.set({ setId: set.id, partTrackId: null });
  }

  protected extract(): void {
    const part = this.partId();
    if (part) {
      this.ui.scoreWindow.set({ setId: null, partTrackId: part });
    }
  }
}
