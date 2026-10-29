import { ChangeDetectionStrategy, Component, computed, inject, linkedSignal, signal } from '@angular/core';
import type {
  Articulation,
  ArticulationOutput,
  ArticulationOutputKind,
  ArticulationSet,
  ArticulationSwitch,
  ArticulationSwitchKind
} from '@anode/api-client';
import { XuiButtonImports } from '@xui/button';
import { XuiTabsImports } from '@xui/tabs';
import { noteName } from '../midi/midi-math';
import { FloatingWindow } from '../shell/floating-window';
import { MidiLibraryStore } from '../state/midi-library.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';
import {
  addArticulation,
  articulationSetProblems,
  cleanArticulationSet,
  emptyArticulationSet,
  loadInto,
  newOutput,
  newSwitch,
  OutputKinds,
  outputValueRange,
  parsePitch,
  removeArticulation,
  saveArticulationSetCommands,
  SwitchKinds,
  withOutputKind
} from './articulation-set';

const Field =
  'border-border bg-surface-inset text-foreground focus-visible:outline-focus h-6 min-w-0 rounded-[5px] border px-1 text-xs focus-visible:outline-2';
const Head = 'text-foreground-muted px-1 py-1 text-start text-[10px] font-semibold tracking-wide uppercase';

/**
 * The Articulation Set editor window (docs/08-phase-7.md, "Articulation sets"; Logic's Articulation Set window): the
 * set's name, and tabs for its articulations (id 1…254, name, symbol), input switches (a keyswitch note or a controller
 * value recorded as an articulation) and outputs (a keyswitch note, a controller, a program or a channel sent before
 * each note). Save puts the set in the project (`setArticulationSet`) and, for a new set, assigns it to the track it was
 * opened from; it can also be saved to and loaded from the user library, or deleted. Opened with
 * `UiStore.articulationSetEditor` (a set's id, or `new`).
 */
