import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { provideIcons } from '@ng-icons/core';
import { matAutoFixNormalRound, matGridOnRound, matZoomInRound, matZoomOutRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiNonIdealStateImports } from '@xui/non-ideal-state';
import { XuiSegmentedControlImports, type XuiSegmentedOption } from '@xui/segmented-control';
import { XuiSelectImports } from '@xui/select';
import { XuiTooltipImports } from '@xui/tooltip';
import { gridLabel, QuantizeGrids } from '../midi/midi-math';
import { PianoRollStore, type PianoRollTool } from '../state/piano-roll.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { deleteCommands, noteCommands, notesLabel, placeNotes, quantizeCommands, transposeNotes } from './note-edit';
import { PianoRollGrid } from './piano-roll-grid';
import { PianoRollKeys } from './piano-roll-keys';
import { VelocityLane } from './velocity-lane';

/**
 * The Piano Roll (Logic's, v1) in the Editors pane: the regions of one track the selection names, as notes on a
 * keyboard; the tool, snap and grid in its toolbar; the velocity lane below. With focus in it, ↑ and ↓ transpose the
 * selected notes (⇧: an octave), Q quantises them (or every note shown) to the grid, Delete deletes them, ⌘A selects
 * every note and Escape none. Each edit is one undo step.
 */
@Component({
  selector: 'an-piano-roll',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    PianoRollGrid,
    PianoRollKeys,
    VelocityLane,
    XuiButtonImports,
    XuiIconImports,
    XuiNonIdealStateImports,
    XuiSegmentedControlImports,
    XuiSelectImports,
    XuiTooltipImports
  ],
  providers: [provideIcons({ matAutoFixNormalRound, matGridOnRound, matZoomInRound, matZoomOutRound })],
  host: {
    class: 'flex h-full min-h-0 flex-col outline-none',
    role: 'region',
    'aria-label': 'Piano Roll',
    tabindex: '-1',
    '(keydown)': 'key($event)',
    '(pointerdown)': 'focus($event)'
  },
  template: `
    <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2" role="toolbar" aria-label="Piano Roll tools">
      <xui-segmented-control
        size="sm"
        ariaLabel="Piano Roll tool"
        [options]="tools"
        [value]="store.tool()"
        (valueChange)="setTool($event)"
      />
      <button
        xuiButton
        type="button"
        size="sm"
        variant="ghost"
        aria-label="Snap notes to the grid"
        xuiTooltip="Snap to the grid"
        [active]="store.snap()"
        [class.text-primary]="store.snap()"
        (click)="store.snap.set(!store.snap())"
      >
        <ng-icon xui name="matGridOnRound" size="sm" />
      </button>
      <xui-select
        class="w-32"
        aria-label="Grid and quantise value"
        [items]="grids"
        [itemText]="gridText"
        [filterable]="false"
        [value]="gridChoice()"
        (valueChange)="setGrid($event?.ticks ?? null)"
      />
      <button
        xuiButton
        type="button"
        size="sm"
        variant="outline"
        data-testid="quantize"
        [disabled]="regions().length === 0"
        [xuiTooltip]="
          'Quantise ' + (selectedCount() ? 'the selected notes' : 'every note') + ' to ' + gridName() + ' (Q)'
        "
        (click)="quantize()"
      >
        <ng-icon xui name="matAutoFixNormalRound" size="sm" />
        Q
      </button>
      <span class="text-foreground-muted min-w-0 truncate text-xs" data-testid="piano-roll-title">{{ title() }}</span>
      <div class="ms-auto flex items-center gap-1">
        <button xuiButton type="button" size="sm" variant="ghost" aria-label="Zoom out" (click)="store.zoom(1 / 1.5)">
          <ng-icon xui name="matZoomOutRound" size="sm" />
        </button>
        <button xuiButton type="button" size="sm" variant="ghost" aria-label="Zoom in" (click)="store.zoom(1.5)">
          <ng-icon xui name="matZoomInRound" size="sm" />
        </button>
      </div>
    </div>
    @if (regions().length === 0) {
      <xui-non-ideal-state
        class="min-h-0 flex-1"
        title="No MIDI region"
        description="Select MIDI regions, or an instrument or MIDI track, to edit their notes here. Double-click an empty spot of an instrument track to make a region."
      />
    } @else {
      <div class="flex min-h-0 flex-1">
        <an-piano-roll-keys class="w-14 shrink-0" [visibleLanes]="gridRef.visibleLanes()" />
        <an-piano-roll-grid #gridRef class="min-w-0 flex-1" />
      </div>
      <div class="border-hairline flex h-16 shrink-0 border-t">
        <div
          class="border-hairline bg-surface text-foreground-muted flex w-14 shrink-0 items-start border-e p-1.5 text-[10px] font-semibold"
        >
          Velocity
        </div>
        <an-velocity-lane class="min-w-0 flex-1" />
      </div>
    }
  `
})
export class PianoRoll {
  protected readonly store = inject(PianoRollStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);

