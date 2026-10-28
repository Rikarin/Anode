import { ChangeDetectionStrategy, Component, DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import type { PdcMode, Track, TrackKind } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import { matAddRound, matExpandMoreRound, matLabelRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiMenuImports } from '@xui/menu';
import { XuiSegmentedControlImports } from '@xui/segmented-control';
import { XuiTooltipImports } from '@xui/tooltip';
import { KeyCommandsStore } from '../state/key-commands.store';
import { MixerStore } from '../state/mixer.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';
import { MixerStrip, type StripLayout } from './mixer-strip';
import { signalFlow } from './routing';

/** Logic's Mixer views: every strip, the tracks' strips, or the buses (aux and VCA strips) and outputs. */
export type MixerView = 'all' | 'tracks' | 'buses';

/** The strip kinds the filters toggle. */
export type StripFilter = Exclude<TrackKind, 'stack'>;

export const StripFilters: readonly { value: StripFilter; label: string }[] = [
  { value: 'audio', label: 'Audio' },
  { value: 'instrument', label: 'Inst' },
  { value: 'midi', label: 'MIDI' },
  { value: 'aux', label: 'Aux' },
  { value: 'vca', label: 'VCA' }
];

const PdcModes: readonly { value: PdcMode; label: string }[] = [
  { value: 'all', label: 'All' },
  { value: 'tracks', label: 'Audio and Software Instrument Tracks' },
  { value: 'off', label: 'Off' }
];

/** The strips a view shows, in track order (stacks show with the tracks; the master is separate). */
export function visibleStrips(tracks: readonly Track[], view: MixerView, hidden: ReadonlySet<StripFilter>): Track[] {
  return tracks.filter(track => {
    if (track.kind !== 'stack' && hidden.has(track.kind)) {
      return false;
    }
    const bus = track.kind === 'aux' || track.kind === 'vca';
    return view === 'all' || (view === 'buses' ? bus : !bus);
  });
}

/** The Mixer's view and filters: per window, not per project. */
@Injectable({ providedIn: 'root' })
export class MixerViewStore {
  readonly view = signal<MixerView>('all');
  readonly hidden = signal<ReadonlySet<StripFilter>>(new Set());

  toggle(filter: StripFilter): void {
    this.hidden.update(set => {
      const next = new Set(set);
      if (!next.delete(filter)) {
        next.add(filter);
      }
      return next;
    });
  }
}

/**
 * The Mixer, like Logic's: a toolbar (the All / Tracks / Buses and Outputs views, filters by strip kind, new aux and VCA
 * strips, I/O Labels, plugin delay compensation and low-latency mode), then a strip per track in track order, and the
 * master apart on the right. The selected strip's signal flow (the aux strips and outputs it reaches) is highlighted.
 * Edits go through {@link MixerStore}, optimistic and coalesced.
 */
@Component({
  selector: 'an-mixer-pane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MixerStrip,
    XuiButtonImports,
    XuiIconImports,
    XuiMenuImports,
    XuiSegmentedControlImports,
    XuiTooltipImports
  ],
  providers: [provideIcons({ matAddRound, matExpandMoreRound, matLabelRound })],
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Mixer' },
  template: `
    <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2" role="toolbar" aria-label="Mixer tools">
      <xui-segmented-control
        size="sm"
        aria-label="Mixer view"
        data-testid="mixer-view"
        [options]="views"
        [value]="store.view()"
        (valueChange)="store.view.set($event ?? 'all')"
      />
      <div class="bezel-group text-foreground h-6.5 text-[11px] font-semibold" role="group" aria-label="Strip filters">
        @for (filter of filters; track filter.value) {
          <button
            type="button"
            class="px-2"
            [attr.aria-pressed]="!store.hidden().has(filter.value)"
            [attr.aria-label]="'Show ' + filter.label + ' strips'"
            [class.text-lit]="!store.hidden().has(filter.value)"
            (click)="store.toggle(filter.value)"
          >
            {{ filter.label }}
          </button>
        }
      </div>
      <button
        type="button"
        class="bezel text-foreground flex h-6.5 items-center gap-1 px-2 text-[11px] font-semibold"
        aria-label="New aux or VCA strip"
        data-testid="mixer-add"
        [xuiMenuTriggerFor]="addMenu"
      >
        <ng-icon xui name="matAddRound" size="14px" />
        <ng-icon xui name="matExpandMoreRound" size="14px" />
      </button>
      <ng-template #addMenu>
        <xui-menu>
          <button xuiMenuItem type="button" data-testid="new-aux" (click)="mixer.addAux()">
            New Auxiliary Channel Strip
          </button>
          <button xuiMenuItem type="button" data-testid="new-vca" (click)="mixer.addVca()">
            New VCA Channel Strip
          </button>
        </xui-menu>
      </ng-template>
      <div class="ms-auto flex items-center gap-2">
        <button
          type="button"
          class="bezel text-foreground flex h-6.5 items-center gap-1 px-2 text-[11px] font-semibold"
          data-testid="mixer-settings"
          [xuiMenuTriggerFor]="settingsMenu"
        >
          <span class="text-foreground-muted">PDC</span>{{ pdcLabel() }}
          @if (project.settings().mixer.lowLatency) {
            <span class="text-lit">Low Latency</span>
          }
          <ng-icon xui name="matExpandMoreRound" size="14px" />
        </button>
        <ng-template #settingsMenu>
          <xui-menu class="min-w-64">
            <span class="text-foreground-muted px-3 pt-1 text-[10px] font-semibold tracking-wide uppercase">
              Plug-in Delay Compensation
            </span>
            @for (mode of pdcModes; track mode.value) {
              <button
                xuiMenuItem
                type="button"
                role="menuitemradio"
                [attr.aria-checked]="project.settings().mixer.pdc === mode.value"
                (click)="mixer.setMixerSettings({ pdc: mode.value })"
              >
                {{ mode.label }}
              </button>
            }
            <xui-menu-divider />
            <button
              xuiMenuItem
              type="button"
              role="menuitemcheckbox"
              [attr.aria-checked]="project.settings().mixer.lowLatency"
              (click)="mixer.setMixerSettings({ lowLatency: !project.settings().mixer.lowLatency })"
            >
              Low Latency Mode ({{ project.settings().mixer.lowLatencyLimitMs }} ms)
            </button>
          </xui-menu>
        </ng-template>
        <button
          xuiButton
          type="button"
          size="sm"
          variant="ghost"
          data-testid="open-io-labels"
          xuiTooltip="I/O Labels"
          aria-label="I/O Labels"
          (click)="ui.ioLabelsOpen.set(true)"
        >
          <ng-icon xui name="matLabelRound" size="sm" />
        </button>
      </div>
    </div>
    <div class="bg-surface-sunken flex min-h-0 flex-1 overflow-auto" data-testid="mixer-strips">
      @for (track of strips(); track track.id) {
        <an-mixer-strip
          [track]="track"
          [layout]="layout()"
          [selected]="selected() === track.id"
          [flow]="flow().has(track.id)"
        />
      } @empty {
        <p class="text-foreground-muted self-center p-4 text-xs">
          @if (project.trackList().length === 0) {
            No tracks yet.
          } @else {
            No strips in this view: pick another view or turn a filter on.
          }
        </p>
      }
      <div class="border-hairline ms-auto border-e"></div>
      <an-mixer-strip [track]="null" [layout]="layout()" [flow]="flow().has('master')" />
    </div>
  `
})
export class MixerPane {
  protected readonly project = inject(ProjectStore);
  protected readonly mixer = inject(MixerStore);
  protected readonly ui = inject(UiStore);
  protected readonly store = inject(MixerViewStore);
  private readonly selection = inject(SelectionStore);

