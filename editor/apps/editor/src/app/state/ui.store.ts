import { Injectable, signal } from '@angular/core';

/** An insert whose editor window is open: a track's, or the master's (`trackId` null); a MIDI FX slot's with `chain`. */
export interface PluginWindow {
  trackId: string | null;
  insertId: string;
  /** The slot's chain: `midiEffects` for a MIDI FX slot (Phase 6); inserts when left out. */
  chain?: 'inserts' | 'midiEffects';
}

/** The editors of the Editors pane (Phase 7: the Event List, the Step Editor and the Audio File Editor). */
export type EditorView =
  'pianoRoll' | 'audio' | 'smartTempo' | 'stepSequencer' | 'drumMachine' | 'eventList' | 'stepEditor' | 'audioFile';

/** A Scripter slot whose Script Editor is open. */
export interface ScriptSlot {
  trackId: string;
  slotId: string;
}

function same(a: PluginWindow, b: PluginWindow): boolean {
  return a.trackId === b.trackId && a.insertId === b.insertId && (a.chain ?? 'inserts') === (b.chain ?? 'inserts');
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
  /** Phase 5's Functions: Strip Silence, Bounce in Place and Selection-Based Processing. */
  readonly stripSilenceOpen = signal(false);
  readonly bounceInPlaceOpen = signal(false);
  readonly processingOpen = signal(false);
  /** Phase 6: the Sound Library window (content packs). */
  readonly soundLibraryOpen = signal(false);
  /** Which editor the Editors pane shows: the Piano Roll, the Audio Track Editor or the Smart Tempo Editor. */
  readonly editorView = signal<EditorView>('pianoRoll');
  /** Phase 7: the MIDI Transform window. */
  readonly transformOpen = signal(false);
  /**
   * Phase 7: the Grooves window (Make Groove Template, the project's grooves and the library's), with the region a
   * template is made from (null: none); null: closed.
   */
  readonly grooves = signal<{ regionId: string | null } | null>(null);
  /** Phase 7: the Step Input Keyboard window. */
  readonly stepInputKeyboardOpen = signal(false);
  /** Phase 7: the articulation set the Articulation Set editor shows (`ars_…`), `new` for a new one; null: closed. */
  readonly articulationSetEditor = signal<string | null>(null);
  /** Phase 7: the Scripter slot whose Script Editor is open; null: closed. */
  readonly scriptEditor = signal<ScriptSlot | null>(null);
  /** Phase 7: the file the Audio File Editor shows (`aud_…`); null: the selected audio region's. */
  readonly audioFileId = signal<string | null>(null);
  /** The generic plugin editor windows, in the order they opened (the last is in front). */
  readonly pluginWindows = signal<readonly PluginWindow[]>([]);

  openPluginWindow(window: PluginWindow): void {
    this.pluginWindows.update(list => [...list.filter(w => !same(w, window)), window]);
  }

  closePluginWindow(window: PluginWindow): void {
    this.pluginWindows.update(list => list.filter(w => !same(w, window)));
  }
}
