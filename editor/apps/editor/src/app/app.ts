import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { XuiButtonImports } from '@xui/button';
import { XuiInputImports } from '@xui/input';
import { XuiNonIdealStateImports } from '@xui/non-ideal-state';
import { XuiSpinnerImports } from '@xui/spinner';
import type { AppCommand } from './core/desktop';
import { Startup } from './core/startup';
import { BounceDialog } from './export/bounce-dialog';
import { KeyCommandsEditor } from './keys/key-commands-editor';
import { GroupSettings } from './mixer/group-settings';
import { IoLabelsDialog } from './mixer/io-labels-dialog';
import { PluginWindowsHost } from './mixer/plugin-window';
import { PluginWindows } from './mixer/plugin-windows';
import { ControlBar } from './shell/control-bar';
import { SaveAlternativeDialog } from './shell/save-alternative';
import { HistoryStore } from './state/history.store';
import { KeyCommandsStore } from './state/key-commands.store';
import { LiveMidiStore } from './state/live-midi.store';
import { MixerStore } from './state/mixer.store';
import { ProjectSession } from './state/project-session';
import { ProjectStore } from './state/project.store';
import { ScreensetStore } from './state/screenset.store';
import { SelectionStore } from './state/selection.store';
import { ThemeStore } from './state/theme.store';
import { TransportStore } from './state/transport.store';
import { UiStore } from './state/ui.store';
import { AudioImport } from './tracks/audio-import';

@Component({
  selector: 'an-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    BounceDialog,
    ControlBar,
    GroupSettings,
    IoLabelsDialog,
    KeyCommandsEditor,
    PluginWindowsHost,
    RouterOutlet,
    SaveAlternativeDialog,
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
        <an-control-bar [class.window-drag]="macWindow" [class.ps-21]="trafficLights()" />
        <main class="relative min-h-0 flex-1 overflow-hidden">
          <router-outlet />
        </main>
        <an-key-commands-editor />
        <an-save-alternative />
        @if (project.isOpen()) {
          <an-bounce-dialog />
          <an-io-labels-dialog />
          <an-group-settings />
          <an-plugin-windows />
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
  private readonly ui = inject(UiStore);
  protected readonly live = inject(LiveMidiStore);
  private readonly audioImport = inject(AudioImport);
  private readonly mixer = inject(MixerStore);

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
    window.anode?.onQuit?.(() => this.session.mayDiscard('quit'));
    window.anode?.onCommand?.(command => void this.command(command));
    // Created at start, so plugin problems are reported wherever the person is.
    inject(PluginWindows);
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
      'project.bounce': () => this.ui.bounceOpen.set(true),
      'mixer.ioLabels': () => this.ui.ioLabelsOpen.set(true),
      'mixer.groupSettings': () => this.openGroupSettings(),
      'track.newAux': () => void this.mixer.addAux(),
      'track.newVca': () => void this.mixer.addVca(),
      'view.musicalTyping': () => this.live.typing.update(on => !on)
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
    }
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
    if (this.live.typeUp(event.code)) {
      event.preventDefault();
    }
  }
}
