import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  input,
  model,
  numberAttribute,
  output
} from '@angular/core';
import { AnPianoKeyboard, pitchName } from './piano-keyboard';

/** A note value of the Step Input Keyboard: its length in ticks (960 a quarter), its label and its spoken name. */
export interface StepNoteValue {
  ticks: number;
  label: string;
  name: string;
}

/** Logic's note values, longest first (the default for `noteValues`). */
export const StepNoteValues: readonly StepNoteValue[] = [
  { ticks: 3840, label: '1', name: 'Whole note' },
  { ticks: 1920, label: '1/2', name: 'Half note' },
  { ticks: 960, label: '1/4', name: 'Quarter note' },
  { ticks: 480, label: '1/8', name: 'Eighth note' },
  { ticks: 240, label: '1/16', name: 'Sixteenth note' },
  { ticks: 120, label: '1/32', name: 'Thirty-second note' },
  { ticks: 60, label: '1/64', name: 'Sixty-fourth note' },
  { ticks: 30, label: '1/128', name: 'Hundred-twenty-eighth note' }
];

/** Logic's velocity presets, ppp to fff. */
export const StepVelocities: readonly { label: string; value: number }[] = [
  { label: 'ppp', value: 16 },
  { label: 'pp', value: 32 },
  { label: 'p', value: 48 },
  { label: 'mp', value: 64 },
  { label: 'mf', value: 80 },
  { label: 'f', value: 96 },
  { label: 'ff', value: 112 },
  { label: 'fff', value: 127 }
];

const Lowest = 0;
const Highest = 120;

/**
 * Logic's Step Input Keyboard, the presentational part: the note value (a radio group, 1 … 1/128), dotted and triplet,
 * the velocity (ppp … fff presets and a value), Rest, Tie and Chord, and a keyboard with an octave shift. A key entered
 * makes a step: the keys that went down together make one chord, sent (`step`) when the first of them is released.
 * The owner holds the state (the inputs) and applies the changes it is told of.
 */
