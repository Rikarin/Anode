import { Injectable, signal } from '@angular/core';

/** Which of the main window's dialogs are open (the Key Commands editor, Save Alternative). */
@Injectable({ providedIn: 'root' })
export class UiStore {
  readonly keyCommandsOpen = signal(false);
  readonly saveAlternativeOpen = signal(false);
}
