import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { ProjectContents, ProjectTrackInfo } from '@anode/api-client';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiInputImports } from '@xui/input';
import { ConnectionService } from '../core/connection';
import { ProjectSession } from '../state/project-session';
import { UiStore } from '../state/ui.store';

type Part = 'regions' | 'inserts' | 'sends' | 'automation';

const Parts: readonly { part: Part; label: string }[] = [
  { part: 'regions', label: 'Regions' },
  { part: 'inserts', label: 'Inserts' },
  { part: 'sends', label: 'Sends and auxes' },
  { part: 'automation', label: 'Automation' }
];

/**
 * File ▸ Import ▸ From Project… (Logic's Import from Project, Cubase's Track Import): a project on the Server's disk is
 * read without opening it, and the chosen tracks join this one as one undo step. The desktop app picks the project with
 * its native dialog; a browser types the path; recent projects are one click away.
 */
@Component({
  selector: 'an-import-project',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiInputImports],
  template: `
    <xui-dialog [(open)]="ui.importProjectOpen" title="Import from Project" size="lg">
      <xui-dialog-body class="flex max-h-[60vh] flex-col gap-3 overflow-auto">
        <form class="flex gap-2" (submit)="$event.preventDefault(); read(path.value)">
          <input
            #path
            xuiInput
            class="flex-1"
            aria-label="Project folder path"
            autocomplete="off"
            data-testid="import-project-path"
            placeholder="/path/to/Song.anode"
            [value]="chosenPath()"
          />
          @if (dialogs) {
            <button xuiButton type="button" variant="outline" (click)="choose()">Choose…</button>
          }
          <button xuiButton type="submit" variant="outline" data-testid="import-project-read" [loading]="reading()">
            Read
          </button>
        </form>
        @if (contents(); as project) {
          <p class="text-foreground-muted text-xs">
            {{ project.name }}: {{ project.tracks.length }} tracks at {{ project.sampleRate }} Hz,
            {{ project.tempo }} BPM.
            @if (converts()) {
              Its audio is converted to this project's rate.
            }
            Regions keep their bars, so a different tempo moves their audio against the grid.
          </p>
          <ul class="flex flex-col" aria-label="Tracks of the project">
            @for (track of project.tracks; track track.id) {
              <li class="odd:bg-surface-inset flex items-center gap-2 rounded-sm px-2 py-1 text-sm">
                <label class="flex min-w-0 flex-1 items-center gap-2" [class.pl-4]="track.parentId">
                  <input
                    type="checkbox"
                    [attr.data-testid]="'import-track-' + track.name"
                    [checked]="selected().has(track.id)"
                    (change)="toggle(track)"
                  />
                  <span class="truncate">{{ track.name }}</span>
                  <span class="text-foreground-muted text-xs">{{ track.kind }}</span>
                </label>
                <span class="text-foreground-muted readout text-xs">{{ summary(track) }}</span>
              </li>
            }
          </ul>
          <fieldset class="flex flex-wrap gap-4">
            <legend class="text-foreground-muted mb-1 text-xs">Bring along</legend>
            @for (entry of parts; track entry.part) {
              <label class="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  [attr.data-testid]="'import-part-' + entry.part"
                  [checked]="brought()[entry.part]"
                  (change)="flip(entry.part)"
                />
                {{ entry.label }}
              </label>
            }
          </fieldset>
        } @else {
          @for (project of session.recent(); track project.path) {
            <button
              type="button"
              class="hover:bg-hover-overlay focus-visible:outline-focus flex flex-col items-start rounded-md px-2 py-1 text-start focus-visible:outline-2"
              [attr.data-testid]="'import-recent-' + project.name"
              (click)="read(project.path)"
            >
              <span class="text-sm font-medium">{{ project.name }}</span>
              <span class="text-foreground-muted w-full truncate text-xs">{{ project.path }}</span>
            </button>
          }
        }
      </xui-dialog-body>
      <xui-dialog-footer>
        <button xuiButton type="button" variant="ghost" (click)="ui.importProjectOpen.set(false)">Cancel</button>
        <button
          xuiButton
          type="button"
          data-testid="import-project-confirm"
          [disabled]="selected().size === 0"
          [loading]="importing()"
          (click)="import()"
        >
          Import
        </button>
      </xui-dialog-footer>
    </xui-dialog>
  `
})
export class ImportProjectDialog {
  protected readonly ui = inject(UiStore);
  protected readonly session = inject(ProjectSession);
  protected readonly dialogs = inject(ConnectionService).desktop?.dialogs ?? null;

  protected readonly parts = Parts;
  protected readonly chosenPath = signal('');
  protected readonly contents = signal<ProjectContents | null>(null);
  protected readonly selected = signal<ReadonlySet<string>>(new Set());
  protected readonly brought = signal<Record<Part, boolean>>({
    regions: true,
    inserts: true,
    sends: true,
    automation: true
  });
  protected readonly reading = signal(false);
  protected readonly importing = signal(false);
  protected readonly converts = computed(() => {
    const contents = this.contents();
    return contents !== null && contents.sampleRate !== this.session.sampleRate();
  });

  constructor() {
    void this.session.refreshRecent();
  }

  protected summary(track: ProjectTrackInfo): string {
    return `${track.regions} regions, ${track.inserts} inserts, ${track.sends} sends, ${track.lanes} lanes`;
  }

  protected async choose(): Promise<void> {
    const path = await this.dialogs?.openProject();
    if (path) {
      await this.read(path);
    }
  }

  protected async read(path: string): Promise<void> {
    this.chosenPath.set(path);
    this.reading.set(true);
    try {
      this.contents.set(await this.session.projectContents(path));
      this.selected.set(new Set());
    } finally {
      this.reading.set(false);
    }
  }

  protected toggle(track: ProjectTrackInfo): void {
    this.selected.update(selected => {
      const next = new Set(selected);
      if (!next.delete(track.id)) {
        next.add(track.id);
      }

      return next;
    });
  }

  protected flip(part: Part): void {
    this.brought.update(parts => ({ ...parts, [part]: !parts[part] }));
  }

  protected async import(): Promise<void> {
    const contents = this.contents();
    if (!contents) {
      return;
    }

    this.importing.set(true);
    try {
      const done = await this.session.importTracks({
        path: contents.path,
        trackIds: [...this.selected()],
        ...this.brought()
      });
      if (done) {
        this.ui.importProjectOpen.set(false);
      }
    } finally {
      this.importing.set(false);
    }
  }
}
