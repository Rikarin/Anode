import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { GrooveTemplate } from '@anode/api-client';
import { XuiButtonImports } from '@xui/button';
import { XuiInputImports } from '@xui/input';
import { FloatingWindow } from '../shell/floating-window';
import { MidiLibraryStore } from '../state/midi-library.store';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';

/** The grid a template is made on. */
export const GrooveGrids = [
  { ticks: 480, label: '1/8 Note' },
  { ticks: 240, label: '1/16 Note' },
  { ticks: 120, label: '1/32 Note' }
] as const;

const Field =
  'border-border bg-surface-inset text-foreground focus-visible:outline-focus h-6 min-w-0 rounded-[5px] border px-1 text-xs focus-visible:outline-2';

const Band =
  'text-foreground border-hairline bg-surface-raised border-y px-3 py-1 text-[11px] font-bold shadow-[inset_0_1px_0_var(--highlight)]';

/** "2 bars of 1/16 notes". */
export function grooveDetails(groove: Pick<GrooveTemplate, 'lengthTicks' | 'stepTicks' | 'points'>): string {
  const grid = GrooveGrids.find(g => g.ticks === groove.stepTicks)?.label ?? `${groove.stepTicks} ticks`;
  return `${groove.points.length} steps of ${grid.replace(' Note', '')}`;
}

/**
 * The Grooves window (docs/08-phase-7.md, "Groove templates"): Make Groove Template from a MIDI region (its notes) or
 * an audio region (its transients), with a name, a length of 1–4 bars and a grid (`makeGroove`); the project's grooves,
 * each saved to the user library or deleted; and the library's grooves, added to the project (`setGroove`). Opened from
 * a region's menu or the Region inspector.
 */
@Component({
  selector: 'an-groove-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow, XuiButtonImports, XuiInputImports],
  template: `
    <an-floating-window title="Grooves" testId="groove-window" width="26rem" (closed)="ui.grooves.set(null)">
      @if (region(); as r) {
        <h3 [class]="band">Make Groove Template</h3>
        <form
          class="grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-1.5 p-3 text-xs"
          aria-label="Make Groove Template"
          (submit)="$event.preventDefault(); make()"
        >
          <span class="text-foreground-muted">From</span>
          <span class="text-foreground truncate font-semibold" data-testid="groove-source"
            >{{ r.name }} ({{ r.kind === 'audio' ? 'transients' : 'notes' }})</span
          >
          <label class="text-foreground-muted" for="groove-name">Name</label>
          <input
            #nameInput
            id="groove-name"
            xuiInput
            class="h-6 text-xs"
            data-testid="groove-name"
            [value]="name()"
            [placeholder]="r.name"
            (input)="name.set(nameInput.value)"
          />
          <label class="text-foreground-muted" for="groove-bars">Length</label>
          <select id="groove-bars" [class]="field" data-testid="groove-bars" (change)="bars.set(+select($event))">
            @for (n of barChoices; track n) {
              <option [value]="n" [selected]="n === bars()">{{ n === 1 ? '1 bar' : n + ' bars' }}</option>
            }
          </select>
          <label class="text-foreground-muted" for="groove-grid">Grid</label>
          <select id="groove-grid" [class]="field" data-testid="groove-grid" (change)="grid.set(+select($event))">
            @for (g of grids; track g.ticks) {
              <option [value]="g.ticks" [selected]="g.ticks === grid()">{{ g.label }}</option>
            }
          </select>
          <span></span>
          <button xuiButton type="submit" size="sm" class="justify-self-start" data-testid="groove-make">
            Make Groove Template
          </button>
        </form>
      }
      <h3 [class]="band">Project Grooves</h3>
      <ul class="flex flex-col py-1" aria-label="Project grooves" data-testid="project-grooves">
        @for (groove of project.grooveList(); track groove.id) {
          <li
            class="flex h-7 items-center gap-2 px-3 text-xs"
            data-testid="project-groove"
            [attr.data-groove-id]="groove.id"
          >
            <span class="text-foreground min-w-0 flex-1 truncate font-semibold">{{ groove.name }}</span>
            <span class="text-foreground-muted readout shrink-0">{{ details(groove) }}</span>
            <button
              xuiButton
              type="button"
              size="sm"
              variant="ghost"
              data-testid="groove-save-library"
              [attr.aria-label]="'Save ' + groove.name + ' to the library'"
              (click)="saveToLibrary(groove)"
            >
              Save to Library
            </button>
            <button
              xuiButton
              type="button"
              size="sm"
              variant="ghost"
              color="error"
              data-testid="groove-delete"
              [attr.aria-label]="'Delete ' + groove.name"
              (click)="remove(groove)"
            >
              Delete
            </button>
          </li>
        } @empty {
          <li class="text-foreground-muted px-3 py-1 text-xs">
            No grooves yet: make one from a region with a groove you like.
          </li>
        }
      </ul>
      <h3 [class]="band">Library</h3>
      <ul class="flex flex-col py-1" aria-label="Library grooves" data-testid="library-grooves">
        @for (groove of library.grooves(); track groove.id) {
          <li
            class="flex h-7 items-center gap-2 px-3 text-xs"
            data-testid="library-groove"
            [attr.data-groove-id]="groove.id"
          >
            <span class="text-foreground min-w-0 flex-1 truncate font-semibold">{{ groove.name }}</span>
            <span class="text-foreground-muted readout shrink-0">{{ details(groove) }}</span>
            <button
              xuiButton
              type="button"
              size="sm"
              variant="ghost"
              data-testid="groove-add-library"
              [attr.aria-label]="'Add ' + groove.name + ' to the project'"
              (click)="addFromLibrary(groove)"
            >
              {{ project.grooves().has(groove.id) ? 'Update in Project' : 'Add to Project' }}
            </button>
          </li>
        } @empty {
          <li class="text-foreground-muted px-3 py-1 text-xs">The library has no grooves.</li>
        }
      </ul>
      <p class="text-foreground-muted min-h-6 px-3 pb-2 text-xs" role="status" data-testid="groove-status">
        {{ status() }}
      </p>
    </an-floating-window>
  `
})
export class GrooveWindow {
  protected readonly ui = inject(UiStore);
  protected readonly project = inject(ProjectStore);
  protected readonly library = inject(MidiLibraryStore);

