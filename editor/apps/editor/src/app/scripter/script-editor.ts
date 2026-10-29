import {
  ChangeDetectionStrategy,
  Component,
  type ElementRef,
  computed,
  effect,
  inject,
  linkedSignal,
  signal,
  viewChild
} from '@angular/core';
import type { Insert, ParameterInfo, ScriptPreset } from '@anode/api-client';
import { AnChoice, AnKnob } from '@anode/audio-ui';
import { provideIcons } from '@ng-icons/core';
import { matExpandMoreRound, matPlayArrowRound } from '@ng-icons/material-icons/round';
import { XuiIconImports } from '@xui/icon';
import { XuiMenuImports } from '@xui/menu';
import { FloatingWindow } from '../shell/floating-window';
import { MidiLibraryStore } from '../state/midi-library.store';
import { MixerStore } from '../state/mixer.store';
import { ProjectStore } from '../state/project.store';
import { type ScriptLine, ScriptLogStore, scriptKey } from '../state/script-log.store';
import { UiStore } from '../state/ui.store';
import { CodeEditor } from './code-editor';
import { StarterScript, currentProblemLine, isRunShortcut, scriptControl } from './script-model';

/**
 * Scripter's Script Editor (docs/08-phase-7.md, ADR-0009): a toolbar (factory presets, Revert, Run), the script in a
 * code editor, the script's `PluginParameters` as controls, and the console (`Trace` lines, dropped counts and
 * problems). Run (⌘↩) sends the text as `setScript`, one undo step; the host compiles it and answers with the
 * parameters (`Insert.runtimeParameters`), its trace lines (`scriptLog`), or a problem (`pluginProblem`) whose line the
 * editor marks until the next run.
 */
