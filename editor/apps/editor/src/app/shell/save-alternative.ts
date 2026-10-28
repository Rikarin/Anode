import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiInputImports } from '@xui/input';
import { ProjectSession } from '../state/project-session';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';

/** Save as Alternative (Logic's File ▸ Alternatives ▸ New Alternative): names the new alternative of the project. */
@Component({
  selector: 'an-save-alternative',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiInputImports],
  template: `
    <xui-dialog [(open)]="ui.saveAlternativeOpen" title="Save as Alternative" size="sm">
      <form (submit)="$event.preventDefault(); save(name.value)">
        <xui-dialog-body class="flex flex-col gap-2">
          <label class="text-foreground-muted text-xs" for="an-alternative-name">Alternative name</label>
          <input #name id="an-alternative-name" xuiInput data-testid="alternative-name" [value]="suggested()" />
        </xui-dialog-body>
        <xui-dialog-footer>
          <button xuiButton type="button" variant="ghost" (click)="ui.saveAlternativeOpen.set(false)">Cancel</button>
          <button xuiButton type="submit" data-testid="save-alternative" [loading]="saving()">Save</button>
        </xui-dialog-footer>
      </form>
    </xui-dialog>
  `
})
export class SaveAlternativeDialog {
  protected readonly ui = inject(UiStore);
  private readonly session = inject(ProjectSession);
  private readonly project = inject(ProjectStore);
  protected readonly saving = signal(false);

  protected suggested(): string {
    return `${this.project.info()?.name ?? 'Project'} ${this.session.alternatives().length + 1}`;
  }

  protected async save(name: string): Promise<void> {
    this.saving.set(true);
    try {
      if (await this.session.saveAlternative(name)) {
        this.ui.saveAlternativeOpen.set(false);
      }
    } finally {
      this.saving.set(false);
    }
  }
}
