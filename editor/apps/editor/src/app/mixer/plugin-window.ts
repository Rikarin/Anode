import { NgComponentOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import type { EditCommand, Insert, ParameterInfo, PluginInfo } from '@anode/api-client';
import { AnChoice, AnKnob, AnReadingMeter } from '@anode/audio-ui';
import { provideIcons } from '@ng-icons/core';
import { matCloseRound, matPowerSettingsNewRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { ConnectionService } from '../core/connection';
import { isChoice, parameterGroups } from '../inspector/parameter-editor';
import { type PluginPanelHost, pluginPanel } from '../plugin-panels/plugin-panel';
import { MeterBus } from '../state/meters';
import { MixerStore } from '../state/mixer.store';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { type PluginWindow, UiStore } from '../state/ui.store';
import { pluginLabel } from './plugin-menu';

/** How often an open window reads its plugin's reading (the meters stream at about 30 frames a second). */
const ReadingMs = 100;

/** The reading a plugin shows: gain reduction for dynamics, loudness for meters, a level otherwise. */
export function readingKind(plugin: PluginInfo | undefined): {
  kind: 'reduction' | 'level';
  min: number;
  label: string;
  unit: string;
} {
  if (plugin?.category === 'Metering') {
    return { kind: 'level', min: -60, label: 'Loudness', unit: 'LUFS' };
  }
  if (plugin?.category === 'Dynamics') {
    return { kind: 'reduction', min: -30, label: 'Gain Reduction', unit: 'dB' };
  }
  return { kind: 'level', min: -60, label: 'Level', unit: 'dB' };
}

/**
 * A generic plugin editor (Logic's Controls view): a floating window over the main window, dragged by its title bar,
 * with the insert's bypass, its reading (a compressor's gain reduction, a meter's loudness) when the engine reports one,
 * and a knob or choice per parameter, grouped. A knob drag sends coalesced `setInsertParameter` edits (one undo step per
 * drag), which the Server records into automation while playing in touch, latch or write.
 */
@Component({
  selector: 'an-plugin-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnChoice, AnKnob, AnReadingMeter, NgComponentOutlet, XuiButtonImports, XuiIconImports],
  providers: [provideIcons({ matCloseRound, matPowerSettingsNewRound })],
  host: {
    class: 'panel fixed z-40 flex max-h-[70vh] w-[26rem] flex-col overflow-hidden rounded-lg',
    role: 'dialog',
    'aria-modal': 'false',
    '[attr.aria-label]': 'title()',
    '[style.left.px]': 'position().x',
    '[style.top.px]': 'position().y',
    '(pointerdown)': 'front()',
    '(keydown.escape)': 'close()'
  },
  template: `
    <div
      class="glass-bar flex h-8 shrink-0 cursor-grab touch-none items-center gap-2 ps-1.5 pe-1 select-none"
      (pointerdown)="grab($event)"
      (pointermove)="drag($event)"
      (pointerup)="release($event)"
      (pointercancel)="release($event)"
    >
      @if (insert(); as i) {
        <button
          type="button"
          class="bezel grid h-5.5 w-6 place-items-center"
          data-testid="plugin-window-bypass"
          [class.text-lit]="!i.bypassed"
          [attr.aria-pressed]="i.bypassed"
          [attr.aria-label]="'Bypass ' + i.name"
          (pointerdown)="$event.stopPropagation()"
          (click)="mixer.toggleBypass(window().trackId, i)"
        >
          <ng-icon xui name="matPowerSettingsNewRound" size="14px" />
        </button>
      }
      <span class="text-foreground min-w-0 flex-1 truncate text-[12px] font-semibold">{{ title() }}</span>
      <button
        xuiButton
        type="button"
        size="sm"
        variant="ghost"
        class="h-6 w-6 px-0"
        [attr.aria-label]="'Close ' + title()"
        (pointerdown)="$event.stopPropagation()"
        (click)="close()"
      >
        <ng-icon xui name="matCloseRound" size="sm" />
      </button>
    </div>
    @if (insert(); as i) {
      <div class="flex min-h-0 flex-col gap-3 overflow-y-auto p-3" [attr.data-testid]="'plugin-window-' + i.pluginId">
        @if (reading() !== null) {
          <an-reading-meter
            class="w-full"
            [label]="readingInfo().label"
            [kind]="readingInfo().kind"
            [min]="readingInfo().min"
            [unit]="readingInfo().unit"
            [value]="reading()"
          />
        }
        @if (loadsFile()) {
          <form
            class="flex items-center gap-1.5"
            aria-label="Impulse response"
            (submit)="$event.preventDefault(); loadFile(i, filePath.value)"
          >
            <span class="text-foreground min-w-0 flex-1 truncate text-[11px]" data-testid="plugin-file">
              {{ fileOf(i) ?? 'No file loaded' }}
            </span>
            @if (desktopDialog) {
              <button xuiButton type="button" size="sm" data-testid="plugin-import-file" (click)="chooseFile(i)">
                Import IR…
              </button>
            }
            <input
              #filePath
              class="bg-surface-inset border-hairline text-foreground h-6 w-32 rounded border px-1.5 text-[11px]"
              aria-label="File path"
              data-testid="plugin-file-path"
              placeholder="/path/to/ir.wav"
            />
            <button xuiButton type="submit" size="sm" variant="outline">Load</button>
          </form>
        }
        @if (panel(); as p) {
          <ng-container *ngComponentOutlet="p; inputs: { host: host() }" />
        }
        @for (group of groups(); track group.name) {
          <section class="flex flex-col gap-1" [attr.aria-label]="group.name">
            <h4 class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">{{ group.name }}</h4>
            <div class="flex flex-wrap items-end gap-x-1 gap-y-2">
              @for (parameter of group.parameters; track parameter.id) {
                @if (choice(parameter)) {
                  <an-choice
                    [label]="parameter.name"
                    [names]="parameter.valueNames ?? []"
                    [min]="parameter.min"
                    [value]="value(i, parameter)"
                    [attr.data-parameter]="parameter.id"
                    (valueChange)="set(i, parameter, $event)"
                  />
                } @else {
                  <an-knob
                    [label]="parameter.name"
                    [min]="parameter.min"
                    [max]="parameter.max"
                    [steps]="parameter.steps"
                    [unit]="parameter.unit"
                    [defaultValue]="parameter.default"
                    [value]="value(i, parameter)"
                    [attr.data-parameter]="parameter.id"
                    (valueChange)="set(i, parameter, $event)"
                  />
                }
              }
            </div>
          </section>
        } @empty {
          <p class="text-foreground-muted text-xs">
            @if (plugin()) {
              {{ i.name }} has no parameters.
            } @else {
              {{ i.name }} ({{ i.pluginId }}) is not in the engine's plugin catalogue.
            }
          </p>
        }
      </div>
    } @else {
      <p class="text-foreground-muted p-3 text-xs">This insert was removed.</p>
    }
  `
})
export class PluginWindowView {
  protected readonly mixer = inject(MixerStore);
  private readonly project = inject(ProjectStore);
  private readonly dialogs = inject(ConnectionService).desktop?.dialogs;
  protected readonly desktopDialog = typeof this.dialogs?.instrumentFile === 'function';
  private readonly plugins = inject(PluginsStore);
  private readonly ui = inject(UiStore);
  private readonly meters = inject(MeterBus);

  readonly window = input.required<PluginWindow>();
  /** Where it opens (it cascades from the others). */
  readonly origin = input({ x: 120, y: 120 });

  private readonly moved = signal<{ x: number; y: number } | null>(null);
  private grabbed: { dx: number; dy: number; pointer: number } | null = null;
  protected readonly position = computed(() => this.moved() ?? this.origin());
  protected readonly choice = isChoice;

  protected readonly insert = computed<Insert | undefined>(() => {
    const { trackId, insertId, chain } = this.window();
    const strip = trackId === null ? this.project.master() : this.project.track(trackId)?.strip;
    return (chain === 'midiEffects' ? strip?.midiEffects : strip?.inserts)?.find(i => i.id === insertId);
  });
  protected readonly plugin = computed(() => this.plugins.plugin(this.insert()?.pluginId));
  /** A custom panel for the plugin (drawbars, Delay Designer taps…) when its parameters match one; else none. */
  protected readonly panel = computed(() => pluginPanel(this.plugin()));
  protected readonly host = computed<PluginPanelHost | null>(() => {
    const plugin = this.plugin();
    const insert = this.insert();
    if (!plugin || !insert) {
      return null;
    }
    return {
      plugin,
      value: id => this.mixer.parameterValue(this.window().trackId, insert, id),
      set: (id, value) => void this.mixer.setParameter(this.window().trackId, insert.id, id, value)
    };
  });
  protected readonly groups = computed(() => parameterGroups(this.plugin()?.parameters ?? []));
  protected readonly title = computed(() => {
    const trackId = this.window().trackId;
    const owner = trackId === null ? 'Stereo Out' : (this.project.track(trackId)?.name ?? '');
    const plugin = this.plugin();
    const name = plugin ? pluginLabel(plugin) : (this.insert()?.name ?? 'Plugin');
    return `${owner}: ${name}`;
  });
  protected readonly readingInfo = computed(() => readingKind(this.plugin()));
  protected readonly reading = signal<number | null>(null);

  constructor() {
    const timer = setInterval(() => {
      const { trackId, insertId } = this.window();
      const value = this.meters.reading(trackId, insertId);
      if (value !== this.reading()) {
        this.reading.set(value);
      }
    }, ReadingMs);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  protected value(insert: Insert, parameter: ParameterInfo): number {
    return this.mixer.parameterValue(this.window().trackId, insert, parameter.id);
  }

  protected set(insert: Insert, parameter: ParameterInfo, value: number): void {
    if (value !== this.value(insert, parameter)) {
      void this.mixer.setParameter(this.window().trackId, insert.id, parameter.id, value);
    }
  }

  /** An effect that loads a file (the convolution reverb's impulse response). */
  protected readonly loadsFile = computed(() => {
    const plugin = this.plugin();
    return !!plugin && !plugin.isInstrument && plugin.fileTypes.length > 0;
  });

  /** The file an insert plays (`file`, set by `loadInsertFile`). */
  protected fileOf(insert: Insert): string | null {
    return insert.file ?? null;
  }

  protected async chooseFile(insert: Insert): Promise<void> {
    const path = await this.dialogs?.instrumentFile?.(this.plugin()?.fileTypes ?? ['.wav']);
    if (path) {
      this.loadFile(insert, path);
    }
  }

  /** Loads a file into an insert ("Import IR…"). */
  protected loadFile(insert: Insert, path: string): void {
    const trimmed = path.trim();
    if (!trimmed) {
      return;
    }
    const trackId = this.window().trackId;
    const command: EditCommand = {
      type: 'loadInsertFile',
      insertId: insert.id,
      path: trimmed,
      trackId: trackId ?? null
    };
    void this.project.execute([command], 'Import Impulse Response');
  }

  protected close(): void {
    this.ui.closePluginWindow(this.window());
  }

  /** Brings this window to the front. */
  protected front(): void {
    const list = this.ui.pluginWindows();
    if (list.at(-1) !== this.window()) {
      this.ui.openPluginWindow(this.window());
    }
  }

  protected grab(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    const { x, y } = this.position();
    this.grabbed = { dx: event.clientX - x, dy: event.clientY - y, pointer: event.pointerId };
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
  }

  protected drag(event: PointerEvent): void {
    if (this.grabbed?.pointer !== event.pointerId) {
      return;
    }
    const x = Math.min(window.innerWidth - 80, Math.max(0, event.clientX - this.grabbed.dx));
    const y = Math.min(window.innerHeight - 32, Math.max(0, event.clientY - this.grabbed.dy));
    this.moved.set({ x, y });
  }

  protected release(event: PointerEvent): void {
    if (this.grabbed?.pointer === event.pointerId) {
      this.grabbed = null;
    }
  }
}

/** The open generic plugin windows, cascading from the top left of the window. */
@Component({
  selector: 'an-plugin-windows',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PluginWindowView],
  template: `
    @for (window of ui.pluginWindows(); track window.trackId + '/' + window.insertId; let i = $index) {
      <an-plugin-window [window]="window" [origin]="{ x: 160 + 28 * (i % 8), y: 110 + 28 * (i % 8) }" />
    }
  `
})
export class PluginWindowsHost {
  protected readonly ui = inject(UiStore);
}
