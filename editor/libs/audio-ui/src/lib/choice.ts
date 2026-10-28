import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, model, output } from '@angular/core';
import { XuiSelectImports } from '@xui/select';

let nextId = 0;

/**
 * A parameter with named values (an oscillator's wave, a filter's type): a compact drop-down, labelled below like
 * {@link AnKnob} so the two line up in a parameter grid. `value` is the index of the chosen name, as a plugin numbers
 * its choices from `min`; `committed` fires on every choice.
 */
@Component({
  selector: 'an-choice',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiSelectImports],
  host: { class: 'inline-flex w-28 flex-col items-stretch gap-0.5' },
  template: `
    <xui-select
      class="an-compact-select w-full"
      [items]="names()"
      [itemText]="text"
      [filterable]="false"
      [disabled]="disabled()"
      [value]="selected()"
      [aria-labelledby]="labelId"
      (valueChange)="choose($event)"
    />
    <span class="text-foreground-muted truncate text-center text-[10px] leading-tight" [id]="labelId">
      {{ label() }}
    </span>
  `
})
export class AnChoice {
  protected readonly labelId = `an-choice-${nextId++}`;

  /** The chosen value: `min` plus the index of its name. */
  readonly value = model(0);
  readonly names = input.required<readonly string[]>();
  readonly min = input(0);
  readonly label = input('');
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly committed = output<number>();

  protected readonly text = (name: string) => name;
  protected readonly selected = computed(() => this.names()[Math.round(this.value() - this.min())] ?? null);

  protected choose(name: string | null): void {
    const index = name === null ? -1 : this.names().indexOf(name);
    if (index >= 0 && index + this.min() !== this.value()) {
      this.value.set(index + this.min());
      this.committed.emit(index + this.min());
    }
  }
}
