import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { XuiNumericInputImports } from '@xui/numeric-input';
import { ProjectStore } from '../state/project.store';
import { type LoopKind, loopCommand, repeatsOf } from '../tracks/region-loop';

/**
 * A region's Loop in the Region inspector, as cells of its grid: Loop on or off, and how many repeats. On, it repeats up
 * to the next region on its track or the project's end, as Logic's checkbox does; the Repeats field, or a drag of the
 * region's upper right corner, sets any other length.
 */
@Component({
  selector: 'an-loop-fields',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiNumericInputImports],
  host: { class: 'contents' },
  template: `
    <span class="text-foreground-muted">Loop</span>
    <button
      type="button"
      class="state-toggle justify-self-start"
      aria-label="Loop region"
      data-testid="region-loop"
      [attr.aria-pressed]="looping()"
      (click)="toggle()"
    >
      L
    </button>
    <span class="text-foreground-muted">Repeats</span>
    <xui-numeric-input
      size="sm"
      aria-label="Region loop, repeats"
      data-testid="region-repeats"
      [min]="0"
      [stepSize]="1"
      [majorStepSize]="4"
      [value]="repeats()"
      (valueChange)="$event !== null && setRepeats($event)"
    />
  `
})
export class LoopFields {
  private readonly project = inject(ProjectStore);

  readonly regionId = input.required<string>();
  readonly kind = input.required<LoopKind>();
  readonly trackId = input.required<string>();
  readonly startTick = input.required<number>();
  /** Where the region ends, in ticks (before any repeat). */
  readonly endTick = input.required<number>();
  readonly loopTicks = input.required<number>();

  protected readonly looping = computed(() => this.loopTicks() > 0);
  protected readonly repeats = computed(() => repeatsOf(this.loopTicks(), this.endTick() - this.startTick()));

  protected toggle(): void {
    if (this.looping()) {
      this.set(0, 'Loop Off');
      return;
    }
    const end = this.endTick();
    const until = this.project.nextRegionStart(this.trackId(), this.startTick()) ?? this.project.settings().endTick;
    this.set(until > end ? until - end : end - this.startTick(), 'Loop On');
  }

  protected setRepeats(repeats: number): void {
    this.set(Math.max(0, repeats) * (this.endTick() - this.startTick()), 'Loop Region');
  }

  private set(loopTicks: number, label: string): void {
    const ticks = Math.round(loopTicks);
    if (ticks !== this.loopTicks()) {
      void this.project.execute([loopCommand(this.regionId(), this.kind(), ticks)], label);
    }
  }
}
