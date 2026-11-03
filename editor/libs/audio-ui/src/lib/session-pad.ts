import {
  booleanAttribute,
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  model,
  output,
  signal
} from '@angular/core';
import { padKey, padPosition, padText, padValueAt, type SessionPadValue } from './session-pad-math';

/** Another region's place on the pad (the track's other regions, shown faintly). */
export interface SessionPadMarker {
  id: string;
  name: string;
  value: SessionPadValue;
}

let nextId = 0;

/**
 * A Session Player's XY pad (Logic's Drummer pad): a sunken field, Simple to Complex left to right and Soft to Loud
 * bottom to top, with a lit puck for the region and faint rings for the track's other regions. Drag anywhere; from the
 * keyboard it is one ARIA slider (←/→ complexity, ↓/↑ intensity by 5 %, ⇧ 1 %, Home and End the corners). `value`
 * follows the drag; `committed` fires when a gesture ends, the moment to send the two numbers.
 */
@Component({
  selector: 'an-session-pad',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-flex flex-col items-stretch gap-1 select-none' },
  template: `
    <div class="flex gap-1">
      <span
        class="text-foreground-muted flex w-8 flex-col justify-between text-end text-[10px] leading-tight font-semibold uppercase"
        aria-hidden="true"
      >
        <span>Loud</span><span>Soft</span>
      </span>
      <div
        class="well focus-visible:outline-focus relative cursor-crosshair touch-none overflow-hidden rounded-[6px] outline-offset-2 focus-visible:outline-2"
        role="slider"
        aria-roledescription="XY pad"
        [style.width.px]="width()"
        [style.height.px]="height()"
        [class.opacity-35]="disabled()"
        [attr.tabindex]="disabled() ? -1 : 0"
        [attr.aria-labelledby]="labelId"
        aria-valuemin="0"
        aria-valuemax="100"
        [attr.aria-valuenow]="Math.round(position().left)"
        [attr.aria-valuetext]="text()"
        [attr.aria-disabled]="disabled() || null"
        (pointerdown)="down($event)"
        (pointermove)="drag($event)"
        (pointerup)="up($event)"
        (pointercancel)="up($event)"
        (keydown)="key($event)"
      >
        <svg class="absolute inset-0 size-full" aria-hidden="true">
          @for (line of grid; track line) {
            <line [attr.x1]="line + '%'" y1="0" [attr.x2]="line + '%'" y2="100%" class="stroke-knob-track" />
            <line x1="0" [attr.y1]="line + '%'" x2="100%" [attr.y2]="line + '%'" class="stroke-knob-track" />
          }
        </svg>
        @for (marker of others(); track marker.id) {
          <span
            class="border-foreground-muted absolute size-2.5 -translate-x-1/2 translate-y-1/2 rounded-full border opacity-60"
            [style.left.%]="marker.value.complexity * 100"
            [style.bottom.%]="marker.value.intensity * 100"
            [title]="marker.name"
            aria-hidden="true"
          ></span>
        }
        <span
          class="knob-body ring-primary absolute size-4 -translate-x-1/2 translate-y-1/2 rounded-full ring-2 transition-transform duration-100"
          [class.scale-125]="held()"
          [style.left.%]="position().left"
          [style.bottom.%]="position().bottom"
          aria-hidden="true"
        ></span>
      </div>
    </div>
    <span
      class="text-foreground-muted flex justify-between ps-9 text-[10px] leading-tight font-semibold uppercase"
      aria-hidden="true"
    >
      <span>Simple</span><span>Complex</span>
    </span>
    <span class="text-foreground ps-9 text-[10px] leading-tight font-semibold" [id]="labelId">{{ label() }}</span>
  `
})
export class AnSessionPad {
  protected readonly labelId = `an-session-pad-${nextId++}`;
  protected readonly grid = [25, 50, 75];
  protected readonly Math = Math;

  readonly value = model<SessionPadValue>({ complexity: 0.5, intensity: 0.5 });
  readonly label = input('Pattern');
  readonly others = input<readonly SessionPadMarker[]>([]);
  readonly width = input(240);
  readonly height = input(160);
  readonly disabled = input(false, { transform: booleanAttribute });
  /** A gesture ended (pointer up, a key press): send it. */
  readonly committed = output<SessionPadValue>();

  protected readonly held = signal(false);
  protected readonly position = computed(() => padPosition(this.value()));
  protected readonly text = computed(() => padText(this.value()));
  private pointer: number | null = null;

  protected down(event: PointerEvent): void {
    if (this.disabled() || event.button !== 0) {
      return;
    }
    const pad = event.currentTarget as HTMLElement;
    pad.setPointerCapture(event.pointerId);
    pad.focus();
    this.pointer = event.pointerId;
    this.held.set(true);
    this.place(pad, event);
  }

  protected drag(event: PointerEvent): void {
    if (this.pointer === event.pointerId) {
      this.place(event.currentTarget as HTMLElement, event);
    }
  }

  protected up(event: PointerEvent): void {
    if (this.pointer !== event.pointerId) {
      return;
    }
    this.pointer = null;
    this.held.set(false);
    this.committed.emit(this.value());
  }

  protected key(event: KeyboardEvent): void {
    if (this.disabled()) {
      return;
    }
    const next = padKey(this.value(), event.key, event.shiftKey);
    if (next) {
      event.preventDefault();
      this.value.set(next);
      this.committed.emit(next);
    }
  }

  private place(pad: HTMLElement, event: PointerEvent): void {
    const next = padValueAt(event.clientX, event.clientY, pad.getBoundingClientRect());
    const current = this.value();
    if (next.complexity !== current.complexity || next.intensity !== current.intensity) {
      this.value.set(next);
    }
  }
}
