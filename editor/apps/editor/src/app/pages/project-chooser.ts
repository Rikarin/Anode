import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { provideIcons } from '@ng-icons/core';
import { matFolderOpenRound, matHistoryRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiSegmentedControlImports, type XuiSegmentedOption } from '@xui/segmented-control';
import { ConnectionService } from '../core/connection';
import { ProjectSession } from '../state/project-session';

/** Sample rates a new project can have (one per project; no conversion on import in Phase 1). */
const SampleRates: XuiSegmentedOption<number>[] = [
  { value: 44_100, label: '44.1 kHz' },
  { value: 48_000, label: '48 kHz' },
  { value: 96_000, label: '96 kHz' }
];

/**
 * The project chooser (Logic's Project Chooser, docs/01-implementation-plan.md §6.1 `projects`): a new project (name,
 * folder, sample rate), the recent ones, or one opened by its folder. The desktop app picks folders with its native
 * dialog; a browser types the path on the Server's disk.
 */
@Component({
  selector: 'an-project-chooser',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiIconImports, XuiInputImports, XuiSegmentedControlImports],
  providers: [provideIcons({ matFolderOpenRound, matHistoryRound })],
  host: { class: 'flex h-full items-start justify-center overflow-auto p-8', role: 'region', 'aria-label': 'Projects' },
  template: `
    <div class="grid w-full max-w-4xl gap-6 md:grid-cols-2">
      <section class="bg-surface border-border flex flex-col gap-4 rounded-lg border p-5" aria-labelledby="new-project">
        <h2 id="new-project" class="text-base font-semibold">New project</h2>
        <form class="flex flex-col gap-3" (submit)="$event.preventDefault(); create()">
          <label class="flex flex-col gap-1 text-sm">
            Name
            <input
              #nameInput
              xuiInput
              data-testid="new-project-name"
              autocomplete="off"
              [value]="name()"
              (input)="name.set(nameInput.value)"
            />
          </label>
          <div class="flex flex-col gap-1 text-sm">
            <span id="new-project-folder">Folder</span>
            <div class="flex gap-2">
              <input
                #folderInput
                xuiInput
                class="flex-1"
                aria-labelledby="new-project-folder"
                data-testid="new-project-folder"
                autocomplete="off"
                placeholder="The Server's projects folder"
                [value]="folder()"
                (input)="folder.set(folderInput.value)"
              />
              @if (dialogs) {
                <button xuiButton type="button" variant="outline" (click)="chooseFolder()">Choose…</button>
              }
            </div>
          </div>
          <label class="flex flex-col gap-1 text-sm">
            Sample rate
            <xui-segmented-control
              size="sm"
              ariaLabel="Sample rate"
              [options]="rates"
              [value]="sampleRate()"
              (valueChange)="sampleRate.set($event ?? 48000)"
            />
          </label>
          <button
            xuiButton
            type="submit"
            data-testid="create-project"
            [disabled]="!name().trim()"
            [loading]="session.busy() !== null"
          >
            Create
          </button>
        </form>
      </section>
      <section class="flex flex-col gap-4" aria-labelledby="open-project">
        <div class="bg-surface border-border flex flex-col gap-3 rounded-lg border p-5">
          <h2 id="open-project" class="text-base font-semibold">Open</h2>
          @if (dialogs) {
            <button xuiButton type="button" variant="outline" (click)="openWithDialog()">
              <ng-icon xui name="matFolderOpenRound" size="sm" />
              Open project…
            </button>
          }
          <form class="flex gap-2" (submit)="$event.preventDefault(); session.open(path())">
            <input
              #pathInput
              xuiInput
              class="flex-1"
              aria-label="Project folder path"
              data-testid="open-project-path"
              autocomplete="off"
              placeholder="/path/to/Song.anode"
              [value]="path()"
              (input)="path.set(pathInput.value)"
            />
            <button xuiButton type="submit" variant="outline" [disabled]="!path().trim()">Open</button>
          </form>
        </div>
        <div class="bg-surface border-border flex flex-col gap-2 rounded-lg border p-5">
          <h2 class="flex items-center gap-2 text-base font-semibold">
            <ng-icon xui name="matHistoryRound" size="sm" />
            Recent
          </h2>
          @for (project of session.recent(); track project.path) {
            <button
              type="button"
              class="hover:bg-surface-raised focus-visible:outline-focus flex flex-col items-start rounded px-2 py-1.5 text-start focus-visible:outline-2"
              [attr.data-testid]="'recent-' + project.name"
              (click)="session.open(project.path)"
            >
              <span class="text-sm font-medium">{{ project.name }}</span>
              <span class="text-foreground-muted w-full truncate text-xs">{{ project.path }}</span>
            </button>
          } @empty {
            <p class="text-foreground-muted text-sm">No recent projects.</p>
          }
        </div>
      </section>
    </div>
  `
})
export class ProjectChooser {
  protected readonly session = inject(ProjectSession);
  protected readonly dialogs = inject(ConnectionService).desktop?.dialogs ?? null;

  protected readonly rates = SampleRates;
  protected readonly name = signal('Untitled');
  protected readonly folder = signal('');
  protected readonly sampleRate = signal(48_000);
  protected readonly path = signal('');

  constructor() {
    void this.session.refreshRecent();
  }

  protected async create(): Promise<void> {
    const name = this.name().trim();
    if (name) {
      await this.session.create({ name, directory: this.folder().trim() || null, sampleRate: this.sampleRate() });
    }
  }

  protected async chooseFolder(): Promise<void> {
    const folder = await this.dialogs?.chooseFolder();
    if (folder) {
      this.folder.set(folder);
    }
  }

  protected async openWithDialog(): Promise<void> {
    const path = await this.dialogs?.openProject();
    if (path) {
      await this.session.open(path);
    }
  }
}
