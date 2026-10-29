import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import type { MidiPort, PluginInfo, Track } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import { matFolderOpenRound, matRefreshRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiSelectImports } from '@xui/select';
import { XuiSpinnerImports } from '@xui/spinner';
import { XuiTooltipImports } from '@xui/tooltip';
import { ConnectionService } from '../core/connection';
import { InstrumentStore } from '../state/instrument.store';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import { ParameterEditor } from './parameter-editor';

/** A device choice in a picker: a port, or every device (null). */
interface DeviceChoice {
  id: string | null;
  name: string;
}

const Channels = Array.from({ length: 17 }, (_, i) => i);

/**
 * The Track inspector for instrument and external MIDI tracks: the instrument (picked from the engine's catalogue,
 * `/plugins`), its parameters, the file it plays for instruments that load one ("Load file…": a native dialog in the
 * desktop app, a path on the Server's disk in a browser), and where the track takes MIDI from and (external MIDI)
 * sends it to, from the engine's ports (`/midi/devices`).
 */
@Component({
  selector: 'an-track-inspector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ParameterEditor,
    XuiButtonImports,
    XuiIconImports,
    XuiInputImports,
    XuiSelectImports,
    XuiSpinnerImports,
    XuiTooltipImports
  ],
  providers: [provideIcons({ matFolderOpenRound, matRefreshRound })],
  host: { class: 'flex flex-col gap-3', role: 'group', '[attr.aria-label]': '"Track: " + track().name' },
  template: `
    <h3
      class="text-foreground border-hairline bg-surface-raised -mx-3 truncate border-y px-3 py-1 text-[11px] font-bold shadow-[inset_0_1px_0_var(--highlight)]"
      [attr.title]="track().name"
    >
      Track: {{ track().name }}
    </h3>
    @if (track().kind === 'instrument') {
      <div class="flex items-center gap-1">
        <xui-select
          class="min-w-0 flex-1"
          aria-label="Instrument"
          data-testid="instrument-picker"
          placeholder="No instrument"
          [items]="plugins.instruments()"
          [itemText]="pluginText"
          [value]="plugin() ?? null"
          (valueChange)="choose($event)"
        />
        <button
          xuiButton
          type="button"
          size="sm"
          variant="ghost"
          aria-label="Rescan plugins"
          xuiTooltip="Rescan the CLAP folders"
          [disabled]="plugins.scanning()"
          (click)="plugins.rescan()"
        >
          @if (plugins.scanning()) {
            <xui-spinner size="sm" aria-label="Scanning plugins" />
          } @else {
            <ng-icon xui name="matRefreshRound" size="sm" />
          }
        </button>
      </div>
      @if (plugin(); as p) {
        @if (p.fileTypes.length > 0) {
          <div class="flex flex-col gap-1 text-xs">
            <span
              class="text-foreground-muted truncate"
              data-testid="instrument-file"
              [attr.title]="track().instrument?.file"
            >
              {{ track().instrument?.file ?? 'No file loaded' }}
            </span>
            @if (desktopDialog) {
              <button xuiButton type="button" size="sm" variant="outline" (click)="loadWithDialog(p)">
                <ng-icon xui name="matFolderOpenRound" size="sm" />
                Load file…
              </button>
            } @else {
              <form class="flex gap-1" (submit)="$event.preventDefault(); loadPath(pathInput.value)">
                <input
                  #pathInput
                  xuiInput
                  class="h-7 min-w-0 flex-1 text-xs"
                  [attr.aria-label]="'Path of a ' + p.fileTypes.join(' or ') + ' file on the server'"
                  [placeholder]="'/path/to/instrument' + (p.fileTypes[0] ?? '')"
                  data-testid="instrument-path"
                />
                <button xuiButton type="submit" size="sm" variant="outline">Load</button>
              </form>
            }
          </div>
        }
        <an-parameter-editor [trackId]="track().id" [plugin]="p" />
      } @else if (track().instrument; as slot) {
        <p class="text-foreground-muted text-xs">
          {{ slot.name }} ({{ slot.pluginId }}) is not in the engine's catalogue.
        </p>
      }
    }
    <section class="flex flex-col gap-1.5 text-xs" aria-label="MIDI input">
      <h4 class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">MIDI In</h4>
      <div class="flex gap-1">
        <xui-select
          class="min-w-0 flex-1"
          aria-label="MIDI input device"
          data-testid="midi-input"
          [items]="inputs()"
          [itemText]="deviceText"
          [filterable]="false"
          [value]="input()"
          (valueChange)="setInput($event, null)"
        />
        <xui-select
          class="an-compact-select w-20"
          aria-label="MIDI input channel"
          [items]="channels"
          [itemText]="inputChannelText"
          [filterable]="false"
          [value]="track().midiInput.channel"
          (valueChange)="setInput(null, $event)"
        />
      </div>
    </section>
    <section class="flex flex-col gap-1.5 text-xs" aria-label="Articulation Set">
      <h4 class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">Articulation Set</h4>
      <div class="flex items-center gap-1">
        <select
          class="border-border bg-surface-inset text-foreground focus-visible:outline-focus h-6 min-w-0 flex-1 rounded-[5px] border px-1 text-xs focus-visible:outline-2"
          aria-label="Articulation set"
          data-testid="articulation-set-select"
          (change)="assignArticulationSet($event)"
        >
          <option value="" [selected]="!track().articulationSetId">None</option>
          @for (set of project.articulationSetList(); track set.id) {
            <option [value]="set.id" [selected]="set.id === track().articulationSetId">{{ set.name }}</option>
          }
        </select>
        <button
          xuiButton
          type="button"
          size="sm"
          variant="ghost"
          aria-label="Edit articulation set"
          data-testid="articulation-set-edit"
          [disabled]="!track().articulationSetId"
          (click)="ui.articulationSetEditor.set(track().articulationSetId ?? null)"
        >
          Edit…
        </button>
        <button
          xuiButton
          type="button"
          size="sm"
          variant="ghost"
          aria-label="New articulation set"
          data-testid="articulation-set-new"
          (click)="ui.articulationSetEditor.set('new')"
        >
          New…
        </button>
      </div>
    </section>
    @if (track().kind === 'midi') {
      <section class="flex flex-col gap-1.5 text-xs" aria-label="MIDI output">
        <h4 class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">MIDI Out</h4>
        <div class="flex gap-1">
          <xui-select
            class="min-w-0 flex-1"
            aria-label="MIDI output device"
            data-testid="midi-output"
            placeholder="No output"
            [items]="outputs()"
            [itemText]="deviceText"
            [filterable]="false"
            [value]="output()"
            (valueChange)="setOutput($event, null)"
          />
          <xui-select
            class="an-compact-select w-20"
            aria-label="MIDI output channel"
            [items]="outputChannels"
            [itemText]="channelText"
            [filterable]="false"
            [value]="track().midiOutput?.channel ?? 1"
            (valueChange)="setOutput(null, $event)"
          />
        </div>
      </section>
    }
  `
})
export class TrackInspector {
  protected readonly plugins = inject(PluginsStore);
  private readonly instruments = inject(InstrumentStore);
  protected readonly project = inject(ProjectStore);
  protected readonly ui = inject(UiStore);
  readonly track = input.required<Track>();

