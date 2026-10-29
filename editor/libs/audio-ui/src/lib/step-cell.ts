import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, output, signal } from '@angular/core';

/** One step of a pattern row, as the Step Sequencer edits it (the contract's `PatternStep`). */
export interface StepValues {
  on: boolean;
  /** 1…127. */
  velocity: number;
  /** The note's length in steps, 0.1…16 (over 1 ties into the next steps). */
  gate: number;
  /** Ratchets, 1…8. */
  repeats: number;
  /** The probability the step plays on a pass, 0…1. */
  chance: number;
  /** The step's shift, −0.5…0.5 of a step. */
  offset: number;
}

/** What a step cell edits: on/off, or one of a step's values (Logic's Step Sequencer edit modes). */
export type StepEditMode = 'onOff' | 'velocity' | 'gate' | 'repeats' | 'chance' | 'offset';
export type StepValueMode = Exclude<StepEditMode, 'onOff'>;

/** A value mode's range in the step's own units, its arrow-key step and ⇧ step, and how its slider reads it. */
export interface StepValueRange {
  min: number;
  max: number;
  step: number;
  big: number;
  /** The slider's aria-valuenow scale: chance and offset read as percent. */
  scale: number;
}

export const StepValueRanges: Readonly<Record<StepValueMode, StepValueRange>> = {
  velocity: { min: 1, max: 127, step: 1, big: 10, scale: 1 },
  gate: { min: 0.1, max: 16, step: 0.1, big: 1, scale: 1 },
  repeats: { min: 1, max: 8, step: 1, big: 2, scale: 1 },
  chance: { min: 0, max: 1, step: 0.05, big: 0.25, scale: 100 },
  offset: { min: -0.5, max: 0.5, step: 0.05, big: 0.25, scale: 100 }
};

/** A step's value in a mode. */
export function stepValue(step: StepValues, mode: StepValueMode): number {
  return step[mode];
}

/** A value clamped to its mode's range and rounded to what the mode resolves (whole velocities and repeats). */
export function clampStepValue(mode: StepValueMode, value: number): number {
  const { min, max } = StepValueRanges[mode];
  const clamped = Math.min(max, Math.max(min, value));
  return mode === 'velocity' || mode === 'repeats' ? Math.round(clamped) : Math.round(clamped * 100) / 100;
}

/** A step value as read out: "100", "1.5 steps", "4×", "75 %", "+10 %". */
export function stepValueText(mode: StepValueMode, value: number): string {
  switch (mode) {
    case 'gate':
      return `${Number(value.toFixed(2))} ${value === 1 ? 'step' : 'steps'}`;
    case 'repeats':
      return `${value}×`;
    case 'chance':
      return `${Math.round(value * 100)} %`;
    case 'offset': {
      const percent = Math.round(value * 100);
      return `${percent > 0 ? '+' : ''}${percent} %`;
    }
    default:
      return `${value}`;
  }
}

/** How full a value's bar is, 0…1 (gate on a log scale, so short gates still show; offset from the centre). */
export function stepBarFraction(mode: StepValueMode, value: number): number {
  const { min, max } = StepValueRanges[mode];
  if (mode === 'gate') {
    return Math.log(value / min) / Math.log(max / min);
  }
  return (value - min) / (max - min);
}

/**
 * One step of Logic's Step Sequencer. In On/Off mode it is a toggle button (`aria-pressed`): click or Space/Enter turns
 * it on or off. In a value mode (velocity, gate, repeats, chance, offset) it is an ARIA slider: ↑/↓ change the value a
 * step (⇧ a larger one), and a vertical drag sets it; `valueChange` streams a drag, `committed` ends it or a key press
 * (an off step shows its value dimmed and is turned on by its owner when edited). It always shows a step's shape: a
 * fill as high as its velocity, repeats as ticks, chance as a dot and lower opacity, offset as a shift; `playing` lights
 * the playhead's step.
 */
