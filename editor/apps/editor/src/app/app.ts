import { ChangeDetectionStrategy, Component, Injector, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { XuiButtonImports } from '@xui/button';
import { XuiInputImports } from '@xui/input';
import { XuiNonIdealStateImports } from '@xui/non-ideal-state';
import { XuiSpinnerImports } from '@xui/spinner';
import { ArticulationSetEditor } from './articulations/articulation-set-editor';
import { ControlSurfacesWindow } from './control/control-surfaces-window';
import { ControlStore } from './control/control.store';
import { ControllerAssignmentsWindow } from './control/controller-assignments-window';
import { LearnMenuHost } from './control/learn-menu';
import { SelectionReporter } from './control/selection-reporter';
import type { AppCommand } from './core/desktop';
import { Notify } from './core/notify';
import { Startup } from './core/startup';
import { EnvironmentWindow } from './environment/environment-window';
import { BounceDialog } from './export/bounce-dialog';
import { BounceInPlaceDialog } from './functions/bounce-in-place-dialog';
import { ProcessingDialog } from './functions/processing-dialog';
import { StripSilenceDialog } from './functions/strip-silence-dialog';
import { GrooveWindow } from './grooves/groove-window';
import { InterchangeDialog } from './interchange/interchange-dialog';
import { KeyCommandsEditor } from './keys/key-commands-editor';
import { SoundLibraryDialog } from './loops/sound-library-dialog';
import { MasteringWindow } from './mastering/mastering-window';
import { GroupSettings } from './mixer/group-settings';
import { IoLabelsDialog } from './mixer/io-labels-dialog';
import { PluginWindowsHost } from './mixer/plugin-window';
import { PluginWindows } from './mixer/plugin-windows';
import { ScoreLayoutWindow } from './score/score-layout-window';
import { ScoreSetsWindow } from './score/score-sets-window';
import { ScoreWindow } from './score/score-window';
import { StaffStylesWindow } from './score/staff-styles-window';
import { ScriptEditor } from './scripter/script-editor';
import { ControlBar } from './shell/control-bar';
import { SaveAlternativeDialog } from './shell/save-alternative';
import { HistoryStore } from './state/history.store';
import { KeyCommandsStore } from './state/key-commands.store';
import { LiveMidiStore } from './state/live-midi.store';
import { MixerStore } from './state/mixer.store';
import { PianoRollStore } from './state/piano-roll.store';
import { ProjectSession } from './state/project-session';
import { ProjectStore } from './state/project.store';
import { ScreensetStore } from './state/screenset.store';
import { SelectionStore } from './state/selection.store';
import { StepInputStore } from './state/step-input.store';
import { ThemeStore } from './state/theme.store';
import { TransportStore } from './state/transport.store';
import { type EditorView, UiStore } from './state/ui.store';
import { StemSplitterDialog } from './stems/stem-splitter-dialog';
import { StepInputKeyboard } from './step-input/step-input-keyboard';
import { SettingsWindow } from './surround/settings-window';
import { SurroundSettingsWindow } from './surround/surround-settings-window';
import { SyncSettingsWindow } from './sync/sync-settings-window';
import { SyncStore } from './sync/sync.store';
import { AudioImport } from './tracks/audio-import';
import { TransformWindow } from './transform/transform-window';
import { MovieDialogHost } from './video/movie-dialog';
import { MovieSettingsWindow } from './video/movie-settings-window';
import { MovieWindow } from './video/movie-window';

/** The interchange dialogs the app menu opens (Phase 9). */
const InterchangeCommands = {
  importMusicXml: { direction: 'import', format: 'musicxml' },
  importAaf: { direction: 'import', format: 'aaf' },
  importMidiOptions: { direction: 'import', format: 'midi' },
  exportMusicXml: { direction: 'export', format: 'musicxml' },
  exportAaf: { direction: 'export', format: 'aaf' },
  exportMidi: { direction: 'export', format: 'midi' },
  importAdm: { direction: 'import', format: 'adm' },
  exportAdm: { direction: 'export', format: 'adm' }
} as const;

@Component({
  selector: 'an-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ArticulationSetEditor,
    BounceDialog,
    BounceInPlaceDialog,
    ControlBar,
    ControllerAssignmentsWindow,
    ControlSurfacesWindow,
    GroupSettings,
    IoLabelsDialog,
    KeyCommandsEditor,
    LearnMenuHost,
    PluginWindowsHost,
    ProcessingDialog,
    RouterOutlet,
    SaveAlternativeDialog,
    ScriptEditor,
    SoundLibraryDialog,
    StemSplitterDialog,
    StepInputKeyboard,
    StripSilenceDialog,
    SyncSettingsWindow,
    TransformWindow,
    GrooveWindow,
    InterchangeDialog,
    ScoreLayoutWindow,
    ScoreSetsWindow,
    ScoreWindow,
    StaffStylesWindow,
    SurroundSettingsWindow,
    SettingsWindow,
    MovieWindow,
    MovieSettingsWindow,
    MovieDialogHost,
    MasteringWindow,
    EnvironmentWindow,
    XuiButtonImports,
    XuiInputImports,
    XuiNonIdealStateImports,
    XuiSpinnerImports
  ],
  host: {
    class: 'flex h-full flex-col',
    '(document:keydown)': 'onKeydown($event)',
    '(document:keyup)': 'onKeyup($event)',
    '(window:blur)': 'live.releaseTyped()'
  },
  template: `
    @switch (startup.state()) {
      @case ('starting') {
        <div class="grid h-full place-items-center">
          <xui-spinner [attr.aria-label]="remoteUrl() ? 'Connecting to ' + remoteUrl() : 'Starting the engine'" />
        </div>
      }
      @case ('failed') {
        <xui-non-ideal-state class="h-full" title="Cannot reach the Anode server" [description]="startup.error() ?? ''">
          <div action class="flex gap-2">
            <button xuiButton type="button" (click)="startup.start()">Try again</button>
            @if (remoteUrl()) {
              <button xuiButton type="button" variant="outline" (click)="useThisComputer()">Use this computer</button>
            }
          </div>
        </xui-non-ideal-state>
      }
      @case ('token') {
        <xui-non-ideal-state
          class="h-full"
          title="Access token needed"
          description="This Anode server asks for its access token. The Anode desktop app shows it in Settings ▸ Network; a headless server keeps it as Anode__Token in its environment file. Or ask whoever set it up."
        >
          <form
            action
            class="flex items-center gap-2"
            (submit)="$event.preventDefault(); startup.useToken(tokenInput.value)"
          >
            <input
              #tokenInput
              xuiInput
              type="password"
              class="w-72"
              aria-label="Access token"
              data-testid="access-token"
              autocomplete="current-password"
            />
            <button xuiButton type="submit">Connect</button>
          </form>
        </xui-non-ideal-state>
      }
      @default {
        @if (remote()) {
          <!-- Phase 10: the touch remote has its own chrome (remote/remote-page.ts). -->
          <main class="relative min-h-0 flex-1 overflow-hidden">
            <router-outlet />
          </main>
        } @else {
          <an-control-bar [class.window-drag]="macWindow" [class.ps-21]="trafficLights()" />
          <main class="relative min-h-0 flex-1 overflow-hidden">
            <router-outlet />
          </main>
          <!-- Dialogs load when first opened, so the initial bundle carries none of them (Phase 10). -->
          @if (ui.keyCommandsOpen()) {
            @defer (on immediate) {
              <an-key-commands-editor />
            }
          }
          @if (ui.saveAlternativeOpen()) {
            @defer (on immediate) {
              <an-save-alternative />
            }
          }
          @if (project.isOpen()) {
            @if (ui.bounceOpen()) {
              @defer (on immediate) {
                <an-bounce-dialog />
              }
            }
            @if (ui.stripSilenceOpen()) {
              @defer (on immediate) {
                <an-strip-silence-dialog />
              }
            }
            @if (ui.bounceInPlaceOpen()) {
              @defer (on immediate) {
                <an-bounce-in-place-dialog />
              }
            }
            @if (ui.processingOpen()) {
              @defer (on immediate) {
                <an-processing-dialog />
              }
            }
            @if (ui.ioLabelsOpen()) {
              @defer (on immediate) {
                <an-io-labels-dialog />
              }
            }
            @if (ui.groupSettings() !== null) {
              @defer (on immediate) {
                <an-group-settings />
              }
            }
            <an-plugin-windows />
            <!-- Phase 7's windows load when first opened (docs/08-phase-7.md; the initial bundle stays small). -->
            @if (ui.transformOpen()) {
              @defer (on immediate) {
                <an-transform-window />
              }
            }
            @if (ui.grooves() !== null) {
              @defer (on immediate) {
                <an-groove-window />
              }
            }
            @if (ui.stepInputKeyboardOpen()) {
              @defer (on immediate) {
                <an-step-input-keyboard />
              }
            }
            @if (ui.articulationSetEditor() !== null) {
              @defer (on immediate) {
                <an-articulation-set-editor />
              }
            }
            @if (ui.scriptEditor() !== null) {
              @defer (on immediate) {
                <an-script-editor />
              }
            }
            <!-- Phase 8's windows (docs/09-phase-8.md), loaded when first opened. -->
            @if (ui.syncSettingsOpen()) {
              @defer (on immediate) {
                <an-sync-settings-window />
              }
            }
            @if (ui.stemSplitter() !== null) {
              @defer (on immediate) {
                <an-stem-splitter-dialog />
              }
            }
            <!-- Phase 9's score windows and interchange dialogs (docs/10-phase-9.md), loaded when first opened: Verovio and
               the score's parts stay out of the initial bundle. -->
            @if (ui.scoreWindow() !== null) {
              @defer (on immediate) {
                <an-score-window />
              }
            }
            @if (ui.staffStylesOpen()) {
              @defer (on immediate) {
                <an-staff-styles-window />
              }
            }
            @if (ui.scoreSetsOpen()) {
              @defer (on immediate) {
                <an-score-sets-window />
              }
            }
            @if (ui.scoreLayoutOpen()) {
              @defer (on immediate) {
                <an-score-layout-window />
              }
            }
            @if (ui.interchange() !== null) {
              @defer (on immediate) {
                <an-interchange-dialog />
              }
            }
            <!-- Phase 10's surround and movie windows (docs/11-phase-10.md), loaded when first opened. -->
            @if (ui.surroundSettingsOpen()) {
              @defer (on immediate) {
                <an-surround-settings-window />
              }
            }
            @if (ui.movieWindowOpen() && project.movie()) {
              @defer (on immediate) {
                <an-movie-window />
              }
            }
            @if (ui.movieSettingsOpen()) {
              @defer (on immediate) {
                <an-movie-settings-window />
              }
            }
            @if (ui.movieDialog() !== null) {
              @defer (on immediate) {
                <an-movie-dialog />
              }
            }
            <!-- Phase 11's MIDI Environment window (ADR-0015), loaded with @xui/node-graph when first opened. -->
            @if (ui.environmentOpen()) {
              @defer (on immediate) {
                <an-environment-window />
              }
            }
            <!-- Phase 11's Mastering Assistant window (docs/12-phase-11.md), loaded when first opened. -->
            @if (ui.masteringOpen()) {
              @defer (on immediate) {
                <an-mastering-window />
              }
            }
          }
          <!-- Settings… (monitoring is the user's, not the project's). -->
          @if (ui.settingsOpen()) {
            @defer (on immediate) {
              <an-settings-window />
            }
          }
          <!-- Control surfaces and assignments are user settings: they open without a project too. -->
          @if (ui.controllerAssignmentsOpen()) {
            @defer (on immediate) {
              <an-controller-assignments-window />
            }
          }
          @if (ui.controlSurfacesOpen()) {
            @defer (on immediate) {
              <an-control-surfaces-window />
            }
          }
          <an-learn-menu-host />
          @if (ui.soundLibraryOpen()) {
            @defer (on immediate) {
              <an-sound-library-dialog />
            }
          }
        }
      }
    }
  `
})
export class App {
  protected readonly startup = inject(Startup);
  private readonly transport = inject(TransportStore);
  protected readonly project = inject(ProjectStore);
  private readonly history = inject(HistoryStore);
  private readonly session = inject(ProjectSession);
  private readonly keys = inject(KeyCommandsStore);
  private readonly screensets = inject(ScreensetStore);
  private readonly selection = inject(SelectionStore);
  private readonly theme = inject(ThemeStore);
  protected readonly ui = inject(UiStore);
  private readonly stepInput = inject(StepInputStore);
  protected readonly live = inject(LiveMidiStore);
  private readonly audioImport = inject(AudioImport);
  private readonly mixer = inject(MixerStore);
  private readonly control = inject(ControlStore);
  private readonly sync = inject(SyncStore);
  private readonly injector = inject(Injector);
  private readonly pianoRoll = inject(PianoRollStore);
  private readonly notify = inject(Notify);

