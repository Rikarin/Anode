import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { DiscoveryInfo } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { ProjectStore } from '../state/project.store';
import { RemoteCommands } from './remote-commands';
import { RemoteLiveLoops } from './remote-live-loops';
import { RemoteMixer } from './remote-mixer';
import { RemotePads } from './remote-pads';
import { RemotePlay } from './remote-play';
import { RemoteSmartControls } from './remote-smart-controls';
import { RemoteStepSequencer } from './remote-step-sequencer';
import { RemoteTracks } from './remote-tracks';
import { RemoteTransport } from './remote-transport';

/** The remote's views (its tabs). The track list is a view on a phone and a column on a tablet. */
export type RemoteView = 'mixer' | 'smart' | 'loops' | 'play' | 'pads' | 'steps' | 'tracks';

export const RemoteViews: readonly { value: RemoteView; label: string }[] = [
  { value: 'mixer', label: 'Mixer' },
  { value: 'smart', label: 'Smart' },
  { value: 'loops', label: 'Loops' },
  { value: 'play', label: 'Play' },
  { value: 'pads', label: 'Pads' },
  { value: 'steps', label: 'Steps' },
  { value: 'tracks', label: 'Tracks' }
];

/**
 * The `remote` route (docs/11-phase-10.md, workstream C): Logic Remote's equivalent for phones and tablets on the LAN,
 * in the same build, lazily loaded. A header names the Server and project (from `/system/discovery`) and the connection;
 * the remote's control bar (transport, LCD, listen-in) sits under it; then one view at a time: the mixer in banks of 8,
 * the selected track's Smart Controls, the Live Loops grid and scenes, the touch keyboard and drum pads (live MIDI),
 * key-command pads, the Step Sequencer and the track list.
 *
 * Touch first: 44 px targets, no hover, `touch-action: none` on the controls that drag, `manipulation` elsewhere (no
 * double-tap zoom). Phones (portrait): the views as a bottom tab bar. Tablets and wider (landscape, ≥ 768 px): the tabs
 * at the top and the track list as a column beside the view.
 */
@Component({
  selector: 'an-remote-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RemoteLiveLoops,
    RemoteMixer,
    RemotePads,
    RemotePlay,
    RemoteSmartControls,
    RemoteStepSequencer,
    RemoteTracks,
    RemoteTransport
  ],
  providers: [RemoteCommands],
  host: {
    class: 'bg-background flex h-full flex-col [touch-action:manipulation]',
    role: 'region',
    'aria-label': 'Remote',
    'data-testid': 'remote'
  },
  template: `
    <header class="glass-bar flex h-10 shrink-0 items-center gap-2 px-3" data-testid="remote-header">
      <span
        class="size-2 shrink-0 rounded-full"
        role="img"
        [class]="events.status() === 'open' ? 'bg-meter-low' : 'bg-meter-mid'"
        [attr.aria-label]="events.status() === 'open' ? 'Connected' : 'Connecting'"
      ></span>
      <span class="text-foreground min-w-0 truncate text-[13px] font-semibold" data-testid="remote-project">
        {{ project.info()?.name ?? 'No project open' }}
      </span>
      @if (discovery(); as d) {
        <span class="text-foreground-muted ms-auto hidden truncate text-[11px] sm:inline" data-testid="remote-server">
          {{ d.name }} · {{ d.host }}:{{ d.port }} · {{ d.version }}
        </span>
      }
    </header>
    @if (project.isOpen()) {
      <an-remote-transport class="shrink-0" />
      <nav
        class="bezel-group text-foreground order-last mx-2 mb-2 flex h-12 shrink-0 text-[12px] font-semibold md:landscape:order-none md:landscape:mt-2 md:landscape:mb-0"
        role="tablist"
        aria-label="Remote views"
      >
        @for (option of views(); track option.value) {
          <button
            type="button"
            role="tab"
            class="min-w-0 flex-1 px-1"
            [attr.data-testid]="'remote-tab-' + option.value"
            [attr.aria-selected]="view() === option.value"
            [class.text-lit]="view() === option.value"
            (click)="view.set(option.value)"
          >
            {{ option.label }}
          </button>
        }
      </nav>
      <div class="flex min-h-0 flex-1 md:landscape:mt-2">
        <an-remote-tracks
          class="border-hairline hidden w-56 shrink-0 border-e md:landscape:flex"
          data-testid="remote-track-column"
        />
        <main class="flex min-h-0 min-w-0 flex-1 flex-col" role="tabpanel" [attr.aria-label]="viewLabel()">
          @switch (view()) {
            @case ('mixer') {
              <an-remote-mixer class="min-h-0 flex-1" />
            }
            @case ('smart') {
              <an-remote-smart-controls class="min-h-0 flex-1 overflow-y-auto" />
            }
            @case ('loops') {
              <an-remote-live-loops class="min-h-0 flex-1" />
            }
            @case ('play') {
              <an-remote-play class="min-h-0 flex-1" />
            }
            @case ('pads') {
              <an-remote-pads class="min-h-0 flex-1" />
            }
            @case ('steps') {
              <an-remote-step-sequencer class="min-h-0 flex-1" />
            }
            @case ('tracks') {
              <an-remote-tracks class="min-h-0 flex-1" />
            }
          }
        </main>
      </div>
    } @else {
      <section class="grid flex-1 place-items-center p-4">
        <p class="panel text-foreground max-w-sm p-5 text-sm" data-testid="remote-no-project">
          No project is open on the Server. Open one in Anode on the computer, and the remote follows.
        </p>
      </section>
    }
  `
})
export class RemotePage {
  protected readonly project = inject(ProjectStore);
  protected readonly events = inject(EventStream);
  private readonly api = inject(ANODE_API);

  readonly view = signal<RemoteView>('mixer');
  protected readonly views = signal(RemoteViews);
  protected readonly viewLabel = computed(() => RemoteViews.find(v => v.value === this.view())?.label ?? '');
  protected readonly discovery = signal<DiscoveryInfo | null>(null);

  constructor() {
    // The commands the pads run are registered for as long as the remote is open.
    inject(RemoteCommands);
    this.api
      .discovery()
      .then(info => this.discovery.set(info))
      .catch(() => undefined); // an older Server: no discovery line
  }
}
