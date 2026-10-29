import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { Articulation } from '@anode/api-client';
import { XuiMenuImports } from '@xui/menu';
import { parsePitch } from '../articulations/articulation-set';
import { formatLength, formatPosition, parseLength, parsePosition } from '../midi-edit/position';
import { noteName } from '../midi/midi-math';
import { PianoRollStore } from '../state/piano-roll.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import {
  articulationCommands,
  articulationGroups,
  articulationIdsOf,
  articulationName,
  articulationText,
  editNoteField,
  type ArticulationGroup,
  type NoteField
} from './articulation-edit';
import { noteCommands, notesLabel, placeNotes } from './note-edit';

/** The articulations of the Piano Roll track's set (empty without one). */
function useArticulations() {
  const store = inject(PianoRollStore);
  const project = inject(ProjectStore);
  return computed<readonly Articulation[]>(() => {
    const id = store.track()?.articulationSetId;
    return (id && project.articulationSets().get(id)?.articulations) || [];
  });
}

/** Lane chips narrower than this (CSS pixels) are drawn this wide. */
const MinChip = 18;

/**
 * The Piano Roll's articulation lane (docs/08-phase-7.md, "Articulation sets"): under the velocity lane, on the Piano
 * Roll's time scale, a chip per note start (a chord's notes share one) with its articulation's symbol or name. A chip is
 * a button: it selects its notes (or keeps the selection when they are in it) and opens a menu of the track's
 * articulations, which sets theirs (`editNotes` with `articulationId`, one undo step).
 */