  protected readonly views: { value: MixerView; label: string }[] = [
    { value: 'all', label: 'All' },
    { value: 'tracks', label: 'Tracks' },
    { value: 'buses', label: 'Buses and Outputs' }
  ];
  protected readonly filters = StripFilters;
  protected readonly pdcModes = PdcModes;
  protected readonly selected = this.selection.track;
  protected readonly strips = computed(() =>
    visibleStrips(this.project.trackList(), this.store.view(), this.store.hidden())
  );
  /** Rows per section, the most any shown strip needs (an empty insert slot and the Send slot included). */
  protected readonly layout = computed<StripLayout>(() => {
    const strips = this.strips();
    const master = this.project.master();
    return {
      input: strips.some(t => t.kind === 'aux'),
      inserts: Math.max(master.inserts.length, ...strips.map(t => t.strip.inserts.length)) + 1,
      sends: Math.max(0, ...strips.map(t => t.strip.sends.length)) + 1
    };
  });
  protected readonly flow = computed(() => {
    const flow = signalFlow(this.project.trackList(), this.selected());
    flow.delete(this.selected() ?? '');
    return flow;
  });
  protected readonly pdcLabel = computed(() => {
    const pdc = this.project.settings().mixer.pdc;
    return pdc === 'all' ? 'All' : pdc === 'tracks' ? 'Tracks' : 'Off';
  });

  constructor() {
    const keys = inject(KeyCommandsStore);
    const register: [string, () => void][] = [
      ['mixer.viewAll', () => this.store.view.set('all')],
      ['mixer.viewTracks', () => this.store.view.set('tracks')],
      ['mixer.viewBuses', () => this.store.view.set('buses')]
    ];
    const unregister = register.map(([id, action]) => keys.register(id, action));
    inject(DestroyRef).onDestroy(() => unregister.forEach(off => off()));
  }
}
