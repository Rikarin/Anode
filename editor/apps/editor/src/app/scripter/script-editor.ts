import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FloatingWindow } from '../shell/floating-window';
import { UiStore } from '../state/ui.store';

/** The Script Editor window (docs/08-phase-7.md, ADR-0009). Placeholder until its workstream lands. */
@Component({
  selector: 'an-script-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow],
  template: `
    <an-floating-window title="Script Editor" testId="script-editor" (closed)="ui.scriptEditor.set(null)">
      <p class="text-foreground-muted p-4 text-[12px]">Script Editor</p>
    </an-floating-window>
  `
})
export class ScriptEditor {
  protected readonly ui = inject(UiStore);
}
