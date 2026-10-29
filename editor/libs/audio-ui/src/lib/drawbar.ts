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

/** A drawbar's tab colour, as on a Hammond: brown for the sub-harmonics, white for octaves, black for the rest. */
export type DrawbarTone = 'brown' | 'white' | 'black';

/** The nine footages of a tonewheel organ's drawbars, left to right, with their tab colours. */
export const DrawbarFootages: readonly { readonly label: string; readonly tone: DrawbarTone }[] = [
  { label: '16′', tone: 'brown' },
  { label: '5⅓′', tone: 'brown' },
  { label: '8′', tone: 'white' },
  { label: '4′', tone: 'white' },
  { label: '2⅔′', tone: 'black' },
  { label: '2′', tone: 'white' },
  { label: '1⅗′', tone: 'black' },
  { label: '1⅓′', tone: 'black' },
  { label: '1′', tone: 'white' }
];

const Max = 8;
/** The rod's travel in CSS pixels, and the tab's height under it. */
const Travel = 96;
const Tab = 22;

let nextId = 0;

/**
 * A Hammond-style drawbar: a bar pulled out of its slot from 0 (in) to 8 (fully out), its tab in the footage's colour
 * with the setting on it. An ARIA slider (vertical): ↑/→ pull it out a step, ↓/← push it in, Home and End reach 0 and
 * 8, and Page keys move 4; drag the bar (or press in the slot) to place it. `committed` fires when an edit is done.
 */
@Component({
  selector: 'an-drawbar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-flex w-7 flex-col items-center gap-1 select-none' },
  template: `
    <div
      class="well focus-visible:outline-focus relative w-5 cursor-ns-resize touch-none rounded-[4px] outline-offset-2 focus-visible:outline-2"
      role="slider"
      aria-orientation="vertical"
      [style.height.px]="slot"
      [class.opacity-35]="disabled()"
      [attr.tabindex]="disabled() ? -1 : 0"
      [attr.aria-labelledby]="labelId"
      aria-valuemin="0"
      aria-valuemax="8"
      [attr.aria-valuenow]="value()"
      [attr.aria-disabled]="disabled() || null"
      (pointerdown)="down($event)"
      (pointermove)="move($event)"
      (pointerup)="up($event)"
      (pointercancel)="up($event)"
      (keydown)="key($event)"
    >
      <span
        class="border-hairline-strong bg-surface-raised absolute inset-x-1 top-0 border-x transition-[height] duration-100"
        [style.height.px]="rod()"
        aria-hidden="true"
      ></span>
      <span
        class="an-drawbar-tab absolute inset-x-0 flex items-end justify-center rounded-[4px] pb-0.5 text-[10px] leading-none font-semibold transition-[top] duration-100"
        [attr.data-tone]="tone()"
        [style.top.px]="rod()"
        [style.height.px]="tab"
        aria-hidden="true"
        >{{ value() }}</span
      >
    </div>
    <span class="text-foreground text-[10px] leading-tight font-semibold" [id]="labelId">{{ label() }}</span>
  `,
  styles: `
    .an-drawbar-tab {
      box-shadow:
        inset 0 1px 0 rgb(255 255 255 / 0.25),
        0 1px 2px rgb(0 0 0 / 0.45);
    }
    .an-drawbar-tab[data-tone='brown'] {
      background-color: var(--drawbar-brown);
      color: var(--drawbar-brown-foreground);
    }
    .an-drawbar-tab[data-tone='white'] {
      background-color: var(--drawbar-white);
      color: var(--drawbar-white-foreground);
    }
    .an-drawbar-tab[data-tone='black'] {
      background-color: var(--drawbar-black);
      color: var(--drawbar-black-foreground);
    }
  `
})
export class AnDrawbar {
  protected readonly labelId = `an-drawbar-${nextId++}`;
  protected readonly slot = Travel + Tab;
  protected readonly tab = Tab;

  /** 0 (in) … 8 (fully out), whole steps. */
  readonly value = model(0);
  /** The footage ("16′"): the slider's name. */
  readonly label = input('');
  readonly tone = input<DrawbarTone>('white');
  readonly disabled = input(false, { transform: booleanAttribute });
  /** The travel, should a host want a shorter one; 8 by default (and by Hammond). */
  readonly max = input(Max, { transform: numberAttribute });
  readonly committed = output<number>();

  private pointer: number | null = null;
  private start = 0;

  protected readonly rod = computed(() => (Math.min(this.max(), Math.max(0, this.value())) / this.max()) * Travel);

  protected down(event: PointerEvent): void {
    if (this.disabled() || event.button !== 0) {
      return;
    }
    const slot = event.currentTarget as HTMLElement;
    slot.setPointerCapture(event.pointerId);
    slot.focus();
    this.pointer = event.pointerId;
    this.start = this.value();
    this.place(slot, event);
  }

  protected move(event: PointerEvent): void {
    if (this.pointer === event.pointerId) {
      this.place(event.currentTarget as HTMLElement, event);
    }
  }

  protected up(event: PointerEvent): void {
    if (this.pointer !== event.pointerId) {
      return;
    }
    this.pointer = null;
    if (this.value() !== this.start) {
      this.committed.emit(this.value());
    }
  }

  protected key(event: KeyboardEvent): void {
    if (this.disabled()) {
      return;
    }
    const v = this.value();
    const moves: Record<string, number> = {
      ArrowUp: v + 1,
      ArrowRight: v + 1,
      ArrowDown: v - 1,
      ArrowLeft: v - 1,
      PageUp: v + 4,
      PageDown: v - 4,
      Home: 0,
      End: this.max()
    };
    const next = moves[event.key];
    if (next === undefined) {
      return;
    }
    event.preventDefault();
    if (this.set(next)) {
      this.committed.emit(this.value());
    }
  }

  /** The tab follows the pointer: its middle under it. */
  private place(slot: HTMLElement, event: PointerEvent): void {
    const box = slot.getBoundingClientRect();
    if (box.height <= 0) {
      return;
    }
    const scale = box.height / (Travel + Tab);
    const travel = (event.clientY - box.top) / scale - Tab / 2;
    this.set((travel / Travel) * this.max());
  }

  private set(value: number): boolean {
    const next = Math.round(Math.min(this.max(), Math.max(0, value)));
    if (next === this.value()) {
      return false;
    }
    this.value.set(next);
    return true;
  }
}
