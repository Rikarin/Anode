import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FloatingWindow } from '../shell/floating-window';
import { UiStore } from '../state/ui.store';

/**
 * File ▸ Project Settings ▸ Movie…: start position and offset; frame rate and timecode start (Phase 10,
 * docs/11-phase-10.md; lazy-loaded).
 */
@Component({
  selector: 'an-movie-settings-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow],
  template: `
    <an-floating-window title="Movie Settings" testId="movie-settings" (closed)="ui.movieSettingsOpen.set(false)">
      <p class="text-foreground-muted p-3 text-xs">Movie Settings</p>
    </an-floating-window>
  `
})
export class MovieSettingsWindow {
  protected readonly ui = inject(UiStore);
}
