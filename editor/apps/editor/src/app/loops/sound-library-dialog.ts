import { ChangeDetectionStrategy, Component, effect, inject, signal, untracked } from '@angular/core';
import type { Pack } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import { matDeleteOutlineRound, matFolderOpenRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiSpinnerImports } from '@xui/spinner';
import { ConnectionService } from '../core/connection';
import { fileSize } from '../state/browser.store';
import { UiStore } from '../state/ui.store';
import { LoopsStore } from './loops.store';

/**
 * The Sound Library window: the installed content packs (name, version, author, loops, size), removing one (after a
 * confirmation; projects keep the loops they use), and installing a `.anodepack`: from a native file dialog in the
 * desktop app, or by its path on the Server's disk (always offered, as the browser has no file dialog onto the Server).
 * Opened by `UiStore.soundLibraryOpen` (the Loop Browser's button, View ▸ Sound Library).
 */
@Component({
  selector: 'an-sound-library-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiIconImports, XuiInputImports, XuiSpinnerImports],
  providers: [provideIcons({ matDeleteOutlineRound, matFolderOpenRound })],
  template: `
    <xui-dialog [(open)]="ui.soundLibraryOpen" title="Sound Library">
      <xui-dialog-body class="flex min-h-0 flex-col gap-3" data-testid="sound-library-dialog">
        <section class="flex flex-col gap-1" aria-label="Installed packs">
          <h3 class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">Installed</h3>
          <div
            class="border-hairline bg-surface-inset max-h-[45vh] min-w-[28rem] overflow-y-auto rounded-md border"
            role="list"
            aria-label="Content packs"
          >
            @for (pack of store.packs(); track pack.id) {
              <div
                class="border-hairline flex h-11 items-center gap-3 border-b px-3 text-xs last:border-b-0"
                role="listitem"
                data-testid="pack-row"
                [attr.data-pack-id]="pack.id"
              >
                <div class="flex min-w-0 flex-1 flex-col">
                  <span class="text-foreground truncate font-semibold">{{ pack.name }}</span>
                  <span class="text-foreground-muted readout truncate text-[11px]">
                    {{ pack.version }} · {{ pack.author }} · {{ pack.loopCount }}
                    {{ pack.loopCount === 1 ? 'loop' : 'loops' }} · {{ size(pack.size) }}
                  </span>
                </div>
                @if (confirming() === pack.id) {
                  <span class="text-foreground-muted text-[11px]">Remove?</span>
                  <button xuiButton type="button" size="sm" variant="ghost" (click)="confirming.set(null)">
                    Cancel
                  </button>
                  <button
                    xuiButton
                    type="button"
                    size="sm"
                    color="error"
                    data-testid="confirm-remove-pack"
                    [attr.aria-label]="'Confirm removing ' + pack.name"
                    (click)="remove(pack)"
                  >
                    Remove
                  </button>
                } @else {
                  <button
                    xuiButton
                    type="button"
                    size="sm"
                    variant="ghost"
                    class="h-6 w-6 shrink-0 px-0"
                    data-testid="remove-pack"
                    [attr.aria-label]="'Remove ' + pack.name"
                    (click)="confirming.set(pack.id)"
                  >
                    <ng-icon xui name="matDeleteOutlineRound" size="sm" />
                  </button>
                }
              </div>
            } @empty {
              <p class="text-foreground-muted p-3 text-xs" role="listitem">
                No content packs installed. Install a .anodepack below.
              </p>
            }
          </div>
        </section>
        <section class="flex flex-col gap-1.5" aria-label="Install a pack">
          <h3 class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">Install</h3>
          @if (desktopDialog) {
            <button
              xuiButton
              type="button"
              size="sm"
              variant="outline"
              class="self-start"
              data-testid="install-pack-file"
              [disabled]="store.installing()"
              (click)="installFromFile()"
            >
              <ng-icon xui name="matFolderOpenRound" size="sm" />
              Install from File…
            </button>
          }
          <form class="flex items-center gap-1" (submit)="$event.preventDefault(); installPath(path.value)">
            <input
              #path
              xuiInput
              class="h-7 min-w-0 flex-1 text-xs"
              placeholder="/path/to/Pack.anodepack on the server"
              aria-label="Pack path"
              data-testid="pack-path"
              spellcheck="false"
              [value]="pathText()"
              (input)="pathText.set(path.value)"
            />
            <button
              xuiButton
              type="submit"
              size="sm"
              data-testid="install-pack"
              [disabled]="!pathText().trim()"
              [loading]="store.installing()"
            >
              Install
            </button>
          </form>
          @if (store.installing()) {
            <p class="text-foreground-muted flex items-center gap-2 text-[11px]" role="status">
              <xui-spinner size="sm" aria-hidden="true" />
              Installing…
            </p>
          }
        </section>
      </xui-dialog-body>
      <xui-dialog-footer>
        <button xuiButton type="button" data-testid="close-sound-library" (click)="ui.soundLibraryOpen.set(false)">
          Done
        </button>
      </xui-dialog-footer>
    </xui-dialog>
  `
})
export class SoundLibraryDialog {
  protected readonly ui = inject(UiStore);
  protected readonly store = inject(LoopsStore);
  protected readonly desktopDialog = typeof inject(ConnectionService).desktop?.dialogs?.contentPack === 'function';
  /** The pack whose Remove waits for a confirmation. */
  protected readonly confirming = signal<string | null>(null);
  protected readonly pathText = signal('');

  constructor() {
    effect(() => {
      if (this.ui.soundLibraryOpen()) {
        untracked(() => {
          this.confirming.set(null);
          void this.store.refetchPacks();
        });
      }
    });
  }

  protected size(bytes: number): string {
    return fileSize(bytes);
  }

  protected async installFromFile(): Promise<void> {
    const path = await window.anode?.dialogs?.contentPack?.();
    if (path) {
      await this.store.install(path);
    }
  }

  protected async installPath(path: string): Promise<void> {
    if (await this.store.install(path)) {
      this.pathText.set('');
    }
  }

  protected async remove(pack: Pack): Promise<void> {
    this.confirming.set(null);
    await this.store.remove(pack);
  }
}
