import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { LoopInfo, LoopKind, LoopScale } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import {
  matArrowDownwardRound,
  matArrowUpwardRound,
  matGraphicEqRound,
  matGridOnRound,
  matLibraryMusicRound,
  matMusicNoteRound,
  matStarBorderRound,
  matStarRound,
  matVolumeUpRound
} from '@ng-icons/material-icons/round';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiSpinnerImports } from '@xui/spinner';
import { XuiTooltipImports } from '@xui/tooltip';
import { KeyNames, keyName } from '../global/global-math';
import { keyAt } from '../music/pattern';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import { type LoopColumn, LoopDragType, LoopKeywords, LoopsStore } from './loops.store';

const KindIcons: Record<LoopKind, string> = {
  audio: 'matGraphicEqRound',
  midi: 'matMusicNoteRound',
  pattern: 'matGridOnRound'
};

const KindNames: Record<LoopKind, string> = { audio: 'Audio', midi: 'MIDI', pattern: 'Pattern' };

const Kinds: readonly { value: LoopKind | null; label: string }[] = [
  { value: null, label: 'All' },
  { value: 'audio', label: 'Audio' },
  { value: 'midi', label: 'MIDI' },
  { value: 'pattern', label: 'Pattern' }
];

const Scales: readonly { value: LoopScale | ''; label: string }[] = [
  { value: '', label: 'Any' },
  { value: 'major', label: 'Major' },
  { value: 'minor', label: 'Minor' },
  { value: 'neither', label: 'Neither' },
  { value: 'both', label: 'Both' }
];

const Columns: readonly { value: LoopColumn; label: string; end: boolean }[] = [
  { value: 'name', label: 'Name', end: false },
  { value: 'beats', label: 'Beats', end: true },
  { value: 'tempo', label: 'Tempo', end: true },
  { value: 'key', label: 'Key', end: true }
];

/** Pixels from the list's end at which the next page loads. */
const MoreThresholdPx = 56;

const Field =
  'border-border bg-surface-inset text-foreground focus-visible:outline-focus h-6 min-w-0 rounded-[5px] border px-1 text-[11px] focus-visible:outline-2';

/**
 * The Loop Browser (Logic's, O): a search field and the Sound Library; Logic's grid of keyword buttons (several narrow
 * the search together), the kind, key, scale and favourites filters; then the loops in sortable columns. A click
 * previews a loop in sync with the project's tempo and key (the Server renders it so); a double-click or Return adds it
 * (on the selected track at the playhead's bar when it suits, else on a new track), and loops drag onto the Tracks area.
 */