@Component({
  selector: 'an-script-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnChoice, AnKnob, CodeEditor, FloatingWindow, XuiIconImports, XuiMenuImports],
  providers: [provideIcons({ matExpandMoreRound, matPlayArrowRound })],
  template: `
    <an-floating-window
      title="Script Editor"
      testId="script-editor"
      width="44rem"
      [origin]="{ x: 200, y: 80 }"
      (closed)="ui.scriptEditor.set(null)"
      (keydown)="key($event)"
    >
      @if (slot(); as s) {
        <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2" role="toolbar" aria-label="Script">
          <button
            type="button"
            class="bezel text-foreground flex h-6.5 items-center gap-1 px-2 text-[11px] font-semibold"
            aria-label="Factory presets"
            data-testid="script-editor-presets"
            [xuiMenuTriggerFor]="presetMenu"
          >
            {{ presetName() ?? 'Presets' }}
            <ng-icon xui name="matExpandMoreRound" size="14px" />
          </button>
          <ng-template #presetMenu>
            <xui-menu class="max-h-96 min-w-48 overflow-y-auto" aria-label="Factory presets">
              @for (preset of presets(); track preset.id) {
                <button
                  xuiMenuItem
                  type="button"
                  [attr.data-testid]="'script-preset-' + preset.id"
                  (click)="loadPreset(preset)"
                >
                  {{ preset.name }}
                </button>
              }
            </xui-menu>
          </ng-template>
          <span class="text-foreground-muted min-w-0 flex-1 truncate text-[11px]" data-testid="script-editor-slot">
            {{ owner() }}: {{ s.name }}
            @if (dirty()) {
              <span class="text-foreground">(edited)</span>
            }
          </span>
          <button
            type="button"
            class="bezel text-foreground flex h-6.5 items-center px-2 text-[11px] font-semibold disabled:opacity-35"
            data-testid="script-editor-revert"
            [disabled]="!dirty()"
            (click)="revert()"
          >
            Revert
          </button>
          <button
            type="button"
            class="bezel text-foreground flex h-6.5 items-center gap-1 px-2 text-[11px] font-semibold"
            data-testid="script-editor-run"
            title="Run (⌘↩)"
            aria-keyshortcuts="Meta+Enter"
            (click)="runScript()"
          >
            <ng-icon xui name="matPlayArrowRound" size="16px" class="text-transport-play" />
            Run
          </button>
        </div>

        <an-code-editor
          class="border-hairline h-72 shrink-0 border-b"
          label="Script"
          testId="script-editor-code"
          [value]="source()"
          [errorLine]="errorLine()"
          (valueChange)="source.set($event)"
          (run)="runScript()"
        />

        @if (parameters().length > 0) {
          <section
            class="border-hairline flex shrink-0 flex-col gap-1 border-b px-3 py-2"
            aria-label="Script parameters"
            data-testid="script-editor-parameters"
          >
            <h4 class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">Parameters</h4>
            <div class="flex flex-wrap items-end gap-x-1 gap-y-2">
              @for (parameter of parameters(); track parameter.id) {
                @switch (control(parameter)) {
                  @case ('choice') {
                    <an-choice
                      [label]="parameter.name"
                      [names]="parameter.valueNames ?? []"
                      [min]="parameter.min"
                      [value]="value(s, parameter)"
                      [attr.data-testid]="'script-parameter-' + parameter.id"
                      (valueChange)="set(s, parameter, $event)"
                    />
                  }
                  @case ('toggle') {
                    <button
                      type="button"
                      class="bezel text-foreground flex h-6.5 items-center px-2 text-[11px] font-semibold"
                      [class.text-lit]="value(s, parameter) >= 0.5"
                      [attr.aria-pressed]="value(s, parameter) >= 0.5"
                      [attr.data-testid]="'script-parameter-' + parameter.id"
                      (click)="set(s, parameter, value(s, parameter) >= 0.5 ? 0 : 1)"
                    >
                      {{ parameter.name }}
                    </button>
                  }
                  @case ('momentary') {
                    <button
                      type="button"
                      class="bezel text-foreground flex h-6.5 items-center px-2 text-[11px] font-semibold"
                      [class.text-lit]="value(s, parameter) >= 0.5"
                      [attr.data-testid]="'script-parameter-' + parameter.id"
                      (pointerdown)="set(s, parameter, 1)"
                      (pointerup)="set(s, parameter, 0)"
                      (pointerleave)="set(s, parameter, 0)"
                      (keydown.space)="set(s, parameter, 1)"
                      (keyup.space)="set(s, parameter, 0)"
                    >
                      {{ parameter.name }}
                    </button>
                  }
                  @case ('label') {
                    <span
                      class="text-foreground-muted flex h-6.5 items-center px-1 text-[11px] font-semibold"
                      [attr.data-testid]="'script-parameter-' + parameter.id"
                    >
                      {{ parameter.name }}
                    </span>
                  }
                  @default {
                    <an-knob
                      [label]="parameter.name"
                      [min]="parameter.min"
                      [max]="parameter.max"
                      [steps]="parameter.steps"
                      [unit]="parameter.unit"
                      [defaultValue]="parameter.default"
                      [value]="value(s, parameter)"
                      [attr.data-testid]="'script-parameter-' + parameter.id"
                      (valueChange)="set(s, parameter, $event)"
                    />
                  }
                }
              }
            </div>
          </section>
        }

        <div class="flex h-6 shrink-0 items-center gap-2 px-3">
          <span
            class="text-foreground-muted flex-1 text-[10px] font-semibold tracking-wide uppercase"
            id="an-script-console"
          >
            Console
          </span>
          <button
            type="button"
            class="text-foreground-muted hover:text-foreground text-[11px] disabled:opacity-35"
            aria-label="Clear console"
            data-testid="script-editor-clear"
            [disabled]="lines().length === 0"
            (click)="clearConsole()"
          >
            Clear
          </button>
        </div>
        <div
          #console
          class="bg-surface-inset border-hairline mx-3 mb-3 h-32 shrink-0 overflow-y-auto rounded border px-2 py-1 font-mono text-[11px] leading-snug"
          role="log"
          aria-live="polite"
          aria-labelledby="an-script-console"
          data-testid="script-editor-console"
          tabindex="0"
        >
          @for (line of lines(); track $index) {
            <div
              class="break-words whitespace-pre-wrap"
              [class.text-foreground]="line.kind === 'trace'"
              [class.text-foreground-muted]="line.kind === 'dropped'"
              [class.text-error]="line.kind === 'problem'"
              [attr.data-kind]="line.kind"
            >
              {{ line.text }}
            </div>
          } @empty {
            <p class="text-foreground-muted font-sans">Trace output and errors appear here.</p>
          }
        </div>
      } @else {
        <p class="text-foreground-muted p-4 text-[12px]">This Scripter slot was removed.</p>
      }
    </an-floating-window>
  `
})
export class ScriptEditor {
  protected readonly ui = inject(UiStore);
  private readonly project = inject(ProjectStore);
  private readonly mixer = inject(MixerStore);
  private readonly log = inject(ScriptLogStore);
  private readonly library = inject(MidiLibraryStore);

  /** The Server's factory scripts (`GET /library/scripts`). */
  protected readonly presets = this.library.scriptPresets;
  protected readonly control = scriptControl;

  private readonly console = viewChild<ElementRef<HTMLElement>>('console');

