import { ChangeDetectionStrategy, Component, computed, ElementRef, inject, signal } from '@angular/core';
import { GridLevel } from '@anode/timeline';
import { beatMappingGesture, mappingFor } from '../flex/beat-mapping';
import { FlexStore } from '../state/flex.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { SmartTempoStore } from '../state/smart-tempo.store';
import { TracksViewStore } from '../state/tracks-view.store';
import { GlobalLaneHeights, transientTicks } from './global-math';

/** The most transient marks drawn at once. */
const MaxMarks = 600;

/** A beat being dragged onto the audio: where it was picked and where the pointer is, in ticks. */
interface BeatDrag {
  fromTick: number;
  toTick: number;
  x: number;
  moved: boolean;
}

/**
 * The Beat Mapping global track (Logic's): the project's bars and beats in its upper half, the transients of the
 * selected audio track's regions (every audio track's when none is selected) in its lower half. Drag from a beat line
 * down onto a transient: the tempo changes so that the beat lands on it (one undo step), earlier mappings kept. A drop
 * away from a transient maps the beat to where it was dropped.
 */
@Component({
  selector: 'an-beat-mapping-lane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'absolute inset-0 block cursor-col-resize touch-none',
    role: 'group',
    'aria-label': 'Beat Mapping: drag a beat onto a transient',
    'data-testid': 'beat-mapping-lane',
    '(pointerdown)': 'down($event)',
    '(pointermove)': 'move($event)',
    '(pointerup)': 'up($event)',
    '(pointercancel)': 'drag.set(null)',
    '(dblclick)': '$event.stopPropagation()'
  },
  template: `
    <svg class="absolute inset-0 size-full overflow-visible" aria-hidden="true">
      <line class="stroke-(--hairline)" x1="0" x2="100%" [attr.y1]="half" [attr.y2]="half" />
      <path class="stroke-foreground-muted fill-none" stroke-width="1" [attr.d]="beatPath()" />
      <path class="stroke-foreground fill-none" stroke-width="1.25" [attr.d]="barPath()" />
      @for (x of marks(); track $index) {
        <line
          class="stroke-(--color-primary)"
          stroke-width="1.5"
          data-testid="beat-mapping-transient"
          [attr.x1]="x"
          [attr.x2]="x"
          [attr.y1]="half + 3"
          [attr.y2]="height - 3"
        />
      }
      @if (dragLine(); as line) {
        <line
          class="stroke-(--color-primary)"
          stroke-width="1.5"
          stroke-dasharray="3 2"
          [attr.x1]="line.x1"
          [attr.y1]="2"
          [attr.x2]="line.x2"
          [attr.y2]="height - 2"
        />
        <circle class="fill-(--color-primary)" r="3" [attr.cx]="line.x1" cy="3" />
      }
    </svg>
  `
})
export class BeatMappingLane {
  private readonly project = inject(ProjectStore);
  private readonly view = inject(TracksViewStore);
  private readonly selection = inject(SelectionStore);
  private readonly flex = inject(FlexStore);
  private readonly smartTempo = inject(SmartTempoStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly height = GlobalLaneHeights.beatMapping;
  protected readonly half = GlobalLaneHeights.beatMapping / 2;
  protected readonly drag = signal<BeatDrag | null>(null);

  private x(tick: number): number {
    return (tick - this.view.originTick()) * this.view.pixelsPerTick();
  }

  /** The visible ticks. */
  private readonly span = computed(() => {
    const from = this.view.originTick();
    const width = this.host.nativeElement.getBoundingClientRect().width || 4000;
    return [from, from + width / this.view.pixelsPerTick()] as const;
  });

  private readonly lines = computed(() => {
    const [from, to] = this.span();
    const meter = this.project.meter();
    const beat = meter.beatTicksAt(Math.max(0, from));
    // Beats while they are at least 6 px apart, else bars only.
    const step = beat * this.view.pixelsPerTick() >= 6 ? beat : meter.barTicksAt(Math.max(0, from));
    return meter.gridLines(from, to, step);
  });

  protected readonly beatPath = computed(() =>
    this.lines()
      .filter(l => l.level !== GridLevel.Bar)
      .map(l => `M${Math.round(this.x(l.tick)) + 0.5} ${this.half * 0.45}V${this.half}`)
      .join('')
  );

  protected readonly barPath = computed(() =>
    this.lines()
      .filter(l => l.level === GridLevel.Bar)
      .map(l => `M${Math.round(this.x(l.tick)) + 0.5} 1V${this.half}`)
      .join('')
  );

  /** The transients, in project ticks, of the selected audio track's regions (else every audio track's). */
  readonly transients = computed(() => {
    const tracks = this.project.trackList().filter(t => t.kind === 'audio');
    const selected = this.selection.track();
    const ids = new Set(tracks.some(t => t.id === selected) && selected ? [selected] : tracks.map(t => t.id));
    const ticks: number[] = [];
    for (const region of this.project.regionList()) {
      if (!ids.has(region.trackId) || region.muted) {
        continue;
      }
      const analysis = this.flex.analysis(region.fileId);
      if (!analysis) {
        continue;
      }
      const timing = this.project.timing(region);
      ticks.push(...transientTicks(region, analysis.transients, frame => timing.tickAt(frame)));
    }
    return ticks.sort((a, b) => a - b);
  });

  protected readonly marks = computed(() => {
    const [from, to] = this.span();
    return this.transients()
      .filter(t => t >= from && t <= to)
      .slice(0, MaxMarks)
      .map(t => Math.round(this.x(t)) + 0.5);
  });

  protected readonly dragLine = computed(() => {
    const drag = this.drag();
    if (!drag?.moved) {
      return null;
    }
    const gesture = this.gesture(drag);
    return gesture ? { x1: this.x(gesture.beatTick), x2: this.x(gesture.targetTick) } : null;
  });

  private tickAt(clientX: number): number {
    const rect = this.host.nativeElement.getBoundingClientRect();
    return this.view.originTick() + (clientX - rect.left) / this.view.pixelsPerTick();
  }

  private gesture(drag: BeatDrag) {
    return beatMappingGesture(
      this.project.meter(),
      drag.fromTick,
      drag.toTick,
      this.transients(),
      1 / this.view.pixelsPerTick()
    );
  }

  protected down(event: PointerEvent): void {
    const rect = this.host.nativeElement.getBoundingClientRect();
    if (event.button !== 0 || event.clientY - rect.top > this.half) {
      return;
    }
    event.stopPropagation();
    this.host.nativeElement.setPointerCapture(event.pointerId);
    const tick = this.tickAt(event.clientX);
    this.drag.set({ fromTick: tick, toTick: tick, x: event.clientX, moved: false });
  }

  protected move(event: PointerEvent): void {
    const drag = this.drag();
    if (!drag) {
      return;
    }
    const moved = drag.moved || Math.abs(event.clientX - drag.x) >= 3;
    this.drag.set({ ...drag, toTick: this.tickAt(event.clientX), moved });
  }

  protected up(event: PointerEvent): void {
    const drag = this.drag();
    this.drag.set(null);
    if (!drag?.moved) {
      return;
    }
    const gesture = this.gesture({ ...drag, toTick: this.tickAt(event.clientX) });
    if (!gesture || Math.abs(gesture.targetTick - gesture.beatTick) < 1) {
      return;
    }
    const sampleRate = this.project.info()?.sampleRate ?? 48_000;
    void this.smartTempo.mapBeats([
      mappingFor(this.project.tempoMap(), gesture.beatTick, gesture.targetTick, sampleRate)
    ]);
  }
}
