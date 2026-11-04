import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { OfflineFile } from '@anode/api-client';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiInputImports } from '@xui/input';
import { ConnectionService } from '../core/connection';
import { OfflineFilesStore } from '../state/offline-files.store';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';

/**
 * Missing Files (Logic's Project Audio ▸ Locate): the project's offline files, with what uses them. It opens with a
 * project that lacks any. Search looks in a folder by name, Locate takes one file; either way the Server refuses what
 * does not match. Closing leaves them offline: their regions stay, silent, and saving keeps them.
 */
@Component({
  selector: 'an-missing-files',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiInputImports],
  template: `
    <xui-dialog [(open)]="ui.missingFilesOpen" title="Missing Files" size="lg">
      <xui-dialog-body class="flex flex-col gap-3 text-xs">
        @if (files().length === 0 && unreadable().length === 0) {
          <p class="text-foreground-muted" data-testid="no-missing-files">Every file of this project is in place.</p>
        }
        @if (files().length > 0) {
          <p class="text-foreground-muted">
            Their regions stay in the project and play silence. Saving keeps them, so you can relink them later.
          </p>
          <form class="flex items-center gap-2" (submit)="$event.preventDefault(); search(folder.value)">
            <label class="text-foreground-muted shrink-0" for="an-missing-folder">Search in</label>
            <input
              #folder
              id="an-missing-folder"
              xuiInput
              class="min-w-0 flex-1"
              data-testid="missing-folder"
              placeholder="The project's folder"
              [value]="directory()"
            />
            @if (chooseFolder) {
              <button xuiButton type="button" variant="ghost" (click)="choose()">Choose…</button>
            }
            <button
              xuiButton
              type="submit"
              variant="outline"
              data-testid="missing-search"
              [loading]="store.searching()"
            >
              Search
            </button>
          </form>
          @if (store.searched(); as searched) {
            <p class="text-foreground-muted" role="status" data-testid="missing-searched">
              {{ store.matches().size }} of {{ files().length }} found in {{ searched }}.
              @if (store.truncated()) {
                The folder is too large to search whole; try one inside it.
              }
            </p>
          }
          <ul class="border-hairline divide-hairline max-h-80 divide-y overflow-auto rounded border">
            @for (file of files(); track file.fileId) {
              <li class="flex flex-col gap-1 px-2 py-1.5" data-testid="missing-file" [attr.data-file]="file.fileId">
                <div class="flex items-center gap-2">
                  <div class="min-w-0 flex-1">
                    <div class="truncate font-semibold">{{ file.name }}</div>
                    <div class="text-foreground-muted truncate">{{ describe(file) }}</div>
                  </div>
                  @if (store.matches().get(file.fileId); as match) {
                    <button
                      xuiButton
                      type="button"
                      variant="outline"
                      data-testid="missing-relink"
                      [title]="match.path"
                      [loading]="store.relinking()"
                      (click)="relink(file, match.path)"
                    >
                      Relink
                    </button>
                  }
                  <button xuiButton type="button" variant="ghost" data-testid="missing-locate" (click)="locate(file)">
                    Locate…
                  </button>
                </div>
                @if (store.matches().get(file.fileId); as match) {
                  <div class="text-foreground-muted truncate" data-testid="missing-match">Found: {{ match.path }}</div>
                } @else if (store.problemOf(file); as problem) {
                  <div class="text-foreground-muted" data-testid="missing-problem">
                    A file of that name does not match. {{ problem }}
                  </div>
                }
                @if (locating() === file.fileId) {
                  <form class="flex items-center gap-2" (submit)="$event.preventDefault(); relink(file, path.value)">
                    <input
                      #path
                      xuiInput
                      class="min-w-0 flex-1"
                      data-testid="missing-path"
                      aria-label="Path of the file on the Server"
                      placeholder="/path/on/the/server/file.wav"
                    />
                    <button xuiButton type="submit" variant="outline" data-testid="missing-path-relink">Relink</button>
                  </form>
                }
              </li>
            }
          </ul>
        }
        @if (unreadable().length > 0) {
          <p class="text-foreground-muted" data-testid="unreadable-tracks">
            {{ unreadable().length }} {{ unreadable().length === 1 ? 'track' : 'tracks' }} could not be read and
            {{ unreadable().length === 1 ? 'is' : 'are' }} not in the project: {{ unreadable().join(', ') }}. Saving
            keeps {{ unreadable().length === 1 ? 'its file' : 'their files' }} in the project folder.
          </p>
        }
      </xui-dialog-body>
      <xui-dialog-footer>
        <button xuiButton type="button" variant="ghost" data-testid="missing-close" (click)="close()">
          {{ files().length > 0 ? 'Keep Offline' : 'Close' }}
        </button>
        @if (files().length > 0) {
          <button
            xuiButton
            type="button"
            data-testid="missing-relink-all"
            [disabled]="store.matches().size === 0"
            [loading]="store.relinking()"
            (click)="relinkFound()"
          >
            Relink Found
          </button>
        }
      </xui-dialog-footer>
    </xui-dialog>
  `
})
export class MissingFilesDialog {
  protected readonly ui = inject(UiStore);
  protected readonly store = inject(OfflineFilesStore);
  private readonly project = inject(ProjectStore);
  private readonly desktop = inject(ConnectionService).desktop;

  protected readonly chooseFolder = typeof this.desktop?.dialogs?.chooseFolder === 'function';
  protected readonly files = computed(() => this.project.offline().files);
  protected readonly unreadable = computed(() => this.project.offline().unreadableTracks);
  protected readonly directory = signal('');
  /** The file whose path is being typed (no native dialog: a browser, or a remote Server). */
  protected readonly locating = signal<string | null>(null);

  /** What is wrong, and what uses the file. */
  protected describe(file: OfflineFile): string {
    const used = this.project.regionsOfFile(file.fileId);
    const uses = `${used} ${used === 1 ? 'region' : 'regions'}`;
    switch (file.reason) {
      case 'changed':
        return `Changed at ${file.path}. ${file.detail ?? ''} Used by ${uses}.`;
      case 'unlisted':
        return `The project lost this file's details. Used by ${uses}.`;
      default:
        return `Not at ${file.path}. Used by ${uses}.`;
    }
  }

  protected async choose(): Promise<void> {
    const folder = await this.desktop?.dialogs?.chooseFolder();
    if (folder) {
      this.directory.set(folder);
      await this.store.search(folder);
    }
  }

  protected search(directory: string): Promise<void> {
    this.directory.set(directory);
    return this.store.search(directory);
  }

  /** The native file dialog in the desktop app, else a path on the Server to type. */
  protected async locate(file: OfflineFile): Promise<void> {
    const dialogs = this.desktop?.dialogs;
    if (!dialogs) {
      this.locating.update(id => (id === file.fileId ? null : file.fileId));
      return;
    }

    const [path] = await dialogs.importAudio();
    if (path) {
      await this.relink(file, path);
    }
  }

  protected async relink(file: OfflineFile, path: string): Promise<void> {
    if (path.trim() && (await this.store.relink([{ fileId: file.fileId, path: path.trim() }]))) {
      this.locating.set(null);
      this.closeWhenDone();
    }
  }

  protected async relinkFound(): Promise<void> {
    if (await this.store.relinkFound()) {
      this.closeWhenDone();
    }
  }

  protected close(): void {
    this.ui.missingFilesOpen.set(false);
    this.store.reset();
  }

  /** The window has nothing left to do once the offline report, which arrives with the relink, is empty. */
  private closeWhenDone(): void {
    if (this.files().length === 0 && this.unreadable().length === 0) {
      this.close();
    }
  }
}
