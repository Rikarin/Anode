import { ChangeDetectionStrategy, Component, computed, inject, linkedSignal, signal } from '@angular/core';
import type { ControlSurface, ControlSurfaceKind, OscClient, OscSettings } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import { matArrowDownwardRound, matArrowUpwardRound, matCloseRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiTooltipImports } from '@xui/tooltip';
import { FloatingWindow } from '../shell/floating-window';
import { UiStore } from '../state/ui.store';
import { ControlStore, SurfaceKindNames } from './control.store';
import { Band, Field, selectValue } from './styles';

const Kinds: readonly ControlSurfaceKind[] = ['mackieControl', 'mackieControlXt', 'hui'];

/** Strips per surface: a Mackie Control or XT has eight; HUI eight too. */
const Strips = 8;

/**
 * Control Surfaces Setup (Logic Pro ▸ Control Surfaces ▸ Setup…, docs/09-phase-8.md): the surfaces in the order
 * extenders follow (each with its kind, ports, connection and bank, moved up and down or removed), a row to add one by
 * kind on a port pair from the MIDI device list (Anode's virtual ports included), and the OSC server: on or off, its
 * port, the addresses clients reach this machine at, the clients that always get feedback, and those that subscribed.
 */
@Component({
  selector: 'an-control-surfaces-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow, XuiButtonImports, XuiIconImports, XuiInputImports, XuiTooltipImports],
  providers: [provideIcons({ matArrowDownwardRound, matArrowUpwardRound, matCloseRound })],
  template: `
    <an-floating-window
      title="Control Surfaces Setup"
      testId="control-surfaces"
      width="40rem"
      [origin]="{ x: 180, y: 88 }"
      (closed)="ui.controlSurfacesOpen.set(false)"
    >
      <h3 [class]="band">Control Surfaces</h3>
      <ol class="flex flex-col py-1" aria-label="Control surfaces" data-testid="surface-list">
        @for (surface of control.surfaces(); track surface.id; let first = $first; let last = $last) {
          <li
            class="flex h-8 items-center gap-2 px-3 text-xs"
            data-testid="surface-row"
            [attr.data-surface]="surface.id"
          >
            <span
              class="size-2 shrink-0 rounded-full"
              role="img"
              [class.bg-meter-low]="connected(surface)"
              [class.bg-meter-unlit]="!connected(surface)"
              [attr.aria-label]="connected(surface) ? 'Connected' : 'Not connected'"
              [attr.data-connected]="connected(surface)"
            ></span>
            <span class="text-foreground w-36 shrink-0 truncate font-semibold">{{ kindName(surface.kind) }}</span>
            <span class="text-foreground-muted min-w-0 flex-1 truncate">
              {{ control.portName(surface.inputPort) }} → {{ control.portName(surface.outputPort) }}
            </span>
            <span class="text-foreground-muted readout shrink-0" data-testid="surface-bank">{{ bank(surface) }}</span>
            <div class="bezel-group text-foreground h-6 shrink-0">
              <button
                type="button"
                class="grid w-7 place-items-center disabled:opacity-35"
                [attr.aria-label]="'Move ' + kindName(surface.kind) + ' up'"
                xuiTooltip="Earlier in the bank order"
                [attr.disabled]="first ? '' : null"
                (click)="control.moveSurface(surface.id, -1)"
              >
                <ng-icon xui name="matArrowUpwardRound" size="14px" />
              </button>
              <button
                type="button"
                class="grid w-7 place-items-center disabled:opacity-35"
                [attr.aria-label]="'Move ' + kindName(surface.kind) + ' down'"
                xuiTooltip="Later in the bank order"
                [attr.disabled]="last ? '' : null"
                (click)="control.moveSurface(surface.id, 1)"
              >
                <ng-icon xui name="matArrowDownwardRound" size="14px" />
              </button>
            </div>
            <button
              xuiButton
              type="button"
              size="sm"
              variant="ghost"
              color="error"
              data-testid="surface-remove"
              [attr.aria-label]="'Remove ' + kindName(surface.kind)"
              (click)="control.removeSurface(surface.id)"
            >
              Remove
            </button>
          </li>
        } @empty {
          <li class="text-foreground-muted px-3 py-1 text-xs">
            No control surfaces. Add a Mackie Control, an extender or a HUI on its MIDI ports.
          </li>
        }
      </ol>
      <form
        class="border-hairline flex flex-wrap items-end gap-2 border-t px-3 py-2 text-xs"
        aria-label="Add a control surface"
        (submit)="$event.preventDefault(); add()"
      >
        <label class="text-foreground-muted flex flex-col gap-1">
          Kind
          <select [class]="field + ' w-40'" data-testid="surface-kind" (change)="kind.set(kindOf(value($event)))">
            @for (k of kinds; track k) {
              <option [value]="k" [selected]="k === kind()">{{ kindName(k) }}</option>
            }
          </select>
        </label>
        <label class="text-foreground-muted flex flex-col gap-1">
          Input
          <select [class]="field + ' w-40'" data-testid="surface-input" (change)="inputPort.set(value($event))">
            @for (port of control.inputs(); track port.id) {
              <option [value]="port.id" [selected]="port.id === inputPort()" [disabled]="used(port.id)">
                {{ port.name }}
              </option>
            }
          </select>
        </label>
        <label class="text-foreground-muted flex flex-col gap-1">
          Output
          <select [class]="field + ' w-40'" data-testid="surface-output" (change)="outputPort.set(value($event))">
            @for (port of control.outputs(); track port.id) {
              <option [value]="port.id" [selected]="port.id === outputPort()" [disabled]="used(port.id)">
                {{ port.name }}
              </option>
            }
          </select>
        </label>
        <button xuiButton type="submit" size="sm" data-testid="surface-add" [disabled]="!canAdd()">Add</button>
      </form>

      <h3 [class]="band">OSC</h3>
      @if (control.osc(); as osc) {
        <div class="grid grid-cols-[7rem_1fr] items-center gap-x-2 gap-y-1.5 p-3 text-xs" data-testid="osc-settings">
          <span class="text-foreground-muted">Server</span>
          <label class="text-foreground flex items-center gap-2">
            <input
              type="checkbox"
              class="accent-(--color-primary)"
              data-testid="osc-enabled"
              [checked]="osc.settings.enabled"
              (change)="setOsc({ enabled: !osc.settings.enabled })"
            />
            Listen for OSC
          </label>
          <label class="text-foreground-muted" for="osc-port">UDP port</label>
          <div class="flex items-center gap-2">
            <input
              #port
              id="osc-port"
              xuiInput
              type="number"
              class="readout h-6 w-24 text-xs"
              min="1024"
              max="65535"
              data-testid="osc-port"
              [value]="osc.settings.port"
              (change)="setPort(port.value)"
            />
            <span class="text-foreground-muted" role="status" data-testid="osc-status">
              {{ osc.listening ? 'Listening' : osc.settings.enabled ? 'Not listening' : 'Off' }}
            </span>
          </div>
          <span class="text-foreground-muted self-start pt-0.5">This machine</span>
          <ul class="flex flex-col gap-0.5" aria-label="Addresses for OSC clients" data-testid="osc-addresses">
            @for (address of osc.addresses; track address) {
              <li class="text-foreground readout select-text">{{ hostPort(address, osc.settings.port) }}</li>
            } @empty {
              <li class="text-foreground-muted">Turn the server on to reach it from other devices.</li>
            }
          </ul>
          <span class="text-foreground-muted self-start pt-0.5">Subscribed</span>
          <ul class="flex flex-col gap-0.5" aria-label="Subscribed clients" data-testid="osc-subscribed">
            @for (client of osc.subscribed; track client.host + client.port) {
              <li class="text-foreground readout">{{ client.host }}:{{ client.port }}</li>
            } @empty {
              <li class="text-foreground-muted">No client has sent /anode/subscribe.</li>
            }
          </ul>
          <span class="text-foreground-muted self-start pt-0.5">Feedback to</span>
          <div class="flex flex-col gap-1">
            <ul class="flex flex-col gap-0.5" aria-label="Feedback clients" data-testid="osc-clients">
              @for (client of osc.settings.clients; track client.host + client.port; let i = $index) {
                <li class="flex items-center gap-2">
                  <span class="text-foreground readout flex-1">{{ client.host }}:{{ client.port }}</span>
                  <button
                    xuiButton
                    type="button"
                    size="sm"
                    variant="ghost"
                    class="h-6 w-6 px-0"
                    [attr.aria-label]="'Remove ' + client.host + ':' + client.port"
                    (click)="removeClient(i)"
                  >
                    <ng-icon xui name="matCloseRound" size="14px" />
                  </button>
                </li>
              }
            </ul>
            <form
              class="flex items-center gap-1.5"
              aria-label="Add a feedback client"
              (submit)="$event.preventDefault(); addClient(host.value, clientPort.value)"
            >
              <input
                #host
                xuiInput
                class="h-6 w-36 text-xs"
                aria-label="Client host"
                placeholder="192.168.1.20"
                data-testid="osc-client-host"
              />
              <input
                #clientPort
                xuiInput
                type="number"
                class="readout h-6 w-20 text-xs"
                aria-label="Client port"
                placeholder="9000"
                data-testid="osc-client-port"
              />
              <button xuiButton type="submit" size="sm" variant="outline">Add</button>
            </form>
          </div>
        </div>
      } @else {
        <p class="text-foreground-muted p-3 text-xs">Reading the OSC settings…</p>
      }
    </an-floating-window>
  `
})
export class ControlSurfacesWindow {
  protected readonly control = inject(ControlStore);
  protected readonly ui = inject(UiStore);
  protected readonly band = Band;
  protected readonly field = Field;
  protected readonly value = selectValue;
  protected readonly kinds = Kinds;

