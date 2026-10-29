import { ChangeDetectionStrategy, Component, computed, input, numberAttribute, output, signal } from '@angular/core';

/**
 * A small number field of the Step Sequencer's row headers (a row's length, a note): an ARIA spinbutton that shows its
 * value formatted ("16", "C3"); ↑/↓ change it by one (⇧ by `bigStep`), and typed text commits on Enter, Tab or leaving
 * the field (Escape puts the value back). `parse` reads typed text; values are clamped to `min`…`max`.
 */
@Component({
  selector: 'an-spin-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-flex' },
  template: `
    <input
      type="text"
      role="spinbutton"
      inputmode="numeric"
      class="bg-surface-inset border-hairline text-foreground readout focus-visible:outline-focus h-5 w-full min-w-0 rounded-[4px] border px-1 text-center text-[11px] focus-visible:outline-2"
      [attr.aria-label]="label()"
      [attr.aria-valuemin]="min()"
      [attr.aria-valuemax]="max()"
      [attr.aria-valuenow]="value()"
      [attr.aria-valuetext]="text()"
      [value]="draft() ?? text()"
      (input)="typed($event)"
      (keydown)="key($event)"
      (blur)="commit()"
    />
  `
})
export class SpinField {
  readonly value = input.required<number>();
  readonly label = input.required<string>();
  readonly min = input(0, { transform: numberAttribute });
  readonly max = input(127, { transform: numberAttribute });
  readonly bigStep = input(10, { transform: numberAttribute });
  readonly format = input<(value: number) => string>(value => `${value}`);
  readonly parse = input<(text: string) => number | null>(text => {
    const n = Number(text.trim());
    return text.trim() !== '' && Number.isFinite(n) ? n : null;
  });
  readonly committed = output<number>();

  /** Typed text not yet committed. */
  protected readonly draft = signal<string | null>(null);
  protected readonly text = computed(() => this.format()(this.value()));

  protected typed(event: Event): void {
    this.draft.set((event.target as HTMLInputElement).value);
  }

  protected key(event: KeyboardEvent): void {
    if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
      event.preventDefault();
      const by = (event.shiftKey ? this.bigStep() : 1) * (event.key === 'ArrowUp' ? 1 : -1);
      this.draft.set(null);
      this.emit(this.value() + by);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      this.commit();
    } else if (event.key === 'Escape') {
      this.draft.set(null);
      (event.target as HTMLInputElement).value = this.text();
    }
  }

  protected commit(): void {
    const draft = this.draft();
    this.draft.set(null);
    if (draft === null) {
      return;
    }
    const parsed = this.parse()(draft);
    if (parsed === null) {
      return;
    }
    this.emit(parsed);
  }

  private emit(value: number): void {
    const next = Math.min(this.max(), Math.max(this.min(), Math.round(value)));
    if (next !== this.value()) {
      this.committed.emit(next);
    }
  }
}
