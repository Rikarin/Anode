import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { MidiRegion, TransformMode, TransformSet } from '@anode/api-client';
import { AnTransformGrid, type TransformGridChange } from '@anode/audio-ui/lazy';
import { XuiButtonImports } from '@xui/button';
import { XuiInputImports } from '@xui/input';
import { XuiNumericInputImports } from '@xui/numeric-input';
import { XuiSegmentedControlImports, type XuiSegmentedOption } from '@xui/segmented-control';
import { XuiSelectImports } from '@xui/select';
import { fitPresetToSelection, isFactorySet } from '../midi-edit/transform';
import { FloatingWindow } from '../shell/floating-window';
import { MidiLibraryStore } from '../state/midi-library.store';
import { PianoRollStore } from '../state/piano-roll.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';
import { TransformMap } from './transform-map';
import {
  applyGridChange,
  type Drafts,
  emptySet,
  gridRows,
  isRandom,
  matching,
  newSeed,
  newSetId,
  presetMenu,
  TransformColumns,
  transformCommand,
  transformScope,
  usesMap
} from './transform-model';

const Modes: XuiSegmentedOption<TransformMode>[] = [
  { value: 'selectOnly', label: 'Select Only' },
  { value: 'operateOnly', label: 'Operate Only' },
  { value: 'selectAndOperate', label: 'Select and Operate' }
];

/**
 * Logic's MIDI Transform window (⌘9; docs/08-phase-7.md "MIDI Transform"): a preset menu (the factory presets, then
 * the user's sets from the library), the conditions and operations grid, the 128-step map when an operation uses it,
 * the mode, the seed of random operations, and Apply. It acts on the selected MIDI regions, else the regions the Piano
 * Roll shows; operations apply to the selected notes and controller events when there are any. Select Only runs here
 * and sets the selection; the other modes send one `transform` command (one undo step), and Select and Operate selects
 * the events it matched. A random set takes a fresh seed after each Apply. Sets save to the library: a factory preset
 * saves as a new set.
 */