@Component({
  selector: 'an-articulation-set-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow, XuiButtonImports, XuiTabsImports],
  template: `
    <an-floating-window
      title="Articulation Set"
      testId="articulation-set-editor"
      width="40rem"
      [origin]="{ x: 220, y: 110 }"
      (closed)="close()"
    >
      <div class="flex flex-col gap-2 p-3 text-xs">
        <div class="grid grid-cols-[5.5rem_1fr] items-center gap-2">
          <label class="text-foreground-muted" for="articulation-set-name">Name</label>
          <input
            #nameField
            id="articulation-set-name"
            type="text"
            data-testid="articulation-set-name"
            [class]="field"
            [value]="draft().name"
            (change)="patch({ name: nameField.value })"
          />
        </div>
        <xui-tabs class="flex min-h-0 flex-col" [(selectedTabId)]="tab">
          <xui-tab id="articulations" title="Articulations">
            <table class="w-full table-fixed border-collapse" aria-label="Articulations">
              <thead>
                <tr>
                  <th [class]="head + ' w-16'" scope="col">ID</th>
                  <th [class]="head" scope="col">Name</th>
                  <th [class]="head + ' w-24'" scope="col">Symbol</th>
                  <th class="w-8"><span class="sr-only">Remove</span></th>
                </tr>
              </thead>
              <tbody>
                @for (a of draft().articulations; track $index; let i = $index) {
                  <tr data-testid="articulation-row">
                    <td class="p-0.5">
                      <input
                        #idField
                        type="number"
                        min="1"
                        max="254"
                        data-testid="articulation-id"
                        [class]="field + ' readout w-full'"
                        [attr.aria-label]="'Articulation ' + (i + 1) + ' id'"
                        [value]="a.id"
                        (change)="setArticulation(i, { id: integer(idField.value) })"
                      />
                    </td>
                    <td class="p-0.5">
                      <input
                        #articulationName
                        type="text"
                        data-testid="articulation-name"
                        [class]="field + ' w-full'"
                        [attr.aria-label]="'Articulation ' + (i + 1) + ' name'"
                        [value]="a.name"
                        (change)="setArticulation(i, { name: articulationName.value })"
                      />
                    </td>
                    <td class="p-0.5">
                      <input
                        #symbolField
                        type="text"
                        maxlength="8"
                        data-testid="articulation-symbol"
                        [class]="field + ' w-full'"
                        [attr.aria-label]="'Articulation ' + (i + 1) + ' symbol'"
                        [value]="a.symbol ?? ''"
                        (change)="setArticulation(i, { symbol: symbolField.value || null })"
                      />
                    </td>
                    <td class="p-0.5 text-center">
                      <button
                        xuiButton
                        type="button"
                        size="sm"
                        variant="ghost"
                        class="h-6 w-6 px-0"
                        data-testid="articulation-remove"
                        [attr.aria-label]="'Remove articulation ' + (i + 1)"
                        (click)="draft.set(removeArticulation(draft(), i))"
                      >
                        −
                      </button>
                    </td>
                  </tr>
                } @empty {
                  <tr>
                    <td colspan="4" class="text-foreground-muted p-2">
                      No articulations yet. Add one for each playing style.
                    </td>
                  </tr>
                }
              </tbody>
            </table>
            <button
              xuiButton
              type="button"
              size="sm"
              variant="outline"
              class="mt-1"
              data-testid="articulation-add"
              [disabled]="draft().articulations.length >= 254"
              (click)="draft.set(addArticulation(draft()))"
            >
              Add Articulation
            </button>
          </xui-tab>
          <xui-tab id="switches" title="Input Switches">
            <table class="w-full table-fixed border-collapse" aria-label="Input switches">
              <thead>
                <tr>
                  <th [class]="head" scope="col">Articulation</th>
                  <th [class]="head + ' w-32'" scope="col">Type</th>
                  <th [class]="head + ' w-20'" scope="col">Key / CC</th>
                  <th [class]="head + ' w-16'" scope="col">Value</th>
                  <th class="w-8"><span class="sr-only">Remove</span></th>
                </tr>
              </thead>
              <tbody>
                @for (s of draft().switches; track $index; let i = $index) {
                  <tr data-testid="switch-row">
                    <td class="p-0.5">
                      <select
                        data-testid="switch-articulation"
                        [class]="field + ' w-full'"
                        [attr.aria-label]="'Switch ' + (i + 1) + ' articulation'"
                        (change)="setSwitch(i, { articulationId: selectedNumber($event) })"
                      >
                        @for (a of draft().articulations; track a.id) {
                          <option [value]="a.id" [selected]="a.id === s.articulationId">{{ a.id }} {{ a.name }}</option>
                        }
                      </select>
                    </td>
                    <td class="p-0.5">
                      <select
                        data-testid="switch-kind"
                        [class]="field + ' w-full'"
                        [attr.aria-label]="'Switch ' + (i + 1) + ' type'"
                        (change)="setSwitch(i, { kind: switchKind($event) })"
                      >
                        @for (k of switchKinds; track k.value) {
                          <option [value]="k.value" [selected]="k.value === s.kind">{{ k.label }}</option>
                        }
                      </select>
                    </td>
                    <td class="p-0.5">
                      <input
                        #switchNumber
                        type="text"
                        data-testid="switch-number"
                        [class]="field + ' readout w-full'"
                        [attr.aria-label]="'Switch ' + (i + 1) + (s.kind === 'note' ? ' key' : ' controller')"
                        [value]="s.kind === 'note' ? noteName(s.number) : s.number"
                        (change)="setSwitch(i, { number: dataNumber(switchNumber, s.kind === 'note', s.number) })"
                      />
                    </td>
                    <td class="p-0.5">
                      <input
                        #switchValue
                        type="text"
                        inputmode="numeric"
                        data-testid="switch-value"
                        placeholder="Any"
                        [class]="field + ' readout w-full'"
                        [attr.aria-label]="'Switch ' + (i + 1) + ' value'"
                        [value]="s.value ?? ''"
                        (change)="setSwitch(i, { value: switchValue.value.trim() ? integer(switchValue.value) : null })"
                      />
                    </td>
                    <td class="p-0.5 text-center">
                      <button
                        xuiButton
                        type="button"
                        size="sm"
                        variant="ghost"
                        class="h-6 w-6 px-0"
                        data-testid="switch-remove"
                        [attr.aria-label]="'Remove switch ' + (i + 1)"
                        (click)="removeSwitch(i)"
                      >
                        −
                      </button>
                    </td>
                  </tr>
                } @empty {
                  <tr>
                    <td colspan="5" class="text-foreground-muted p-2">
                      No input switches: notes are recorded without articulations.
                    </td>
                  </tr>
                }
              </tbody>
            </table>
            <button
              xuiButton
              type="button"
              size="sm"
              variant="outline"
              class="mt-1"
              data-testid="switch-add"
              [disabled]="draft().articulations.length === 0"
              (click)="addSwitch()"
            >
              Add Switch
            </button>
          </xui-tab>
          <xui-tab id="outputs" title="Outputs">
            <table class="w-full table-fixed border-collapse" aria-label="Outputs">
              <thead>
                <tr>
                  <th [class]="head" scope="col">Articulation</th>
                  <th [class]="head + ' w-32'" scope="col">Type</th>
                  <th [class]="head + ' w-20'" scope="col">Key / CC</th>
                  <th [class]="head + ' w-16'" scope="col">Value</th>
                  <th class="w-8"><span class="sr-only">Remove</span></th>
                </tr>
              </thead>
              <tbody>
                @for (o of draft().outputs; track $index; let i = $index) {
                  <tr data-testid="output-row">
                    <td class="p-0.5">
                      <select
                        data-testid="output-articulation"
                        [class]="field + ' w-full'"
                        [attr.aria-label]="'Output ' + (i + 1) + ' articulation'"
                        (change)="setOutput(i, { articulationId: selectedNumber($event) })"
                      >
                        @for (a of draft().articulations; track a.id) {
                          <option [value]="a.id" [selected]="a.id === o.articulationId">{{ a.id }} {{ a.name }}</option>
                        }
                      </select>
                    </td>
                    <td class="p-0.5">
                      <select
                        data-testid="output-kind"
                        [class]="field + ' w-full'"
                        [attr.aria-label]="'Output ' + (i + 1) + ' type'"
                        (change)="setOutputKind(i, selectedValue($event))"
                      >
                        @for (k of outputKinds; track k.value) {
                          <option [value]="k.value" [selected]="k.value === o.kind">{{ k.label }}</option>
                        }
                      </select>
                    </td>
                    <td class="p-0.5">
                      @if (o.kind === 'note' || o.kind === 'controlChange') {
                        <input
                          #outputNumber
                          type="text"
                          data-testid="output-number"
                          [class]="field + ' readout w-full'"
                          [attr.aria-label]="'Output ' + (i + 1) + (o.kind === 'note' ? ' key' : ' controller')"
                          [value]="o.kind === 'note' ? noteName(o.number) : o.number"
                          (change)="setOutput(i, { number: dataNumber(outputNumber, o.kind === 'note', o.number) })"
                        />
                      }
                    </td>
                    <td class="p-0.5">
                      <input
                        #outputValue
                        type="number"
                        data-testid="output-value"
                        [class]="field + ' readout w-full'"
                        [attr.min]="range(o.kind).min"
                        [attr.max]="range(o.kind).max"
                        [attr.aria-label]="'Output ' + (i + 1) + ' ' + valueName(o.kind)"
                        [value]="o.value"
                        (change)="setOutput(i, { value: integer(outputValue.value) })"
                      />
                    </td>
                    <td class="p-0.5 text-center">
                      <button
                        xuiButton
                        type="button"
                        size="sm"
                        variant="ghost"
                        class="h-6 w-6 px-0"
                        data-testid="output-remove"
                        [attr.aria-label]="'Remove output ' + (i + 1)"
                        (click)="removeOutput(i)"
                      >
                        −
                      </button>
                    </td>
                  </tr>
                } @empty {
                  <tr>
                    <td colspan="5" class="text-foreground-muted p-2">
                      No outputs: add a keyswitch note (or a controller, program or channel) per articulation.
                    </td>
                  </tr>
                }
              </tbody>
            </table>
            <button
              xuiButton
              type="button"
              size="sm"
              variant="outline"
              class="mt-1"
              data-testid="output-add"
              [disabled]="draft().articulations.length === 0"
              (click)="addOutput()"
            >
              Add Output
            </button>
          </xui-tab>
        </xui-tabs>
        @if (problems().length > 0) {
          <ul class="text-foreground flex flex-col gap-0.5" role="alert" data-testid="articulation-set-problems">
            @for (problem of problems(); track $index) {
              <li class="flex gap-1">
                <span class="text-transport-record" aria-hidden="true">●</span>
                {{ problem }}
              </li>
            }
          </ul>
        }
        <div class="border-hairline flex flex-wrap items-center gap-2 border-t pt-2">
          <select
            #libraryField
            aria-label="Load from Library"
            data-testid="articulation-set-load-library"
            [class]="field + ' w-44'"
            (change)="loadFromLibrary(libraryField)"
          >
            <option value="">Load from Library…</option>
            @for (set of library.articulationSets(); track set.id) {
              <option [value]="set.id">{{ set.name }}</option>
            }
          </select>
          <button
            xuiButton
            type="button"
            size="sm"
            variant="outline"
            data-testid="articulation-set-save-library"
            [disabled]="problems().length > 0"
            (click)="saveToLibrary()"
          >
            Save to Library
          </button>
          @if (!isNew()) {
            <button
              xuiButton
              type="button"
              size="sm"
              variant="ghost"
              data-testid="articulation-set-delete"
              (click)="remove()"
            >
              Delete
            </button>
          }
          <span class="ms-auto"></span>
          <button
            xuiButton
            type="button"
            size="sm"
            variant="ghost"
            data-testid="articulation-set-cancel"
            (click)="close()"
          >
            Cancel
          </button>
          <button
            xuiButton
            type="button"
            size="sm"
            data-testid="articulation-set-save"
            [disabled]="problems().length > 0 || saving()"
            (click)="save()"
          >
            Save
          </button>
        </div>
      </div>
    </an-floating-window>
  `
})
export class ArticulationSetEditor {
  protected readonly ui = inject(UiStore);
  private readonly project = inject(ProjectStore);
  protected readonly library = inject(MidiLibraryStore);

