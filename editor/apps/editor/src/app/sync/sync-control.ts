import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { provideIcons } from '@ng-icons/core';
import { matLinkRound } from '@ng-icons/material-icons/round';
import { XuiIconImports } from '@xui/icon';
import { XuiTooltipImports } from '@xui/tooltip';
import { UiStore } from '../state/ui.store';
import { SyncStore } from './sync.store';

/**
 * The control bar's sync controls (docs/09-phase-8.md): the indicator (the source, a lock LED, and the external tempo
 * while following MIDI Clock or MTC) that opens the Synchronization settings, and the Link button, which turns Ableton
 * Link on and off and shows how many other apps are in the session.
 */
@Component({
  selector: 'an-sync-control',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiIconImports, XuiTooltipImports],
  providers: [provideIcons({ matLinkRound })],
  host: { class: 'contents' },
  template: `
    <div class="bezel-group text-foreground h-6.5 text-[11px] font-semibold" role="toolbar" aria-label="Sync">
      <button
        type="button"
        class="flex items-center gap-1.5 px-2"
        data-testid="sync-indicator"
        [attr.aria-label]="label()"
        [attr.data-source]="sync.source()"
        [attr.data-locked]="sync.locked()"
        [xuiTooltip]="tip()"
        (click)="ui.syncSettingsOpen.set(true)"
      >
        <span
          class="size-1.5 shrink-0 rounded-full"
          aria-hidden="true"
          [class.bg-meter-low]="sync.locked()"
          [class.bg-meter-mid]="!sync.locked()"
        ></span>
        <span>{{ sync.sourceInfo()?.short }}</span>
        @if (sync.externalTempo(); as tempo) {
          <span class="readout" data-testid="sync-tempo">{{ tempo.toFixed(1) }}</span>
        }
      </button>
      <button
        type="button"
        class="flex items-center gap-1 px-2 2xl:px-2.5"
        data-testid="link-toggle"
        aria-label="Link"
        [class.text-lit]="sync.linkOn()"
        [attr.aria-pressed]="sync.linkOn() ? 'true' : 'false'"
        [xuiTooltip]="linkTip()"
        (click)="sync.toggleLink()"
      >
        <span class="flex 2xl:hidden"><ng-icon xui name="matLinkRound" size="14px" /></span>
        <span class="max-2xl:sr-only">Link</span>
        @if (sync.linkOn()) {
          <span class="readout" data-testid="link-peers" [attr.aria-label]="sync.linkPeers() + ' peers'">{{
            sync.linkPeers()
          }}</span>
        }
      </button>
    </div>
  `
})
export class SyncControl {
  protected readonly sync = inject(SyncStore);
  protected readonly ui = inject(UiStore);

  protected readonly label = computed(() => {
    const source = this.sync.sourceInfo()?.label ?? 'Internal';
    const tempo = this.sync.externalTempo();
    return `Sync: ${source}, ${this.sync.locked() ? 'locked' : 'not locked'}${tempo !== null ? `, ${tempo.toFixed(1)} bpm` : ''}`;
  });
  protected readonly tip = computed(() => `${this.label()} (Synchronization settings)`);
  protected readonly linkTip = computed(() => {
    if (!this.sync.linkOn()) {
      return 'Ableton Link: share tempo and beat with apps on this network';
    }
    const peers = this.sync.linkPeers();
    return `Ableton Link on: ${peers === 1 ? '1 peer' : `${peers} peers`}`;
  });
}
