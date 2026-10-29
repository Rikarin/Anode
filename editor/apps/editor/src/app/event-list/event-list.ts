import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  Injector,
  signal,
  untracked
} from '@angular/core';
import { AnEventRow, type EventCellCommit, type EventRowClick } from '@anode/audio-ui/lazy';
import { provideIcons } from '@ng-icons/core';
import { matAddRound, matDeleteOutlineRound, matExpandMoreRound } from '@ng-icons/material-icons/round';
import { XuiIconImports } from '@xui/icon';
import { XuiMenuImports } from '@xui/menu';
import { XuiNonIdealStateImports } from '@xui/non-ideal-state';
import { XuiTooltipImports } from '@xui/tooltip';
import { PianoRollStore } from '../state/piano-roll.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TransportStore } from '../state/transport.store';
import {
  buildRows,
  clickSelection,
  deleteRowsCommands,
  editCell,
  editLabel,
  type EventColumn,
  EventColumns,
  type EventFilter,
  EventFilters,
  EventGridColumns,
  type EventKind,
  type EventRowData,
  insertCommand,
  InsertKinds,
  playingRow,
  rowCells,
  statusText
} from './event-rows';

/**
 * The Event List (Logic's), an editor of the Editors pane: every MIDI event of the regions the Piano Roll shows, one
 * row each, in time order: notes, control changes, pitch bend, channel and poly pressure, program changes, with each
 * note's articulation. Filter buttons narrow the kinds shown; a cell edits in place (bar beat division tick for
 * positions and lengths); "+" inserts an event at the selected event or the playhead; Delete removes the selection;
 * Follow keeps the row at the playhead in view while playing. Its selection is the editor's note and controller
 * selection, shared with the Piano Roll. Each edit is one undo step.
 */
