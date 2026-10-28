import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  input,
  numberAttribute,
  output
} from '@angular/core';

/** Where a slot's menu opens: client pixels (the chevron's bottom-left, or the name button's). */
export interface SlotMenuPoint {
  x: number;
  y: number;
}

/**
 * One insert slot of a channel strip (Logic's Audio FX slots): a filled slot has a bypass key, the plugin's name (which
 * opens its editor) and a chevron for its menu; an empty slot is a sunken well that chooses a plugin. Bypassed, the
 * name greys out; bypassed by the host after a crash (`problem`), it carries a warning edge. The host is draggable when
 * filled (with `data-index`), so the owner can reorder slots by drag and drop; ⌥↑ and ⌥↓ on the name do the same from
 * the keyboard (`move`). The context-menu key or ⇧F10 opens the menu.
 */
@Component({
  selector: 'an-insert-slot',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'strip-slot group w-full',
    '[attr.data-empty]': 'name() === null ? "" : null',
    '[attr.data-index]': 'index()',
    '[attr.data-problem]': 'problem() || null',
    '[attr.draggable]': 'name() === null ? null : "true"'
  },
  template: `
    @if (name(); as plugin) {
      <button
        type="button"
        class="grid h-full w-4 shrink-0 place-items-center rounded-s-[3px] outline-none"
        [class.text-lit]="!bypassed()"
        [class.text-foreground-muted]="bypassed()"
        [attr.aria-pressed]="bypassed()"
        [attr.aria-label]="'Bypass ' + plugin + ' on ' + channel()"
        [attr.title]="bypassed() ? 'Bypassed: click to turn on' : 'On: click to bypass'"
        (click)="bypassToggle.emit()"
      >
        <svg viewBox="0 0 12 12" class="size-2.5" aria-hidden="true">
          <path
            d="M6 1.5v4M3.4 3.2a3.6 3.6 0 1 0 5.2 0"
            fill="none"
            stroke="currentColor"
            stroke-width="1.5"
            stroke-linecap="round"
          />
        </svg>
      </button>
      <button
        type="button"
        class="h-full min-w-0 flex-1 truncate px-0.5 text-center font-semibold outline-none"
        [class.text-foreground]="!bypassed()"
        [class.text-foreground-muted]="bypassed()"
        [class.line-through]="problem()"
        [attr.aria-label]="'Insert ' + (index() + 1) + ' on ' + channel() + ': ' + plugin"
        [attr.title]="title()"
        (click)="open.emit()"
        (keydown)="key($event)"
      >
        {{ plugin }}
      </button>
      <button
        #chevron
        type="button"
        class="text-foreground-muted hover:text-foreground grid h-full w-3.5 shrink-0 place-items-center rounded-e-[3px] outline-none"
        [attr.aria-label]="'Options for ' + plugin + ' on ' + channel()"
        aria-haspopup="menu"
        (click)="openMenu(chevron)"
      >
        <svg viewBox="0 0 8 8" class="size-2" aria-hidden="true">
          <path d="M1.5 3l2.5 2.5L6.5 3" fill="none" stroke="currentColor" stroke-width="1.25" stroke-linecap="round" />
        </svg>
      </button>
    } @else {
      <button
        type="button"
        class="text-foreground-muted h-full w-full truncate px-1 text-center opacity-0 outline-none group-hover:opacity-100 focus-visible:opacity-100"
        [attr.aria-label]="'Insert ' + (index() + 1) + ' on ' + channel() + ': empty, choose a plugin'"
        (click)="open.emit()"
      >
        Audio FX
      </button>
    }
  `
})
export class AnInsertSlot {
  /** The plugin's name; null for an empty slot. */
  readonly name = input<string | null>(null);
  /** The strip's name, for accessible names. */
  readonly channel = input.required<string>();
  /** The slot's position, from 0. */
  readonly index = input(0, { transform: numberAttribute });
  readonly bypassed = input(false, { transform: booleanAttribute });
  /** The host bypassed the plugin (it crashed or failed to load). */
  readonly problem = input(false, { transform: booleanAttribute });

  /** The name was activated: open the plugin's editor, or, on an empty slot, choose a plugin. */
  readonly open = output();
  readonly bypassToggle = output();
  /** Open the slot's menu at this point. */
  readonly menu = output<SlotMenuPoint>();
  /** Move the insert up (−1) or down (1) a slot. */
  readonly move = output<-1 | 1>();

  protected readonly title = computed(() => {
    const name = this.name() ?? '';
    if (this.problem()) {
      return `${name} stopped working and is bypassed`;
    }
    return this.bypassed() ? `${name} (bypassed)` : name;
  });

  protected openMenu(anchor: HTMLElement): void {
    const rect = anchor.getBoundingClientRect();
    this.menu.emit({ x: rect.left, y: rect.bottom });
  }

  protected key(event: KeyboardEvent): void {
    if (event.altKey && (event.key === 'ArrowUp' || event.key === 'ArrowDown')) {
      event.preventDefault();
      this.move.emit(event.key === 'ArrowUp' ? -1 : 1);
    } else if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
      event.preventDefault();
      const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
      this.menu.emit({ x: rect.left, y: rect.bottom });
    }
  }
}
