import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiInputImports } from '@xui/input';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TemplatesStore } from '../state/templates.store';
import { UiStore } from '../state/ui.store';

/**
 * Save as Template… (the whole project, for the chooser) and Save Tracks as Template… (the selected tracks with their
 * routing, for the project menu): their name and category.
 */
@Component({
  selector: 'an-save-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiInputImports],
  template: `
    <xui-dialog [open]="ui.saveTemplateOpen() !== null" [title]="title()" size="sm" (openChange)="close($event)">
      <form (submit)="$event.preventDefault(); save(name.value, category.value)">
        <xui-dialog-body class="flex flex-col gap-2">
          <label class="text-foreground-muted text-xs" for="an-template-name">Template name</label>
          <input
            #name
            id="an-template-name"
            xuiInput
            autocomplete="off"
            data-testid="template-name"
            [value]="suggested()"
          />
          <label class="text-foreground-muted text-xs" for="an-template-category">Category</label>
          <input
            #category
            id="an-template-category"
            xuiInput
            autocomplete="off"
            data-testid="template-category"
            placeholder="My Templates"
          />
          <p class="text-foreground-muted text-xs">{{ note() }}</p>
        </xui-dialog-body>
        <xui-dialog-footer>
          <button xuiButton type="button" variant="ghost" (click)="ui.saveTemplateOpen.set(null)">Cancel</button>
          <button xuiButton type="submit" data-testid="save-template" [disabled]="empty()" [loading]="saving()">
            Save
          </button>
        </xui-dialog-footer>
      </form>
    </xui-dialog>
  `
})
export class SaveTemplateDialog {
  protected readonly ui = inject(UiStore);
  private readonly templates = inject(TemplatesStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  protected readonly saving = signal(false);

  protected readonly title = computed(() =>
    this.ui.saveTemplateOpen() === 'tracks' ? 'Save Tracks as Template' : 'Save as Template'
  );
  protected readonly empty = computed(
    () => this.ui.saveTemplateOpen() === 'tracks' && this.selection.tracks().size === 0
  );
  protected readonly note = computed(() => {
    if (this.ui.saveTemplateOpen() !== 'tracks') {
      return 'New projects can start from it, with its tracks, audio and settings.';
    }

    const count = this.selection.tracks().size;
    return count === 0
      ? 'Select the tracks first.'
      : `${count} selected. Their plugins and routing are saved, and the auxes they reach; no regions or automation.`;
  });

  protected suggested(): string {
    return this.ui.saveTemplateOpen() === 'tracks'
      ? ([...this.selection.tracks()].map(id => this.project.tracks().get(id)?.name)[0] ?? 'Tracks')
      : (this.project.info()?.name ?? 'Template');
  }

  protected close(open: boolean): void {
    if (!open) {
      this.ui.saveTemplateOpen.set(null);
    }
  }

  protected async save(name: string, category: string): Promise<void> {
    this.saving.set(true);
    try {
      const tracks = this.ui.saveTemplateOpen() === 'tracks';
      if (await (tracks ? this.templates.saveTracks(name, category) : this.templates.saveProject(name, category))) {
        this.ui.saveTemplateOpen.set(null);
      }
    } finally {
      this.saving.set(false);
    }
  }
}