  /** Phase 10: the `remote` route shows only itself, without the desktop editor's shell, keys or windows. */
  protected readonly remote = signal(isRemoteRoute(typeof location === 'undefined' ? '' : location.hash.slice(1)));

  /**
   * In the macOS desktop app the control bar is the title bar: it drags the window and leaves room for the traffic
   * lights (apps/desktop/src/main.ts). Full screen hides them, and the room goes with them.
   */
  protected readonly macWindow = window.anode?.platform === 'darwin';
  private readonly fullScreen = signal(false);
  protected readonly trafficLights = computed(() => this.macWindow && !this.fullScreen());

  /** The remote Server's URL when the desktop app uses one. */
  protected readonly remoteUrl = computed(() => {
    const target = this.startup.target();
    return target?.mode === 'remote' ? target.baseUrl : null;
  });

  constructor() {
    window.anode?.onFullScreen(fullScreen => this.fullScreen.set(fullScreen));
    // The desktop app asks before it quits: the open project lives in its own Server and goes with it.
    window.anode?.onQuit?.(() => this.session.mayQuit());
    window.anode?.onCommand?.(command => void this.command(command));
    window.anode?.onOpenSettings?.(() => this.ui.settingsOpen.set(true));
    inject(Router).events.subscribe(event => {
      if (event instanceof NavigationEnd) {
        this.remote.set(isRemoteRoute(event.urlAfterRedirects));
      }
    });
    // Created at start, so plugin problems are reported wherever the person is.
    inject(PluginWindows);
    // Phase 8: the Server hears which track is selected (selected-track assignments and surfaces follow it).
    inject(SelectionReporter);
    this.registerKeys();
    void this.startup.start();
  }