@Component({
  selector: 'an-event-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnEventRow, XuiIconImports, XuiMenuImports, XuiNonIdealStateImports, XuiTooltipImports],
  providers: [provideIcons({ matAddRound, matDeleteOutlineRound, matExpandMoreRound })],
  host: {
    class: 'flex h-full min-h-0 flex-col outline-none',
    role: 'region',
    'aria-label': 'Event List',
    'data-testid': 'event-list-view'
  },
  template: `
    <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2" role="toolbar" aria-label="Event List tools">
      <div
        class="bezel-group text-foreground h-6.5 text-[11px] font-semibold"
        role="group"
        aria-label="Show event kinds"
        data-testid="event-filters"
      >
        @for (filter of filterList; track filter.value) {
          <button
            type="button"
            class="px-2"
            [attr.aria-pressed]="filters().has(filter.value)"
            [attr.data-testid]="'event-filter-' + filter.value"
            [class.text-lit]="filters().has(filter.value)"
            [xuiTooltip]="'Show only ' + filter.label.toLowerCase() + ' (with the other pressed kinds)'"
            (click)="toggleFilter(filter.value)"
          >
            {{ filter.label }}
          </button>
        }
      </div>
      <button
        type="button"
        class="bezel text-foreground flex h-6.5 items-center gap-0.5 px-1.5 text-[11px] font-semibold"
        aria-label="Insert event"
        data-testid="event-insert"
        xuiTooltip="Insert an event at the selected event or the playhead"
        [disabled]="regions().length === 0"
        [xuiMenuTriggerFor]="insertMenu"
      >
        <ng-icon xui name="matAddRound" size="15px" />
        <ng-icon xui name="matExpandMoreRound" size="14px" />
      </button>
      <ng-template #insertMenu>
        <xui-menu aria-label="Insert event">
          @for (item of insertKinds; track item.kind) {
            <button
              xuiMenuItem
              type="button"
              [attr.data-testid]="'event-insert-' + item.kind"
              (click)="insert(item.kind)"
            >
              {{ item.label }}
            </button>
          }
        </xui-menu>
      </ng-template>
      <button
        type="button"
        class="bezel text-foreground grid h-6.5 w-7 place-items-center"
        aria-label="Delete selected events"
        data-testid="event-delete"
        xuiTooltip="Delete the selected events (Delete)"
        [disabled]="selectedRows().length === 0"
        (click)="deleteSelected()"
      >
        <ng-icon xui name="matDeleteOutlineRound" size="15px" />
      </button>
      <button
        type="button"
        class="bezel h-6.5 px-2 text-[11px] font-semibold"
        data-testid="event-follow"
        xuiTooltip="Follow the playhead while playing"
        [attr.aria-pressed]="follow()"
        [class.text-lit]="follow()"
        [class.text-foreground]="!follow()"
        (click)="follow.set(!follow())"
      >
        Follow
      </button>
      <span class="text-foreground-muted min-w-0 truncate text-xs" data-testid="event-list-title">{{ title() }}</span>
      <span class="text-foreground-muted readout ms-auto shrink-0 text-xs" data-testid="event-count">
        {{ rows().length === 1 ? '1 event' : rows().length + ' events' }}
      </span>
    </div>
    @if (error(); as message) {
      <div
        class="border-hairline bg-surface-raised text-foreground flex h-6 shrink-0 items-center gap-2 border-b px-2 text-[11px]"
        role="status"
        data-testid="event-error"
      >
        <span class="text-transport-record" aria-hidden="true">●</span>{{ message }}
      </div>
    }
    @if (regions().length === 0) {
      <xui-non-ideal-state
        class="min-h-0 flex-1"
        title="No MIDI region"
        description="Select MIDI regions, or an instrument or MIDI track, to list their events here."
      />
    } @else {
      <div
        class="bg-surface min-h-0 flex-1 overflow-auto"
        role="grid"
        aria-label="Events"
        aria-multiselectable="true"
        data-testid="event-list"
        [attr.aria-rowcount]="rows().length + 1"
      >
        <div role="rowgroup" class="bg-surface-raised border-hairline sticky top-0 z-10 border-b">
          <div role="row" class="grid h-6 items-center" [style.grid-template-columns]="gridColumns">
            @for (column of columns; track column.key) {
              <div
                role="columnheader"
                class="text-foreground-muted px-1.5 text-[10px] font-semibold tracking-wide uppercase"
              >
                {{ column.label }}
              </div>
            }
          </div>
        </div>
        <div role="rowgroup">
          @for (row of rows(); track row.key; let i = $index) {
            <an-event-row
              [attr.data-row-key]="row.key"
              [attr.data-testid]="'event-row-' + i"
              [attr.aria-rowindex]="i + 2"
              [cells]="cellsOf(row)"
              [columns]="gridColumns"
              [selected]="selectedKeys().has(row.key)"
              [playing]="playingKey() === row.key"
              [editing]="editing()?.key === row.key ? (editing()?.column ?? null) : null"
              [active]="activeKey() === row.key"
              [activeCell]="activeColumn()"
              (pick)="click(i, $event)"
              (edit)="startEdit(row, $event)"
              (commit)="commit(row, $event)"
              (cancelled)="editing.set(null)"
              (move)="move(i, $event.by, $event.column, $event.shift)"
              (remove)="deleteSelected()"
            />
          } @empty {
            <div role="row">
              <div role="gridcell" class="text-foreground-muted p-4 text-[12px]">
                {{
                  filters().size > 0
                    ? 'No events of the kinds shown. Release a filter to see more.'
                    : 'No events. Insert one with +.'
                }}
              </div>
            </div>
          }
        </div>
      </div>
    }
  `
})
export class EventList {
  private readonly pianoRoll = inject(PianoRollStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly transport = inject(TransportStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly filterList = EventFilters;
  protected readonly insertKinds = InsertKinds;
  protected readonly columns = EventColumns;
  protected readonly gridColumns = EventGridColumns;

  /** The pressed filters: the kinds shown (none pressed: every kind). */
  readonly filters = signal<ReadonlySet<EventFilter>>(new Set());
  /** Follow the playhead while playing. */
  readonly follow = signal(true);
  /** The cell being edited. */
  readonly editing = signal<{ key: string; column: EventColumn } | null>(null);
  /** Why the last typed value was refused; '' when it was not. */
  readonly error = signal('');
  /** The row holding the grid's focus stop, and its column. */
  protected readonly activeKey = signal<string | null>(null);
  protected readonly activeColumn = signal(0);
  /** The playhead while following it, in ticks. */
  private readonly playhead = signal<number | null>(null);
  /** The row a plain click selected last: ⇧-click extends from it. */
  private anchor = -1;

  protected readonly regions = this.pianoRoll.regions;
  readonly rows = computed(() => buildRows(this.regions(), this.filters()));
  protected readonly selectedKeys = computed(() => {
    const keys = new Set<string>();
    for (const id of this.selection.notes()) {
      keys.add(`n${id}`);
    }
    for (const id of this.selection.controls()) {
      keys.add(`c${id}`);
    }
    return keys;
  });
  readonly selectedRows = computed(() => this.rows().filter(r => this.selectedKeys().has(r.key)));
  /** The articulations of the shown track's set, or null when it has none. */
  private readonly articulations = computed(() => {
    const setId = this.pianoRoll.track()?.articulationSetId;
    return (setId ? this.project.articulationSets().get(setId)?.articulations : null) ?? null;
  });
  protected readonly playingKey = computed(() => {
    const tick = this.playhead();
    if (tick === null) {
      return null;
    }
    return this.rows()[playingRow(this.rows(), tick)]?.key ?? null;
  });
  protected readonly title = computed(() => {
    const regions = this.regions();
    const track = this.pianoRoll.track();
    const names = regions.length === 1 ? (regions[0]?.name ?? '') : `${regions.length} regions`;
    return regions.length === 0 ? '' : track ? `${track.name}: ${names}` : names;
  });

  constructor() {
    // Follow: while playing, track the playhead every frame.
    let frame = 0;
    const stop = () => {
      cancelAnimationFrame(frame);
      frame = 0;
    };
    effect(() => {
      if (!this.follow() || !this.transport.playing()) {
        stop();
        untracked(() => this.playhead.set(null));
        return;
      }
      const tick = () => {
        this.playhead.set(this.transport.positionAt(performance.now()));
        frame = requestAnimationFrame(tick);
      };
      untracked(tick);
    });
    inject(DestroyRef).onDestroy(stop);
    // The row at the playhead scrolls into view.
    effect(() => {
      const key = this.playingKey();
      if (key) {
        const row = this.rowElement(key);
        if (row && 'scrollIntoView' in row) {
          row.scrollIntoView({ block: 'nearest' });
        }
      }
    });
  }

  protected cellsOf(row: EventRowData) {
    return rowCells(row, this.project.meter(), this.articulations());
  }

  toggleFilter(filter: EventFilter): void {
    this.filters.update(set => {
      const next = new Set(set);
      if (!next.delete(filter)) {
        next.add(filter);
      }
      return next;
    });
  }

  protected click(index: number, event: EventRowClick): void {
    const rows = this.rows();
    const keys = clickSelection(rows, this.selectedKeys(), index, this.anchor, event);
    if (!event.shift) {
      this.anchor = index;
    }
    this.setSelection(keys);
    this.activeKey.set(rows[index]?.key ?? null);
    this.activeColumn.set(
      Math.max(
        0,
        EventColumns.findIndex(c => c.key === event.key)
      )
    );
  }

  /** Selects the rows with these keys (notes and controller events). */
  setSelection(keys: Iterable<string>): void {
    const notes: number[] = [];
    const controls: number[] = [];
    for (const key of keys) {
      (key.startsWith('n') ? notes : controls).push(Number(key.slice(1)));
    }
    this.selection.selectNotes(notes);
    this.selection.selectControls(controls);
  }

  startEdit(row: EventRowData, column: string): void {
    this.error.set('');
    this.activeKey.set(row.key);
    this.editing.set({ key: row.key, column: column as EventColumn });
  }

  /** Commits a cell's text: one undo step, or the reason it was refused (and no edit). */
  async commit(row: EventRowData, { key, text }: EventCellCommit): Promise<void> {
    this.editing.set(null);
    const column = key as EventColumn;
    const edit = editCell(row, column, text, this.project.meter());
    if ('error' in edit) {
      this.error.set(edit.error);
      this.focusRow(row.key, column);
      return;
    }
    this.error.set('');
    if ('command' in edit) {
      await this.project.execute([edit.command], editLabel(row, column));
    }
    this.focusRow(row.key, column);
  }

  protected move(index: number, by: number, column: number, shift: boolean): void {
    const rows = this.rows();
    const next = rows[Math.max(0, Math.min(rows.length - 1, index + by))];
    if (!next) {
      return;
    }
    const nextIndex = rows.indexOf(next);
    const keys = clickSelection(rows, this.selectedKeys(), nextIndex, this.anchor, { shift, toggle: false });
    if (!shift) {
      this.anchor = nextIndex;
    }
    this.setSelection(keys);
    this.focusRow(next.key, EventColumns[column]?.key ?? 'position');
  }

  /** Inserts an event of `kind` at the first selected event, or the playhead; the new event is selected. */
  async insert(kind: EventKind): Promise<void> {
    const first = this.selectedRows()[0];
    const tick = first ? first.tick : this.transport.positionAt(performance.now());
    const command = insertCommand(kind, tick, this.regions(), first?.channel ?? 1);
    if (!command || !('regionId' in command)) {
      return;
    }
    const regionId = command.regionId ?? '';
    const before = new Set(this.idsOf(regionId));
    const result = await this.project.execute([command], `Insert ${statusText(kind)}`);
    if (!result) {
      return;
    }
    const added = this.idsOf(regionId).filter(id => !before.has(id));
    const key = added[0];
    if (key) {
      this.setSelection([key]);
      this.anchor = this.rows().findIndex(r => r.key === key);
      this.focusRow(key, 'position');
    }
  }

  /** Deletes the selected events shown (one undo step). */
  async deleteSelected(): Promise<void> {
    const rows = this.selectedRows();
    if (rows.length === 0) {
      return;
    }
    const index = this.rows().indexOf(rows[0] as EventRowData);
    const label = rows.length === 1 ? `Delete ${statusText(rows[0]?.kind ?? 'note')}` : `Delete ${rows.length} Events`;
    const result = await this.project.execute(deleteRowsCommands(rows), label);
    if (result) {
      const next = this.rows()[Math.min(index, this.rows().length - 1)];
      if (next) {
        this.focusRow(next.key, EventColumns[this.activeColumn()]?.key ?? 'position');
      }
    }
  }

  /** Row keys of a region's events. */
  private idsOf(regionId: string): string[] {
    const region = this.project.midiRegions().get(regionId);
    return region ? [...region.notes.map(n => `n${n.id}`), ...region.controls.map(c => `c${c.id}`)] : [];
  }

  private rowElement(key: string): HTMLElement | null {
    return this.host.nativeElement.querySelector<HTMLElement>(`[data-row-key="${key}"]`);
  }

  /** Moves the grid's focus stop to a row's cell, and focuses it once drawn. */
  private focusRow(key: string, column: EventColumn): void {
    const index = Math.max(
      0,
      EventColumns.findIndex(c => c.key === column)
    );
    this.activeKey.set(key);
    this.activeColumn.set(index);
    afterNextRender(
      () => {
        const cell = this.rowElement(key)?.querySelectorAll<HTMLElement>('[role="gridcell"]')[index];
        cell?.focus({ preventScroll: false });
      },
      { injector: this.injector }
    );
  }
}
