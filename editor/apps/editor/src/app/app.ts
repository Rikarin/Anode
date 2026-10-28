import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { XuiButtonImports } from '@xui/button';
import { XuiInputImports } from '@xui/input';
import { XuiNonIdealStateImports } from '@xui/non-ideal-state';
import { XuiSpinnerImports } from '@xui/spinner';
import type { AppCommand } from './core/desktop';
import { Startup } from './core/startup';
import { ControlBar } from './shell/control-bar';
import { HistoryStore } from './state/history.store';
import { LiveMidiStore } from './state/live-midi.store';
import { ProjectSession } from './state/project-session';
import { ProjectStore } from './state/project.store';
import { SelectionStore } from './state/selection.store';
import { TransportStore } from './state/transport.store';
import { plural } from './tracks/region-edit';

@Component({
  selector: 'an-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ControlBar, RouterOutlet, XuiButtonImports, XuiInputImports, XuiNonIdealStateImports, XuiSpinnerImports],
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
      }
    }
  `
})
export class App {
  protected readonly startup = inject(Startup);
  private readonly transport = inject(TransportStore);
  private readonly project = inject(ProjectStore);
  private readonly history = inject(HistoryStore);
  private readonly session = inject(ProjectSession);
  private readonly selection = inject(SelectionStore);
  protected readonly live = inject(LiveMidiStore);

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
    void this.startup.start();
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
    }
  }

  /** Back to this computer's own engine (the window reloads on it). */
  protected async useThisComputer(): Promise<void> {
    await window.anode?.setTarget({ mode: 'local' });
  }

  /**
   * The key commands until the key-command registry lands (docs/01-implementation-plan.md §6.1): musical typing first
   * (A W S E D F T G Y H U J K play the selected instrument track, Z and X move the octave), then Space, R, C and
   * Return for the transport, Delete for the selected regions, ⌘Z / ⇧⌘Z for undo and redo, ⌘S to save. Text fields
   * keep their own keys. It returns nothing on purpose: an Angular listener that returns false cancels the event
   * everywhere.
   */
  protected onKeydown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    if (!this.project.isOpen() || event.altKey || target?.closest('input, textarea, [contenteditable]')) {
      return;
    }

    const key = event.key.toLowerCase();
    const command = event.metaKey || event.ctrlKey;
    if (!command && !event.shiftKey && this.live.typeDown(event.code, event.repeat)) {
      event.preventDefault();
      return;
    }

    const action = command
      ? {
          z: () => void (event.shiftKey ? this.history.redo() : this.history.undo()),
          y: () => void this.history.redo(),
          s: () => void this.session.save()
        }[key]
      : {
          ' ': () => this.transport.togglePlay(),
          r: () => this.transport.record(),
          c: () => this.transport.toggleCycle(),
          enter: () => this.transport.goToStart(),
          delete: () => this.deleteSelection(),
          backspace: () => this.deleteSelection()
        }[key];
    // Space and Return on a focused button press it; the shortcuts are for the rest of the window.
    if (action && !(target?.closest('button, [role="slider"]') && (key === ' ' || key === 'enter'))) {
      event.preventDefault();
      action();
    }
  }

  protected onKeyup(event: KeyboardEvent): void {
    if (this.live.typeUp(event.code)) {
      event.preventDefault();
    }
  }

  private deleteSelection(): void {
    const ids = [...this.selection.regions()];
    if (ids.length > 0) {
      void this.project.execute([{ type: 'deleteRegions', regionIds: ids }], plural('Delete', ids.length));
    }
  }
}