  /** What the shell's key commands do (the Tracks area and others register their own). */
  private registerKeys(): void {
    const actions: Record<string, () => void> = {
      'transport.play': () => this.transport.togglePlay(),
      'transport.record': () => this.transport.record(),
      'transport.cycle': () => this.transport.toggleCycle(),
      'transport.goToStart': () => this.transport.goToStart(),
      'transport.flashback': () => void this.transport.flashbackCapture(),
      'transport.autopunch': () => this.transport.toggleAutopunch(),
      'transport.replace': () => this.transport.toggleReplace(),
      'transport.metronome': () => this.transport.toggleMetronome(),
      'edit.undo': () => void this.history.undo(),
      'edit.redo': () => void this.history.redo(),
      'project.save': () => void this.session.save(),
      'project.saveAlternative': () => this.ui.saveAlternativeOpen.set(true),
      'view.keyCommands': () => this.ui.keyCommandsOpen.set(true),
      'view.theme': () => this.theme.toggle(),
      'view.mixer': () => this.screensets.showPane('mixer'),
      'view.editors': () => this.screensets.showPane('editors'),
      'view.browsers': () => this.screensets.showPane('browsers'),
      'view.lists': () => this.screensets.showPane('lists'),
      'view.notes': () => this.screensets.showPane('notes'),
      'view.loopBrowser': () => this.screensets.showPane('loops'),
      'view.smartControls': () => this.screensets.showPane('smartControls'),
      'view.stepSequencer': () => {
        this.ui.editorView.set('stepSequencer');
        this.screensets.showPane('editors');
      },
      'view.soundLibrary': () => this.ui.soundLibraryOpen.set(true),
      'view.eventList': () => this.showEditor('eventList'),
      'view.stepEditor': () => this.showEditor('stepEditor'),
      'view.audioFileEditor': () => this.showEditor('audioFile'),
      'view.stepInputKeyboard': () => this.ui.stepInputKeyboardOpen.update(open => !open),
      'midi.transform': () => this.ui.transformOpen.set(true),
      'midi.stepInput': () => void this.stepInput.toggleMidi(this.ui.stepInputKeyboardOpen()),
      'view.liveLoops': () =>
        void this.project.execute(
          [{ type: 'setLiveLoops', shown: !this.project.liveLoops().shown }],
          this.project.liveLoops().shown ? 'Hide Live Loops' : 'Show Live Loops'
        ),
      'project.bounce': () => this.ui.bounceOpen.set(true),
      'region.stripSilence': () => this.ui.stripSilenceOpen.set(true),
      'region.bounceInPlace': () => this.ui.bounceInPlaceOpen.set(true),
      'region.process': () => this.ui.processingOpen.set(true),
      'mixer.ioLabels': () => this.ui.ioLabelsOpen.set(true),
      'mixer.groupSettings': () => this.openGroupSettings(),
      'track.newAux': () => void this.mixer.addAux(),
      'track.newVca': () => void this.mixer.addVca(),
      'view.musicalTyping': () => this.live.typing.update(on => !on),
      'control.surfaces': () => this.ui.controlSurfacesOpen.set(true),
      'control.assignments': () => this.ui.controllerAssignmentsOpen.set(true),
      'control.learn': () => void this.learnNew(),
      'project.syncSettings': () => this.ui.syncSettingsOpen.set(true),
      'region.stemSplitter': () => this.openStemSplitter(),
      'sync.link': () => void this.sync.toggleLink(),
      'view.score': () => this.showEditor('score'),
      'score.sets': () => this.ui.scoreSetsOpen.set(true),
      'score.staffStyles': () => this.ui.staffStylesOpen.set(true),
      'score.layout': () => this.ui.scoreLayoutOpen.set(true),
      'score.print': () => void this.printScore(),
      'view.environment': () => this.ui.environmentOpen.set(true)
    };
    for (let n = 1; n <= 9; n++) {
      actions[`screenset.recall${n}`] = () => void this.screensets.recall(n);
    }
    for (const [id, action] of Object.entries(actions)) {
      this.keys.register(id, action);
    }
  }

