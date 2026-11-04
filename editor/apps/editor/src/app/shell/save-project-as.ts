import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiInputImports } from '@xui/input';
import { ConnectionService } from '../core/connection';
import { ProjectSession } from '../state/project-session';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';

/**
 * File ▸ Save As… and Save a Copy…: where the whole package goes, a folder on the Server's disk that does not exist yet.
 * The desktop app picks it with its native dialog; a browser types the path. Save As goes on in the new package; a copy
 * leaves the open project where it is.
 */
@Component({
  selector: 'an-save-project-as',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiInputImports],
  template: `
    <xui-dialog [open]="ui.saveAsOpen() !== null" [title]="title()" size="md" (openChange)="close($event)">
      <form (submit)="$event.preventDefault(); save(path.value)">
        <xui-dialog-body class="flex flex-col gap-2">
          <label class="text-foreground-muted text-xs" for="an-save-as-path">Project folder</label>
          <div class="flex gap-2">
            <input
              #path
              id="an-save-as-path"
              xuiInput
              class="flex-1"
              autocomplete="off"
              data-testid="save-as-path"
              [value]="target()"
            />
            @if (dialogs?.saveProject) {
              <button xuiButton type="button" variant="outline" (click)="choose()">Choose…</button>
            }
          </div>
          <p class="text-foreground-muted text-xs">{{ note() }}</p>
        </xui-dialog-body>
        <xui-dialog-footer>
          <button xuiButton type="button" variant="ghost" (click)="ui.saveAsOpen.set(null)">Cancel</button>
          <button xuiButton type="submit" data-testid="save-as-confirm" [loading]="saving()">Save</button>
        </xui-dialog-footer>
      </form>
    </xui-dialog>
  `
})
export class SaveProjectAsDialog {
  protected readonly ui = inject(UiStore);
  private readonly session = inject(ProjectSession);
  private readonly project = inject(ProjectStore);
  protected readonly dialogs = inject(ConnectionService).desktop?.dialogs ?? null;
  protected readonly saving = signal(false);
  protected readonly chosen = signal<string | null>(null);

  protected readonly title = computed(() => (this.ui.saveAsOpen() === 'saveCopy' ? 'Save a Copy' : 'Save As'));
  protected readonly note = computed(() =>
    this.ui.saveAsOpen() === 'saveCopy'
      ? 'Writes the whole project there, unsaved edits included. You stay in this project.'
      : 'Writes the whole project there, unsaved edits included, and goes on there. This project stays as last saved.'
  );

  /** A free place to start from: beside the project, with a name the person will likely change. */
  protected readonly target = computed(() => {
    const chosen = this.chosen();
    if (chosen) {
      return chosen;
    }

    const path = this.project.info()?.path ?? '';
    return path.replace(/\.anode$/, '') + (this.ui.saveAsOpen() === 'saveCopy' ? ' copy.anode' : ' 2.anode');
  });

  protected async choose(): Promise<void> {
    const path = await this.dialogs?.saveProject?.(this.target());
    if (path) {
      this.chosen.set(path);
    }
  }

  protected close(open: boolean): void {
    if (!open) {
      this.ui.saveAsOpen.set(null);
    }
  }

  protected async save(path: string): Promise<void> {
    const mode = this.ui.saveAsOpen();
    if (!mode) {
      return;
    }

    this.saving.set(true);
    try {
      if (await (mode === 'saveCopy' ? this.session.saveCopy(path) : this.session.saveAs(path))) {
        this.ui.saveAsOpen.set(null);
      }
    } finally {
      this.saving.set(false);
    }
  }
}
