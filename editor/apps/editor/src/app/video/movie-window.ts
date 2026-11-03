import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FloatingWindow } from '../shell/floating-window';
import { UiStore } from '../state/ui.store';

/** The floating movie window, locked to the transport (Phase 10, docs/11-phase-10.md; lazy-loaded). */
@Component({
  selector: 'an-movie-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow],
  template: `
    <an-floating-window title="Movie" testId="movie-window" (closed)="ui.movieWindowOpen.set(false)">
      <p class="text-foreground-muted p-3 text-xs">Movie</p>
    </an-floating-window>
  `
})
export class MovieWindow {
  protected readonly ui = inject(UiStore);
}