@Component({
  selector: 'an-transform-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AnTransformGrid,
    FloatingWindow,
    TransformMap,
    XuiButtonImports,
    XuiInputImports,
    XuiNumericInputImports,
    XuiSegmentedControlImports,
    XuiSelectImports
  ],
  template: `
    <an-floating-window
      title="MIDI Transform"
      testId="transform-window"
      width="min(60rem, 96vw)"
      [origin]="{ x: 120, y: 80 }"
      (closed)="ui.transformOpen.set(false)"
    >
      <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2" role="toolbar" aria-label="Transform set">
        <xui-select
          class="an-compact-select w-56"
          aria-label="Transform preset"
          data-testid="transform-preset"
          placeholder="Presets"
          [items]="presets()"
          [itemText]="setName"
          [filterable]="false"
          [value]="chosen()"
          (valueChange)="$event && load($event)"
        />
        <button xuiButton type="button" size="sm" variant="ghost" data-testid="transform-new" (click)="reset()">
          New
        </button>
        <span class="text-foreground min-w-0 flex-1 truncate text-[12px] font-semibold" data-testid="transform-name">{{
          set().name
        }}</span>
        <button
          xuiButton
          type="button"
          size="sm"
          variant="outline"
          data-testid="transform-save"
          [attr.aria-expanded]="saving()"
          (click)="startSave()"
        >
          Save…
        </button>
        @if (userSet()) {
          <button
            xuiButton
            type="button"
            size="sm"
            variant="ghost"
            color="error"
            data-testid="transform-delete"
            (click)="remove()"
          >
            Delete
          </button>
        }
      </div>
      @if (saving()) {
        <form
          class="border-hairline bg-surface-raised flex items-center gap-2 border-b px-2 py-1.5"
          aria-label="Save the set to the library"
          (submit)="$event.preventDefault(); save(false)"
        >
          <label class="text-foreground-muted text-[11px]" for="transform-save-name">Name</label>
          <input
            #name
            id="transform-save-name"
            xuiInput
            class="h-6 w-56 text-xs"
            data-testid="transform-save-name"
            [value]="saveName()"
            (input)="saveName.set(name.value)"
          />
          @if (userSet()) {
            <button
              xuiButton
              type="button"
              size="sm"
              variant="outline"
              data-testid="transform-save-replace"
              [disabled]="!saveName().trim()"
              (click)="save(true)"
            >
              Replace
            </button>
          }
          <button
            xuiButton
            type="submit"
            size="sm"
            data-testid="transform-save-confirm"
            [disabled]="!saveName().trim()"
          >
            {{ userSet() ? 'Save as New' : 'Save' }}
          </button>
          <button xuiButton type="button" size="sm" variant="ghost" (click)="saving.set(false)">Cancel</button>
        </form>
      }
      <div class="flex flex-col gap-3 p-3">
        <an-transform-grid
          [columns]="columns"
          [rows]="rows()"
          label="Transform conditions and operations"
          (changed)="edit($event)"
        />
        @if (mapShown()) {
          <an-transform-map [map]="set().map ?? null" (changed)="setMap($event)" />
        }
        <div class="flex flex-wrap items-center gap-3 text-xs">
          <xui-segmented-control
            size="sm"
            aria-label="Transform mode"
            data-testid="transform-mode"
            [options]="modes"
            [value]="set().mode"
            (valueChange)="$event && setMode($event)"
          />
          @if (random()) {
            <label class="text-foreground-muted flex items-center gap-1.5">
              Seed
              <xui-numeric-input
                class="w-32"
                size="sm"
                aria-label="Random seed"
                data-testid="transform-seed"
                [min]="0"
                [stepSize]="1"
                [value]="set().seed"
                (valueChange)="$event !== null && setSeed($event)"
              />
            </label>
          }
          <span class="text-foreground-muted min-w-0 flex-1 truncate" data-testid="transform-target">{{
            targetText()
          }}</span>
        </div>
      </div>
      <div class="border-hairline flex items-center gap-2 border-t px-3 py-2">
        <span
          class="text-foreground-muted min-w-0 flex-1 truncate text-xs"
          role="status"
          data-testid="transform-status"
          >{{ status() }}</span
        >
        <button
          xuiButton
          type="button"
          size="sm"
          data-testid="transform-apply"
          [attr.aria-description]="modeLabel()"
          [disabled]="regions().length === 0 || busy()"
          (click)="apply()"
        >
          Apply
        </button>
      </div>
    </an-floating-window>
  `
})
export class TransformWindow {
  protected readonly ui = inject(UiStore);
  private readonly library = inject(MidiLibraryStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly pianoRoll = inject(PianoRollStore);

  protected readonly columns = TransformColumns;
  protected readonly modes = Modes;
  protected readonly setName = (set: TransformSet) => set.name;

  /** The set being edited. */
  readonly set = signal<TransformSet>(emptySet());
  /** Typed values that did not parse, shown as typed. */
  private readonly drafts = signal<Drafts>(new Map());
  /** The library set the menu shows as loaded (null: a new set). */
  private readonly chosenId = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly saveName = signal('');
  protected readonly status = signal('');
  protected readonly busy = signal(false);

  protected readonly presets = computed(() => presetMenu(this.library.transformSets()));
  protected readonly chosen = computed(() => this.presets().find(s => s.id === this.chosenId()) ?? null);
  protected readonly rows = computed(() => gridRows(this.set(), this.project.meter(), this.drafts()));
  protected readonly random = computed(() => isRandom(this.set()));
  protected readonly mapShown = computed(() => usesMap(this.set()));
  /** The loaded set is the user's (in the library, not a factory preset). */
  protected readonly userSet = computed(() => {
    const id = this.set().id;
    return id !== '' && !isFactorySet(id) && this.presets().some(s => s.id === id);
  });
  protected readonly modeLabel = computed(() => Modes.find(m => m.value === this.set().mode)?.label ?? '');

  /** The MIDI regions it acts on: the selected ones, else those the Piano Roll shows. */
  readonly regions = computed<readonly MidiRegion[]>(() => {
    const midi = this.project.midiRegions();
    const selected = [...this.selection.regions()]
      .map(id => midi.get(id))
      .filter((r): r is MidiRegion => r !== undefined)
      .sort((a, b) => a.startTick - b.startTick);
    return selected.length > 0 ? selected : this.pianoRoll.regions();
  });
  private readonly scope = computed(() =>
    transformScope(this.regions(), this.selection.notes(), this.selection.controls())
  );
  protected readonly targetText = computed(() => {
    const regions = this.regions();
    if (regions.length === 0) {
      return 'Select a MIDI region to transform its events.';
    }
    const where = regions.length === 1 ? (regions[0]?.name ?? '') : `${regions.length} MIDI regions`;
    return this.scope().selection ? `${where}: the selected events` : `${where}: every event`;
  });

  constructor() {
    void this.library.loadTransformSets();
  }

  /** Loads a library set into the editor (Reverse Position and Pitch fitted to the events it will act on). */
  load(preset: TransformSet): void {
    const scope = this.scope();
    let set: TransformSet = structuredClone(fitPresetToSelection(preset, scope));
    if (isRandom(set)) {
      set = { ...set, seed: newSeed() };
    }
    this.set.set(set);
    this.drafts.set(new Map());
    this.chosenId.set(preset.id);
    this.saving.set(false);
    this.status.set('');
  }

  /** A new, empty set. */
  reset(): void {
    this.set.set(emptySet());
    this.drafts.set(new Map());
    this.chosenId.set(null);
    this.saving.set(false);
    this.status.set('');
  }

  edit(change: TransformGridChange): void {
    const next = applyGridChange(this.set(), this.drafts(), change, this.project.meter());
    const wasRandom = isRandom(this.set());
    this.set.set(!wasRandom && isRandom(next.set) ? { ...next.set, seed: newSeed() } : next.set);
    this.drafts.set(next.drafts);
  }

  setMap(map: number[]): void {
    this.set.update(set => ({ ...set, map }));
  }

  setMode(mode: TransformMode): void {
    this.set.update(set => ({ ...set, mode }));
  }

  setSeed(seed: number): void {
    this.set.update(set => ({ ...set, seed: Math.max(0, Math.round(seed)) }));
  }

  /** Runs the set in its mode on the regions' events. */
  async apply(): Promise<void> {
    const set = this.set();
    const regions = this.regions();
    if (regions.length === 0 || this.busy()) {
      return;
    }
    if (this.drafts().size > 0) {
      this.status.set('Fix the values marked invalid first.');
      return;
    }
    const meter = this.project.meter();
    if (set.mode === 'selectOnly') {
      const found = matching(transformScope(regions, new Set(), new Set(), true), set, meter);
      this.selection.selectNotes(found.selectedNoteIds);
      this.selection.selectControls(found.selectedControlIds);
      this.status.set(selectedText(found.selectedNoteIds.length + found.selectedControlIds.length));
      return;
    }

    const scope = this.scope();
    const found = matching(scope, set, meter);
    this.busy.set(true);
    try {
      const result = await this.project.execute([transformCommand(scope, set)], `Transform: ${set.name}`);
      if (!result) {
        return;
      }
      const count = found.selectedNoteIds.length + found.selectedControlIds.length;
      if (set.mode === 'selectAndOperate') {
        this.selection.selectNotes(found.selectedNoteIds);
        this.selection.selectControls(found.selectedControlIds);
      }
      this.status.set(count === 1 ? 'Transformed 1 event.' : `Transformed ${count} events.`);
      if (isRandom(set)) {
        this.set.update(s => ({ ...s, seed: newSeed() }));
      }
    } finally {
      this.busy.set(false);
    }
  }

  protected startSave(): void {
    const set = this.set();
    this.saveName.set(isFactorySet(set.id) ? `${set.name} Copy` : set.name);
    this.saving.update(open => !open);
  }

  /** Saves to the library: over the loaded user set (`replace`), else as a new set. */
  async save(replace: boolean): Promise<void> {
    const name = this.saveName().trim();
    if (!name) {
      return;
    }
    const id = replace && this.userSet() ? this.set().id : newSetId();
    const set: TransformSet = { ...this.set(), id, name };
    if (await this.library.saveTransformSet(set)) {
      this.set.set(set);
      this.chosenId.set(id);
      this.saving.set(false);
      this.status.set(`Saved “${name}”.`);
    }
  }

  /** Deletes the loaded user set from the library. */
  async remove(): Promise<void> {
    const set = this.set();
    if (!this.userSet()) {
      return;
    }
    if (await this.library.deleteTransformSet(set.id)) {
      this.reset();
      this.status.set(`Deleted “${set.name}”.`);
    }
  }
}

function selectedText(count: number): string {
  return count === 1 ? 'Selected 1 event.' : `Selected ${count} events.`;
}
