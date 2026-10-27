import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { XuiButtonImports } from '@xui/button';
import { XuiInputImports } from '@xui/input';
import { XuiNonIdealStateImports } from '@xui/non-ideal-state';
import { XuiSpinnerImports } from '@xui/spinner';
import { Startup } from './core/startup';
import { ControlBar } from './shell/control-bar';
import { TransportStore } from './state/transport.store';

@Component({
  selector: 'an-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ControlBar, RouterOutlet, XuiButtonImports, XuiInputImports, XuiNonIdealStateImports, XuiSpinnerImports],
  host: { class: 'flex h-full flex-col', '(document:keydown)': 'onKeydown($event)' },
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
    void this.startup.start();
  }

  /** Back to this computer's own engine (the window reloads on it). */
  protected async useThisComputer(): Promise<void> {
    await window.anode?.setTarget({ mode: 'local' });
  }

  /**
   * The transport keys (Space, R, C) until the key-command registry lands (docs/01-implementation-plan.md §6.1). It
   * returns nothing on purpose: an Angular listener that returns false cancels the event everywhere.
   */
  protected onKeydown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    if (event.metaKey || event.ctrlKey || event.altKey || target?.closest('input, textarea, [contenteditable]')) {
      return;
    }

    const action = {
      ' ': () => this.togglePlay(),
      r: () => this.transport.record(),
      c: () => this.transport.toggleCycle()
    }[event.key.toLowerCase()];
    if (action) {
      event.preventDefault();
      action();
    }
  }

  private togglePlay(): void {
    if (this.transport.playing()) {
      this.transport.stop();
    } else {
      this.transport.play();
    }
  }
}