  protected readonly tools: XuiSegmentedOption<PianoRollTool>[] = [
    { value: 'pointer', label: 'Pointer' },
    { value: 'pencil', label: 'Pencil' },
    { value: 'eraser', label: 'Eraser' }
  ];
  protected readonly grids = QuantizeGrids;
  protected readonly gridText = (grid: { label: string }) => grid.label;
  protected readonly gridChoice = computed(() => this.grids.find(g => g.ticks === this.store.grid()) ?? null);
  protected readonly regions = this.store.regions;
  protected readonly gridName = computed(() => gridLabel(this.store.grid()));
  protected readonly selectedCount = computed(() => {
    const selected = this.selection.notes();
    return this.regions().reduce((sum, r) => sum + r.notes.filter(n => selected.has(n.id)).length, 0);
  });
  protected readonly title = computed(() => {
    const regions = this.regions();
    const track = this.store.track();
    if (regions.length === 0) {
      return '';
    }
    const names = regions.length === 1 ? (regions[0]?.name ?? '') : `${regions.length} regions`;
    return track ? `${track.name}: ${names}` : names;
  });

  protected setTool(tool: PianoRollTool | null): void {
    if (tool) {
      this.store.tool.set(tool);
    }
  }

  protected setGrid(ticks: number | null): void {
    if (ticks) {
      this.store.grid.set(ticks);
    }
  }

  protected focus(event: PointerEvent): void {
    const target = event.target as HTMLElement | null;
    if (!target?.closest('button, input, xui-select, [role="combobox"]')) {
      (event.currentTarget as HTMLElement).focus({ preventScroll: true });
    }
  }

  /** Quantises the selected notes, or every note shown, to the grid. */
  quantize(): void {
    const commands = quantizeCommands(this.regions(), this.selection.notes(), this.store.grid());
    if (commands.length > 0) {
      void this.project.execute(commands, `Quantize ${this.gridName()}`);
    }
  }

  protected key(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    if (target?.closest('input, textarea, [contenteditable], [role="combobox"], [role="listbox"]')) {
      return;
    }

    const command = event.metaKey || event.ctrlKey;
    const handled = this.handle(event.key, event.shiftKey, command);
    if (handled) {
      event.preventDefault();
      event.stopPropagation();
    }
  }

  /** Runs a Piano Roll key command; false for keys it leaves to the window. */
  handle(key: string, shift = false, command = false): boolean {
    const selected = this.selection.notes();
    const notes = placeNotes(this.regions());
    const chosen = notes.filter(n => selected.has(n.id));
    const regions = new Map(this.regions().map(r => [r.id, r]));
    const original = new Map(notes.map(n => [n.id, n]));
    if (command) {
      if (key.toLowerCase() === 'a') {
        this.selection.selectNotes(notes.map(n => n.id));
        return true;
      }
      return false;
    }

    switch (key) {
      case 'ArrowUp':
      case 'ArrowDown': {
        const moved = transposeNotes(chosen, (key === 'ArrowUp' ? 1 : -1) * (shift ? 12 : 1));
        const commands = noteCommands(moved, original, regions);
        if (commands.length > 0) {
          void this.project.execute(commands, notesLabel('Transpose', moved.length));
        }
        return chosen.length > 0;
      }
      case 'q':
      case 'Q':
        this.quantize();
        return true;
      case 'Delete':
      case 'Backspace': {
        if (chosen.length === 0) {
          return false;
        }
        void this.project.execute(deleteCommands(chosen), notesLabel('Delete', chosen.length));
        return true;
      }
      case 'Escape':
        this.selection.clearNotes();
        return true;
      default:
        return false;
    }
  }
}