  /** The slot the window edits. */
  protected readonly target = computed(() => this.ui.scriptEditor());
  protected readonly slot = computed<Insert | undefined>(() => {
    const target = this.target();
    return target ? this.project.track(target.trackId)?.strip.midiEffects.find(i => i.id === target.slotId) : undefined;
  });
  protected readonly owner = computed(() => {
    const target = this.target();
    return target ? (this.project.track(target.trackId)?.name ?? '') : '';
  });
  /** The script as stored (null: none yet). */
  private readonly saved = computed(() => this.slot()?.source ?? null);

  /**
   * The text in the editor: the slot's script (or the starter) when the window opens on a slot; it follows the stored
   * script (an undo, another client) while it is unedited, and keeps edits otherwise.
   */
  readonly source = linkedSignal<{ key: string; saved: string | null }, string>({
    source: () => {
      const target = this.target();
      return { key: target ? scriptKey(target.trackId, target.slotId) : '', saved: this.saved() };
    },
    computation: (next, previous) => {
      const text = next.saved ?? StarterScript;
      if (previous?.source.key !== next.key) {
        return text;
      }
      const unedited = previous.value === (previous.source.saved ?? StarterScript);
      return unedited ? text : previous.value;
    },
    equal: (a, b) => a === b
  });
  protected readonly dirty = computed(() => this.source() !== (this.saved() ?? StarterScript));

  /** The preset last loaded, while the text is still its own. */
  private readonly preset = signal<ScriptPreset | null>(null);
  protected readonly presetName = computed(() => {
    const preset = this.preset();
    return preset?.source === this.source() ? preset.name : null;
  });

  /** The console's lines for the slot. */
  protected readonly lines = computed<readonly ScriptLine[]>(() => {
    const target = this.target();
    return target ? this.log.linesOf(target.trackId, target.slotId) : [];
  });
  /** The last console line when Run was pressed: problems before it no longer mark a line. */
  private readonly runMark = signal<ScriptLine | null>(null);
  protected readonly errorLine = computed(() => currentProblemLine(this.lines(), this.runMark()));

  /** The script's parameters: the stored list, else the latest the host reported. */
  protected readonly parameters = computed<readonly ParameterInfo[]>(() => {
    const target = this.target();
    const stored = this.slot()?.runtimeParameters;
    if (stored) {
      return stored;
    }
    return target ? (this.log.parameters().get(scriptKey(target.trackId, target.slotId)) ?? []) : [];
  });

  constructor() {
    void this.library.loadScriptPresets();
    effect(() => {
      // Keep the newest line in view.
      const count = this.lines().length;
      const element = this.console()?.nativeElement;
      if (element && count > 0) {
        element.scrollTop = element.scrollHeight;
      }
    });
  }

  /** Sends the editor's text to the slot (one undo step). */
  runScript(label = 'Run Script'): Promise<unknown> {
    const target = this.target();
    if (!target || !this.slot()) {
      return Promise.resolve(null);
    }
    this.runMark.set(this.lines().at(-1) ?? null);
    return this.project.execute(
      [{ type: 'setScript', trackId: target.trackId, slotId: target.slotId, source: this.source() }],
      label
    );
  }

  /** Loads a factory preset and runs it, as choosing a plugin setting does. */
  loadPreset(preset: ScriptPreset): Promise<unknown> {
    this.source.set(preset.source);
    this.preset.set(preset);
    return this.runScript(`Load Script “${preset.name}”`);
  }

  /** Back to the stored script. */
  revert(): void {
    this.source.set(this.saved() ?? StarterScript);
  }

  clearConsole(): void {
    const target = this.target();
    if (target) {
      this.log.clear(target.trackId, target.slotId);
      this.runMark.set(null);
    }
  }

  /** A parameter's value: as stored or automated, else its default. */
  protected value(slot: Insert, parameter: ParameterInfo): number {
    const target = this.target();
    if (!target) {
      return parameter.default;
    }
    const known = parameter.id in slot.parameters || slot.runtimeParameters;
    const insert = known ? slot : { ...slot, parameters: { ...slot.parameters, [parameter.id]: parameter.default } };
    return this.mixer.parameterValue(target.trackId, insert, parameter.id);
  }

  /** Sets a parameter (coalesced `setInsertParameter` on the MIDI FX chain; automates `midiEffect:{slot}:{id}`). */
  protected set(slot: Insert, parameter: ParameterInfo, value: number): void {
    const target = this.target();
    if (target && value !== this.value(slot, parameter)) {
      void this.mixer.setParameter(target.trackId, slot.id, parameter.id, value);
    }
  }

  /** ⌘↩ anywhere in the window runs the script (the code editor handles its own). */
  protected key(event: KeyboardEvent): void {
    if (!event.defaultPrevented && isRunShortcut(event)) {
      event.preventDefault();
      void this.runScript();
    }
  }
}