@Component({
  selector: 'an-step-cell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-block h-6 w-6 shrink-0 select-none' },
  template: `
    @if (mode() === 'onOff') {
      <button
        type="button"
        class="focus-visible:outline-focus relative block size-full overflow-hidden rounded-[3px] border transition-colors duration-100 focus-visible:outline-2 focus-visible:outline-offset-1"
        [class]="faceClass()"
        [attr.aria-label]="label()"
        [attr.aria-pressed]="step().on ? 'true' : 'false'"
        [attr.data-playing]="playing() || null"
        [disabled]="disabled()"
        (click)="toggled.emit()"
      >
        @if (step().on) {
          <span
            class="bg-primary absolute inset-x-0 bottom-0"
            [style.height.%]="(step().velocity / 127) * 100"
            aria-hidden="true"
          ></span>
          @if (step().repeats > 1) {
            <span class="absolute inset-x-0.5 bottom-0.5 flex justify-between" aria-hidden="true">
              @for (r of repeatMarks(); track $index) {
                <span class="bg-foreground h-1 w-px"></span>
              }
            </span>
          }
          @if (step().chance < 1) {
            <span class="bg-foreground absolute top-0.5 right-0.5 size-1 rounded-full" aria-hidden="true"></span>
          }
          @if (step().offset !== 0) {
            <span
              class="bg-foreground absolute top-0.5 h-px w-1.5"
              [style.left]="'calc(50% - 3px + ' + step().offset * 100 + '%)'"
              aria-hidden="true"
            ></span>
          }
        }
      </button>
    } @else {
      <div
        role="slider"
        class="well focus-visible:outline-focus relative block size-full cursor-ns-resize touch-none overflow-hidden rounded-[3px] focus-visible:outline-2 focus-visible:outline-offset-1"
        [class.outline-1]="playing()"
        [class.outline-lit]="playing()"
        [class.outline]="playing()"
        [attr.tabindex]="disabled() ? -1 : 0"
        [attr.aria-label]="label() + ' ' + valueMode()"
        [attr.aria-valuemin]="range().min * range().scale"
        [attr.aria-valuemax]="range().max * range().scale"
        [attr.aria-valuenow]="valueNow()"
        [attr.aria-valuetext]="valueText()"
        [attr.aria-disabled]="disabled() || null"
        [attr.data-playing]="playing() || null"
        [attr.data-on]="step().on"
        (pointerdown)="grab($event)"
        (pointermove)="drag($event)"
        (pointerup)="release($event)"
        (pointercancel)="release($event)"
        (keydown)="key($event)"
      >
        @if (valueMode() === 'offset') {
          <span class="bg-hairline-strong absolute inset-y-0 left-1/2 w-px" aria-hidden="true"></span>
          <span
            class="bg-primary absolute inset-y-1 rounded-[1px]"
            [class.opacity-40]="!step().on"
            [style.left.%]="offsetFrom()"
            [style.width.%]="offsetWidth()"
            aria-hidden="true"
          ></span>
        } @else {
          <span
            class="bg-primary absolute inset-x-0.5 bottom-0.5 rounded-[1px]"
            [class.opacity-40]="!step().on"
            [style.height.%]="barHeight()"
            aria-hidden="true"
          ></span>
        }
      </div>
    }
  `
})
export class AnStepCell {
  /** The step shown. */
  readonly step = input.required<StepValues>();
  readonly mode = input<StepEditMode>('onOff');
  /** The step's name ("Kick step 1"); a value mode's slider adds the mode ("Kick step 1 velocity"). */
  readonly label = input.required<string>();
  /** The playhead is on this step. */
  readonly playing = input(false, { transform: booleanAttribute });
  /** A beat's first step, drawn a little lighter when off. */
  readonly beat = input(false, { transform: booleanAttribute });
  readonly disabled = input(false, { transform: booleanAttribute });
  /** On/Off mode: a click or Space/Enter. */
  readonly toggled = output();
  /** A value mode: the value during a drag. */
  readonly valueChange = output<number>();
  /** A value mode: the value when a drag ends, or after a key press. */
  readonly committed = output<number>();

