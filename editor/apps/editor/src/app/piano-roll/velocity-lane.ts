import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  signal,
  viewChild
} from '@angular/core';
import { velocityColor } from '../midi/midi-math';
import { PianoRollStore } from '../state/piano-roll.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { noteCommands, notesLabel, offsetVelocities, type PlacedNote, placeNotes, velocityAtY } from './note-edit';

/** How close (CSS pixels) to a note's velocity bar the pointer grabs it. */
const GrabPx = 6;

/**
 * The Piano Roll's velocity lane: a bar per note at its start, as tall as its velocity and in its colour, on the Piano
 * Roll's time scale. Drag a bar up or down to change that note's velocity, or every selected note's by the same amount
 * when it is selected; dropping sends one `editNotes` batch. Drawn with Canvas2D on the main thread (a few hundred
 * bars at most are on screen).
 */
@Component({
  selector: 'an-velocity-lane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'relative block min-w-0 touch-none overflow-hidden',
    '(pointerdown)': 'down($event)',
    '(pointermove)': 'move($event)',
    '(pointerup)': 'up($event)',
    '(pointercancel)': 'cancel()'
  },
  template: `<canvas #canvas class="absolute inset-0 size-full" role="img" [attr.aria-label]="summary()"></canvas>`
})
export class VelocityLane {
  private readonly store = inject(PianoRollStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly size = signal({ width: 1, height: 1 });

  private readonly notes = computed(() => placeNotes(this.store.regions()));
  /** Velocities being dragged, by note id. */
  private readonly preview = signal<ReadonlyMap<number, number> | null>(null);
  private drag: { note: PlacedNote; y: number; selected: PlacedNote[] } | null = null;

  protected readonly summary = computed(() => {
    const selected = this.notes().filter(n => this.selection.notes().has(n.id));
    if (selected.length === 0) {
      return 'Velocity lane';
    }
    const average = Math.round(selected.reduce((sum, n) => sum + n.velocity, 0) / selected.length);
    return `Velocity lane: ${selected.length} selected, average velocity ${average}`;
  });

  constructor() {
    const resize = typeof ResizeObserver === 'function' ? new ResizeObserver(() => this.measure()) : null;
    afterNextRender(() => {
      this.measure();
      resize?.observe(this.host.nativeElement);
    });
    inject(DestroyRef).onDestroy(() => resize?.disconnect());
    effect(() => this.draw());
  }

  private measure(): void {
    const rect = this.host.nativeElement.getBoundingClientRect();
    this.size.set({ width: Math.max(1, Math.round(rect.width)), height: Math.max(1, Math.round(rect.height)) });
  }

  private x(tick: number): number {
    return (tick - this.store.originTick()) * this.store.pixelsPerTick();
  }

  private draw(): void {
    const { width, height } = this.size();
    const notes = this.notes();
    const selected = this.selection.notes();
    const preview = this.preview();
    const originTick = this.store.originTick();
    const ppt = this.store.pixelsPerTick();
    const canvas = this.canvas().nativeElement;
    const context = typeof canvas.getContext === 'function' ? canvas.getContext('2d') : null;
    if (!context) {
      return;
    }

    const dpr = devicePixelRatio || 1;
    canvas.width = Math.round(width * dpr);
    canvas.height = Math.round(height * dpr);
    context.setTransform(dpr, 0, 0, dpr, 0, 0);
    const styles = getComputedStyle(this.host.nativeElement);
    context.fillStyle = styles.getPropertyValue('--color-surface-sunken').trim() || '#18191c';
    context.fillRect(0, 0, width, height);
    context.fillStyle = styles.getPropertyValue('--color-border').trim() || '#2b2d33';
    context.fillRect(0, Math.round(height / 2), width, 1);

    const usable = height - 4;
    for (const note of notes) {
      const x = (note.start - originTick) * ppt;
      if (x < -4 || x > width + 4) {
        continue;
      }
      const velocity = preview?.get(note.id) ?? note.velocity;
      const top = height - 2 - (velocity / 127) * usable;
      context.fillStyle = velocityColor(velocity);
      context.fillRect(Math.round(x), top, 2, height - top);
      context.fillRect(Math.round(x) - 2, top - 1, 6, 3);
      if (selected.has(note.id)) {
        context.strokeStyle = styles.getPropertyValue('--color-foreground').trim() || '#f2f3f7';
        context.strokeRect(Math.round(x) - 2.5, top - 1.5, 7, 4);
      }
    }
  }

  private local(event: PointerEvent): { x: number; y: number } {
    const rect = this.host.nativeElement.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }

  /** The note whose bar is nearest `x`, within {@link GrabPx}; a selected one wins a tie. */
  private noteAt(x: number): PlacedNote | null {
    const selected = this.selection.notes();
    let best: PlacedNote | null = null;
    let distance = GrabPx;
    for (const note of this.notes()) {
      const d = Math.abs(this.x(note.start) + 1 - x);
      if (d < distance || (d === distance && best && selected.has(note.id) && !selected.has(best.id))) {
        best = note;
        distance = d;
      }
    }
    return best;
  }

  protected down(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }

    const { x, y } = this.local(event);
    const note = this.noteAt(x);
    if (!note) {
      return;
    }

    const selected = this.selection.notes();
    if (!selected.has(note.id)) {
      this.selection.selectNotes([note.id]);
    }
    const notes = selected.has(note.id) ? this.notes().filter(n => selected.has(n.id)) : [note];
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    this.drag = { note, y, selected: notes };
    this.update(y);
  }

  protected move(event: PointerEvent): void {
    if (this.drag) {
      this.update(this.local(event).y);
    }
  }

  protected up(event: PointerEvent): void {
    const drag = this.drag;
    this.drag = null;
    (event.currentTarget as HTMLElement).releasePointerCapture(event.pointerId);
    if (!drag) {
      return;
    }

    const edited = this.edited(drag, this.local(event).y);
    const original = new Map(this.notes().map(n => [n.id, n]));
    const regions = new Map(this.store.regions().map(r => [r.id, r]));
    const commands = noteCommands(edited, original, regions);
    if (commands.length === 0) {
      this.preview.set(null);
      return;
    }
    void this.project
      .execute(commands, notesLabel('Velocity of', edited.length).replace('Velocity of Note', 'Note Velocity'))
      .finally(() => this.preview.set(null));
  }

  protected cancel(): void {
    this.drag = null;
    this.preview.set(null);
  }

  private update(y: number): void {
    if (this.drag) {
      this.preview.set(new Map(this.edited(this.drag, y).map(n => [n.id, n.velocity])));
    }
  }

  /** The grabbed note takes the velocity at the pointer; the other selected notes move by as much. */
  private edited(drag: { note: PlacedNote; selected: PlacedNote[] }, y: number): PlacedNote[] {
    const target = velocityAtY(y - 2, this.size().height - 4);
    return offsetVelocities(drag.selected, target - drag.note.velocity);
  }
}
