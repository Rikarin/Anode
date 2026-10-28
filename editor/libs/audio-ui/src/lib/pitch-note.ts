import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, output } from '@angular/core';

/** A Flex Pitch note's hotspots (Logic's): drift top left, vibrato bottom centre, formant bottom right, gain bottom left. */
export type PitchHotspot = 'drift' | 'vibrato' | 'formant' | 'gain';

/** A drag (or key press) on a hotspot: `dy` pixels from where it started, up negative; `done` at its end. */
export interface HotspotDrag {
  hotspot: PitchHotspot;
  dy: number;
  done: boolean;
}

/** A pitch drag: `cents` from where it started (⌥: `fine`, not whole semitones); `done` at its end. */
export interface PitchDrag {
  cents: number;
  fine: boolean;
  done: boolean;
}

/** Pixels a hotspot key press moves (as a drag would). */
const HotspotKeyPixels = 5;
/** Pixels the pointer moves before a press on the body is a drag. */
const DragThreshold = 3;

interface Spot {
  readonly id: PitchHotspot;
  readonly label: string;
  readonly className: string;
}

const Spots: readonly Spot[] = [
  { id: 'drift', label: 'Drift', className: 'left-0.5 top-0.5' },
  { id: 'vibrato', label: 'Vibrato', className: 'bottom-0.5 left-1/2 -translate-x-1/2' },
  { id: 'formant', label: 'Formant', className: 'bottom-0.5 right-0.5' },
  { id: 'gain', label: 'Gain', className: 'bottom-0.5 left-0.5' }
];

/**
 * A Flex Pitch note (Logic's Audio Track Editor): a rounded blob in the track's colour at its target pitch, with its
 * played pitch curve drawn through it and four hotspots, shown while it is selected or hovered. The owner places and
 * sizes it (`width`, `height` in pixels) and keeps the maths: the blob is a slider of the note's pitch whose ↑/↓ nudge
 * a semitone (⌥: a cent) and whose vertical drag reports cents (`centsPerPixel`); each hotspot is a slider whose drag
 * and arrow keys report pixels moved. Presses select (⇧ adds); a double-click splits the note where it lands.
 */
@Component({
  selector: 'an-pitch-note',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'group absolute block',
    '[style.width.px]': 'width()',
    '[style.height.px]': 'height()'
  },
  template: `
    <div
      class="focus-visible:outline-focus absolute inset-0 cursor-ns-resize touch-none overflow-hidden rounded-[5px] border focus-visible:outline-2 focus-visible:outline-offset-1"
      role="slider"
      tabindex="0"
      data-testid="pitch-note"
      [class.border-white]="selected()"
      [class.border-hairline-strong]="!selected()"
      [style.background]="body()"
      [attr.aria-label]="'Note ' + label()"
      aria-valuemin="0"
      aria-valuemax="12700"
      [attr.aria-valuenow]="cents()"
      [attr.aria-valuetext]="label()"
      (pointerdown)="down($event)"
      (pointermove)="drag($event)"
      (pointerup)="up($event)"
      (pointercancel)="up($event)"
      (dblclick)="split.emit(fraction($event))"
      (keydown)="key($event)"
    >
      @if (curve()) {
        <svg class="pointer-events-none absolute inset-0 size-full overflow-visible" aria-hidden="true">
          <polyline class="fill-none stroke-white/80" stroke-width="1.25" [attr.points]="curve()" />
        </svg>
      }
    </div>
    @for (spot of spots; track spot.id) {
      <div
        class="bg-surface border-foreground focus-visible:outline-focus absolute size-2.5 cursor-ns-resize touch-none rounded-full border opacity-0 group-hover:opacity-100 focus-visible:opacity-100 focus-visible:outline-2"
        role="slider"
        tabindex="0"
        [class.opacity-100]="selected()"
        [class]="spot.className"
        [attr.data-testid]="'pitch-hotspot-' + spot.id"
        [attr.aria-label]="spot.label"
        [attr.aria-valuemin]="range(spot.id)[0]"
        [attr.aria-valuemax]="range(spot.id)[1]"
        [attr.aria-valuenow]="valueOf(spot.id)"
        [attr.aria-valuetext]="textOf(spot.id)"
        (pointerdown)="spotDown($event, spot.id)"
        (pointermove)="spotDrag($event)"
        (pointerup)="spotUp($event)"
        (pointercancel)="spotUp($event)"
        (keydown)="spotKey($event, spot.id)"
      ></div>
    }
  `
})
export class AnPitchNote {
  readonly width = input(48);
  readonly height = input(14);
  /** The pitch it plays, in MIDI cents (6900 is A4). */
  readonly cents = input(6000);
  /** Its label: "C3 +12". */
  readonly label = input('C3');
  /** The track colour (any CSS colour). */
  readonly color = input('#30a0e0');
  readonly selected = input(false, { transform: booleanAttribute });
  /** The played pitch curve, as SVG points in the note's own pixels. */
  readonly curve = input('');
  /** Cents per pixel of a vertical drag (the owner's rows). */
  readonly centsPerPixel = input(10);
  readonly drift = input(1);
  readonly vibrato = input(1);
  readonly formantCents = input(0);
  readonly gainDb = input(0);