  /** What the app menu asks for (File ▸ New, Open…, Save, Close, Import Audio…). */
  async command(command: AppCommand): Promise<void> {
    const dialogs = window.anode?.dialogs;
    switch (command) {
      case 'newProject':
        await this.session.close();
        break;
      case 'openProject': {
        const path = await dialogs?.openProject();
        if (path) {
          await this.session.open(path);
        }
        break;
      }
      case 'saveProject':
        await this.session.save();
        break;
      case 'closeProject':
        await this.session.close();
        break;
      case 'importAudio':
        // The Tracks area's Import button has the context (track, playhead); the menu item uses the same.
        document.querySelector<HTMLButtonElement>('[aria-label="Import audio"]')?.click();
        break;
      case 'importMidi': {
        const path = await dialogs?.importMidi?.();
        if (path) {
          await this.audioImport.importServerFile(path, 'midi', null, 0);
        }
        break;
      }
      case 'keyCommands':
        this.ui.keyCommandsOpen.set(true);
        break;
      case 'saveAlternative':
        this.ui.saveAlternativeOpen.set(true);
        break;
      case 'bounce':
        if (this.project.isOpen()) {
          this.ui.bounceOpen.set(true);
        }
        break;
      case 'ioLabels':
        this.ui.ioLabelsOpen.set(true);
        break;
      case 'controlSurfaces':
        this.ui.controlSurfacesOpen.set(true);
        break;
      case 'controllerAssignments':
        this.ui.controllerAssignmentsOpen.set(true);
        break;
      case 'syncSettings':
        if (this.project.isOpen()) {
          this.ui.syncSettingsOpen.set(true);
        }
        break;
      case 'stemSplitter':
        this.openStemSplitter();
        break;
      case 'importMusicXml':
      case 'importAaf':
      case 'importMidiOptions':
      case 'exportMusicXml':
      case 'exportAaf':
      case 'exportMidi':
        if (this.project.isOpen()) {
          this.ui.interchange.set(InterchangeCommands[command]);
        }
        break;
      case 'printScore':
        await this.printScore();
        break;
      case 'importAdm':
      case 'exportAdm':
        if (this.project.isOpen()) {
          this.ui.interchange.set(InterchangeCommands[command]);
        }
        break;
      case 'surroundSettings':
        if (this.project.isOpen()) {
          this.ui.surroundSettingsOpen.set(true);
        }
        break;
      case 'environment':
        if (this.project.isOpen()) {
          this.ui.environmentOpen.set(true);
        }
        break;
      case 'masteringAssistant':
        if (this.project.isOpen()) {
          this.ui.masteringOpen.set(true);
        }
        break;
      case 'movieSettings':
      case 'movieWindow':
        if (this.project.isOpen() && this.requireMovie()) {
          (command === 'movieWindow' ? this.ui.movieWindowOpen : this.ui.movieSettingsOpen).set(true);
        }
        break;
      case 'openMovie':
        if (this.project.isOpen()) {
          this.ui.movieDialog.set('open');
        }
        break;
      case 'exportMovieAudio':
      case 'detectSceneCuts':
        if (this.project.isOpen() && this.requireMovie()) {
          this.ui.movieDialog.set(command === 'exportMovieAudio' ? 'exportAudio' : 'detectScenes');
        }
        break;
      case 'removeMovie':
      case 'importMovieAudio': {
        if (!this.project.isOpen()) {
          break;
        }
        // Loaded on first use: the video code stays out of the initial bundle.
        const { MovieActions } = await import('./video/movie-actions');
        const actions = this.injector.get(MovieActions);
        await (command === 'removeMovie' ? actions.removeMovie() : actions.importAudio());
        break;
      }
    }
  }

