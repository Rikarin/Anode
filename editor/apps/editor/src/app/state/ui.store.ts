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
  | 'pianoRoll'
  | 'score'
  | 'audio'
  | 'smartTempo'
  | 'stepSequencer'
  | 'drumMachine'
  | 'eventList'
  | 'stepEditor'
  | 'audioFile';

/** Phase 9's score windows: a score set's score, or one track's extracted part (the Score Editor in a window). */
export interface ScoreWindow {
  setId: string | null;
  partTrackId: string | null;
}

/** Phase 9's interchange dialogs (File ▸ Import / Export). */
export interface InterchangeDialog {
  direction: 'import' | 'export';
  /** Phase 10 adds ADM BWF (File ▸ Export ▸ Project as ADM BWF…, Import ▸ ADM BWF…). */
  format: 'musicxml' | 'aaf' | 'midi' | 'adm';
}

/** Phase 10's movie dialogs (the Movie menu): Open Movie…, Export Audio to Movie…, Detect Scene Cuts…. */
export type MovieDialog = 'open' | 'exportAudio' | 'detectScenes';

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
  /** Phase 8: Controller Assignments (Logic Pro ▸ Control Surfaces ▸ Controller Assignments…). */
  readonly controllerAssignmentsOpen = signal(false);
  /** Phase 8: Control Surfaces Setup, with OSC (Logic Pro ▸ Control Surfaces ▸ Setup…). */
  readonly controlSurfacesOpen = signal(false);
  /** Phase 8: the project's Synchronization settings (File ▸ Project Settings ▸ Synchronization…). */
  readonly syncSettingsOpen = signal(false);
  /** Phase 8: the Stem Splitter, with the audio region it splits; null: closed. */
  readonly stemSplitter = signal<{ regionId: string } | null>(null);
  /** Phase 9: the Score Editor in a window, for a score set or an extracted part; null: closed. */
  readonly scoreWindow = signal<ScoreWindow | null>(null);
  /** Phase 9: the Staff Styles window. */
  readonly staffStylesOpen = signal(false);
  /** Phase 9: the Score Sets window. */
  readonly scoreSetsOpen = signal(false);
  /** Phase 9: the Score Layout window. */
  readonly scoreLayoutOpen = signal(false);
  /** Phase 9: the MusicXML, AAF or MIDI file import or export dialog; null: closed. */
  readonly interchange = signal<InterchangeDialog | null>(null);
  /** Phase 10: File ▸ Project Settings ▸ Surround… (the project's format). */
  readonly surroundSettingsOpen = signal(false);
  /** Phase 10: File ▸ Project Settings ▸ Movie… (start position, offset, frame rate, timecode). */
  readonly movieSettingsOpen = signal(false);
  /** Phase 10: Settings… (the app menu's ⌘,): the user's audio settings, monitoring among them. */
  readonly settingsOpen = signal(false);
  /** Phase 10: the floating movie window. */
  readonly movieWindowOpen = signal(false);
  /** Phase 10: a Movie menu dialog; null: none. */
  readonly movieDialog = signal<MovieDialog | null>(null);
  /** The generic plugin editor windows, in the order they opened (the last is in front). */
  readonly pluginWindows = signal<readonly PluginWindow[]>([]);

  openPluginWindow(window: PluginWindow): void {
    this.pluginWindows.update(list => [...list.filter(w => !same(w, window)), window]);
  }

  closePluginWindow(window: PluginWindow): void {
    this.pluginWindows.update(list => list.filter(w => !same(w, window)));
  }
}