  protected readonly kind = signal<ControlSurfaceKind>('mackieControl');
  private readonly usedPorts = computed(
    () => new Set(this.control.surfaces().flatMap(s => [s.inputPort, s.outputPort]))
  );
  /** The first free input and output (Anode's virtual pair when it is free, as most controllers' drivers are not). */
  protected readonly inputPort = linkedSignal(() =>
    this.firstFree(
      this.control.inputs().map(p => p.id),
      'anode:in'
    )
  );
  protected readonly outputPort = linkedSignal(() =>
    this.firstFree(
      this.control.outputs().map(p => p.id),
      'anode:out'
    )
  );
  protected readonly canAdd = computed(
    () => !!this.inputPort() && !!this.outputPort() && !this.used(this.inputPort()) && !this.used(this.outputPort())
  );

  constructor() {
    void this.control.load();
  }

  protected kindName(kind: ControlSurfaceKind): string {
    return SurfaceKindNames[kind];
  }

  protected kindOf(value: string): ControlSurfaceKind {
    return value as ControlSurfaceKind;
  }

  protected used(port: string): boolean {
    return this.usedPorts().has(port);
  }

  protected connected(surface: ControlSurface): boolean {
    return this.control.surfaceStatus().get(surface.id)?.connected ?? false;
  }