@Component({
  selector: 'an-loop-browser',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiIconImports, XuiInputImports, XuiSpinnerImports, XuiTooltipImports],
  providers: [
    provideIcons({
      matArrowDownwardRound,
      matArrowUpwardRound,
      matGraphicEqRound,
      matGridOnRound,
      matLibraryMusicRound,
      matMusicNoteRound,
      matStarBorderRound,
      matStarRound,
      matVolumeUpRound
    })
  ],
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Loop Browser' },
  template: `
    <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2">
      <input
        #search
        xuiInput
        type="search"
        role="searchbox"
        class="h-6 min-w-0 flex-1 text-xs"
        placeholder="Search Loops"
        aria-label="Search loops"
        data-testid="loop-search"
        [value]="store.filters().text"
        (input)="store.setText(search.value)"
      />
      <button
        type="button"
        class="bezel text-foreground flex h-6.5 shrink-0 items-center gap-1 px-2 text-[11px] font-semibold"
        data-testid="open-sound-library"
        aria-label="Sound Library"
        xuiTooltip="Install and remove content packs"
        (click)="ui.soundLibraryOpen.set(true)"
      >
        <ng-icon xui name="matLibraryMusicRound" size="14px" aria-hidden="true" />
        Sound Library
      </button>
    </div>

    <div class="border-hairline flex shrink-0 flex-col gap-1.5 border-b px-2 py-2">
      <div class="grid grid-cols-4 gap-1" role="group" aria-label="Keywords">
        @for (keyword of keywords; track keyword) {
          <button
            type="button"
            class="bezel text-foreground h-5.5 min-w-0 truncate px-1 text-[11px] font-medium"
            data-testid="loop-keyword"
            [attr.aria-pressed]="store.filters().tags.has(keyword)"
            [class.text-lit]="store.filters().tags.has(keyword)"
            (click)="store.toggleTag(keyword)"
          >
            {{ keyword }}
          </button>
        }
      </div>
      <div class="flex flex-wrap items-center gap-1.5">
        <div class="bezel-group text-foreground h-6 text-[11px] font-semibold" role="group" aria-label="Loop kind">
          @for (kind of kinds; track kind.label) {
            <button
              type="button"
              class="px-2"
              data-testid="loop-kind"
              [attr.aria-pressed]="store.filters().kind === kind.value"
              [class.text-lit]="store.filters().kind === kind.value"
              (click)="store.setKind(kind.value)"
            >
              {{ kind.label }}
            </button>
          }
        </div>
        <select
          [class]="field + ' w-14'"
          aria-label="Key"
          data-testid="loop-key"
          [value]="store.filters().key ?? ''"
          (change)="setKey($event)"
        >
          <option value="">Any</option>
          @for (name of keyNames; track $index) {
            <option [value]="$index" [selected]="store.filters().key === $index">{{ name }}</option>
          }
        </select>
        <select
          [class]="field + ' w-18'"
          aria-label="Scale"
          data-testid="loop-scale"
          [value]="store.filters().scale ?? ''"
          (change)="setScale($event)"
        >
          @for (scale of scales; track scale.value) {
            <option [value]="scale.value" [selected]="(store.filters().scale ?? '') === scale.value">
              {{ scale.label }}
            </option>
          }
        </select>
        <button
          type="button"
          class="bezel grid size-6 place-items-center"
          data-testid="loop-favourites-only"
          aria-label="Favourites only"
          xuiTooltip="Show only favourites"
          [attr.aria-pressed]="store.filters().favouritesOnly"
          [class.text-lit]="store.filters().favouritesOnly"
          [class.text-foreground]="!store.filters().favouritesOnly"
          (click)="store.toggleFavouritesOnly()"
        >
          <ng-icon xui [name]="store.filters().favouritesOnly ? 'matStarRound' : 'matStarBorderRound'" size="14px" />
        </button>
        <button
          type="button"
          class="bezel text-foreground ms-auto h-6 px-2 text-[11px] font-semibold disabled:opacity-35"
          data-testid="loop-reset"
          aria-label="Reset filters"
          [disabled]="!filtered()"
          (click)="store.reset()"
        >
          Reset
        </button>
      </div>
      <p class="text-foreground-muted truncate text-[11px]" data-testid="loop-sync">
        Preview in project key/tempo:
        <span class="text-foreground readout font-semibold">{{ projectTempo() }} bpm · {{ projectKey() }}</span>
      </p>
    </div>

    <div class="flex min-h-0 flex-1 flex-col" role="table" aria-label="Loops" [attr.aria-rowcount]="store.total() + 1">
      <div
        class="border-hairline bg-surface-raised text-foreground-muted grid h-6 shrink-0 items-center border-b px-2 text-[10px] font-semibold tracking-wide uppercase"
        [class]="gridColumns"
        role="row"
      >
        <span role="columnheader" aria-label="Favourite"></span>
        <span role="columnheader" aria-label="Kind"></span>
        @for (column of columns; track column.value) {
          <span
            role="columnheader"
            [class.text-end]="column.end"
            [attr.aria-sort]="
              store.sort().column === column.value ? (store.sort().descending ? 'descending' : 'ascending') : 'none'
            "
          >
            <button
              type="button"
              class="hover:text-foreground inline-flex items-center gap-0.5 uppercase"
              [attr.data-testid]="'loop-sort-' + column.value"
              (click)="store.sortBy(column.value)"
            >
              {{ column.label }}
              @if (store.sort().column === column.value) {
                <ng-icon
                  xui
                  size="10px"
                  aria-hidden="true"
                  [name]="store.sort().descending ? 'matArrowDownwardRound' : 'matArrowUpwardRound'"
                />
              }
            </button>
          </span>
        }
        <span role="columnheader" aria-label="Preview"></span>
      </div>
      <div class="min-h-0 flex-1 overflow-y-auto" role="rowgroup" (scroll)="scrolled($event)">
        @for (loop of store.loops(); track loop.id) {
          <div
            class="hover:bg-hover-overlay focus-visible:outline-focus grid h-7 cursor-default items-center px-2 text-xs focus-visible:outline-2 focus-visible:-outline-offset-2"
            [class]="gridColumns"
            [class.bg-muted]="store.previewing() === loop.id"
            role="row"
            tabindex="0"
            draggable="true"
            data-testid="loop-row"
            [attr.data-loop-id]="loop.id"
            [attr.aria-label]="loop.name"
            [attr.title]="describe(loop)"
            (click)="store.togglePreview(loop)"
            (dblclick)="add(loop)"
            (keydown.enter)="add(loop)"
            (keydown.space)="$event.preventDefault(); store.togglePreview(loop)"
            (dragstart)="drag(loop, $event)"
          >
            <span role="cell">
              <button
                type="button"
                class="grid size-5 place-items-center"
                [class.text-lit]="loop.favourite"
                [class.text-foreground-muted]="!loop.favourite"
                [attr.aria-label]="'Favourite ' + loop.name"
                [attr.aria-pressed]="loop.favourite"
                (click)="$event.stopPropagation(); store.toggleFavourite(loop)"
                (dblclick)="$event.stopPropagation()"
              >
                <ng-icon xui size="14px" [name]="loop.favourite ? 'matStarRound' : 'matStarBorderRound'" />
              </button>
            </span>
            <span role="cell" class="text-foreground-muted grid place-items-center">
              <ng-icon xui size="sm" [name]="kindIcon(loop.kind)" [attr.aria-label]="kindName(loop.kind)" />
            </span>
            <span role="cell" class="text-foreground min-w-0 truncate ps-1">{{ loop.name }}</span>
            <span role="cell" class="text-foreground-muted readout text-end">{{ loop.beats ?? '–' }}</span>
            <span role="cell" class="text-foreground-muted readout text-end">{{ tempo(loop) }}</span>
            <span role="cell" class="text-foreground-muted text-end">{{ key(loop) }}</span>
            <span role="cell" class="grid place-items-end">
              <button
                type="button"
                class="grid size-5 place-items-center"
                [class.text-lit]="store.previewing() === loop.id"
                [class.text-foreground-muted]="store.previewing() !== loop.id"
                [attr.aria-label]="'Preview ' + loop.name"
                [attr.aria-pressed]="store.previewing() === loop.id"
                (click)="$event.stopPropagation(); store.togglePreview(loop)"
                (dblclick)="$event.stopPropagation()"
              >
                <ng-icon xui size="14px" name="matVolumeUpRound" />
              </button>
            </span>
          </div>
        } @empty {
          @if (!store.loading()) {
            <p class="text-foreground-muted p-3 text-xs" role="row">
              <span role="cell">
                @if (store.packs().length === 0 && !filtered()) {
                  No loops installed. Open the Sound Library to install a content pack.
                } @else {
                  No loops match. Turn a keyword or filter off, or press Reset.
                }
              </span>
            </p>
          }
        }
        @if (store.loading()) {
          <div class="grid h-7 place-items-center" role="row">
            <span role="cell"><xui-spinner size="sm" aria-label="Searching loops" /></span>
          </div>
        }
      </div>
      <div
        class="border-hairline text-foreground-muted readout flex h-6 shrink-0 items-center border-t px-2 text-[11px]"
        aria-live="polite"
        data-testid="loop-count"
      >
        {{ store.total() }} {{ store.total() === 1 ? 'loop' : 'loops' }}
      </div>
    </div>
  `
})
export class LoopBrowser {
  protected readonly store = inject(LoopsStore);
  protected readonly ui = inject(UiStore);
  private readonly project = inject(ProjectStore);

