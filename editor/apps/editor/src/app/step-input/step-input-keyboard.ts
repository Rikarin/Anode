import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FloatingWindow } from '../shell/floating-window';
import { UiStore } from '../state/ui.store';

/** The Step Input Keyboard window (docs/08-phase-7.md). Placeholder until its workstream lands. */
@Component({
  selector: 'an-step-input-keyboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow],
  template: `
    <an-floating-window
      title="Step Input Keyboard"
      testId="step-input-keyboard"
      (closed)="ui.stepInputKeyboardOpen.set(false)"
    >
      <p class="text-foreground-muted p-4 text-[12px]">Step Input Keyboard</p>
    </an-floating-window>
  `
})
export class StepInputKeyboard {
  protected readonly ui = inject(UiStore);
}
