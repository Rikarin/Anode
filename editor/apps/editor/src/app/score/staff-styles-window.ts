import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { DrumMapEntry, Notehead, Staff, StaffStyle } from '@anode/api-client';
import { XuiButtonImports } from '@xui/button';
import { Band, Field, selectValue } from '../control/styles';
import { ANODE_API } from '../core/api';
import { FloatingWindow } from '../shell/floating-window';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';
import { logicPitchName } from './notation-math';
import {
  Clefs,
  duplicateStyle,
  newStyle,
  normaliseStaves,
  Noteheads,
  StraightGrids,
  TripletGrids
} from './score-model';

/**
 * Staff Styles (Logic's Staff Style window, docs/10-phase-9.md): the factory styles (read-only; Duplicate makes a
 * project copy) and the project's own, edited in place: name, staves (clef, transposition in semitones, split point,
 * voices), a brace, the display quantise defaults (a straight and a triplet grid) and, for a percussion staff, the drum
 * map. Each change is one `setStaffStyle`; Assign gives the style to the selected tracks (`assignStaffStyle`).
 */
@Component({
  selector: 'an-staff-styles-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow, XuiButtonImports],
  template: `
    <an-floating-window
      title="Staff Styles"
      testId="staff-styles-window"
      width="46rem"
      (closed)="ui.staffStylesOpen.set(false)"
    >
      <div class="flex min-h-0 text-xs">
        <div
          class="border-hairline bg-surface w-52 shrink-0 overflow-y-auto border-e"
          role="listbox"
          aria-label="Staff styles"
        >
          <h3 [class]="band">Project</h3>
          @for (style of projectStyles(); track style.id) {
            <button
              type="button"
              role="option"
              class="text-foreground flex h-7 w-full items-center px-3 text-start"
              [class.bg-primary/25]="selectedId() === style.id"
              [attr.aria-selected]="selectedId() === style.id"
              [attr.data-testid]="'staff-style-' + style.id"
              (click)="selectedId.set(style.id)"
            >
              <span class="truncate">{{ style.name }}</span>
            </button>
          } @empty {
            <p class="text-foreground-muted px-3 py-2">None yet: duplicate a factory style or make a new one.</p>
          }
          <h3 [class]="band">Factory</h3>
          @for (style of factory(); track style.id) {
            <button
              type="button"
              role="option"
              class="text-foreground flex h-7 w-full items-center px-3 text-start"
              [class.bg-primary/25]="selectedId() === style.id"
              [attr.aria-selected]="selectedId() === style.id"
              [attr.data-testid]="'staff-style-' + style.id"
              (click)="selectedId.set(style.id)"
            >
              <span class="truncate">{{ style.name }}</span>
            </button>
          }
        </div>
        <div class="flex min-w-0 flex-1 flex-col gap-3 p-3">
          <div class="flex gap-2">
            <button
              xuiButton
              type="button"
              size="sm"
              variant="outline"
              data-testid="staff-style-new"
              (click)="create()"
            >
              New
            </button>
            <button
              xuiButton
              type="button"
              size="sm"
              variant="outline"
              data-testid="staff-style-duplicate"
              [disabled]="!selected()"
              (click)="duplicate()"
            >
              Duplicate
            </button>
            <button
              xuiButton
              type="button"
              size="sm"
              variant="ghost"
              data-testid="staff-style-delete"
              [disabled]="!editable()"
              (click)="remove()"
            >
              Delete
            </button>
            <div class="flex-1"></div>
            <button
              xuiButton
              type="button"
              size="sm"
              data-testid="staff-style-assign"
              [disabled]="!selected() || targetTracks().length === 0"
              (click)="assign()"
            >
              Assign to {{ targetTracks().length === 1 ? 'Selected Track' : 'Selected Tracks' }}
            </button>
          </div>
          @if (selected(); as style) {
            <div class="grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-1.5">
              <label class="text-foreground-muted" for="an-style-name">Name</label>
              <input
                id="an-style-name"
                [class]="field"
                data-testid="staff-style-name"
                [value]="style.name"
                [disabled]="!editable()"
                (change)="patch({ name: value($event) || style.name })"
              />
              <label class="text-foreground-muted" for="an-style-grid">Quantise</label>
              <div class="flex gap-2">
                <select
                  id="an-style-grid"
                  [class]="field"
                  aria-label="Display quantise"
                  [disabled]="!editable()"
                  (change)="patch({ displayQuantizeTicks: +value($event) })"
                >
                  @for (grid of straightGrids; track grid.ticks) {
                    <option [value]="grid.ticks" [selected]="grid.ticks === style.displayQuantizeTicks">
                      {{ grid.ticks ? grid.label : 'Off' }}
                    </option>
                  }
                </select>
                <select
                  [class]="field"
                  aria-label="Triplet grid"
                  [disabled]="!editable()"
                  (change)="patch({ tripletQuantizeTicks: +value($event) })"
                >
                  @for (grid of tripletGrids; track grid.ticks) {
                    <option [value]="grid.ticks" [selected]="grid.ticks === style.tripletQuantizeTicks">
                      {{ grid.label }}
                    </option>
                  }
                </select>
              </div>
              <span class="text-foreground-muted">Brace</span>
              <label class="text-foreground flex items-center gap-2">
                <input
                  type="checkbox"
                  class="accent-(--color-primary)"
                  [checked]="style.brace"
                  [disabled]="!editable()"
                  (change)="patch({ brace: !style.brace })"
                />
                Join the staves with a brace
              </label>
            </div>
            <table class="w-full border-collapse" aria-label="Staves">
              <thead>
                <tr class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">
                  <th class="text-start font-semibold">Staff</th>
                  <th class="text-start font-semibold">Clef</th>
                  <th class="text-start font-semibold">Transpose</th>
                  <th class="text-start font-semibold">Split</th>
                  <th class="text-start font-semibold">Voices</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (staff of style.staves; track $index; let i = $index; let last = $last) {
                  <tr [attr.data-testid]="'staff-row-' + i">
                    <td class="text-foreground readout pe-2">{{ i + 1 }}</td>
                    <td class="pe-2">
                      <select
                        [class]="field"
                        [attr.aria-label]="'Clef of staff ' + (i + 1)"
                        [disabled]="!editable()"
                        (change)="setStaff(i, { clef: $any(value($event)) })"
                      >
                        @for (clef of clefs; track clef.value) {
                          <option [value]="clef.value" [selected]="clef.value === staff.clef">{{ clef.label }}</option>
                        }
                      </select>
                    </td>
                    <td class="pe-2">
                      <input
                        type="number"
                        [class]="field + ' readout'"
                        min="-48"
                        max="48"
                        [attr.aria-label]="'Transposition of staff ' + (i + 1) + ' in semitones'"
                        [value]="staff.transpose"
                        [disabled]="!editable()"
                        (change)="setStaff(i, { transpose: clamp(+value($event), -48, 48) })"
                      />
                    </td>
                    <td class="pe-2">
                      @if (!last) {
                        <input
                          type="number"
                          [class]="field + ' readout'"
                          min="0"
                          max="127"
                          [attr.aria-label]="'Split point of staff ' + (i + 1)"
                          [title]="pitchName(staff.splitPitch ?? 60)"
                          [value]="staff.splitPitch ?? 60"
                          [disabled]="!editable()"
                          (change)="setStaff(i, { splitPitch: clamp(+value($event), 0, 127) })"
                        />
                      } @else {
                        <span class="text-foreground-muted">—</span>
                      }
                    </td>
                    <td class="pe-2">
                      <input
                        type="number"
                        [class]="field + ' readout'"
                        min="1"
                        max="4"
                        [attr.aria-label]="'Voices of staff ' + (i + 1)"
                        [value]="staff.voices"
                        [disabled]="!editable()"
                        (change)="setStaff(i, { voices: clamp(+value($event), 1, 4) })"
                      />
                    </td>
                    <td>
                      <button
                        xuiButton
                        type="button"
                        size="sm"
                        variant="ghost"
                        [attr.aria-label]="'Remove staff ' + (i + 1)"
                        [disabled]="!editable() || style.staves.length === 1"
                        (click)="removeStaff(i)"
                      >
                        ×
                      </button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
            <div>
              <button
                xuiButton
                type="button"
                size="sm"
                variant="outline"
                data-testid="staff-add"
                [disabled]="!editable() || style.staves.length >= 4"
                (click)="addStaff()"
              >
                + Staff
              </button>
            </div>
            @if (percussion()) {
              <h3 [class]="band + ' -mx-3'">Drum Map</h3>
              <table class="w-full border-collapse" aria-label="Drum map">
                <thead>
                  <tr class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">
                    <th class="text-start font-semibold">Pitch</th>
                    <th class="text-start font-semibold">Position</th>
                    <th class="text-start font-semibold">Head</th>
                    <th class="text-start font-semibold">Name</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  @for (entry of style.drumMap ?? []; track $index; let i = $index) {
                    <tr>
                      <td class="pe-2">
                        <input
                          type="number"
                          min="0"
                          max="127"
                          [class]="field + ' readout'"
                          [attr.aria-label]="'Pitch of drum ' + (i + 1)"
                          [value]="entry.pitch"
                          [disabled]="!editable()"
                          (change)="setDrum(i, { pitch: clamp(+value($event), 0, 127) })"
                        />
                      </td>
                      <td class="pe-2">
                        <input
                          type="number"
                          min="-6"
                          max="14"
                          [class]="field + ' readout'"
                          [attr.aria-label]="'Staff position of drum ' + (i + 1)"
                          [value]="entry.staffPosition"
                          [disabled]="!editable()"
                          (change)="setDrum(i, { staffPosition: clamp(+value($event), -6, 14) })"
                        />
                      </td>
                      <td class="pe-2">
                        <select
                          [class]="field"
                          [attr.aria-label]="'Notehead of drum ' + (i + 1)"
                          [disabled]="!editable()"
                          (change)="setDrum(i, { notehead: $any(value($event)) })"
                        >
                          @for (head of noteheads; track head) {
                            <option [value]="head" [selected]="head === entry.notehead">{{ head }}</option>
                          }
                        </select>
                      </td>
                      <td class="pe-2">
                        <input
                          [class]="field"
                          [attr.aria-label]="'Name of drum ' + (i + 1)"
                          [value]="entry.name"
                          [disabled]="!editable()"
                          (change)="setDrum(i, { name: value($event) })"
                        />
                      </td>
                      <td>
                        <button
                          xuiButton
                          type="button"
                          size="sm"
                          variant="ghost"
                          [attr.aria-label]="'Remove drum ' + (i + 1)"
                          [disabled]="!editable()"
                          (click)="removeDrum(i)"
                        >
                          ×
                        </button>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
              <div>
                <button
                  xuiButton
                  type="button"
                  size="sm"
                  variant="outline"
                  [disabled]="!editable()"
                  (click)="addDrum()"
                >
                  + Drum
                </button>
              </div>
            }
            @if (!editable()) {
              <p class="text-foreground-muted">A factory style: duplicate it to change it.</p>
            }
          } @else {
            <p class="text-foreground-muted">Choose a style.</p>
          }
        </div>
      </div>
    </an-floating-window>
  `
})
export class StaffStylesWindow {
  protected readonly ui = inject(UiStore);
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);

  protected readonly band = Band;
  protected readonly field = Field;
  protected readonly clefs = Clefs;
  protected readonly noteheads: readonly Notehead[] = Noteheads;
  protected readonly straightGrids = StraightGrids.filter(g => g.ticks !== 0 || true);
  protected readonly tripletGrids = TripletGrids;
  protected readonly value = selectValue;
  protected readonly pitchName = logicPitchName;

  protected readonly factory = signal<readonly StaffStyle[]>([]);
  protected readonly selectedId = signal<string | null>(null);
  protected readonly projectStyles = computed(() => this.project.score().staffStyles);
  protected readonly selected = computed(() => {
    const id = this.selectedId();
    return [...this.projectStyles(), ...this.factory()].find(s => s.id === id) ?? null;
  });
  protected readonly editable = computed(() => {
    const id = this.selectedId();
    return this.projectStyles().some(s => s.id === id);
  });
  protected readonly percussion = computed(() => this.selected()?.staves.some(s => s.clef === 'percussion') ?? false);
  /** The tracks Assign gives the style to: the selected MIDI tracks, else the selected track. */
  protected readonly targetTracks = computed(() => {
    const ids = new Set([
      ...this.selection.tracks(),
      ...(this.selection.track() ? [this.selection.track() as string] : [])
    ]);
    return [...ids].filter(id => {
      const kind = this.project.track(id)?.kind;
      return kind === 'instrument' || kind === 'midi';
    });
  });

  constructor() {
    void this.api.staffStyles().then(styles => {
      this.factory.set(styles);
      // Opens on the selected track's style.
      const track = this.project.track(this.selection.track() ?? '');
      this.selectedId.set(track?.staffStyleId ?? styles[2]?.id ?? styles[0]?.id ?? null);
    });
  }

  protected clamp(value: number, min: number, max: number): number {
    return Number.isFinite(value) ? Math.min(max, Math.max(min, Math.round(value))) : min;
  }

  private save(style: StaffStyle, label: string): void {
    void this.project.execute([{ type: 'setStaffStyle', style }], label);
  }

  protected patch(change: Partial<StaffStyle>): void {
    const style = this.selected();
    if (style && this.editable()) {
      this.save({ ...style, ...change }, 'Edit Staff Style');
    }
  }

  protected setStaff(index: number, change: Partial<Staff>): void {
    const style = this.selected();
    if (!style) {
      return;
    }
    const staves = normaliseStaves(style.staves.map((s, i) => (i === index ? { ...s, ...change } : s)));
    const percussion = staves.some(s => s.clef === 'percussion');
    this.patch({ staves, drumMap: percussion ? (style.drumMap ?? []) : null });
  }

  protected addStaff(): void {
    const style = this.selected();
    if (style) {
      this.patch({
        staves: normaliseStaves([...style.staves, { clef: 'bass', transpose: 0, splitPitch: null, voices: 2 }])
      });
    }
  }

  protected removeStaff(index: number): void {
    const style = this.selected();
    if (style && style.staves.length > 1) {
      this.patch({ staves: normaliseStaves(style.staves.filter((_, i) => i !== index)) });
    }
  }

  protected setDrum(index: number, change: Partial<DrumMapEntry>): void {
    const style = this.selected();
    if (style) {
      this.patch({ drumMap: (style.drumMap ?? []).map((d, i) => (i === index ? { ...d, ...change } : d)) });
    }
  }

  protected addDrum(): void {
    const style = this.selected();
    if (style) {
      this.patch({
        drumMap: [...(style.drumMap ?? []), { pitch: 38, staffPosition: 5, notehead: 'normal', name: 'Drum' }]
      });
    }
  }

  protected removeDrum(index: number): void {
    const style = this.selected();
    if (style) {
      this.patch({ drumMap: (style.drumMap ?? []).filter((_, i) => i !== index) });
    }
  }

  protected create(): void {
    const style = newStyle();
    this.save(style, 'New Staff Style');
    this.selectedId.set(style.id);
  }

  protected duplicate(): void {
    const style = this.selected();
    if (style) {
      const copy = duplicateStyle(style);
      this.save(copy, 'Duplicate Staff Style');
      this.selectedId.set(copy.id);
    }
  }

  protected remove(): void {
    const id = this.selectedId();
    if (id && this.editable()) {
      void this.project.execute([{ type: 'deleteStaffStyle', styleId: id }], 'Delete Staff Style');
      this.selectedId.set(this.factory()[0]?.id ?? null);
    }
  }

  protected assign(): void {
    const id = this.selectedId();
    const trackIds = this.targetTracks();
    if (id && trackIds.length) {
      void this.project.execute([{ type: 'assignStaffStyle', trackIds, styleId: id }], 'Assign Staff Style');
    }
  }
}