  protected readonly keywords = LoopKeywords;
  protected readonly kinds = Kinds;
  protected readonly scales = Scales;
  protected readonly columns = Columns;
  protected readonly keyNames = KeyNames;
  protected readonly field = Field;
  /** Star, kind, name, beats, tempo, key, preview. */
  protected readonly gridColumns = 'grid-cols-[1.25rem_1.25rem_minmax(0,1fr)_2.5rem_3rem_2.25rem_1.5rem] gap-x-1';

  protected readonly filtered = this.store.filtering;
  protected readonly projectTempo = computed(() => Math.round(this.project.settings().tempo * 10) / 10);
  protected readonly projectKey = computed(() => keyName(keyAt(this.project.settings().keys, 0)));

  constructor() {
    void this.store.start();
  }

  protected setKey(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.store.setKey(value === '' ? null : Number(value));
  }

  protected setScale(event: Event): void {
    const value = (event.target as HTMLSelectElement).value as LoopScale | '';
    this.store.setScale(value === '' ? null : value);
  }

  protected kindIcon(kind: LoopKind): string {
    return KindIcons[kind];
  }

  protected kindName(kind: LoopKind): string {
    return KindNames[kind];
  }

  protected tempo(loop: LoopInfo): string {
    return loop.tempo ? String(Math.round(loop.tempo)) : '–';
  }

  protected key(loop: LoopInfo): string {
    if (loop.key === null || loop.key === undefined) {
      return '–';
    }
    return `${KeyNames[loop.key] ?? '?'}${loop.scale === 'minor' ? 'm' : ''}`;
  }

  protected describe(loop: LoopInfo): string {
    const parts = [KindNames[loop.kind], loop.oneShot ? 'one-shot' : null, loop.tags.join(', ')].filter(Boolean);
    return `${loop.name}\n${parts.join(' · ')}\nClick to preview, double-click to add`;
  }

  protected add(loop: LoopInfo): void {
    void this.store.add(loop);
  }

  protected drag(loop: LoopInfo, event: DragEvent): void {
    if (!event.dataTransfer) {
      return;
    }
    event.dataTransfer.effectAllowed = 'copy';
    event.dataTransfer.setData(LoopDragType, loop.id);
    event.dataTransfer.setData('text/plain', loop.name);
  }

  /** The next page when the list nears its end. */
  protected scrolled(event: Event): void {
    const list = event.target as HTMLElement;
    if (list.scrollTop + list.clientHeight >= list.scrollHeight - MoreThresholdPx) {
      void this.store.more();
    }
  }
}