  protected readonly field = Field;
  protected readonly head = Head;
  protected readonly switchKinds = SwitchKinds;
  protected readonly outputKinds = OutputKinds;
  protected readonly noteName = noteName;
  protected readonly range = outputValueRange;
  protected readonly removeArticulation = removeArticulation;
  protected readonly addArticulation = addArticulation;
  protected readonly tab = signal<string | null>('articulations');
  protected readonly saving = signal(false);

  /** The track the window was opened from (the selected one): a new set is assigned to it. */
  private readonly openedFrom = inject(SelectionStore).track();

  /** The set being edited: a copy of the project's, or a new one; edits stay here until Save. */
  protected readonly draft = linkedSignal<string | null, ArticulationSet>({
    source: this.ui.articulationSetEditor,
    computation: id => {
      const existing = id && id !== 'new' ? this.project.articulationSets().get(id) : undefined;
      return existing ? structuredClone(existing) : emptyArticulationSet();
    }
  });
  protected readonly isNew = computed(() => !this.project.articulationSets().has(this.draft().id));
  protected readonly problems = computed(() => articulationSetProblems(this.draft()));

  constructor() {
    void this.library.loadArticulationSets();
  }

  protected close(): void {
    this.ui.articulationSetEditor.set(null);
  }

  protected patch(change: Partial<ArticulationSet>): void {
    this.draft.update(set => ({ ...set, ...change }));
  }