  protected readonly band = Band;
  protected readonly field = Field;
  protected readonly grids = GrooveGrids;
  protected readonly barChoices = [1, 2, 3, 4];
  protected readonly details = grooveDetails;

  protected readonly name = signal('');
  protected readonly bars = signal(1);
  protected readonly grid = signal(240);
  protected readonly status = signal('');

  /** The region a template is made from: a MIDI or audio region of the project. */
  protected readonly region = computed(() => {
    const id = this.ui.grooves()?.regionId;
    if (!id) {
      return null;
    }
    const midi = this.project.midiRegions().get(id);
    if (midi) {
      return { id, name: midi.name, kind: 'midi' as const };
    }
    const audio = this.project.regions().get(id);
    return audio ? { id, name: audio.name, kind: 'audio' as const } : null;
  });

  constructor() {
    void this.library.loadGrooves();
  }

  protected select(event: Event): string {
    return (event.target as HTMLSelectElement).value;
  }

  /** Makes a template from the region (one undo step). */
  async make(): Promise<void> {
    const region = this.region();
    if (!region) {
      return;
    }
    const name = this.name().trim();
    const before = new Set(this.project.grooves().keys());
    const result = await this.project.execute(
      [
        {
          type: 'makeGroove',
          regionId: region.id,
          ...(name ? { name } : {}),
          lengthBars: this.bars(),
          stepTicks: this.grid()
        }
      ],
      'Make Groove Template'
    );
    if (result) {
      const made = [...this.project.grooves().values()].find(g => !before.has(g.id));
      this.status.set(`Made “${made?.name ?? (name || region.name)}”.`);
      this.name.set('');
    }
  }

  async saveToLibrary(groove: GrooveTemplate): Promise<void> {
    if (await this.library.saveGroove(groove)) {
      this.status.set(`Saved “${groove.name}” to the library.`);
    }
  }

  async remove(groove: GrooveTemplate): Promise<void> {
    if (await this.project.execute([{ type: 'deleteGroove', grooveId: groove.id }], 'Delete Groove')) {
      this.status.set(`Deleted “${groove.name}”.`);
    }
  }

  /** Adds a library groove to the project (or replaces the project's copy). */
  async addFromLibrary(groove: GrooveTemplate): Promise<void> {
    if (await this.project.execute([{ type: 'setGroove', groove: structuredClone(groove) }], 'Add Groove')) {
      this.status.set(`Added “${groove.name}” to the project.`);
    }
  }
}