  /** A press on the body: select it (`shift` adds). */
  readonly pressed = output<{ shift: boolean }>();
  readonly pitchDrag = output<PitchDrag>();
  /** ↑/↓: cents to move by (100, or 1 with ⌥). */
  readonly nudge = output<number>();
  readonly hotspotDrag = output<HotspotDrag>();
  /** A double-click: where (0…1 of its width) to split it. */
  readonly split = output<number>();

  protected readonly spots = Spots;

  protected readonly body = computed(() => {
    const c = this.color();
    const top = this.selected() ? 92 : 78;
    const bottom = this.selected() ? 70 : 56;
    return `linear-gradient(to bottom, color-mix(in srgb, ${c} ${top}%, white), color-mix(in srgb, ${c} ${bottom}%, black))`;
  });

  private dragging: { x: number; y: number; pointer: number; moved: boolean } | null = null;
  private spotDragging: { hotspot: PitchHotspot; y: number; pointer: number } | null = null;

  protected range(id: PitchHotspot): readonly [number, number] {
    switch (id) {
      case 'drift':
      case 'vibrato':
        return [0, 200];
      case 'formant':
        return [-1200, 1200];
      case 'gain':
        return [-30, 12];
    }
  }

  protected valueOf(id: PitchHotspot): number {
    switch (id) {
      case 'drift':
        return Math.round(this.drift() * 100);
      case 'vibrato':
        return Math.round(this.vibrato() * 100);
      case 'formant':
        return Math.round(this.formantCents());
      case 'gain':
        return Math.round(this.gainDb() * 10) / 10;
    }
  }

  protected textOf(id: PitchHotspot): string {
    const v = this.valueOf(id);
    switch (id) {
      case 'drift':
      case 'vibrato':
        return `${v} %`;
      case 'formant':
        return `${v > 0 ? '+' : ''}${v} cents`;
      case 'gain':
        return `${v > 0 ? '+' : ''}${v.toFixed(1)} dB`;
    }
  }

  protected fraction(event: MouseEvent): number {
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    return rect.width > 0 ? Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width)) : 0.5;
  }

  protected down(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    this.dragging = { x: event.clientX, y: event.clientY, pointer: event.pointerId, moved: false };
    this.pressed.emit({ shift: event.shiftKey });
  }

  protected drag(event: PointerEvent): void {
    const d = this.dragging;
    if (d?.pointer !== event.pointerId) {
      return;
    }
    if (!d.moved && Math.abs(event.clientY - d.y) < DragThreshold) {
      return;
    }
    d.moved = true;
    this.pitchDrag.emit({ cents: (d.y - event.clientY) * this.centsPerPixel(), fine: event.altKey, done: false });
  }

  protected up(event: PointerEvent): void {
    const d = this.dragging;
    if (d?.pointer !== event.pointerId) {
      return;
    }
    this.dragging = null;
    if (d.moved) {
      this.pitchDrag.emit({ cents: (d.y - event.clientY) * this.centsPerPixel(), fine: event.altKey, done: true });
    }
  }

  protected key(event: KeyboardEvent): void {
    const sign = event.key === 'ArrowUp' ? 1 : event.key === 'ArrowDown' ? -1 : 0;
    if (sign !== 0) {
      event.preventDefault();
      this.nudge.emit(sign * (event.altKey ? 1 : 100));
    }
  }

  protected spotDown(event: PointerEvent, hotspot: PitchHotspot): void {
    if (event.button !== 0) {
      return;
    }
    event.stopPropagation();
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    this.spotDragging = { hotspot, y: event.clientY, pointer: event.pointerId };
  }

  protected spotDrag(event: PointerEvent): void {
    const d = this.spotDragging;
    if (d?.pointer === event.pointerId) {
      this.hotspotDrag.emit({ hotspot: d.hotspot, dy: event.clientY - d.y, done: false });
    }
  }

  protected spotUp(event: PointerEvent): void {
    const d = this.spotDragging;
    if (d?.pointer === event.pointerId) {
      this.spotDragging = null;
      this.hotspotDrag.emit({ hotspot: d.hotspot, dy: event.clientY - d.y, done: true });
    }
  }

  protected spotKey(event: KeyboardEvent, hotspot: PitchHotspot): void {
    const sign =
      event.key === 'ArrowUp' || event.key === 'ArrowRight'
        ? -1
        : event.key === 'ArrowDown' || event.key === 'ArrowLeft'
          ? 1
          : 0;
    if (sign !== 0) {
      event.preventDefault();
      event.stopPropagation();
      this.hotspotDrag.emit({ hotspot, dy: sign * HotspotKeyPixels, done: true });
    }
  }
}
