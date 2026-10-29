import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import type { PatternRow } from '@anode/api-client';
import { AnPatternRow, type StepEdit, type StepEditMode } from '@anode/audio-ui';
import { provideIcons } from '@ng-icons/core';
import {
  matAddRound,
  matArrowBackRound,
  matArrowForwardRound,
  matCasinoRound,
  matCloseRound,
  matExpandMoreRound,
  matFiberManualRecordRound,
  matShuffleRound,
  matSwapHorizRound
} from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiMenuImports } from '@xui/menu';
import { XuiTooltipImports } from '@xui/tooltip';
import { noteName } from '../midi/midi-math';
import { patternDraw, stepAt } from '../music/pattern';
import { TransportStore } from '../state/transport.store';
import { PatternTemplates, PlayModes, rateLabel, rowName, RowRates, stepsPerBeat, unusedPads } from './pattern-edit';
import { SpinField } from './spin-field';
import { StepSequencerStore } from './step-sequencer.store';

const Modes: readonly { value: StepEditMode; label: string }[] = [
  { value: 'onOff', label: 'On/Off' },
  { value: 'velocity', label: 'Velocity' },
  { value: 'gate', label: 'Gate' },
  { value: 'repeats', label: 'Repeats' },
  { value: 'chance', label: 'Chance' },
  { value: 'offset', label: 'Offset' }
];

const PlayModeIcons: Record<PatternRow['playMode'], string> = {
  forward: 'matArrowForwardRound',
  reverse: 'matArrowBackRound',
  pingPong: 'matSwapHorizRound',
  random: 'matShuffleRound'
};