  /** The value a drag shows before its owner stores it. */
  private readonly dragged = signal<number | null>(null);
  private dragging: { y: number; value: number; pointer: number } | null = null;

  protected readonly valueMode = computed<StepValueMode>(() => {
    const mode = this.mode();
    return mode === 'onOff' ? 'velocity' : mode;
  });
  protected readonly range = computed(() => StepValueRanges[this.valueMode()]);
  protected readonly value = computed(() => this.dragged() ?? stepValue(this.step(), this.valueMode()));
  protected readonly valueNow = computed(() => Math.round(this.value() * this.range().scale * 100) / 100);
  protected readonly valueText = computed(() => {
    const text = stepValueText(this.valueMode(), this.value());
    return this.step().on ? text : `${text}, off`;
  });
  protected readonly barHeight = computed(() => Math.max(4, stepBarFraction(this.valueMode(), this.value()) * 100));
  protected readonly offsetFrom = computed(() => 50 + Math.min(0, this.value()) * 100);
  protected readonly offsetWidth = computed(() => Math.max(4, Math.abs(this.value()) * 100));
  protected readonly repeatMarks = computed(() => Array.from({ length: Math.min(8, this.step().repeats) }));

  protected readonly faceClass = computed(() => {
    const on = this.step().on;
    const face = on
      ? 'bg-primary/45 border-primary'
      : this.beat()
        ? 'bg-surface-raised border-hairline-strong hover:bg-muted'
        : 'bg-surface-inset border-hairline hover:bg-muted';
    const chance = on && this.step().chance < 1 ? ' opacity-70' : '';
    const playing = this.playing() ? ' outline outline-1 outline-lit' : '';
    return face + chance + playing;
  });

  protected key(event: KeyboardEvent): void {
    if (this.disabled()) {
      return;
    }
    const { step, big } = this.range();
    const by = { ArrowUp: 1, ArrowDown: -1, PageUp: 1, PageDown: -1 }[event.key];
    if (by === undefined) {
      if (event.key === 'Home' || event.key === 'End') {
        event.preventDefault();
        this.commit(event.key === 'Home' ? this.range().min : this.range().max);
      }
      return;
    }
    event.preventDefault();
    const amount = event.shiftKey || event.key.startsWith('Page') ? big : step;
    this.commit(this.value() + by * amount);
  }

  protected grab(event: PointerEvent): void {
    if (this.disabled() || event.button !== 0) {
      return;
    }
    const element = event.currentTarget as HTMLElement;
    element.setPointerCapture(event.pointerId);
    element.focus();
    this.dragging = { y: event.clientY, value: this.value(), pointer: event.pointerId };
  }

  protected drag(event: PointerEvent): void {
    if (this.dragging?.pointer !== event.pointerId) {
      return;
    }
    const { min, max } = this.range();
    // The whole range over 120 px (⇧: 600 px, fine).
    const perPixel = (max - min) / (event.shiftKey ? 600 : 120);
    const next = clampStepValue(this.valueMode(), this.dragging.value + (this.dragging.y - event.clientY) * perPixel);
    if (next !== this.value()) {
      this.dragged.set(next);
      this.valueChange.emit(next);
    }
  }

  protected release(event: PointerEvent): void {
    if (this.dragging?.pointer !== event.pointerId) {
      return;
    }
    this.dragging = null;
    const value = this.dragged();
    this.dragged.set(null);
    // Also when the drag came back to where it started, so an owner showing the drag can let it go.
    if (value !== null) {
      this.committed.emit(value);
    }
  }

  private commit(value: number): void {
    const next = clampStepValue(this.valueMode(), value);
    if (next !== this.value() || !this.step().on) {
      this.committed.emit(next);
    }
  }
}
