import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { AnStepKeyboard } from '@anode/audio-ui';
import { formatPosition } from '../midi-edit/position';
import { NoteValues } from '../midi-edit/step-input';
import { FloatingWindow } from '../shell/floating-window';
import { ProjectStore } from '../state/project.store';
import { StepInputStore } from '../state/step-input.store';
import { UiStore } from '../state/ui.store';

/**
 * The Step Input Keyboard window (docs/08-phase-7.md, "Step input"; Logic's ⌥⌘K): note values, dotted and triplet,
 * the velocity, Rest, Tie, Chord and keys (`an-step-keyboard`), the step position, and where the steps go. Opening it
 * turns step input on for the selected MIDI region (or the selected instrument or MIDI track, where the first step
 * makes a region) at the playhead; closing it turns step input off again, unless MIDI step input is on.
 */
@Component({
  selector: 'an-step-input-keyboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnStepKeyboard, FloatingWindow],
  template: `
    <an-floating-window
      title="Step Input Keyboard"
      testId="step-input-keyboard"
      width="36rem"
      [origin]="{ x: 200, y: 140 }"
      (closed)="ui.stepInputKeyboardOpen.set(false)"
    >
      <div class="flex flex-col gap-2 p-2">
        <div class="flex items-center gap-2 text-[11px]">
          <span class="text-foreground-muted">Position</span>
          <span
            class="lcd-glass text-lcd-foreground readout rounded-[5px] px-2 py-0.5 text-[13px] font-semibold"
            role="status"
            aria-label="Step position"
            data-testid="step-position"
            >{{ position() }}</span
          >
          <span class="text-foreground min-w-0 flex-1 truncate" data-testid="step-target">{{ targetText() }}</span>
          <span class="text-foreground-muted readout" data-testid="step-length">{{ stepText() }}</span>
        </div>
        <an-step-keyboard
          [noteValues]="noteValues"
          [noteValue]="state().noteValueTicks"
          [dotted]="state().dotted"
          [triplet]="state().triplet"
          [velocity]="state().velocity"
          [chord]="state().chord"
          [disabled]="!state().on"
          [(from)]="from"
          (noteValueChange)="steps.set({ noteValueTicks: $event })"
          (dottedChange)="steps.set({ dotted: $event })"
          (tripletChange)="steps.set({ triplet: $event })"
          (velocityChange)="steps.set({ velocity: $event })"
          (chordChange)="steps.set({ chord: $event })"
          (rest)="steps.rest()"
          (tie)="steps.tie()"
          (step)="steps.step($event)"
        />
      </div>
    </an-floating-window>
  `
})
export class StepInputKeyboard {
  protected readonly ui = inject(UiStore);
  protected readonly steps = inject(StepInputStore);
  private readonly project = inject(ProjectStore);

  protected readonly noteValues = NoteValues;
  protected readonly from = signal(48);
  protected readonly state = this.steps.state;
  protected readonly position = computed(() => formatPosition(this.state().positionTick, this.project.meter()));
  protected readonly stepText = computed(() => {
    const value = NoteValues.find(v => v.ticks === this.state().noteValueTicks)?.label ?? `${this.steps.stepTicks()}`;
    return `${value}${this.state().dotted ? '.' : ''}${this.state().triplet ? 'T' : ''}`;
  });
  protected readonly targetText = computed(() => {
    const state = this.state();
    if (!state.on) {
      return 'Select a MIDI region, or an instrument or MIDI track, then reopen to enter steps.';
    }
    const region = state.regionId ? this.project.midiRegions().get(state.regionId) : undefined;
    if (region) {
      return `Into ${region.name}`;
    }
    const track = state.trackId ? this.project.tracks().get(state.trackId) : undefined;
    return track ? `New region on ${track.name}` : '';
  });

  constructor() {
    // Opening turns step input on at the playhead (unless MIDI step input already has it on); closing turns it off.
    if (!this.steps.on()) {
      void this.steps.start();
    }
    inject(DestroyRef).onDestroy(() => {
      if (this.steps.on() && !this.steps.state().midiInput) {
        void this.steps.set({ on: false });
      }
    });
  }
}
