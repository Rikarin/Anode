import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, output } from '@angular/core';

/** What a Live Loops cell plays (the contract's `CellKind`). */
export type LiveLoopsCellKind = 'audio' | 'midi' | 'pattern';

/** A cell slot's state: the launcher's, or `empty` when the slot holds no cell. */
export type LiveLoopsCellState = 'empty' | 'stopped' | 'queued' | 'playing' | 'stopping';

/** A click or key press on a cell, with the modifiers that select instead of launching. */
export interface LiveLoopsCellActivation {
  /** ⇧ or ⌘ (Ctrl): extend the selection. */
  extend: boolean;
}

/** Where a cell's menu opens: client pixels. */
export interface LiveLoopsCellMenuPoint {
  x: number;
  y: number;
}

/**
 * One cell of the Live Loops grid (Logic's): a filled cell shows its kind and name on a raised face with a bar of its
 * track's colour; queued it has a dashed ring in that colour (a static ring rather than Logic's blink, per DESIGN.md's
 * motion rules), playing its face is lit in the colour with a progress ring, stopping its tint dims. An empty slot is a
 * sunken well. Recording into it gives a red ring. The host is the ARIA gridcell; its button (named by `label`)
 * launches; the context-menu key or ⇧F10 opens the menu.
 */
@Component({
  selector: 'an-live-loops-cell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'block p-0.5',
    role: 'gridcell',
    '[attr.aria-label]': 'label()',
    '[attr.aria-selected]': 'selected()',
    '[attr.data-state]': 'state()',
    '[attr.data-recording]': 'recording() || null',
    '[attr.draggable]': 'state() === "empty" ? null : "true"'
  },
  template: `
    <button
      type="button"
      class="focus-visible:outline-focus relative flex size-full min-h-0 flex-col items-start gap-0.5 overflow-hidden rounded-[4px] px-1 pt-1.5 pb-1 text-start outline-offset-1 transition-colors duration-100 focus-visible:outline-2"
      [class.well]="empty()"
      [class.bg-surface-raised]="!empty() && !lit()"
      [class.border]="!empty()"
      [class.border-hairline-strong]="!empty()"
      [class.ring-2]="ringed()"
      [class.ring-inset]="ringed()"
      [class.ring-transport-record]="recording()"
      [class.ring-primary]="!recording() && dropTarget()"
      [class.ring-foreground]="!recording() && !dropTarget() && selected()"
      [style.background-color]="lit() ? tint() : null"
      [attr.aria-label]="label()"
      [attr.tabindex]="focusable() ? 0 : -1"
      (click)="click($event)"
      (keydown)="key($event)"
    >
      @if (!empty()) {
        <span class="absolute inset-x-0 top-0 h-[3px]" [style.background-color]="color()" aria-hidden="true"></span>
        <span class="text-foreground flex w-full items-center gap-1" aria-hidden="true">
          <svg viewBox="0 0 12 12" class="size-3 shrink-0" fill="none" stroke="currentColor" stroke-width="1.25">
            @switch (kind()) {
              @case ('audio') {
                <path d="M1 6h1.5M3.5 3.5v5M5.5 1.5v9M7.5 3v6M9.5 4.5v3M11 6h0" stroke-linecap="round" />
              }
              @case ('pattern') {
                <rect x="1" y="2" width="2.5" height="2.5" rx=".5" fill="currentColor" stroke="none" />
                <rect x="4.75" y="2" width="2.5" height="2.5" rx=".5" />
                <rect x="8.5" y="2" width="2.5" height="2.5" rx=".5" fill="currentColor" stroke="none" />
                <rect x="1" y="7.5" width="2.5" height="2.5" rx=".5" />
                <rect x="4.75" y="7.5" width="2.5" height="2.5" rx=".5" fill="currentColor" stroke="none" />
                <rect x="8.5" y="7.5" width="2.5" height="2.5" rx=".5" />
              }
              @default {
                <path d="M1.5 3h4M4 6h5M2.5 9h3.5M7.5 9h3" stroke-width="1.5" stroke-linecap="round" />
              }
            }
          </svg>
          <span class="flex-1"></span>
          @if (progress() !== null) {
            <svg viewBox="0 0 12 12" class="size-3 shrink-0 -rotate-90" data-progress>
              <circle cx="6" cy="6" r="4.5" fill="none" class="stroke-knob-track" stroke-width="2" />
              <circle
                cx="6"
                cy="6"
                r="4.5"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                [attr.stroke-dasharray]="circumference"
                [attr.stroke-dashoffset]="dashOffset()"
              />
            </svg>
          } @else if (state() === 'queued') {
            <svg viewBox="0 0 12 12" class="size-3 shrink-0" fill="currentColor"><path d="M3.5 2.5v7l6-3.5z" /></svg>
          }
        </span>
        <span class="text-foreground line-clamp-2 w-full text-[11px] leading-tight font-semibold break-all">
          {{ name() }}
        </span>
      } @else if (recording()) {
        <span class="bg-transport-record m-auto size-2 rounded-full" aria-hidden="true"></span>
      }
      @if (state() === 'queued') {
        <span
          class="pointer-events-none absolute inset-0 rounded-[4px] border-2 border-dashed"
          [style.border-color]="color()"
          aria-hidden="true"
        ></span>
      }
    </button>
  `
})
export class AnLiveLoopsCell {
  protected readonly circumference = 2 * Math.PI * 4.5;

  /** The cell's name; null for an empty slot. */
  readonly name = input<string | null>(null);
  readonly kind = input<LiveLoopsCellKind | null>(null);
  readonly state = input<LiveLoopsCellState>('empty');
  /** Played through its length, 0…1, while playing or stopping; null otherwise. */
  readonly progress = input<number | null>(null);
  /** The track's colour (`#rrggbb`). */
  readonly color = input('#3d8ce8');
  /** The accessible name: `<cell>, <track>, scene <n>`. */
  readonly label = input('');
  readonly selected = input(false, { transform: booleanAttribute });
  /** Recording into this slot. */
  readonly recording = input(false, { transform: booleanAttribute });
  /** A drag hovers over it that it would take. */
  readonly dropTarget = input(false, { transform: booleanAttribute });
  /** The grid's roving tab stop: only one cell is in the tab order. */
  readonly focusable = input(true, { transform: booleanAttribute });

  /** A click, Enter or Space. */
  readonly activate = output<LiveLoopsCellActivation>();
  /** The context-menu key or ⇧F10: the owner opens the cell's menu here. */
  readonly menu = output<LiveLoopsCellMenuPoint>();

  protected readonly empty = computed(() => this.state() === 'empty');
  protected readonly lit = computed(() => this.state() === 'playing' || this.state() === 'stopping');
  protected readonly ringed = computed(() => this.recording() || this.dropTarget() || this.selected());
  /**
   * The face lit in the track's colour, mixed into the raised surface so the name keeps its contrast; a stopping cell
   * dims to a fainter tint (the name stays at full contrast, so the cell dims rather than fades).
   */
  protected readonly tint = computed(
    () => `color-mix(in oklab, ${this.color()} ${this.state() === 'stopping' ? 16 : 38}%, var(--color-surface-raised))`
  );
  protected readonly dashOffset = computed(
    () => this.circumference * (1 - Math.min(1, Math.max(0, this.progress() ?? 0)))
  );

  protected click(event: MouseEvent): void {
    this.activate.emit({ extend: event.shiftKey || event.metaKey || event.ctrlKey });
  }

  protected key(event: KeyboardEvent): void {
    if (event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) {
      event.preventDefault();
      const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
      this.menu.emit({ x: box.left, y: box.bottom });
    }
  }
}