@Component({
  selector: 'an-step-keyboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnPianoKeyboard],
  host: {
    class: 'flex flex-col gap-2 select-none',
    role: 'group',
    '[attr.aria-label]': 'label()'
  },
  template: `
    <div class="flex flex-wrap items-center gap-2" role="toolbar" [attr.aria-label]="label() + ' options'">
      <div
        class="bezel-group text-foreground h-6.5 text-[11px] font-semibold"
        role="radiogroup"
        aria-label="Note value"
        data-testid="step-note-value"
      >
        @for (value of noteValues(); track value.ticks) {
          <button
            type="button"
            role="radio"
            class="readout px-1.5"
            [class.text-lit]="value.ticks === noteValue()"
            [attr.aria-checked]="value.ticks === noteValue()"
            [attr.title]="value.name"
            [attr.tabindex]="value.ticks === focusValue() ? 0 : -1"
            [attr.data-testid]="'step-value-' + value.ticks"
            [disabled]="disabled()"
            (click)="noteValueChange.emit(value.ticks)"
            (keydown)="arrowValue($event, value.ticks)"
          >
            {{ value.label }}
          </button>
        }
      </div>
      <div class="bezel-group text-foreground h-6.5 text-[11px] font-semibold">
        <button
          type="button"
          class="px-2"
          aria-label="Dotted"
          title="Dotted: one and a half times the note value"
          data-testid="step-dotted"
          [class.text-lit]="dotted()"
          [attr.aria-pressed]="dotted()"
          [disabled]="disabled()"
          (click)="dottedChange.emit(!dotted())"
        >
          <svg viewBox="0 0 14 14" class="size-3.5" aria-hidden="true">
            <ellipse cx="5" cy="10" rx="3" ry="2.2" fill="currentColor" transform="rotate(-20 5 10)" />
            <path d="M7.6 9.4V1.5" stroke="currentColor" stroke-width="1.2" />
            <circle cx="11.5" cy="10" r="1.3" fill="currentColor" />
          </svg>
        </button>
        <button
          type="button"
          class="px-2"
          aria-label="Triplet"
          title="Triplet: two thirds of the note value"
          data-testid="step-triplet"
          [class.text-lit]="triplet()"
          [attr.aria-pressed]="triplet()"
          [disabled]="disabled()"
          (click)="tripletChange.emit(!triplet())"
        >
          <svg viewBox="0 0 10 14" class="h-3.5 w-2.5" aria-hidden="true">
            <path
              d="M2 2.5h5.5L4.6 6.4c1.9 0 3.4 1.1 3.4 2.9 0 1.9-1.5 3.2-3.4 3.2-1.3 0-2.3-.5-2.9-1.3"
              fill="none"
              stroke="currentColor"
              stroke-width="1.4"
              stroke-linecap="round"
              stroke-linejoin="round"
            />
          </svg>
        </button>
      </div>
    </div>
    <div class="flex flex-wrap items-center gap-2">
      <div
        class="bezel-group text-foreground h-6.5 text-[11px] font-semibold italic"
        role="radiogroup"
        aria-label="Velocity preset"
        data-testid="step-velocity-presets"
      >
        @for (preset of velocities; track preset.label) {
          <button
            type="button"
            role="radio"
            class="px-1.5"
            [class.text-lit]="preset.value === velocity()"
            [attr.aria-checked]="preset.value === velocity()"
            [attr.aria-label]="preset.label + ', velocity ' + preset.value"
            [attr.tabindex]="preset.label === focusVelocity() ? 0 : -1"
            [attr.data-testid]="'step-velocity-' + preset.label"
            [disabled]="disabled()"
            (click)="velocityChange.emit(preset.value)"
            (keydown)="arrowVelocity($event, preset.label)"
          >
            {{ preset.label }}
          </button>
        }
      </div>
      <label class="text-foreground-muted flex items-center gap-1 text-[11px]">
        Velocity
        <input
          #velocityField
          type="number"
          min="1"
          max="127"
          class="border-border bg-surface-inset text-foreground focus-visible:outline-focus readout h-6 w-12 rounded-[5px] border px-1 text-xs focus-visible:outline-2"
          aria-label="Velocity"
          data-testid="step-velocity"
          [value]="velocity()"
          [disabled]="disabled()"
          (change)="setVelocity(velocityField)"
          (keydown.enter)="setVelocity(velocityField)"
        />
      </label>
      <div class="bezel-group text-foreground ms-auto h-6.5 text-[11px] font-semibold">
        <button
          type="button"
          class="px-2"
          title="Rest: advance by one step"
          data-testid="step-rest"
          [disabled]="disabled()"
          (click)="rest.emit()"
        >
          Rest
        </button>
        <button
          type="button"
          class="px-2"
          title="Tie: lengthen the last step by one step"
          data-testid="step-tie"
          [disabled]="disabled()"
          (click)="tie.emit()"
        >
          Tie
        </button>
        <button
          type="button"
          class="px-2"
          title="Chord: keys stack at one position until Chord is turned off"
          data-testid="step-chord"
          [class.text-lit]="chord()"
          [attr.aria-pressed]="chord()"
          [disabled]="disabled()"
          (click)="chordChange.emit(!chord())"
        >
          Chord
        </button>
      </div>
    </div>
    <div class="flex items-stretch gap-1">
      <button
        type="button"
        class="bezel text-foreground w-6 shrink-0 text-[11px] font-semibold"
        aria-label="Octave down"
        data-testid="step-octave-down"
        [title]="'Octave down (from ' + lowestName() + ')'"
        [disabled]="disabled() || from() <= lowest"
        (click)="shift(-12)"
      >
        <svg viewBox="0 0 8 8" class="mx-auto size-2.5" aria-hidden="true">
          <path d="M5.5 1.5L3 4l2.5 2.5" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" />
        </svg>
      </button>
      <div class="well min-w-0 flex-1 overflow-x-auto rounded-[5px] p-1">
        <an-piano-keyboard
          class="h-20"
          label="Step input keys"
          data-testid="step-keys"
          [from]="from()"
          [octaves]="octaves()"
          [keyWidth]="keyWidth()"
          [disabled]="disabled()"
          (noteOn)="down($event.pitch)"
          (noteOff)="up($event)"
        />
      </div>
      <button
        type="button"
        class="bezel text-foreground w-6 shrink-0 text-[11px] font-semibold"
        aria-label="Octave up"
        data-testid="step-octave-up"
        [title]="'Octave up (from ' + lowestName() + ')'"
        [disabled]="disabled() || from() >= highest"
        (click)="shift(12)"
      >
        <svg viewBox="0 0 8 8" class="mx-auto size-2.5" aria-hidden="true">
          <path d="M2.5 1.5L5 4 2.5 6.5" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" />
        </svg>
      </button>
    </div>
  `
})
export class AnStepKeyboard {
  readonly label = input('Step Input Keyboard');
  readonly noteValues = input<readonly StepNoteValue[]>(StepNoteValues);
  /** The note value, in ticks. */
  readonly noteValue = input(240, { transform: numberAttribute });
  readonly dotted = input(false, { transform: booleanAttribute });
  readonly triplet = input(false, { transform: booleanAttribute });
  readonly velocity = input(100, { transform: numberAttribute });
  readonly chord = input(false, { transform: booleanAttribute });
  readonly disabled = input(false, { transform: booleanAttribute });
  /** The keyboard's lowest key (a C); the octave buttons move it. */
  readonly from = model(48);
  readonly octaves = input(3, { transform: numberAttribute });
  readonly keyWidth = input(18, { transform: numberAttribute });