  protected setArticulation(index: number, change: Partial<Articulation>): void {
    this.draft.update(set => {
      const before = set.articulations[index];
      if (!before) {
        return set;
      }
      const next = { ...before, ...change };
      // A changed id carries the switches and outputs that named the old one.
      const moved = (id: number) => (id === before.id && next.id !== before.id ? next.id : id);
      return {
        ...set,
        articulations: set.articulations.map((a, i) => (i === index ? next : a)),
        switches: set.switches.map(s => ({ ...s, articulationId: moved(s.articulationId) })),
        outputs: set.outputs.map(o => ({ ...o, articulationId: moved(o.articulationId) }))
      };
    });
  }

  protected addSwitch(): void {
    this.draft.update(set => ({ ...set, switches: [...set.switches, newSwitch(set)] }));
  }

  protected setSwitch(index: number, change: Partial<ArticulationSwitch>): void {
    this.draft.update(set => ({
      ...set,
      switches: set.switches.map((s, i) => (i === index ? { ...s, ...change } : s))
    }));
  }

  protected removeSwitch(index: number): void {
    this.draft.update(set => ({ ...set, switches: set.switches.filter((_, i) => i !== index) }));
  }

  protected addOutput(): void {
    this.draft.update(set => ({ ...set, outputs: [...set.outputs, newOutput(set)] }));
  }

