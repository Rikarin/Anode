import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import type { ProjectTemplate } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import { matFolderOpenRound, matHistoryRound, matRestoreRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiSegmentedControlImports, type XuiSegmentedOption } from '@xui/segmented-control';
import { ConnectionService } from '../core/connection';
import { ProjectSession } from '../state/project-session';
import { TemplatesStore } from '../state/templates.store';

/** Sample rates a new project can have (one per project; no conversion on import in Phase 1). */
const SampleRates: XuiSegmentedOption<number>[] = [
  { value: 44_100, label: '44.1 kHz' },
  { value: 48_000, label: '48 kHz' },
  { value: 96_000, label: '96 kHz' }
];

/**
 * The project chooser (Logic's Project Chooser, docs/01-implementation-plan.md §6.1 `projects`): a new project (name,
 * folder, template, sample rate), the recent ones, or one opened by its folder, and first of all any edits a crash left unsaved. The desktop app picks folders with its native
 * dialog; a browser types the path on the Server's disk.
 */
@Component({
  selector: 'an-project-chooser',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiIconImports, XuiInputImports, XuiSegmentedControlImports],
  providers: [provideIcons({ matFolderOpenRound, matHistoryRound, matRestoreRound })],
  host: {
    class: 'flex h-full items-start justify-center overflow-auto p-8 pt-16',
    role: 'region',
    'aria-label': 'Projects'
  },
  template: `
    <div class="grid w-full max-w-4xl gap-6 md:grid-cols-2">
      @if (session.recoverable().length > 0) {
        <section
          class="panel flex flex-col gap-3 p-5 md:col-span-2"
          aria-labelledby="recover-projects"
          data-testid="recovery"
        >
          <div class="flex flex-col gap-1">
            <h2 id="recover-projects" class="flex items-center gap-2 text-[15px] font-semibold">
              <ng-icon xui name="matRestoreRound" size="sm" />
              Recover unsaved edits
            </h2>
            <p class="text-foreground-muted text-sm">
              Anode stopped before these edits were saved. Discarded edits stay under Revert to Backup.
            </p>
          </div>
          @for (project of session.recoverable(); track project.path) {
            <div class="flex items-center gap-3">
              <div class="flex min-w-0 flex-1 flex-col">
                <span class="text-sm font-medium">{{ project.name }}</span>
                <span class="text-foreground-muted truncate text-xs" [attr.title]="project.path">
                  Autosaved {{ when(project.autosaved) }}
                </span>
              </div>
              <button
                xuiButton
                type="button"
                variant="outline"
                [attr.data-testid]="'discard-recovery-' + project.name"
                [attr.aria-label]="'Discard the autosaved edits of ' + project.name"
                (click)="session.discardRecovery(project.path)"
              >
                Discard
              </button>
              <button
                xuiButton
                type="button"
                [attr.data-testid]="'recover-' + project.name"
                [attr.aria-label]="'Recover ' + project.name"
                [loading]="session.busy() !== null"
                (click)="session.recover(project.path)"
              >
                Recover
              </button>
            </div>
          }
        </section>
      }
      <section class="panel flex flex-col gap-4 p-5" aria-labelledby="new-project">
        <h2 id="new-project" class="text-[15px] font-semibold">New project</h2>
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
          <div class="flex flex-col gap-1 text-sm">
            <span id="new-project-template">Template</span>
            <div
              class="border-hairline flex max-h-48 flex-col overflow-auto rounded-md border p-1"
              role="group"
              aria-labelledby="new-project-template"
              data-testid="project-templates"
            >
              @for (group of templates.projectCategories(); track group.category) {
                <span class="text-foreground-muted px-2 pt-1 text-[10px] font-semibold tracking-wide uppercase">
                  {{ group.category }}
                </span>
                @for (template of group.templates; track template.id) {
                  <div class="flex items-center">
                    <button
                      type="button"
                      class="hover:bg-hover-overlay focus-visible:outline-focus aria-pressed:bg-surface-raised flex min-w-0 flex-1 flex-col items-start rounded-sm px-2 py-1 text-start focus-visible:outline-2"
                      [attr.aria-pressed]="chosen()?.id === template.id"
                      [attr.data-testid]="'template-' + template.name"
                      [attr.title]="template.description || template.name"
                      (click)="choose(template)"
                    >
                      <span class="text-sm">{{ template.name }}</span>
                    </button>
                    @if (!template.factory) {
                      <button
                        xuiButton
                        type="button"
                        variant="ghost"
                        size="sm"
                        [attr.aria-label]="'Delete the template ' + template.name"
                        (click)="remove(template)"
                      >
                        Delete
                      </button>
                    }
                  </div>
                }
              }
            </div>
          </div>
          @if (chosen()?.hasAudio) {
            <p class="text-foreground-muted text-xs">
              This template holds audio at {{ chosen()?.sampleRate }} Hz, so the project starts at that rate.
            </p>
          } @else {
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
          }
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
        <div class="panel flex flex-col gap-3 p-5">
          <h2 id="open-project" class="text-[15px] font-semibold">Open</h2>
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
        <div class="panel flex flex-col gap-2 p-5">
          <h2 class="flex items-center gap-2 text-[15px] font-semibold">
            <ng-icon xui name="matHistoryRound" size="sm" />
            Recent
          </h2>
          @for (project of session.recent(); track project.path) {
            <button
              type="button"
              class="hover:bg-hover-overlay focus-visible:outline-focus -mx-2 flex flex-col items-start rounded-md px-2 py-1.5 text-start transition-colors duration-100 focus-visible:outline-2"
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
  protected readonly templates = inject(TemplatesStore);
  protected readonly dialogs = inject(ConnectionService).desktop?.dialogs ?? null;

  protected readonly rates = SampleRates;
  protected readonly name = signal('Untitled');
  protected readonly folder = signal('');
  protected readonly sampleRate = signal(48_000);
  protected readonly path = signal('');
  /** The template a new project starts from; none: an empty project. */
  protected readonly chosen = signal<ProjectTemplate | null>(null);

  constructor() {
    void this.templates.refresh();
    void this.session.refreshRecent();
    void this.session.refreshRecoverable();
  }

  protected when(iso: string): string {
    return new Date(iso).toLocaleString();
  }

  protected async create(): Promise<void> {
    const name = this.name().trim();
    if (name) {
      const template = this.chosen();
      await this.session.create({
        name,
        directory: this.folder().trim() || null,
        ...(template?.hasAudio ? {} : { sampleRate: this.sampleRate() }),
        template: template?.id ?? null
      });
    }
  }

  protected choose(template: ProjectTemplate): void {
    this.chosen.set(template);
  }

  protected async remove(template: ProjectTemplate): Promise<void> {
    if (this.chosen()?.id === template.id) {
      this.chosen.set(null);
    }

    await this.templates.deleteProject(template.id);
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