  /** The tracks the surface's strips show, `Tracks 1–8`. */
  protected bank(surface: ControlSurface): string {
    const offset = this.control.surfaceStatus().get(surface.id)?.bankOffset ?? 0;
    return `Tracks ${offset + 1}–${offset + Strips}`;
  }

  protected add(): void {
    if (this.canAdd()) {
      void this.control.addSurface(this.kind(), this.inputPort(), this.outputPort());
    }
  }

  protected setOsc(change: Partial<OscSettings>): void {
    const osc = this.control.osc();
    if (osc) {
      void this.control.setOsc({ ...osc.settings, ...change });
    }
  }

  protected setPort(text: string): void {
    const port = Number(text);
    if (Number.isInteger(port) && port !== this.control.osc()?.settings.port) {
      this.setOsc({ port });
    }
  }

  protected addClient(host: string, port: string): void {
    const client: OscClient = { host: host.trim(), port: Number(port) };
    const osc = this.control.osc();
    if (osc && client.host && Number.isInteger(client.port)) {
      this.setOsc({ clients: [...osc.settings.clients, client] });
    }
  }

  protected removeClient(index: number): void {
    const osc = this.control.osc();
    if (osc) {
      this.setOsc({ clients: osc.settings.clients.filter((_, i) => i !== index) });
    }
  }

  /** An address with the port, IPv6 in brackets. */
  protected hostPort(address: string, port: number): string {
    return address.includes(':') ? `[${address}]:${port}` : `${address}:${port}`;
  }

  private firstFree(ids: readonly string[], preferred: string): string {
    const used = this.usedPorts();
    if (ids.includes(preferred) && !used.has(preferred)) {
      return preferred;
    }
    return ids.find(id => !used.has(id)) ?? '';
  }
}
