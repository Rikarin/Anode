import {
  booleanAttribute,
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  model,
  numberAttribute,
  output,
  signal
} from '@angular/core';
import { type XuiGraphPoint, XuiNodeGraphImports } from '@xui/node-graph';

/**
 * An Environment object on its layer's canvas (Logic's Environment, ADR-0015) as an `@xui/node-graph` node: a card
 * with the object's name over its kind, its inlets as ports on the left (`in0`, `in1`…) and its outlets on the right
 * (`out0`…), named as the kind names them, and its body (a monitor's events, a fader, a keyboard) projected below.
 * The accent is the kind's colour. It must sit inside an `xui-node-graph`; the host is `display: contents`. The graph's
 * own node selection is off (it makes nodes `role="option"`, which may hold no ports or controls): the owner selects
 * on `pressed` and passes `selected`. Bodies with controls mark themselves `xuiGraphNoDrag`.
 */
@Component({
  selector: 'an-environment-node',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiNodeGraphImports],
  host: { class: 'contents' },
  template: `
    <xui-graph-node
      [nodeId]="nodeId()"
      [position]="position()"
      [width]="width()"
      [accent]="accent()"
      [selectable]="false"
      [class]="selected() ? 'ring-primary ring-2' : ''"
      [attr.data-testid]="'environment-node-' + name()"
      [attr.data-selected]="selected()"
      (click)="pressed.emit($event)"
      (activated)="activated.emit()"
    >
      <xui-graph-node-header>
        <span class="text-foreground min-w-0 flex-1 truncate text-[11px] font-semibold">{{ name() }}</span>
        @if (name() !== kindName()) {
          <span class="text-foreground-muted truncate text-[10px]">{{ kindName() }}</span>
        }
      </xui-graph-node-header>
      @for (inlet of inlets(); track $index) {
        <xui-graph-port [portId]="'in' + $index" direction="input" side="left" [label]="inlet" />
      }
      @for (outlet of outlets(); track $index) {
        <xui-graph-port [portId]="'out' + $index" direction="output" side="right" [label]="outlet" />
      }
      <ng-content />
    </xui-graph-node>
  `
})
export class AnEnvironmentNode {
  readonly nodeId = input.required<string>();
  readonly name = input.required<string>();
  /** The kind's name ("Transformer"); shown under the name when the object has its own. */
  readonly kindName = input.required<string>();
  readonly inlets = input<readonly string[]>([]);
  readonly outlets = input<readonly string[]>([]);
  readonly position = input<XuiGraphPoint>({ x: 0, y: 0 });
  readonly width = input(160, { transform: numberAttribute });
  /** The kind's colour (a CSS colour, a token). */
  readonly accent = input('var(--color-primary)');
  /** The object is the window's selection (its inspector shows it): a ring in `--color-primary`. */
  readonly selected = input(false, { transform: booleanAttribute });
  /** A click on the node: select it (the window keeps the selection; the graph's own is off, see below). */
  readonly pressed = output<MouseEvent>();
  /** A double-click on the header: open the object's inspector. */
  readonly activated = output();
}

/**
 * A monitor object's body: its last events (oldest first, at most eight) as a small `well` of `readout` lines, or a
 * muted dash before any arrive. A `role="log"` named by the monitor.
 */
@Component({
  selector: 'an-environment-monitor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block px-2 pb-2' },
  template: `
    <div
      class="well text-foreground readout min-h-12 rounded-[5px] px-1.5 py-1 text-[10px] leading-tight"
      role="log"
      [attr.aria-label]="label()"
    >
      @for (line of lines(); track $index) {
        <div class="truncate">{{ line }}</div>
      } @empty {
        <div class="text-foreground-muted">—</div>
      }
    </div>
  `
})
export class AnEnvironmentMonitor {
  readonly lines = input<readonly string[]>([]);
  readonly label = input('Monitor');
}

/**
 * A fader object's body: a horizontal `well` slot with a `bg-primary` fill and a metal cap, its value (0…127) in
 * `readout`. Drag it, or from the keyboard (one ARIA slider): ←/→ by 1 (⇧ 10), Page keys by 16, Home/End the ends.
 * `valueChange` fires as it moves (the window sends live MIDI), `committed` when a gesture ends.
 */
@Component({
  selector: 'an-environment-fader',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex items-center gap-2 px-2 pb-2', 'data-xui-graph-no-drag': '' },
  template: `
    <div
      class="well focus-visible:outline-focus relative h-3 flex-1 cursor-pointer touch-none rounded-full outline-offset-2 focus-visible:outline-2"
      role="slider"
      tabindex="0"
      aria-valuemin="0"
      aria-valuemax="127"
      [attr.aria-label]="label()"
      [attr.aria-valuenow]="value()"
      [attr.aria-disabled]="disabled() || null"
      (pointerdown)="down($event)"
      (pointermove)="move($event)"
      (pointerup)="up($event)"
      (pointercancel)="up($event)"
      (keydown)="key($event)"
    >
      <span class="bg-primary absolute inset-y-0 left-0 rounded-full" [style.width.%]="percent()"></span>
      <span
        class="fader-cap absolute top-1/2 h-4 w-2 -translate-x-1/2 -translate-y-1/2 rounded-[2px]"
        [style.left.%]="percent()"
      ></span>
    </div>
    <span class="text-foreground readout w-6 text-end text-[11px]">{{ value() }}</span>
  `
})
export class AnEnvironmentFader {
  readonly value = model(0);
  readonly label = input('Fader');
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly committed = output<number>();

  protected readonly percent = computed(() => (Math.min(127, Math.max(0, this.value())) / 127) * 100);
  private readonly pointer = signal<number | null>(null);

  protected down(event: PointerEvent): void {
    if (this.disabled() || event.button !== 0) {
      return;
    }
    event.stopPropagation();
    const slot = event.currentTarget as HTMLElement;
    slot.setPointerCapture(event.pointerId);
    slot.focus();
    this.pointer.set(event.pointerId);
    this.place(slot, event);
  }

  protected move(event: PointerEvent): void {
    if (this.pointer() === event.pointerId) {
      event.stopPropagation();
      this.place(event.currentTarget as HTMLElement, event);
    }
  }

  protected up(event: PointerEvent): void {
    if (this.pointer() === event.pointerId) {
      this.pointer.set(null);
      this.committed.emit(this.value());
    }
  }

  protected key(event: KeyboardEvent): void {
    if (this.disabled()) {
      return;
    }
    const step = event.shiftKey ? 10 : 1;
    const v = this.value();
    const next: Record<string, number> = {
      ArrowLeft: v - step,
      ArrowDown: v - step,
      ArrowRight: v + step,
      ArrowUp: v + step,
      PageDown: v - 16,
      PageUp: v + 16,
      Home: 0,
      End: 127
    };
    const to = next[event.key];
    if (to !== undefined) {
      event.preventDefault();
      event.stopPropagation();
      this.set(to);
      this.committed.emit(this.value());
    }
  }

  private place(slot: HTMLElement, event: PointerEvent): void {
    const box = slot.getBoundingClientRect();
    if (box.width > 0) {
      this.set(((event.clientX - box.left) / box.width) * 127);
    }
  }

  private set(value: number): void {
    const next = Math.round(Math.min(127, Math.max(0, value)));
    if (next !== this.value()) {
      this.value.set(next);
    }
  }
}
