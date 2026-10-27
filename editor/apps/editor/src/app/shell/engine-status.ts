import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { XuiTooltipImports } from '@xui/tooltip';
import { EventStream } from '../core/events';
import { SystemStore } from '../state/system.store';

/** A dot and a word for the Server connection and its engine host, with the versions in the tooltip. */
@Component({
  selector: 'an-engine-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiTooltipImports],
  template: `
    <span
      class="flex items-center gap-1.5 text-xs"
      tabindex="0"
      data-testid="engine-status"
      [attr.data-state]="state()"
      [xuiTooltip]="detail()"
    >
      <span class="size-2 rounded-full" [class]="dot()" aria-hidden="true"></span>
      <span class="text-foreground-muted">{{ label() }}</span>
    </span>
  `
})
export class EngineStatus {
  private readonly system = inject(SystemStore);
  private readonly events = inject(EventStream);

  /** The engine's state, or `offline` while the Server's event stream is down. */
  protected readonly state = computed(() =>
    this.events.status() === 'open' ? (this.system.engineState() ?? 'unknown') : 'offline'
  );

  protected readonly label = computed(() => {
    switch (this.state()) {
      case 'connected':
        return 'Engine';
      case 'inprocess':
        return 'Engine (in-process)';
      case 'disconnected':
        return 'No engine';
      case 'offline':
        return 'Offline';
      default:
        return 'Unknown';
    }
  });

  protected readonly dot = computed(() => {
    const state = this.state();
    return state === 'connected' || state === 'inprocess'
      ? 'bg-success'
      : state === 'disconnected'
        ? 'bg-warning'
        : 'bg-error';
  });

  protected readonly detail = computed(() => {
    const info = this.system.info();
    if (!info) {
      return 'Not connected to anode-server';
    }

    const host = info.engine.hostVersion ? `, anode-host ${info.engine.hostVersion}` : '';
    const message = info.engine.message ? ` (${info.engine.message})` : '';
    return `anode-server ${info.version}${host}${message}`;
  });
}
