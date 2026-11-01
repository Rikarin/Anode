import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { MidiRegion, RegionNotation, Track } from '@anode/api-client';
import { Field, selectValue } from '../control/styles';
import { StaffStyleLibrary } from '../score/staff-style-library';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';

/** Display quantise grids (ticks; 0: the staff style's). */
export const NotationGrids: readonly { ticks: number; label: string }[] = [
  { ticks: 0, label: 'Staff style' },
  { ticks: 3840, label: '1/1' },
  { ticks: 1920, label: '1/2' },
  { ticks: 960, label: '1/4' },
  { ticks: 480, label: '1/8' },
  { ticks: 240, label: '1/16' },
  { ticks: 120, label: '1/32' },
  { ticks: 60, label: '1/64' }
];

/** Triplet grids (ticks; 0: none). */
export const NotationTripletGrids: readonly { ticks: number; label: string }[] = [
  { ticks: 0, label: 'None' },
  { ticks: 640, label: '1/6' },
  { ticks: 320, label: '1/12' },
  { ticks: 160, label: '1/24' },
  { ticks: 80, label: '1/48' }
];

type NotationFlag = 'interpretation' | 'noOverlap' | 'syncopation';

const FlagLabels: Record<NotationFlag, string> = {
  interpretation: 'Interpretation',
  noOverlap: 'No Overlap',
  syncopation: 'Syncopation'
};

/** The `setRegionNotation` for a change, on every selected MIDI region (as the inspector edits a selection). */
export function regionNotationCommand(regionIds: readonly string[], change: Partial<RegionNotation>) {
  return { type: 'setRegionNotation' as const, regionIds: [...regionIds], ...change };
}

/**
 * The Region inspector's score settings (Logic's, docs/10-phase-9.md): display quantise (a straight grid, or the staff
 * style's; a triplet grid), interpretation, no overlap, syncopation and the most dots. They move where notes are
 * drawn, never the notes; a change applies to every selected MIDI region.
 */
@Component({
  selector: 'an-region-notation',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex flex-col gap-2', role: 'group', 'aria-label': 'Score settings' },
  template: `
    <h4 class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">Score</h4>
    <div class="grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-1.5 text-xs">
      <label class="text-foreground-muted" for="an-notation-quantize">Quantize</label>
      <select
        id="an-notation-quantize"
        [class]="field"
        data-testid="region-display-quantize"
        (change)="set({ displayQuantizeTicks: +value($event) })"
      >
        @for (grid of grids; track grid.ticks) {
          <option [value]="grid.ticks" [selected]="grid.ticks === notation().displayQuantizeTicks">
            {{ grid.label }}
          </option>
        }
      </select>
      <label class="text-foreground-muted" for="an-notation-triplets">Triplets</label>
      <select
        id="an-notation-triplets"
        [class]="field"
        data-testid="region-triplet-quantize"
        (change)="set({ tripletQuantizeTicks: +value($event) })"
      >
        @for (grid of tripletGrids; track grid.ticks) {
          <option [value]="grid.ticks" [selected]="grid.ticks === notation().tripletQuantizeTicks">
            {{ grid.label }}
          </option>
        }
      </select>
      @for (flag of flags; track flag) {
        <span class="text-foreground-muted">{{ flagLabels[flag] }}</span>
        <label class="text-foreground flex items-center gap-2">
          <input
            type="checkbox"
            class="accent-(--color-primary)"
            [attr.data-testid]="'region-' + flag"
            [attr.aria-label]="flagLabels[flag]"
            [checked]="notation()[flag]"
            (change)="toggle(flag)"
          />
        </label>
      }
      <label class="text-foreground-muted" for="an-notation-dots">Max Dots</label>
      <select
        id="an-notation-dots"
        [class]="field"
        data-testid="region-max-dots"
        (change)="set({ maxDots: +value($event) })"
      >
        @for (dots of [0, 1, 2, 3]; track dots) {
          <option [value]="dots" [selected]="dots === notation().maxDots">{{ dots }}</option>
        }
      </select>
    </div>
  `
})
export class RegionNotationInspector {
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  readonly region = input.required<MidiRegion>();

  protected readonly field = Field;
  protected readonly value = selectValue;
  protected readonly grids = NotationGrids;
  protected readonly tripletGrids = NotationTripletGrids;
  protected readonly flags: readonly NotationFlag[] = ['interpretation', 'noOverlap', 'syncopation'];
  protected readonly flagLabels = FlagLabels;
  protected readonly notation = computed(() => this.region().notation);

  protected toggle(flag: NotationFlag): void {
    this.set({ [flag]: !this.notation()[flag] });
  }

  protected set(change: Partial<RegionNotation>): void {
    const midi = this.project.midiRegions();
    const selected = [...this.selection.regions()].filter(id => midi.has(id));
    const ids = selected.includes(this.region().id) ? selected : [this.region().id];
    void this.project.execute([regionNotationCommand(ids, change)], 'Score Settings');
  }
}

/** A track's staff style in the Track inspector, and the way to the Staff Styles window. */
@Component({
  selector: 'an-track-staff-style',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-1.5 text-xs' },
  template: `
    <label class="text-foreground-muted" for="an-track-staff-style">Staff Style</label>
    <div class="flex gap-1">
      <select
        id="an-track-staff-style"
        [class]="field"
        data-testid="track-staff-style"
        (change)="assign(value($event))"
      >
        @for (style of library.all(); track style.id) {
          <option [value]="style.id" [selected]="style.id === current()">{{ style.name }}</option>
        }
      </select>
      <button
        type="button"
        class="bezel text-foreground h-6 shrink-0 px-2 text-[11px] font-semibold"
        aria-label="Staff Styles…"
        (click)="ui.staffStylesOpen.set(true)"
      >
        …
      </button>
    </div>
  `
})
export class TrackStaffStyle {
  protected readonly library = inject(StaffStyleLibrary);
  protected readonly ui = inject(UiStore);
  private readonly project = inject(ProjectStore);
  readonly track = input.required<Track>();

  protected readonly field = Field;
  protected readonly value = selectValue;
  protected readonly current = computed(() => this.library.styleOf(this.track().staffStyleId)?.id ?? null);

  constructor() {
    void this.library.load();
  }

  protected assign(styleId: string): void {
    if (styleId && styleId !== this.current()) {
      void this.project.execute(
        [{ type: 'assignStaffStyle', trackIds: [this.track().id], styleId }],
        'Assign Staff Style'
      );
    }
  }
}