  protected readonly desktopDialog = typeof inject(ConnectionService).desktop?.dialogs?.instrumentFile === 'function';
  protected readonly channels = Channels;
  protected readonly outputChannels = Channels.slice(1);
  protected readonly pluginText = (plugin: PluginInfo) =>
    plugin.format === 'clap' ? `${plugin.name} (CLAP, ${plugin.vendor})` : plugin.name;
  protected readonly deviceText = (device: DeviceChoice) => device.name;
  protected readonly inputChannelText = (channel: number) => (channel === 0 ? 'All' : `Ch ${channel}`);
  protected readonly channelText = (channel: number) => `Ch ${channel}`;
  private readonly loading = signal(false);

  protected readonly plugin = computed(() => this.plugins.plugin(this.track().instrument?.pluginId));
  private readonly allDevices: DeviceChoice = { id: null, name: 'All devices' };
  protected readonly inputs = computed<DeviceChoice[]>(() => {
    const ports: DeviceChoice[] = this.plugins.devices().inputs.map(port);
    const current = this.track().midiInput.device;
    // A device the project names that is not plugged in stays listed, so the picker can show it.
    if (current && !ports.some(p => p.id === current)) {
      ports.push({ id: current, name: `${current} (missing)` });
    }
    return [this.allDevices, ...ports];
  });
  protected readonly input = computed(
    () => this.inputs().find(d => d.id === (this.track().midiInput.device ?? null)) ?? this.allDevices
  );
  protected readonly outputs = computed<DeviceChoice[]>(() => {
    const ports: DeviceChoice[] = this.plugins.devices().outputs.map(port);
    const current = this.track().midiOutput?.device;
    if (current && !ports.some(p => p.id === current)) {
      ports.push({ id: current, name: `${current} (missing)` });
    }
    return ports;
  });
  protected readonly output = computed(
    () => this.outputs().find(d => d.id === this.track().midiOutput?.device) ?? null
  );

  protected choose(plugin: PluginInfo | null): void {
    if (plugin && plugin.id !== this.track().instrument?.pluginId) {
      this.instruments.setInstrument(this.track().id, plugin.id);
    }
  }

  protected async loadWithDialog(plugin: PluginInfo): Promise<void> {
    const dialogs = window.anode?.dialogs;
    if (!dialogs?.instrumentFile || this.loading()) {
      return;
    }
    this.loading.set(true);
    try {
      const path = await dialogs.instrumentFile(plugin.fileTypes);
      if (path) {
        this.instruments.loadFile(this.track().id, path);
      }
    } finally {
      this.loading.set(false);
    }
  }

  protected loadPath(path: string): void {
    this.instruments.loadFile(this.track().id, path);
  }

  /** Gives the track an articulation set of the project, or none (`assignArticulationSet`). */
  protected assignArticulationSet(event: Event): void {
    const setId = (event.target as HTMLSelectElement).value || null;
    if (setId !== (this.track().articulationSetId ?? null)) {
      void this.project.execute(
        [{ type: 'assignArticulationSet', trackId: this.track().id, setId }],
        'Assign Articulation Set'
      );
    }
  }

  protected setInput(device: DeviceChoice | null, channel: number | null): void {
    const current = this.track().midiInput;
    const next = {
      device: device ? device.id : (current.device ?? null),
      channel: channel ?? current.channel
    };
    if (next.device !== (current.device ?? null) || next.channel !== current.channel) {
      this.instruments.setMidiInput(this.track().id, next);
    }
  }

  protected setOutput(device: DeviceChoice | null, channel: number | null): void {
    const current = this.track().midiOutput;
    const id = device?.id ?? current?.device;
    if (!id) {
      return;
    }
    const next = { device: id, channel: channel ?? current?.channel ?? 1 };
    if (next.device !== current?.device || next.channel !== current.channel) {
      this.instruments.setMidiOutput(this.track().id, next);
    }
  }
}

function port(p: MidiPort): DeviceChoice {
  return { id: p.id, name: p.name };
}
