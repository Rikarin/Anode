import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ProjectStore } from '../state/project.store';

/**
 * The Sound Library window: the installed content packs, installing a `.anodepack` from a file and removing a pack
 * (docs/07-phase-6.md). Opened by `UiStore.soundLibraryOpen`. Phase 6 placeholder.
 */
@Component({
  selector: 'an-sound-library-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: ``
})
export class SoundLibraryDialog {
  protected readonly project = inject(ProjectStore);
}
