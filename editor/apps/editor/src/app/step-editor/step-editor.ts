import { ChangeDetectionStrategy, Component } from '@angular/core';

/** The Step Editor, an editor of the Editors pane (docs/08-phase-7.md). Placeholder until its workstream lands. */
@Component({
  selector: 'an-step-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex h-full min-h-0 flex-col' },
  template: `<p class="text-foreground-muted p-4 text-[12px]">Step Editor</p>`
})
export class StepEditor {
  /** Placeholder: replaced by the editor. */
  protected readonly placeholder = true;
}
