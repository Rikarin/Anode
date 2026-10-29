import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { provideIcons } from '@ng-icons/core';
import { matCloseRound } from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';

/**
 * A floating window over the main window (the generic plugin window's frame, DESIGN.md "Panes"): a `panel` with a
 * `glass-bar` title bar it is dragged by, a close button, Escape to close, and its content below. Phase 7's windows
 * (MIDI Transform, Step Input Keyboard, Articulation Set, Script Editor) are built in it. Its host is the dialog
 * (`role="dialog"`, named by `title`, `data-testid` from `testId`).
 */
@Component({
  selector: 'an-floating-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiIconImports],
  providers: [provideIcons({ matCloseRound })],
  host: {
    class: 'panel fixed z-40 flex max-h-[85vh] flex-col overflow-hidden rounded-lg',
    role: 'dialog',
    'aria-modal': 'false',
    '[attr.aria-label]': 'title()',
    '[attr.data-testid]': 'testId()',
    '[style.left.px]': 'position().x',
    '[style.top.px]': 'position().y',
    '[style.width]': 'width()',
    '(keydown.escape)': 'closed.emit()'
  },
  template: `
    <div
      class="glass-bar flex h-8 shrink-0 cursor-grab touch-none items-center gap-2 ps-3 pe-1 select-none"
      (pointerdown)="grab($event)"
      (pointermove)="drag($event)"
      (pointerup)="release($event)"
      (pointercancel)="release($event)"
    >
      <span class="text-foreground min-w-0 flex-1 truncate text-[12px] font-semibold">{{ title() }}</span>
      <ng-content select="[windowActions]" />
      <button
        xuiButton
        type="button"
        size="sm"
        variant="ghost"
        class="h-6 w-6 px-0"
        [attr.aria-label]="'Close ' + title()"
        [attr.data-testid]="testId() ? testId() + '-close' : null"
        (pointerdown)="$event.stopPropagation()"
        (click)="closed.emit()"
      >
        <ng-icon xui name="matCloseRound" size="sm" />
      </button>
    </div>
    <div class="flex min-h-0 flex-1 flex-col overflow-auto">
      <ng-content />
    </div>
  `
})
export class FloatingWindow {
  readonly title = input.required<string>();
  readonly testId = input<string | null>(null);
  /** The window's CSS width. */
  readonly width = input('32rem');
  /** Where it opens. */
  readonly origin = input({ x: 160, y: 96 });
  readonly closed = output();

  private readonly moved = signal<{ x: number; y: number } | null>(null);
  private grabbed: { dx: number; dy: number; pointer: number } | null = null;
  protected readonly position = computed(() => this.moved() ?? this.origin());

  protected grab(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    const { x, y } = this.position();
    this.grabbed = { dx: event.clientX - x, dy: event.clientY - y, pointer: event.pointerId };
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
  }

  protected drag(event: PointerEvent): void {
    if (this.grabbed?.pointer !== event.pointerId) {
      return;
    }
    const x = Math.min(window.innerWidth - 80, Math.max(0, event.clientX - this.grabbed.dx));
    const y = Math.min(window.innerHeight - 32, Math.max(0, event.clientY - this.grabbed.dy));
    this.moved.set({ x, y });
  }

  protected release(event: PointerEvent): void {
    if (this.grabbed?.pointer === event.pointerId) {
      this.grabbed = null;
    }
  }
}
