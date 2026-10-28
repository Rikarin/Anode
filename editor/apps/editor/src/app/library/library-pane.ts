import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { PatchInfo } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import { matDeleteOutlineRound, matTuneRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiSpinnerImports } from '@xui/spinner';
import { XuiTooltipImports } from '@xui/tooltip';
import { patchCategories, PatchesStore, patchFits } from '../state/patches.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';

/**
 * The Library (Logic's patch browser): the saved channel-strip patches by category, those that suit the selected
 * track's kind; clicking one applies it to the selected track (instrument, inserts and sends: one undo step), and Save
 * keeps the selected track's strip as a new patch.
 */
@Component({
  selector: 'an-library-pane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiIconImports, XuiInputImports, XuiSpinnerImports, XuiTooltipImports],
  providers: [provideIcons({ matDeleteOutlineRound, matTuneRound })],
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Library' },
  template: `
    @if (track(); as t) {
      <div class="border-hairline flex min-h-0 flex-1 flex-col overflow-y-auto" role="list" aria-label="Patches">
        @for (category of categories(); track category.name) {
          <div
            class="border-hairline bg-surface-raised text-foreground sticky top-0 border-y px-3 py-1 text-[11px] font-bold"
            role="presentation"
          >
            {{ category.name }}
          </div>
          @for (patch of category.patches; track patch.id) {
            <div class="group flex h-7 items-center gap-2 px-3 text-xs" role="listitem">
              <ng-icon xui name="matTuneRound" size="sm" class="text-primary shrink-0" aria-hidden="true" />
              <button
                type="button"
                class="text-foreground hover:text-lit min-w-0 flex-1 truncate text-start"
                [attr.data-testid]="'patch-' + patch.id"
                [attr.aria-label]="'Apply patch ' + patch.name + ' to ' + t.name"
                (click)="store.apply(t.id, patch)"
              >
                {{ patch.name }}
              </button>
              <button
                xuiButton
                type="button"
                size="sm"
                variant="ghost"
                class="h-5 w-5 px-0 opacity-0 group-hover:opacity-100 focus-visible:opacity-100"
                [attr.aria-label]="'Delete patch ' + patch.name"
                (click)="store.remove(patch)"
              >
                <ng-icon xui name="matDeleteOutlineRound" size="sm" />
              </button>
            </div>
          }
        } @empty {
          <p class="text-foreground-muted p-3 text-xs">
            @if (store.loading()) {
              <xui-spinner size="sm" aria-label="Loading patches" />
            } @else {
              No {{ kindName() }} patches yet. Save this track's channel strip below to make one.
            }
          </p>
        }
      </div>
      <form
        class="border-hairline flex shrink-0 flex-col gap-1.5 border-t p-2"
        aria-label="Save patch"
        (submit)="$event.preventDefault(); save(name.value, category.value)"
      >
        <span class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">
          Save {{ t.name }} as a patch
        </span>
        <input
          #name
          xuiInput
          class="h-7 text-xs"
          placeholder="Patch name"
          aria-label="Patch name"
          data-testid="patch-name"
          [value]="t.name"
        />
        <div class="flex gap-1">
          <input
            #category
            xuiInput
            class="h-7 min-w-0 flex-1 text-xs"
            placeholder="Category"
            aria-label="Patch category"
            data-testid="patch-category"
            [value]="suggestedCategory()"
          />
          <button xuiButton type="submit" size="sm" data-testid="save-patch" [loading]="saving()">Save</button>
        </div>
      </form>
    } @else {
      <p class="text-foreground-muted p-3 text-xs">Select a track to see the patches for it.</p>
    }
  `
})
export class LibraryPane {
  protected readonly store = inject(PatchesStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);

  protected readonly saving = signal(false);
  protected readonly track = computed(() => {
    const id = this.selection.track();
    const track = id ? this.project.track(id) : undefined;
    return track && track.kind !== 'stack' && track.kind !== 'vca' ? track : null;
  });
  protected readonly categories = computed(() => {
    const kind = this.track()?.kind;
    return kind ? patchCategories(this.store.patches().filter((p: PatchInfo) => patchFits(p, kind))) : [];
  });
  protected readonly kindName = computed(
    () =>
      ({ audio: 'audio', instrument: 'instrument', midi: 'MIDI', aux: 'aux', vca: 'VCA', stack: 'stack' })[
        this.track()?.kind ?? 'audio'
      ]
  );
  protected readonly suggestedCategory = computed(() => (this.track()?.kind === 'instrument' ? 'Keys' : 'Vocals'));

  protected async save(name: string, category: string): Promise<void> {
    const track = this.track();
    if (!track || !name.trim()) {
      return;
    }
    this.saving.set(true);
    await this.store.save(track.id, name, category || 'User');
    this.saving.set(false);
  }
}
