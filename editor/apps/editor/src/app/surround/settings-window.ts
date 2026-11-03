import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FloatingWindow } from '../shell/floating-window';
import { UiStore } from '../state/ui.store';

/**
 * Settings… (the app menu's ⌘,): the user's audio settings, monitoring among them (binaural, down-mix, speaker map)
 * (Phase 10, docs/11-phase-10.md; lazy-loaded).
 */
@Component({
  selector: 'an-settings-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow],
  template: `
    <an-floating-window title="Settings" testId="settings" (closed)="ui.settingsOpen.set(false)">
      <p class="text-foreground-muted p-3 text-xs">Settings</p>
    </an-floating-window>
  `
})
export class SettingsWindow {
  protected readonly ui = inject(UiStore);
}
