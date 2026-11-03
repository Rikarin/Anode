import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { UiStore } from '../state/ui.store';

/** The Movie menu's dialogs: Open Movie…, Export Audio to Movie…, Detect Scene Cuts… (Phase 10; lazy-loaded). */
@Component({
  selector: 'an-movie-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: ``
})
export class MovieDialogHost {
  protected readonly ui = inject(UiStore);
}