  /** Whether the project has a movie; says so when it has none. */
  private requireMovie(): boolean {
    if (this.project.movie()) {
      return true;
    }
    this.notify.info('The project has no movie: Movie ▸ Open Movie… adds one.');
    return false;
  }

  /** File ▸ Print Score…: the open score window's score, else the Piano Roll's track, else every MIDI track. */
  private async printScore(): Promise<void> {
    if (!this.project.isOpen()) {
      return;
    }
    const window = this.ui.scoreWindow();
    const track = this.pianoRoll.track()?.id ?? null;
    const query = window?.setId
      ? { set: window.setId }
      : window?.partTrackId
        ? { part: window.partTrackId }
        : track
          ? { part: track }
          : {};
    // Loaded on first use: printing engraves with Verovio, which stays out of the initial bundle.
    const { ScorePrint } = await import('./score/score-print');
    await this.injector.get(ScorePrint).print(query, this.project.info()?.name ?? 'Score');
  }

  /** Edit ▸ Stem Splitter…: on the selected audio region (the first, when several are). */
  private openStemSplitter(): void {
    const regions = this.project.regions();
    const regionId = [...this.selection.regions()].find(id => regions.has(id));
    if (regionId) {
      this.ui.stemSplitter.set({ regionId });
    }
  }