@Component({
  selector: 'an-articulation-lane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiMenuImports],
  host: {
    class: 'bg-surface-sunken relative block min-w-0 overflow-hidden',
    role: 'group',
    'aria-label': 'Articulation lane',
    'data-testid': 'articulation-lane'
  },
  template: `
    @for (group of visible(); track group.start) {
      <button
        type="button"
        class="bezel text-foreground absolute top-0.5 bottom-0.5 truncate rounded-[3px] px-0.5 text-start text-[10px] leading-none font-semibold"
        data-testid="articulation-chip"
        [class.text-lit]="isSelected(group)"
        [style.left.px]="x(group.start)"
        [style.width.px]="width(group)"
        [attr.data-tick]="group.start"
        [attr.aria-label]="chipLabel(group)"
        [xuiMenuTriggerFor]="menu"
        (click)="choose(group)"
      >
        {{ text(group) }}
      </button>
    }
    <ng-template #menu>
      <xui-menu>
        <button
          xuiMenuItem
          type="button"
          role="menuitemradio"
          data-testid="articulation-choice"
          data-articulation="0"
          [attr.aria-checked]="targetIds().length === 1 && targetIds()[0] === 0"
          (click)="apply(0)"
        >
          None
        </button>
        @for (a of articulations(); track a.id) {
          <button
            xuiMenuItem
            type="button"
            role="menuitemradio"
            data-testid="articulation-choice"
            [attr.data-articulation]="a.id"
            [attr.aria-checked]="targetIds().length === 1 && targetIds()[0] === a.id"
            (click)="apply(a.id)"
          >
            {{ a.symbol ? a.symbol + ' ' : '' }}{{ a.name }}
          </button>
        }
      </xui-menu>
    </ng-template>
  `
})
export class ArticulationLane {
  private readonly store = inject(PianoRollStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  protected readonly articulations = useArticulations();

  private readonly groups = computed(() => articulationGroups(this.store.regions()));
  /** The groups from the left edge on (the lane clips the rest). */
  protected readonly visible = computed(() => {
    const origin = this.store.originTick();
    const ppt = this.store.pixelsPerTick();
    const end = origin + 8000 / ppt;
    return this.groups().filter(g => g.start + g.length >= origin && g.start <= end);
  });
  /** The notes the open menu sets. */
  private readonly target = signal<ReadonlySet<number>>(new Set());
  protected readonly targetIds = computed(() => {
    const target = this.target();
    return articulationIdsOf(this.groups().flatMap(g => g.notes.filter(n => target.has(n.id))));
  });

  protected x(tick: number): number {
    return Math.round((tick - this.store.originTick()) * this.store.pixelsPerTick());
  }

  protected width(group: ArticulationGroup): number {
    return Math.max(MinChip, Math.round(group.length * this.store.pixelsPerTick()) - 1);
  }

  protected text(group: ArticulationGroup): string {
    return articulationText(articulationIdsOf(group.notes), this.articulations());
  }

  protected chipLabel(group: ArticulationGroup): string {
    const at = formatPosition(group.start, this.project.meter());
    const notes = group.notes.map(n => noteName(n.pitch)).join(' ');
    return `Articulation of ${notes} at ${at}: ${articulationName(articulationIdsOf(group.notes), this.articulations())}`;
  }

  protected isSelected(group: ArticulationGroup): boolean {
    const selected = this.selection.notes();
    return group.notes.some(n => selected.has(n.id));
  }

  /** A chip clicked: its notes are the menu's target, the whole selection when they are in it. */
  protected choose(group: ArticulationGroup): void {
    const selected = this.selection.notes();
    if (group.notes.some(n => selected.has(n.id))) {
      this.target.set(new Set(selected));
    } else {
      const ids = group.notes.map(n => n.id);
      this.selection.selectNotes(ids);
      this.target.set(new Set(ids));
    }
  }

  protected apply(articulationId: number): void {
    const target = this.target();
    const commands = articulationCommands(this.store.regions(), target, articulationId);
    if (commands.length > 0) {
      void this.project.execute(commands, notesLabel('Articulation of', target.size));
    }
  }
}

const Field =
  'border-border bg-surface-inset text-foreground focus-visible:outline-focus readout h-6 min-w-0 rounded-[5px] border px-1 text-xs focus-visible:outline-2 disabled:opacity-50';

/**
 * The Piano Roll's note inspector (Logic's event inspector for the selected notes): position (bar beat division
 * tick), length, pitch (a name, C3 = 60, or a number), velocity and articulation. The fields show the first selected
 * note; a change moves every selected note by as much (position, pitch, velocity), or sets them all (length,
 * articulation), as one undo step.
 */
@Component({
  selector: 'an-note-inspector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'flex min-w-0 items-center gap-2 overflow-x-auto px-2 text-[11px]',
    role: 'group',
    'aria-label': 'Note inspector',
    'data-testid': 'note-inspector'
  },
  template: `
    <span class="text-foreground shrink-0 font-semibold" data-testid="note-inspector-count">{{ heading() }}</span>
    <label class="text-foreground-muted flex shrink-0 items-center gap-1">
      Position
      <input
        #positionField
        type="text"
        data-testid="note-position"
        aria-label="Note position"
        [class]="field + ' w-24'"
        [disabled]="!first()"
        [value]="position()"
        (change)="edit('position', positionField)"
        (keydown.enter)="positionField.blur()"
      />
    </label>
    <label class="text-foreground-muted flex shrink-0 items-center gap-1">
      Length
      <input
        #lengthField
        type="text"
        data-testid="note-length"
        aria-label="Note length"
        [class]="field + ' w-20'"
        [disabled]="!first()"
        [value]="length()"
        (change)="edit('length', lengthField)"
        (keydown.enter)="lengthField.blur()"
      />
    </label>
    <label class="text-foreground-muted flex shrink-0 items-center gap-1">
      Pitch
      <input
        #pitchField
        type="text"
        data-testid="note-pitch"
        aria-label="Note pitch"
        [class]="field + ' w-12'"
        [disabled]="!first()"
        [value]="pitch()"
        (change)="edit('pitch', pitchField)"
        (keydown.enter)="pitchField.blur()"
      />
    </label>
    <label class="text-foreground-muted flex shrink-0 items-center gap-1">
      Velocity
      <input
        #velocityField
        type="number"
        min="1"
        max="127"
        data-testid="note-velocity"
        aria-label="Note velocity"
        [class]="field + ' w-14'"
        [disabled]="!first()"
        [value]="first()?.velocity ?? ''"
        (change)="edit('velocity', velocityField)"
        (keydown.enter)="velocityField.blur()"
      />
    </label>
    <label class="text-foreground-muted flex shrink-0 items-center gap-1">
      Articulation
      <select
        data-testid="note-articulation"
        aria-label="Note articulation"
        [class]="field + ' w-32'"
        [disabled]="!first()"
        (change)="setArticulation($event)"
      >
        <option value="" disabled [hidden]="selectedIds().length < 2" [selected]="selectedIds().length > 1">
          Mixed
        </option>
        <option value="0" [selected]="selectedIds().length === 1 && selectedIds()[0] === 0">None</option>
        @for (a of articulations(); track a.id) {
          <option [value]="a.id" [selected]="selectedIds().length === 1 && selectedIds()[0] === a.id">
            {{ a.symbol ? a.symbol + ' ' : '' }}{{ a.name }}
          </option>
        }
      </select>
    </label>
  `
})
export class NoteInspector {
  private readonly store = inject(PianoRollStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  protected readonly articulations = useArticulations();
  protected readonly field = Field;

  private readonly notes = computed(() => placeNotes(this.store.regions()));
  private readonly selected = computed(() => {
    const ids = this.selection.notes();
    return this.notes().filter(n => ids.has(n.id));
  });
  protected readonly first = computed(
    () => [...this.selected()].sort((a, b) => a.start - b.start || a.pitch - b.pitch)[0] ?? null
  );
  protected readonly selectedIds = computed(() => {
    const ids = this.selection.notes();
    return articulationIdsOf(this.store.regions().flatMap(r => r.notes.filter(n => ids.has(n.id))));
  });
  protected readonly heading = computed(() => {
    const count = this.selected().length;
    return count === 0 ? 'No note selected' : count === 1 ? '1 note' : `${count} notes`;
  });
  protected readonly position = computed(() => {
    const note = this.first();
    return note ? formatPosition(note.start, this.project.meter()) : '';
  });
  protected readonly length = computed(() => {
    const note = this.first();
    return note ? formatLength(note.length, this.project.meter(), note.start) : '';
  });
  protected readonly pitch = computed(() => {
    const note = this.first();
    return note ? noteName(note.pitch) : '';
  });

  /** A field committed: parsed, applied to the selection, or put back when it does not parse. */
  protected edit(field: NoteField, input: HTMLInputElement): void {
    const first = this.first();
    if (!first) {
      return;
    }
    const meter = this.project.meter();
    const text = input.value;
    const value =
      field === 'position'
        ? parsePosition(text, meter)
        : field === 'length'
          ? parseLength(text, meter, first.start)
          : field === 'pitch'
            ? parsePitch(text)
            : text.trim()
              ? Number(text)
              : null;
    const edited = value === null || Number.isNaN(value) ? [] : editNoteField(this.selected(), field, value);
    if (edited.length === 0) {
      input.value =
        field === 'position'
          ? this.position()
          : field === 'length'
            ? this.length()
            : field === 'pitch'
              ? this.pitch()
              : String(first.velocity);
      return;
    }
    const original = new Map(this.notes().map(n => [n.id, n]));
    const regions = new Map(this.store.regions().map(r => [r.id, r]));
    const verb = { position: 'Move', length: 'Resize', pitch: 'Transpose', velocity: 'Velocity of' }[field];
    void this.project.execute(noteCommands(edited, original, regions), notesLabel(verb, edited.length));
  }

  protected setArticulation(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    if (value === '') {
      return;
    }
    const ids = new Set(this.selected().map(n => n.id));
    const commands = articulationCommands(this.store.regions(), ids, Number(value));
    if (commands.length > 0) {
      void this.project.execute(commands, notesLabel('Articulation of', ids.size));
    }
  }
}