/** Reads a note typed as a number (60) or a name as Logic writes them (C3, F#2, Bb-1). */
export function parseNote(text: string): number | null {
  const t = text.trim();
  if (/^-?\d+$/.test(t)) {
    return Number(t);
  }
  const match = /^([A-Ga-g])([#♯b♭]?)(-?\d)$/.exec(t);
  if (!match) {
    return null;
  }
  const base = { c: 0, d: 2, e: 4, f: 5, g: 7, a: 9, b: 11 }[(match[1] ?? 'c').toLowerCase()] ?? 0;
  const accidental = match[2] === '#' || match[2] === '♯' ? 1 : match[2] ? -1 : 0;
  return (Number(match[3]) + 2) * 12 + base + accidental;
}

/**
 * The Step Sequencer (Logic's pattern editor) for the selected pattern region (docs/07-phase-6.md): a header with the
 * region's name, the edit mode, swing, Randomise, Reseed, templates, step and live record and Convert to MIDI; then a
 * grid of rows, each with its header (name, mute, length, rate, play mode, note, delete) and its steps. With no
 * pattern region selected, guidance and New Pattern Region.
 */
@Component({
  selector: 'an-step-sequencer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnPatternRow, SpinField, XuiButtonImports, XuiIconImports, XuiMenuImports, XuiTooltipImports],
  providers: [
    provideIcons({
      matAddRound,
      matArrowBackRound,
      matArrowForwardRound,
      matCasinoRound,
      matCloseRound,
      matExpandMoreRound,
      matFiberManualRecordRound,
      matShuffleRound,
      matSwapHorizRound
    })
  ],
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Step Sequencer' },
  template: `
    @if (store.region(); as region) {
      @if (store.pattern(); as pattern) {
        <div
          class="glass-bar flex min-h-9 shrink-0 flex-wrap items-center gap-2 px-2 py-1"
          role="toolbar"
          aria-label="Step Sequencer tools"
        >
          <span class="text-foreground max-w-40 truncate text-[12px] font-semibold" data-testid="pattern-region-name">
            {{ region.name }}
          </span>
          <div
            class="bezel-group text-foreground h-6.5 text-[11px] font-semibold"
            role="radiogroup"
            aria-label="Edit mode"
            data-testid="step-edit-mode"
          >
            @for (mode of modes; track mode.value) {
              <button
                type="button"
                role="radio"
                class="px-2"
                [class.text-lit]="store.mode() === mode.value"
                [attr.aria-checked]="store.mode() === mode.value"
                (click)="store.mode.set(mode.value)"
              >
                {{ mode.label }}
              </button>
            }
          </div>
          <label class="text-foreground flex items-center gap-1.5 text-[11px] font-semibold">
            Swing
            <input
              type="range"
              class="accent-primary w-20"
              min="0"
              max="100"
              step="1"
              data-testid="pattern-swing"
              [value]="swingPercent()"
              [attr.aria-valuetext]="swingPercent() + ' %'"
              (input)="swing($event, false)"
              (change)="swing($event, true)"
            />
            <span class="readout w-8 text-end font-normal">{{ swingPercent() }} %</span>
          </label>
          <button
            type="button"
            class="bezel text-foreground flex h-6.5 items-center gap-1 px-2 text-[11px] font-semibold"
            data-testid="pattern-randomize"
            [xuiMenuTriggerFor]="randomMenu"
          >
            <ng-icon xui name="matCasinoRound" size="14px" />
            Randomise
            <ng-icon xui name="matExpandMoreRound" size="14px" />
          </button>
          <ng-template #randomMenu>
            <xui-menu>
              <button xuiMenuItem type="button" (click)="store.randomise('steps')">Randomise Steps</button>
              <button xuiMenuItem type="button" (click)="store.randomise('velocity')">Randomise Velocity</button>
              <xui-menu-divider />
              <button
                xuiMenuItem
                type="button"
                [disabled]="!store.selectedRow()"
                (click)="store.randomise('steps', true)"
              >
                Randomise Selected Row Steps
              </button>
              <button
                xuiMenuItem
                type="button"
                [disabled]="!store.selectedRow()"
                (click)="store.randomise('velocity', true)"
              >
                Randomise Selected Row Velocity
              </button>
            </xui-menu>
          </ng-template>
          <button
            type="button"
            class="bezel text-foreground h-6.5 px-2 text-[11px] font-semibold"
            data-testid="pattern-reseed"
            xuiTooltip="A new seed for the steps' chance: they play differently"
            (click)="store.reseed()"
          >
            Reseed
          </button>
          <button
            type="button"
            class="bezel text-foreground flex h-6.5 items-center gap-1 px-2 text-[11px] font-semibold"
            data-testid="pattern-templates"
            [xuiMenuTriggerFor]="templateMenu"
          >
            Templates
            <ng-icon xui name="matExpandMoreRound" size="14px" />
          </button>
          <ng-template #templateMenu>
            <xui-menu>
              @for (template of templates; track template.id) {
                @if (template.id === 'clear') {
                  <xui-menu-divider />
                }
                <button xuiMenuItem type="button" (click)="store.applyTemplate(template)">{{ template.label }}</button>
              }
            </xui-menu>
          </ng-template>
          <div class="bezel-group text-foreground h-6.5 text-[11px] font-semibold">
            <button
              type="button"
              class="flex items-center gap-1 px-2"
              data-testid="pattern-step-record"
              xuiTooltip="Step record: each note played lights the next step of its row"
              [attr.aria-pressed]="store.stepRecord() ? 'true' : 'false'"
              [class.text-lit]="store.stepRecord()"
              (click)="store.toggleStepRecord()"
            >
              <ng-icon xui class="text-transport-record" name="matFiberManualRecordRound" size="12px" />
              Step Record
            </button>
            <button
              type="button"
              class="flex items-center gap-1 px-2"
              data-testid="pattern-live-record"
              xuiTooltip="Live record: while playing, notes light the step under the playhead"
              [attr.aria-pressed]="store.liveRecord() ? 'true' : 'false'"
              [class.text-lit]="store.liveRecord()"
              (click)="store.toggleLiveRecord()"
            >
              <ng-icon xui class="text-transport-record" name="matFiberManualRecordRound" size="12px" />
              Live Record
            </button>
          </div>
          <button
            type="button"
            class="bezel text-foreground ms-auto h-6.5 px-2 text-[11px] font-semibold"
            data-testid="convert-pattern-to-midi"
            xuiTooltip="Replace the pattern region with a MIDI region of the notes it plays"
            (click)="store.convertToMidi()"
          >
            Convert to MIDI
          </button>
        </div>
        <div class="min-h-0 flex-1 overflow-auto">
          <div role="grid" aria-label="Pattern" class="w-fit min-w-full" [attr.aria-rowcount]="pattern.rows.length">
            @for (row of pattern.rows; track row.id) {
              <an-pattern-row
                [name]="names().get(row.id) ?? row.name"
                [steps]="row.steps"
                [mode]="store.mode()"
                [playhead]="playheads().get(row.id) ?? null"
                [stepsPerBeat]="beat()"
                [headerWidth]="headerWidth"
                [muted]="row.muted"
                [selected]="store.selectedRow() === row.id"
                (toggled)="store.toggleStep(row.id, $event)"
                (valueChange)="value(row.id, $event, false)"
                (committed)="value(row.id, $event, true)"
              >
                @let name = names().get(row.id) ?? row.name;
                <button
                  type="button"
                  class="text-foreground hover:bg-muted min-w-0 flex-1 truncate rounded-[4px] px-1 text-start text-[11px] font-semibold"
                  [attr.aria-pressed]="store.selectedRow() === row.id ? 'true' : 'false'"
                  [attr.aria-label]="'Select ' + name"
                  (click)="store.selectedRow.set(store.selectedRow() === row.id ? null : row.id)"
                >
                  {{ name }}
                </button>
                <button
                  type="button"
                  class="state-toggle toggle-mute"
                  [attr.aria-label]="'Mute ' + name"
                  [attr.aria-pressed]="row.muted ? 'true' : 'false'"
                  (click)="store.toggleRowMute(row.id)"
                >
                  M
                </button>
                <an-spin-field
                  class="w-8"
                  [label]="name + ' length'"
                  [value]="row.length"
                  [min]="1"
                  [max]="64"
                  [bigStep]="4"
                  (committed)="store.setRowLength(row.id, $event)"
                />
                <button
                  type="button"
                  class="bezel text-foreground readout h-5 w-11 px-1 text-[10px]"
                  [attr.aria-label]="name + ' rate'"
                  [xuiTooltip]="'Rate: ' + rateLabel(row.stepTicks)"
                  [xuiMenuTriggerFor]="rateMenu"
                >
                  {{ rateLabel(row.stepTicks) }}
                </button>
                <ng-template #rateMenu>
                  <xui-menu>
                    @for (rate of rates; track rate.label) {
                      <button
                        xuiMenuItem
                        type="button"
                        role="menuitemradio"
                        [attr.aria-checked]="(row.stepTicks ?? null) === rate.ticks"
                        (click)="store.setRowRate(row.id, rate.ticks)"
                      >
                        {{ rate.label }}
                      </button>
                    }
                  </xui-menu>
                </ng-template>
                <button
                  type="button"
                  class="bezel text-foreground grid h-5 w-6 place-items-center"
                  [attr.aria-label]="name + ' play mode'"
                  [xuiTooltip]="'Play mode: ' + playModeLabel(row.playMode)"
                  [xuiMenuTriggerFor]="playMenu"
                >
                  <ng-icon xui [name]="playModeIcons[row.playMode]" size="14px" />
                </button>
                <ng-template #playMenu>
                  <xui-menu>
                    @for (mode of playModes; track mode.value) {
                      <button
                        xuiMenuItem
                        type="button"
                        role="menuitemradio"
                        [attr.aria-checked]="row.playMode === mode.value"
                        (click)="store.setRowPlayMode(row.id, mode.value)"
                      >
                        {{ mode.label }}
                      </button>
                    }
                  </xui-menu>
                </ng-template>
                @if (row.kind === 'pad' && store.pads(); as pads) {
                  <button
                    type="button"
                    class="bezel text-foreground readout h-5 w-11 px-1 text-[10px]"
                    [attr.aria-label]="name + ' note'"
                    [xuiTooltip]="'Pad: ' + name + ' (' + noteName(row.note) + ')'"
                    [xuiMenuTriggerFor]="padMenu"
                  >
                    {{ noteName(row.note) }}
                  </button>
                  <ng-template #padMenu>
                    <xui-menu>
                      @for (pad of pads; track pad.note) {
                        <button
                          xuiMenuItem
                          type="button"
                          role="menuitemradio"
                          [attr.aria-checked]="row.note === pad.note"
                          (click)="store.setRowNote(row.id, pad.note)"
                        >
                          {{ pad.name }} ({{ noteName(pad.note) }})
                        </button>
                      }
                    </xui-menu>
                  </ng-template>
                } @else if (row.kind === 'chordDegree') {
                  <an-spin-field
                    class="w-11"
                    [label]="name + ' note'"
                    [value]="row.note"
                    [min]="1"
                    [max]="7"
                    [bigStep]="1"
                    (committed)="store.setRowNote(row.id, $event)"
                  />
                } @else {
                  <an-spin-field
                    class="w-11"
                    [label]="name + ' note'"
                    [value]="row.note"
                    [min]="0"
                    [max]="127"
                    [bigStep]="12"
                    [format]="noteName"
                    [parse]="parseNote"
                    (committed)="store.setRowNote(row.id, $event)"
                  />
                }
                <button
                  type="button"
                  class="text-foreground-muted hover:text-foreground grid size-5 place-items-center rounded-[4px]"
                  [attr.aria-label]="'Delete ' + name"
                  (click)="store.removeRow(row.id)"
                >
                  <ng-icon xui name="matCloseRound" size="14px" />
                </button>
              </an-pattern-row>
            }
          </div>
          <div class="flex h-9 items-center px-2">
            <button
              type="button"
              class="bezel text-foreground flex h-6.5 items-center gap-1 px-2 text-[11px] font-semibold"
              data-testid="add-pattern-row"
              [xuiMenuTriggerFor]="addMenu"
            >
              <ng-icon xui name="matAddRound" size="14px" />
              Add Row
            </button>
            <ng-template #addMenu>
              <xui-menu>
                @if (store.pads(); as pads) {
                  @for (pad of unused(); track pad.note) {
                    <button xuiMenuItem type="button" (click)="store.addRow('pad', pad.note)">
                      {{ pad.name }} ({{ noteName(pad.note) }})
                    </button>
                  } @empty {
                    <button xuiMenuItem type="button" disabled>Every pad has a row</button>
                  }
                } @else {
                  <button xuiMenuItem type="button" (click)="store.addRow('note', 60)">Note Row</button>
                  <button xuiMenuItem type="button" (click)="store.addRow('chordDegree', 1)">Chord Degree Row</button>
                }
              </xui-menu>
            </ng-template>
          </div>
        </div>
      }
    } @else {
      <div class="flex min-h-0 flex-1 flex-col items-center justify-center gap-3 p-6 text-center">
        <p class="text-foreground-muted max-w-80 text-[12px]">
          Select a pattern region to edit its steps, or make one on the selected instrument or Drum Machine Designer
          track at the playhead.
        </p>
        <button
          xuiButton
          type="button"
          size="sm"
          variant="outline"
          data-testid="new-pattern-region"
          [disabled]="!store.targetTrack()"
          (click)="store.newRegion()"
        >
          New Pattern Region
        </button>
      </div>
    }
  `
})
export class StepSequencer {
  protected readonly store = inject(StepSequencerStore);
  private readonly transport = inject(TransportStore);

  protected readonly modes = Modes;
  protected readonly templates = PatternTemplates;
  protected readonly rates = RowRates;
  protected readonly playModes = PlayModes;
  protected readonly playModeIcons = PlayModeIcons;
  protected readonly headerWidth = 280;
  protected readonly noteName = noteName;
  protected readonly parseNote = parseNote;
  protected readonly rateLabel = rateLabel;

  /** The playhead, in ticks, to 40 ticks: read from animation frames while playing. */
  private readonly position = signal(0);

  protected readonly swingPercent = computed(() => Math.round((this.store.pattern()?.swing ?? 0) * 100));
  protected readonly beat = computed(() => {
    const pattern = this.store.pattern();
    return pattern ? stepsPerBeat(pattern) : 4;
  });
  protected readonly names = computed(() => {
    const pads = this.store.pads() ?? [];
    return new Map((this.store.pattern()?.rows ?? []).map(row => [row.id, rowName(row, pads)]));
  });
  protected readonly unused = computed(() => {
    const pattern = this.store.pattern();
    return pattern ? unusedPads(pattern, this.store.pads() ?? []) : [];
  });
  /** The step each row plays under the playhead (its own length, rate and play mode), while playing over the region. */
  protected readonly playheads = computed(() => {
    const playheads = new Map<string, number>();
    const region = this.store.region();
    const pattern = this.store.pattern();
    if (!region || !pattern || !this.transport.playing()) {
      return playheads;
    }
    const tick = this.position() - region.startTick;
    if (tick < 0 || tick >= region.lengthTicks) {
      return playheads;
    }
    for (const row of pattern.rows) {
      const stepTicks = row.stepTicks ?? pattern.stepTicks;
      if (stepTicks > 0) {
        const draw = (step: string, pass: number) => patternDraw(pattern.seed, region.id, row.id, step, pass);
        playheads.set(row.id, stepAt(row, Math.floor(tick / stepTicks), draw));
      }
    }
    return playheads;
  });

  constructor() {
    effect(onCleanup => {
      if (!this.transport.playing() || !this.store.region()) {
        return;
      }
      let frame = 0;
      const read = () => {
        // To 40 ticks (a 32nd triplet), so the rows redraw only when a step can change.
        this.position.set(Math.floor(this.transport.positionAt(performance.now()) / 40) * 40);
        frame = requestAnimationFrame(read);
      };
      read();
      onCleanup(() => cancelAnimationFrame(frame));
    });
  }

  protected playModeLabel(mode: PatternRow['playMode']): string {
    return PlayModes.find(m => m.value === mode)?.label ?? mode;
  }

  protected value(rowId: string, edit: StepEdit, done: boolean): void {
    const mode = this.store.mode();
    if (mode !== 'onOff') {
      void this.store.setStep(rowId, edit.index, mode, edit.value, done);
    }
  }

  protected swing(event: Event, done: boolean): void {
    const percent = Number((event.target as HTMLInputElement).value);
    void this.store.setSwing(percent / 100, done);
  }
}