  readonly noteValueChange = output<number>();
  readonly dottedChange = output<boolean>();
  readonly tripletChange = output<boolean>();
  readonly velocityChange = output<number>();
  readonly chordChange = output<boolean>();
  readonly rest = output();
  readonly tie = output();
  /** A step: the pitches of the keys that went down together, lowest first. */
  readonly step = output<number[]>();

  protected readonly velocities = StepVelocities;
  protected readonly lowest = Lowest;
  protected readonly highest = Highest;
  protected readonly lowestName = computed(() => pitchName(this.from()));
  /** The radio that takes Tab focus: the checked one, else the first. */
  protected readonly focusValue = computed(() => {
    const values = this.noteValues();
    return values.find(v => v.ticks === this.noteValue())?.ticks ?? values[0]?.ticks;
  });
  protected readonly focusVelocity = computed(
    () => StepVelocities.find(v => v.value === this.velocity())?.label ?? StepVelocities[0]?.label
  );

  private readonly held = new Set<number>();
  private readonly stale = new Set<number>();

  protected down(pitch: number): void {
    this.stale.delete(pitch);
    this.held.add(pitch);
  }

  /** The first key released sends the keys down with it as one step; the others' releases do nothing. */
  protected up(pitch: number): void {
    if (this.stale.delete(pitch) || !this.held.has(pitch)) {
      return;
    }
    const pitches = [...this.held].sort((a, b) => a - b);
    for (const p of pitches) {
      if (p !== pitch) {
        this.stale.add(p);
      }
    }
    this.held.clear();
    this.step.emit(pitches);
  }

  protected shift(by: number): void {
    this.from.set(Math.min(Highest, Math.max(Lowest, this.from() + by)));
  }

  protected setVelocity(field: HTMLInputElement): void {
    const value = Math.round(Number(field.value));
    if (Number.isFinite(value) && value >= 1 && value <= 127) {
      if (value !== this.velocity()) {
        this.velocityChange.emit(value);
      }
    } else {
      field.value = String(this.velocity());
    }
  }

  protected arrowValue(event: KeyboardEvent, ticks: number): void {
    const values = this.noteValues();
    const at = values.findIndex(v => v.ticks === ticks);
    const next = this.arrow(event, at, values.length);
    const value = next === null ? undefined : values[next];
    if (value) {
      this.noteValueChange.emit(value.ticks);
    }
  }

  protected arrowVelocity(event: KeyboardEvent, label: string): void {
    const at = StepVelocities.findIndex(v => v.label === label);
    const next = this.arrow(event, at, StepVelocities.length);
    const preset = next === null ? undefined : StepVelocities[next];
    if (preset) {
      this.velocityChange.emit(preset.value);
    }
  }

  /** A radio group's arrows: the index to check and focus, or null for other keys. */
  private arrow(event: KeyboardEvent, at: number, count: number): number | null {
    const step =
      event.key === 'ArrowRight' || event.key === 'ArrowDown'
        ? 1
        : event.key === 'ArrowLeft' || event.key === 'ArrowUp'
          ? -1
          : 0;
    if (step === 0 || at < 0) {
      return null;
    }
    event.preventDefault();
    const next = (at + step + count) % count;
    const group = (event.currentTarget as HTMLElement).parentElement;
    group?.querySelectorAll<HTMLElement>('[role="radio"]')[next]?.focus();
    return next;
  }
}