  protected setOutput(index: number, change: Partial<ArticulationOutput>): void {
    this.draft.update(set => ({
      ...set,
      outputs: set.outputs.map((o, i) => (i === index ? { ...o, ...change } : o))
    }));
  }

  protected setOutputKind(index: number, kind: string): void {
    this.draft.update(set => ({
      ...set,
      outputs: set.outputs.map((o, i) => (i === index ? withOutputKind(o, kind as ArticulationOutputKind) : o))
    }));
  }

  protected removeOutput(index: number): void {
    this.draft.update(set => ({ ...set, outputs: set.outputs.filter((_, i) => i !== index) }));
  }

  protected valueName(kind: ArticulationOutputKind): string {
    return kind === 'note' ? 'velocity' : kind === 'channel' ? 'channel' : kind === 'program' ? 'program' : 'value';
  }

  protected integer(text: string): number {
    const value = Number(text.trim());
    return text.trim() && Number.isFinite(value) ? Math.round(value) : Number.NaN;
  }

  /** A switch's or output's key (a note name or a number) or controller number; the field shows it back. */
  protected dataNumber(field: HTMLInputElement, note: boolean, before: number): number {
    const value = note ? parsePitch(field.value) : this.integer(field.value);
    if (value === null || Number.isNaN(value)) {
      field.value = note ? noteName(before) : String(before);
      return before;
    }
    if (note) {
      field.value = noteName(value);
    }
    return value;
  }

  protected selectedNumber(event: Event): number {
    return Number((event.target as HTMLSelectElement).value);
  }

  protected selectedValue(event: Event): string {
    return (event.target as HTMLSelectElement).value;
  }

  protected switchKind(event: Event): ArticulationSwitchKind {
    return this.selectedValue(event) === 'controlChange' ? 'controlChange' : 'note';
  }

  protected loadFromLibrary(field: HTMLSelectElement): void {
    const from = this.library.articulationSets().find(s => s.id === field.value);
    field.value = '';
    if (from) {
      this.draft.set(loadInto(this.draft(), from));
    }
  }

  protected async saveToLibrary(): Promise<void> {
    await this.library.saveArticulationSet(cleanArticulationSet(this.draft()));
  }

  /** Saves the set into the project (one undo step); a new one is assigned to the track it was opened from. */
  protected async save(): Promise<void> {
    const set = this.draft();
    const track = this.openedFrom ? this.project.tracks().get(this.openedFrom) : undefined;
    const assignTo = this.isNew() && track && (track.kind === 'instrument' || track.kind === 'midi') ? track.id : null;
    this.saving.set(true);
    try {
      const result = await this.project.execute(
        saveArticulationSetCommands(set, assignTo),
        this.isNew() ? 'New Articulation Set' : 'Edit Articulation Set'
      );
      if (result) {
        this.ui.articulationSetEditor.set(set.id);
      }
    } finally {
      this.saving.set(false);
    }
  }

  protected async remove(): Promise<void> {
    const result = await this.project.execute(
      [{ type: 'deleteArticulationSet', setId: this.draft().id }],
      'Delete Articulation Set'
    );
    if (result) {
      this.close();
    }
  }
}
