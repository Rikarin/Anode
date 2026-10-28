import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  input,
  numberAttribute,
  output
} from '@angular/core';
import type { SlotMenuPoint } from './insert-slot';
import { MaxFaderDb, SilenceDb, faderDb, faderPosition, formatDb } from './taper';

/** Pixels of vertical drag for the whole travel; ⇧ makes it ten times finer. */
const DragPixels = 160;
const Sweep = 270;

/** A point on the mini dial (radius `r` around 10,10) at `normal` of the sweep from 7:30. */
function point(normal: number, r: number): { x: number; y: number } {
  const angle = ((135 + normal * Sweep) * Math.PI) / 180;
  return { x: 10 + r * Math.cos(angle), y: 10 + r * Math.sin(angle) };
}

/** An SVG arc along the mini dial from `from` to `to` (fractions of the sweep). */
export function sendArc(from: number, to: number): string {
  const r = 8.5;
  const a = point(from, r);
  const b = point(to, r);
  const large = (to - from) * Sweep > 180 ? 1 : 0;
  return `M ${a.x.toFixed(2)} ${a.y.toFixed(2)} A ${r} ${r} 0 ${large} 1 ${b.x.toFixed(2)} ${b.y.toFixed(2)}`;
}

/** A send level after a key: arrows move 1 % of the fader travel (⇧ 0.1 %), Page keys 10 %, Home/End the ends. */
export function sendLevelAfterKey(db: number, key: string, fine = false): number | null {
  const step = fine ? 0.001 : 0.01;
  const moves: Record<string, number> = {
    ArrowUp: step,
    ArrowRight: step,
    ArrowDown: -step,
    ArrowLeft: -step,
    PageUp: 0.1,
    PageDown: -0.1
  };
  const move = moves[key];
  if (move !== undefined) {
    return roundDb(faderDb(Math.min(1, Math.max(0, faderPosition(db) + move))));
  }
  if (key === 'Home') {
    return SilenceDb;
  }
  if (key === 'End') {
    return MaxFaderDb;
  }
  return null;
}

function roundDb(db: number): number {
  return db <= SilenceDb ? SilenceDb : Math.round(db * 10) / 10;
}

/**
 * One send of a channel strip (Logic's Sends slots): the bus it goes to (a button that opens the bus menu), a small
 * level knob on the fader's taper (−∞ to +6 dB; drag vertically, ⇧ for fine; arrows, Page keys, Home and End;
 * double-click for 0 dB) and the pre-fader key (lit when the send taps before the fader). Values come in as inputs and
 * go out as events.
 */
@Component({
  selector: 'an-send-knob',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'strip-slot w-full gap-0.5 pe-0.5' },
  template: `
    <button
      #bus
      type="button"
      class="text-foreground h-full min-w-0 flex-1 truncate ps-1 text-start font-semibold outline-none"
      aria-haspopup="menu"
      [attr.aria-label]="'Send ' + (index() + 1) + ' of ' + channel() + ': ' + busName()"
      [attr.title]="busName()"
      (click)="openMenu(bus)"
    >
      {{ busName() }}
    </button>
    <button
      type="button"
      class="h-full shrink-0 px-0.5 text-[9px] font-bold uppercase outline-none"
      [class.text-lit]="preFader()"
      [class.text-foreground]="!preFader()"
      [attr.aria-pressed]="preFader()"
      [attr.aria-label]="'Pre-fader send to ' + busName() + ' on ' + channel()"
      [attr.title]="preFader() ? 'Pre-fader: click for post-fader' : 'Post-fader: click for pre-fader'"
      (click)="preToggle.emit()"
    >
      pre
    </button>
    <span
      role="slider"
      tabindex="0"
      class="relative size-4 shrink-0 cursor-ns-resize touch-none rounded-full outline-none"
      aria-valuemin="-144"
      aria-valuemax="6"
      [attr.aria-valuenow]="levelDb()"
      [attr.aria-valuetext]="text()"
      [attr.aria-label]="busName() + ' send level of ' + channel()"
      [attr.title]="busName() + ': ' + text() + ' (double-click for 0 dB)'"
      (pointerdown)="grab($event)"
      (pointermove)="drag($event)"
      (pointerup)="release($event)"
      (pointercancel)="release($event)"
      (dblclick)="set(0, true)"
      (keydown)="key($event)"
    >
      <span class="knob-body absolute inset-[24%]" aria-hidden="true"></span>
      <svg viewBox="0 0 20 20" class="absolute inset-0 size-full" aria-hidden="true">
        <path [attr.d]="track" class="stroke-knob-track fill-none" stroke-width="2.5" stroke-linecap="round" />
        @if (arc(); as d) {
          <path [attr.d]="d" class="stroke-knob-arc fill-none" stroke-width="2.5" stroke-linecap="round" />
        }
      </svg>
    </span>
  `
})
export class AnSendKnob {
  /** The bus's name ("Bus 1", or its I/O label). */
  readonly busName = input.required<string>();
  /** The strip's name, for accessible names. */
  readonly channel = input.required<string>();
  /** The send's position on the strip, from 0. */
  readonly index = input(0, { transform: numberAttribute });
  readonly levelDb = input(SilenceDb, { transform: numberAttribute });
  readonly preFader = input(false, { transform: booleanAttribute });

  /** The level moved (every step of a drag). */
  readonly levelChange = output<number>();
  /** A level gesture ended. */
  readonly levelCommit = output<number>();
  readonly preToggle = output();
  /** Open the bus menu at this point. */
  readonly menu = output<SlotMenuPoint>();

  protected readonly track = sendArc(0, 1);
  private readonly position = computed(() => faderPosition(this.levelDb()));
  protected readonly arc = computed(() => (this.position() < 0.002 ? null : sendArc(0, this.position())));
  protected readonly text = computed(() => formatDb(this.levelDb()));
  private dragStart: { y: number; position: number } | null = null;
  private current = 0;

  protected openMenu(anchor: HTMLElement): void {
    const rect = anchor.getBoundingClientRect();
    this.menu.emit({ x: rect.left, y: rect.bottom });
  }

  protected grab(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    const element = event.currentTarget as HTMLElement;
    element.setPointerCapture(event.pointerId);
    element.focus();
    this.dragStart = { y: event.clientY, position: this.position() };
    this.current = this.levelDb();
  }

  protected drag(event: PointerEvent): void {
    if (!this.dragStart) {
      return;
    }
    const scale = event.shiftKey ? DragPixels * 10 : DragPixels;
    const position = Math.min(1, Math.max(0, this.dragStart.position + (this.dragStart.y - event.clientY) / scale));
    this.dragStart = { y: event.clientY, position };
    this.set(roundDb(faderDb(position)), false);
  }

  protected release(event: PointerEvent): void {
    if (this.dragStart) {
      this.dragStart = null;
      (event.currentTarget as HTMLElement).releasePointerCapture(event.pointerId);
      this.levelCommit.emit(this.current);
    }
  }

  protected key(event: KeyboardEvent): void {
    const next = sendLevelAfterKey(this.levelDb(), event.key, event.shiftKey);
    if (next !== null) {
      event.preventDefault();
      this.set(next, true);
    }
  }

  protected set(db: number, commit: boolean): void {
    this.current = db;
    if (db !== this.levelDb()) {
      this.levelChange.emit(db);
    }
    if (commit) {
      this.levelCommit.emit(db);
    }
  }
}
