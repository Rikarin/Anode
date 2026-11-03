import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FloatingWindow } from '../shell/floating-window';
import { UiStore } from '../state/ui.store';

/** File ▸ Project Settings ▸ Surround…: the project's surround format (Phase 10, docs/11-phase-10.md; lazy-loaded). */
@Component({
  selector: 'an-surround-settings-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow],
  template: `
    <an-floating-window title="Surround" testId="surround-settings" (closed)="ui.surroundSettingsOpen.set(false)">
      <p class="text-foreground-muted p-3 text-xs">Surround</p>
    </an-floating-window>
  `
})
export class SurroundSettingsWindow {
  protected readonly ui = inject(UiStore);
}