  /** Learn New Assignment (⌘L): Controller Assignments in Learn, waiting for a control to be clicked. */
  private async learnNew(): Promise<void> {
    this.ui.controllerAssignmentsOpen.set(true);
    if (!this.control.loaded()) {
      await this.control.load();
    }
    if (!this.control.learning()) {
      await this.control.startLearn(null);
    }
  }

  /** Shows an editor of the Editors pane (Phase 7: the Event List, the Step Editor, the Audio File Editor). */
  private showEditor(view: EditorView): void {
    this.ui.editorView.set(view);
    this.screensets.showPane('editors');
  }

  /** Group Settings for the selected track's group (or the first group). */
  private openGroupSettings(): void {
    const selected = this.selection.track();
    const number =
      (selected ? this.project.track(selected)?.strip.groups[0] : undefined) ?? this.project.groupList()[0]?.number;
    if (number !== undefined) {
      this.ui.groupSettings.set(number);
    }
  }

  /** Back to this computer's own engine (the window reloads on it). */
  protected async useThisComputer(): Promise<void> {
    await window.anode?.setTarget({ mode: 'local' });
  }

  /**
   * Every key press goes through the key command registry (KeyCommandsStore, Logic-like defaults with the person's
   * rebindings): musical typing first (A W S E D F T G Y H U J K play the selected instrument track, Z and X move the
   * octave), then whatever command the key is bound to. Text fields keep their own keys, and Space and Return on a
   * focused button press it. It returns nothing on purpose: an Angular listener that returns false cancels the event
   * everywhere.
   */
  protected onKeydown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    if (
      this.remote() ||
      !this.project.isOpen() ||
      target?.closest('input, textarea, select, [contenteditable], [role="dialog"], [role="menu"]')
    ) {
      return;
    }

    const modified = event.metaKey || event.ctrlKey || event.altKey;
    if (!modified && !event.shiftKey && this.live.typeDown(event.code, event.repeat)) {
      event.preventDefault();
      return;
    }

    const id = this.keys.resolve(event);
    const pressKey = event.key === ' ' || event.key === 'Enter';
    // Space and Return on a focused button press it; the shortcuts are for the rest of the window.
    if (id && !(pressKey && !modified && target?.closest('button, [role="slider"], [role="radio"]'))) {
      event.preventDefault();
      this.keys.run(id);
    }
  }

  protected onKeyup(event: KeyboardEvent): void {
    if (!this.remote() && this.live.typeUp(event.code)) {
      event.preventDefault();
    }
  }
}

/** Whether a router URL is the remote's (`/remote`, `/remote/…`, `/remote?…`). */
export function isRemoteRoute(url: string): boolean {
  return /^\/remote(?:[/?#]|$)/.test(url);
}
