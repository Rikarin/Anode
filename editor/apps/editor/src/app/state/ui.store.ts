import { Injectable, signal } from '@angular/core';

/** An insert whose editor window is open: a track's, or the master's (`trackId` null). */
export interface PluginWindow {
  trackId: string | null;
  insertId: string;
}

function same(a: PluginWindow, b: PluginWindow): boolean {
  return a.trackId === b.trackId && a.insertId === b.insertId;
}

/**
 * Which of the main window's dialogs are open: the Key Commands editor, Save Alternative, and Phase 4's Bounce, I/O
 * Labels, Group Settings (with the group it shows) and the generic plugin windows.
 */
@Injectable({ providedIn: 'root' })
export class UiStore {
  readonly keyCommandsOpen = signal(false);
  readonly saveAlternativeOpen = signal(false);
  readonly bounceOpen = signal(false);
  readonly ioLabelsOpen = signal(false);
  /** The group whose settings show, or null when the window is closed. */
  readonly groupSettings = signal<number | null>(null);
  /** The generic plugin editor windows, in the order they opened (the last is in front). */
  readonly pluginWindows = signal<readonly PluginWindow[]>([]);

  openPluginWindow(window: PluginWindow): void {
    this.pluginWindows.update(list => [...list.filter(w => !same(w, window)), window]);
  }

  closePluginWindow(window: PluginWindow): void {
    this.pluginWindows.update(list => list.filter(w => !same(w, window)));
  }
}
