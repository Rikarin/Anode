import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { MtcFrameRate, SyncSource } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import { matCloseRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { Band, Field, selectValue } from '../control/styles';
import { FloatingWindow } from '../shell/floating-window';
import { PluginsStore } from '../state/plugins.store';
import { UiStore } from '../state/ui.store';
import { formatOffset, FrameRates, parseOffset, SyncSources, SyncStore } from './sync.store';

/**
 * The project's Synchronization settings (File ▸ Project Settings ▸ Synchronization…, docs/09-phase-8.md): the sync
 * source (internal, MIDI Clock, MTC or Ableton Link, one at a time), the MIDI outputs sent clock and MTC with their
 * delays, the MTC frame rate and the project's start offset, the clock and MTC inputs, and Link's start/stop sync.
 * Each change is a `setSyncSettings` edit (undoable, saved with the project); the status line is the live state.
 */
@Component({
  selector: 'an-sync-settings-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow, XuiButtonImports, XuiIconImports, XuiInputImports],
  providers: [provideIcons({ matCloseRound })],
  template: `
    <an-floating-window
      title="Synchronization"
      testId="sync-settings"
      width="36rem"
      [origin]="{ x: 220, y: 96 }"
      (closed)="ui.syncSettingsOpen.set(false)"
    >
      @let s = sync.settings();
      <h3 [class]="band">Sync Source</h3>
      <div class="flex flex-col gap-2 p-3 text-xs">
        <div
          class="bezel-group text-foreground h-6.5 self-start text-[11px] font-semibold"
          role="radiogroup"
          aria-label="Sync source"
        >
          @for (source of sources; track source.value) {
            <button
              type="button"
              role="radio"
              class="px-3"
              [attr.data-testid]="'sync-source-' + source.value"
              [attr.aria-checked]="s.source === source.value"
              [class.text-lit]="s.source === source.value"
              (click)="setSource(source.value)"
            >
              {{ source.label }}
            </button>
          }
        </div>
        <p class="text-foreground-muted" role="status" data-testid="sync-status">{{ status() }}</p>
      </div>

      <h3 [class]="band">MIDI Clock and MTC Out</h3>
      <div class="flex flex-col gap-1.5 p-3 text-xs">
        <table
          class="w-full border-separate border-spacing-y-1"
          aria-label="Sync destinations"
          data-testid="sync-destinations"
        >
          <thead>
            <tr class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">
              <th class="text-start font-semibold">Output</th>
              <th class="w-16 font-semibold">Clock</th>
              <th class="w-16 font-semibold">MTC</th>
              <th class="w-24 font-semibold">Delay (ms)</th>
              <th class="w-8"><span class="sr-only">Remove</span></th>
            </tr>
          </thead>
          <tbody>
            @for (destination of s.destinations; track destination.port) {
              <tr data-testid="sync-destination" [attr.data-port]="destination.port">
                <td class="text-foreground truncate font-semibold">{{ portName(destination.port) }}</td>
                <td class="text-center">
                  <input
                    type="checkbox"
                    class="accent-(--color-primary)"
                    [attr.aria-label]="'Send MIDI Clock to ' + portName(destination.port)"
                    data-testid="sync-destination-clock"
                    [checked]="destination.clock"
                    (change)="sync.setDestination(destination.port, { clock: !destination.clock })"
                  />
                </td>
                <td class="text-center">
                  <input
                    type="checkbox"
                    class="accent-(--color-primary)"
                    [attr.aria-label]="'Send MTC to ' + portName(destination.port)"
                    data-testid="sync-destination-mtc"
                    [checked]="destination.mtc"
                    (change)="sync.setDestination(destination.port, { mtc: !destination.mtc })"
                  />
                </td>
                <td class="text-center">
                  <input
                    #delay
                    xuiInput
                    type="number"
                    class="readout h-6 w-20 text-xs"
                    min="-50"
                    max="50"
                    step="0.1"
                    [attr.aria-label]="'Delay of ' + portName(destination.port)"
                    data-testid="sync-destination-delay"
                    [value]="destination.delayMs"
                    (change)="setDelay(destination.port, delay.value)"
                  />
                </td>
                <td>
                  <button
                    xuiButton
                    type="button"
                    size="sm"
                    variant="ghost"
                    class="h-6 w-6 px-0"
                    [attr.aria-label]="'Remove ' + portName(destination.port)"
                    (click)="sync.removeDestination(destination.port)"
                  >
                    <ng-icon xui name="matCloseRound" size="14px" />
                  </button>
                </td>
              </tr>
            } @empty {
              <tr>
                <td colspan="5" class="text-foreground-muted">Anode sends clock and timecode to no output.</td>
              </tr>
            }
          </tbody>
        </table>
        <label class="text-foreground-muted flex items-center gap-2">
          Add output
          <select [class]="field + ' w-48'" data-testid="sync-add-destination" (change)="addDestination($event)">
            <option value="" selected>Choose…</option>
            @for (port of freeOutputs(); track port.id) {
              <option [value]="port.id">{{ port.name }}</option>
            }
          </select>
        </label>
      </div>

      <h3 [class]="band">Timecode and Inputs</h3>
      <div class="grid grid-cols-[8rem_1fr] items-center gap-x-2 gap-y-1.5 p-3 text-xs">
        <label class="text-foreground-muted" for="sync-frame-rate">MTC frame rate</label>
        <select
          id="sync-frame-rate"
          [class]="field + ' w-40'"
          data-testid="sync-frame-rate"
          (change)="setFrameRate($event)"
        >
          @for (rate of frameRates; track rate.value) {
            <option [value]="rate.value" [selected]="rate.value === s.frameRate">{{ rate.label }}</option>
          }
        </select>
        <label class="text-foreground-muted" for="sync-offset">Project start</label>
        <div class="flex items-center gap-2">
          <input
            #offset
            id="sync-offset"
            xuiInput
            class="readout h-6 w-28 text-xs"
            data-testid="sync-offset"
            [value]="offsetText()"
            [attr.aria-invalid]="offsetInvalid(offset.value) ? 'true' : null"
            (change)="setOffset(offset)"
          />
          <span class="text-foreground-muted">hh:mm:ss:ff of timecode at bar 1</span>
        </div>
        <label class="text-foreground-muted" for="sync-clock-in">MIDI Clock in</label>
        <select
          id="sync-clock-in"
          [class]="field + ' w-48'"
          data-testid="sync-clock-in"
          (change)="setInput('clockInPort', $event)"
        >
          <option value="" [selected]="!s.clockInPort">Any input</option>
          @for (port of inputs(); track port.id) {
            <option [value]="port.id" [selected]="port.id === s.clockInPort">{{ port.name }}</option>
          }
        </select>
        <label class="text-foreground-muted" for="sync-mtc-in">MTC in</label>
        <select
          id="sync-mtc-in"
          [class]="field + ' w-48'"
          data-testid="sync-mtc-in"
          (change)="setInput('mtcInPort', $event)"
        >
          <option value="" [selected]="!s.mtcInPort">Any input</option>
          @for (port of inputs(); track port.id) {
            <option [value]="port.id" [selected]="port.id === s.mtcInPort">{{ port.name }}</option>
          }
        </select>
      </div>

      <h3 [class]="band">Ableton Link</h3>
      <div class="flex flex-col gap-1.5 p-3 text-xs">
        <label class="text-foreground flex items-center gap-2">
          <input
            type="checkbox"
            class="accent-(--color-primary)"
            data-testid="sync-link-start-stop"
            [checked]="s.linkStartStop"
            (change)="sync.apply({ linkStartStop: !s.linkStartStop }, 'Link Start/Stop Sync')"
          />
          Start and stop with the Link session
        </label>
        <p class="text-foreground-muted">
          Link shares tempo and beat with apps on this network; it is on when it is the sync source (the control bar’s
          Link button).
        </p>
      </div>
    </an-floating-window>
  `
})
export class SyncSettingsWindow {
  protected readonly sync = inject(SyncStore);
  protected readonly ui = inject(UiStore);
  private readonly plugins = inject(PluginsStore);

  protected readonly band = Band;
  protected readonly field = Field;
  protected readonly sources = SyncSources;
  protected readonly frameRates = FrameRates;
  protected readonly inputs = computed(() => this.plugins.devices().inputs);
  protected readonly freeOutputs = computed(() => {
    const used = new Set(this.sync.settings().destinations.map(d => d.port));
    return this.plugins.devices().outputs.filter(p => !used.has(p.id));
  });
  protected readonly offsetText = computed(() => formatOffset(this.sync.settings().startOffsetSeconds));
  protected readonly status = computed(() => {
    const source = this.sync.sourceInfo()?.label ?? 'Internal';
    switch (this.sync.source()) {
      case 'internal':
        return 'Anode is the clock: it plays at its own tempo.';
      case 'link': {
        const peers = this.sync.linkPeers();
        return `Ableton Link: ${peers === 0 ? 'no other apps' : peers === 1 ? '1 other app' : `${peers} other apps`} in the session.`;
      }
      default: {
        const tempo = this.sync.externalTempo();
        return this.sync.locked()
          ? `Following ${source}${tempo !== null ? ` at ${tempo.toFixed(1)} bpm` : ''}.`
          : `Waiting for ${source}…`;
      }
    }
  });

  protected portName(id: string): string {
    const devices = this.plugins.devices();
    return [...devices.outputs, ...devices.inputs].find(p => p.id === id)?.name ?? id;
  }

  protected setSource(source: SyncSource): void {
    if (source !== this.sync.source()) {
      void this.sync.setSource(source);
    }
  }

  protected addDestination(event: Event): void {
    const port = selectValue(event);
    (event.target as HTMLSelectElement).value = '';
    if (port) {
      void this.sync.addDestination(port);
    }
  }

  protected setDelay(port: string, text: string): void {
    const delayMs = Number(text);
    if (Number.isFinite(delayMs)) {
      void this.sync.setDestination(port, { delayMs: Math.min(50, Math.max(-50, delayMs)) });
    }
  }

  protected setFrameRate(event: Event): void {
    void this.sync.apply({ frameRate: selectValue(event) as MtcFrameRate }, 'MTC Frame Rate');
  }

  protected offsetInvalid(text: string): boolean {
    return parseOffset(text) === null;
  }

  protected setOffset(field: HTMLInputElement): void {
    const seconds = parseOffset(field.value);
    if (seconds === null) {
      return;
    }
    field.value = formatOffset(seconds);
    if (seconds !== this.sync.settings().startOffsetSeconds) {
      void this.sync.apply({ startOffsetSeconds: seconds }, 'Project Start Offset');
    }
  }

  /** A clock or MTC input; "Any input" is the empty string (the command's "any"). */
  protected setInput(field: 'clockInPort' | 'mtcInPort', event: Event): void {
    void this.sync.apply({ [field]: selectValue(event) }, field === 'clockInPort' ? 'MIDI Clock Input' : 'MTC Input');
  }
}
