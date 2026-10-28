import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  signal,
  viewChild
} from '@angular/core';
import type { ArrangementMarker, EditCommand, Marker, TempoEvent } from '@anode/api-client';
import { XuiContextMenuImports } from '@xui/context-menu';
import { XuiInputImports } from '@xui/input';
import { XuiMenuImports } from '@xui/menu';
import { ProjectStore } from '../state/project.store';
import { type GlobalLane, GlobalLanes, TracksViewStore } from '../state/tracks-view.store';
import { parseSignature, TransportStore } from '../state/transport.store';
import { snapBy } from '../tracks/snap';
import {
  addKey,
  addSignature,
  addTempoPoint,
  bpmAtY,
  bpmRange,
  GlobalLaneHeights,
  keyName,
  moveTempoPoint,
  nearestBoundary,
  parseKey,
  removeEvent,
  removeTempoPoint,
  tempoPath,
  toggleRamp,
  yAtBpm
} from './global-math';

/** A drag in a global track: what it holds and where it started. */
interface Drag {
  kind: 'section' | 'marker' | 'tempo';
  id: string;
  index: number;
  x: number;
  y: number;
  startTick: number;
  moved: boolean;
}

/** An inline field over a global track (a name, a signature, a key). */
interface Field {
  lane: GlobalLane;
  id: string | null;
  tick: number;
  value: string;
  x: number;
}

/**
 * The global tracks' lanes (Logic's): the arrangement track's sections (drag to move one to a section boundary, ⌥-drag
 * to copy it there; double-click renames; the menu deletes, with or without its content), markers (drag to move,
 * double-click to rename, double-click the empty lane to add one), the tempo track (drag a point, double-click to add
 * one, ⌥-click a point or its menu for a ramp to the next), the signature and key tracks (double-click to add or edit).
 * Positions follow the Tracks area's zoom and scroll; edits snap by the snap mode and go to the Server as one step.
 */
@Component({
  selector: 'an-global-lanes',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiContextMenuImports, XuiInputImports, XuiMenuImports],
  host: {
    class: 'border-hairline bg-surface/85 block overflow-hidden border-b select-none',
    role: 'group',
    'aria-label': 'Global tracks',
    '(pointermove)': 'move($event)',
    '(pointerup)': 'up($event)',
    '(pointercancel)': 'drag.set(null)'
  },
  template: `
    @for (lane of lanes(); track lane) {
      <div
        class="border-hairline relative border-b"
        role="group"
        [attr.aria-label]="laneNames[lane] + ' track'"
        [attr.data-testid]="'global-' + lane"
        [style.height.px]="heights[lane]"
        (dblclick)="addAt(lane, $event)"
      >
        @switch (lane) {
          @case ('arrangement') {
            @for (section of sections(); track section.id) {
              <button
                type="button"
                class="text-foreground focus-visible:outline-focus absolute top-0.5 bottom-0.5 truncate rounded-[4px] px-1.5 text-start text-[11px] font-semibold shadow-[inset_0_0_0_1px_var(--hairline-strong)] focus-visible:outline-2"
                [style.left.px]="x(section.startTick) + sectionShift(section)"
                [style.width.px]="Math.max(4, section.lengthTicks * ppt() - 1)"
                [style.background-color]="section.color"
                [class.opacity-70]="drag()?.id === section.id"
                [attr.aria-label]="'Section ' + section.name"
                [attr.data-testid]="'section-' + section.name"
                [xuiContextMenuTriggerFor]="sectionMenu"
                [xuiContextMenuTriggerData]="{ $implicit: section }"
                (pointerdown)="press('section', section.id, 0, section.startTick, $event)"
                (dblclick)="$event.stopPropagation(); edit('arrangement', section.id, section.startTick, section.name)"
                (keydown.delete)="deleteSections([section.id], false)"
                (keydown.backspace)="deleteSections([section.id], false)"
              >
                <span class="mix-blend-normal [text-shadow:0_1px_1px_rgb(0_0_0/0.45)]">{{ section.name }}</span>
              </button>
            }
          }
          @case ('marker') {
            @for (marker of markers(); track marker.id) {
              <button
                type="button"
                class="text-foreground focus-visible:outline-focus bg-surface-raised absolute top-0.5 bottom-0.5 max-w-40 truncate rounded-e-[4px] border-s-2 px-1.5 text-start text-[11px] font-medium focus-visible:outline-2"
                [style.left.px]="x(markerTick(marker))"
                [style.border-color]="marker.color || 'var(--color-primary)'"
                [attr.aria-label]="'Marker ' + marker.name"
                [attr.data-testid]="'marker-' + marker.name"
                [xuiContextMenuTriggerFor]="markerMenu"
                [xuiContextMenuTriggerData]="{ $implicit: marker }"
                (pointerdown)="press('marker', marker.id, 0, marker.tick, $event)"
                (dblclick)="$event.stopPropagation(); edit('marker', marker.id, marker.tick, marker.name)"
                (keydown.delete)="deleteMarker(marker)"
                (keydown.backspace)="deleteMarker(marker)"
              >
                {{ marker.name }}
              </button>
            }
          }
          @case ('tempo') {
            <svg class="absolute inset-0 size-full overflow-visible" aria-hidden="true">
              <polyline class="fill-none stroke-(--color-primary)" stroke-width="1.5" [attr.points]="tempoLine()" />
            </svg>
            @for (event of tempoEvents(); track $index) {
              <button
                type="button"
                class="border-primary bg-surface focus-visible:outline-focus absolute size-2.5 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 focus-visible:outline-2"
                [class.bg-primary]="event.curve === 'ramp'"
                [style.left.px]="x(event.tick)"
                [style.top.px]="tempoY(event.bpm)"
                role="slider"
                [attr.aria-label]="'Tempo point ' + ($index + 1) + (event.curve === 'ramp' ? ', ramp' : '')"
                aria-valuemin="20"
                aria-valuemax="999"
                [attr.aria-valuenow]="event.bpm"
                [attr.aria-valuetext]="event.bpm + ' bpm'"
                [attr.data-testid]="'tempo-point-' + $index"
                [xuiContextMenuTriggerFor]="tempoMenu"
                [xuiContextMenuTriggerData]="{ $implicit: $index }"
                (pointerdown)="press('tempo', '', $index, event.tick, $event)"
                (dblclick)="$event.stopPropagation()"
                (keydown)="tempoKey($index, $event)"
              ></button>
              <span
                class="text-foreground-muted pointer-events-none absolute text-[10px] font-medium tabular-nums"
                [style.left.px]="x(event.tick) + 6"
                [style.top.px]="Math.max(0, tempoY(event.bpm) - 14)"
                >{{ event.bpm }}</span
              >
            }
          }
          @case ('signature') {
            @for (event of settings().signatures; track event.tick; let i = $index) {
              <button
                type="button"
                class="text-foreground hover:bg-hover-overlay focus-visible:outline-focus absolute top-0.5 bottom-0.5 rounded-[4px] px-1 text-[11px] font-semibold tabular-nums focus-visible:outline-2"
                [style.left.px]="x(event.tick)"
                [attr.aria-label]="'Signature ' + event.numerator + '/' + event.denominator"
                [xuiContextMenuTriggerFor]="eventMenu"
                [xuiContextMenuTriggerData]="{ $implicit: { lane: 'signature', index: i } }"
                (dblclick)="
                  $event.stopPropagation();
                  edit('signature', String(i), event.tick, event.numerator + '/' + event.denominator)
                "
              >
                {{ event.numerator }}/{{ event.denominator }}
              </button>
            }
          }
          @case ('key') {
            @for (event of settings().keys; track event.tick; let i = $index) {
              <button
                type="button"
                class="text-foreground hover:bg-hover-overlay focus-visible:outline-focus absolute top-0.5 bottom-0.5 rounded-[4px] px-1 text-[11px] font-semibold focus-visible:outline-2"
                [style.left.px]="x(event.tick)"
                [attr.aria-label]="'Key ' + keyName(event)"
                [xuiContextMenuTriggerFor]="eventMenu"
                [xuiContextMenuTriggerData]="{ $implicit: { lane: 'key', index: i } }"
                (dblclick)="$event.stopPropagation(); edit('key', String(i), event.tick, keyName(event))"
              >
                {{ keyName(event) }}
              </button>
            }
          }
        }
        @if (field(); as f) {
          @if (f.lane === lane) {
            <input
              #fieldInput
              xuiInput
              class="absolute top-0 h-5 w-32 px-1 text-[11px]"
              [style.left.px]="f.x"
              [attr.aria-label]="fieldLabel(f)"
              [value]="f.value"
              (pointerdown)="$event.stopPropagation()"
              (dblclick)="$event.stopPropagation()"
              (keydown.enter)="commitField(fieldInput.value)"
              (keydown.escape)="field.set(null)"
              (blur)="commitField(fieldInput.value)"
            />
          }
        }
      </div>
    }
    <ng-template #sectionMenu let-section>
      <xui-menu>
        <button xuiMenuItem type="button" (click)="edit('arrangement', section.id, section.startTick, section.name)">
          Rename
        </button>
        <xui-menu-divider />
        <button xuiMenuItem type="button" (click)="deleteSections([section.id], false)">Delete Marker</button>
        <button xuiMenuItem type="button" color="error" (click)="deleteSections([section.id], true)">
          Delete Section and Content
        </button>
      </xui-menu>
    </ng-template>
    <ng-template #markerMenu let-marker>
      <xui-menu>
        <button xuiMenuItem type="button" (click)="edit('marker', marker.id, marker.tick, marker.name)">Rename</button>
        <button xuiMenuItem type="button" color="error" (click)="deleteMarker(marker)">Delete Marker</button>
      </xui-menu>
    </ng-template>
    <ng-template #tempoMenu let-index>
      <xui-menu>
        <button xuiMenuItem type="button" [disabled]="index === tempoEvents().length - 1" (click)="ramp(index)">
          {{ tempoEvents()[index]?.curve === 'ramp' ? 'Step to Next Point' : 'Ramp to Next Point' }}
        </button>
        <button xuiMenuItem type="button" color="error" [disabled]="index === 0" (click)="removeTempo(index)">
          Delete Point
        </button>
      </xui-menu>
    </ng-template>
    <ng-template #eventMenu let-target>
      <xui-menu>
        <button xuiMenuItem type="button" color="error" [disabled]="target.index === 0" (click)="removeEvent(target)">
          Delete
        </button>
      </xui-menu>
    </ng-template>
  `
})
export class GlobalTracks {
  private readonly project = inject(ProjectStore);
  private readonly view = inject(TracksViewStore);
  private readonly transport = inject(TransportStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly fieldInput = viewChild<ElementRef<HTMLInputElement>>('fieldInput');

  protected readonly Math = Math;
  protected readonly String = String;
  protected readonly keyName = keyName;
  protected readonly heights = GlobalLaneHeights;
  protected readonly laneNames: Record<GlobalLane, string> = {
    arrangement: 'Arrangement',
    marker: 'Marker',
    tempo: 'Tempo',
    signature: 'Signature',
    key: 'Key'
  };

  protected readonly settings = this.project.settings;
  protected readonly sections = this.project.sections;
  protected readonly markers = this.project.markers;
  protected readonly lanes = computed(() => GlobalLanes.filter(lane => this.view.globalLanes().has(lane)));
  protected readonly ppt = this.view.pixelsPerTick;
  protected readonly drag = signal<Drag | null>(null);
  /** Where the drag is now, in ticks (snapped). */
  private readonly dragTick = signal(0);
  /** The tempo track while a point is dragged (a local preview). */
  private readonly tempoDraft = signal<TempoEvent[] | null>(null);
  protected readonly field = signal<Field | null>(null);

  protected readonly tempoEvents = computed(() => this.tempoDraft() ?? this.settings().tempoEvents);
  private readonly range = computed(() => bpmRange(this.settings().tempoEvents));
  protected readonly tempoLine = computed(() => {
    const width = this.host.nativeElement.getBoundingClientRect().width || 4000;
    return tempoPath(
      this.tempoEvents(),
      tick => this.x(tick),
      bpm => this.tempoY(bpm),
      width
    );
  });

  constructor() {
    effect(() => {
      const input = this.fieldInput()?.nativeElement;
      input?.focus();
      input?.select();
    });
  }

  protected x(tick: number): number {
    return (tick - this.view.originTick()) * this.ppt();
  }

  protected tempoY(bpm: number): number {
    return yAtBpm(bpm, this.range(), GlobalLaneHeights.tempo);
  }

  private tickAt(clientX: number): number {
    const rect = this.host.nativeElement.getBoundingClientRect();
    return this.view.originTick() + (clientX - rect.left) / this.ppt();
  }

  private snap(tick: number): number {
    const mode = this.view.snap() ? this.view.snapMode() : 'ticks';
    return snapBy(tick, mode, {
      meter: this.project.meter(),
      tempo: this.project.tempoMap(),
      sampleRate: this.project.info()?.sampleRate ?? 48_000,
      pixelsPerTick: this.ppt()
    });
  }

  /** A dragged section's offset while it moves (it lands on the nearest boundary). */
  protected sectionShift(section: ArrangementMarker): number {
    const drag = this.drag();
    if (drag?.kind !== 'section' || drag.id !== section.id || !drag.moved) {
      return 0;
    }
    return (this.dragTick() - drag.startTick) * this.ppt();
  }

  protected markerTick(marker: Marker): number {
    const drag = this.drag();
    return drag?.kind === 'marker' && drag.id === marker.id && drag.moved ? this.dragTick() : marker.tick;
  }

  protected press(kind: Drag['kind'], id: string, index: number, startTick: number, event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    event.stopPropagation();
    if (kind === 'tempo' && event.altKey) {
      this.ramp(index);
      return;
    }
    (event.target as HTMLElement).setPointerCapture(event.pointerId);
    this.drag.set({ kind, id, index, x: event.clientX, y: event.clientY, startTick, moved: false });
    this.dragTick.set(startTick);
  }

  protected move(event: PointerEvent): void {
    const drag = this.drag();
    if (!drag) {
      return;
    }
    if (!drag.moved && Math.hypot(event.clientX - drag.x, event.clientY - drag.y) < 3) {
      return;
    }
    if (!drag.moved) {
      this.drag.set({ ...drag, moved: true });
    }
    const travel = (event.clientX - drag.x) / this.ppt();
    if (drag.kind === 'section') {
      this.dragTick.set(drag.startTick + travel);
      return;
    }
    const tick = this.snap(drag.startTick + travel);
    this.dragTick.set(tick);
    if (drag.kind === 'tempo') {
      const lane = this.laneTop('tempo');
      const y = event.clientY - this.host.nativeElement.getBoundingClientRect().top - lane;
      this.tempoDraft.set(
        moveTempoPoint(this.settings().tempoEvents, drag.index, tick, bpmAtY(y, this.range(), GlobalLaneHeights.tempo))
      );
    }
  }

  protected up(event: PointerEvent): void {
    const drag = this.drag();
    this.drag.set(null);
    if (!drag?.moved) {
      this.tempoDraft.set(null);
      return;
    }
    switch (drag.kind) {
      case 'section': {
        const section = this.sections().find(s => s.id === drag.id);
        if (!section) {
          return;
        }
        const others = this.sections();
        // Dropped with its start nearest a boundary; boundaries at its own edges leave it where it is.
        const to = nearestBoundary(others, this.dragTick());
        const copy = event.altKey;
        if (!copy && (to === section.startTick || to === section.startTick + section.lengthTicks)) {
          return;
        }
        void this.project.execute(
          [{ type: 'moveSection', sectionId: section.id, toTick: to, copy }],
          copy ? 'Copy Section' : 'Move Section'
        );
        return;
      }
      case 'marker':
        void this.project.execute(
          [{ type: 'setMarker', markerId: drag.id, tick: Math.round(this.dragTick()) }],
          'Move Marker'
        );
        return;
      case 'tempo': {
        const events = this.tempoDraft();
        if (events) {
          void this.project
            .execute([{ type: 'setTempoEvents', events }], 'Tempo Change')
            .finally(() => this.tempoDraft.set(null));
        }
        return;
      }
    }
  }

  /** Double-click on an empty part of a lane: a new section, marker, tempo point, signature or key there. */
  protected addAt(lane: GlobalLane, event: MouseEvent): void {
    const tick = this.snap(this.tickAt(event.clientX));
    const meter = this.project.meter();
    switch (lane) {
      case 'arrangement':
        this.addSection(tick);
        return;
      case 'marker':
        void this.project.execute(
          [{ type: 'addMarker', name: `Marker ${this.markers().length + 1}`, tick, lengthTicks: 0 }],
          'Create Marker'
        );
        return;
      case 'tempo': {
        const y = event.clientY - this.host.nativeElement.getBoundingClientRect().top - this.laneTop('tempo');
        const bpm = bpmAtY(y, this.range(), GlobalLaneHeights.tempo);
        void this.project.execute(
          [{ type: 'setTempoEvents', events: addTempoPoint(this.settings().tempoEvents, tick, bpm) }],
          'Tempo Change'
        );
        return;
      }
      case 'signature':
      case 'key': {
        const at = meter.snapBar(tick);
        const current =
          lane === 'signature' ? `${meter.signatureAt(at).numerator}/${meter.signatureAt(at).denominator}` : '';
        this.edit(lane, null, at, current || 'C major');
        return;
      }
    }
  }

  /** A section after the last (or at `tick` in an empty arrangement): 8 bars, named as Logic would. */
  addSection(tick?: number): void {
    const sections = this.sections();
    const meter = this.project.meter();
    const start = sections.length
      ? Math.max(...sections.map(s => s.startTick + s.lengthTicks))
      : meter.snapBar(tick ?? 0);
    const startBar = Math.round(meter.barAt(start));
    const order = ['Intro', 'Verse', 'Chorus', 'Verse', 'Chorus', 'Bridge', 'Chorus', 'Outro'];
    void this.project.execute(
      [
        {
          type: 'addSection',
          name: order[sections.length] ?? `Section ${sections.length + 1}`,
          startTick: start,
          lengthTicks: meter.barStart(startBar + 8) - start
        }
      ],
      'Create Section'
    );
  }

  /** A marker at the playhead (⌥'). */
  addMarkerAtPlayhead(): void {
    const tick = this.project.meter().snapBar(this.transport.positionAt(performance.now()));
    void this.project.execute(
      [{ type: 'addMarker', name: `Marker ${this.markers().length + 1}`, tick, lengthTicks: 0 }],
      'Create Marker'
    );
  }

  protected edit(lane: GlobalLane, id: string | null, tick: number, value: string): void {
    this.field.set({ lane, id, tick, value, x: Math.max(0, this.x(tick)) });
  }

  protected fieldLabel(field: Field): string {
    return {
      arrangement: 'Section name',
      marker: 'Marker name',
      tempo: 'Tempo',
      signature: 'Time signature',
      key: 'Key'
    }[field.lane];
  }

  protected commitField(text: string): void {
    const field = this.field();
    this.field.set(null);
    const value = text.trim();
    if (!field || !value || value === field.value) {
      return;
    }
    const command = this.fieldCommand(field, value);
    if (command) {
      void this.project.execute([command.command], command.label);
    }
  }

  private fieldCommand(field: Field, value: string): { command: EditCommand; label: string } | null {
    const settings = this.settings();
    switch (field.lane) {
      case 'arrangement':
        return field.id
          ? { command: { type: 'setSection', sectionId: field.id, name: value }, label: 'Rename Section' }
          : null;
      case 'marker':
        return field.id
          ? { command: { type: 'setMarker', markerId: field.id, name: value }, label: 'Rename Marker' }
          : null;
      case 'signature': {
        const signature = parseSignature(value);
        if (!signature) {
          return null;
        }
        const events =
          field.id !== null
            ? settings.signatures.map((e, i) => (String(i) === field.id ? { ...e, ...signature } : e))
            : addSignature(
                settings.signatures,
                this.project.meter(),
                field.tick,
                signature.numerator,
                signature.denominator
              );
        return { command: { type: 'setSignatures', events }, label: 'Time Signature Change' };
      }
      case 'key': {
        const key = parseKey(value);
        if (!key) {
          return null;
        }
        const events =
          field.id !== null
            ? settings.keys.map((e, i) => (String(i) === field.id ? { ...e, ...key } : e))
            : addKey(settings.keys, field.tick, key.tonic, key.mode);
        return { command: { type: 'setKeys', events }, label: 'Key Change' };
      }
      default:
        return null;
    }
  }

  protected deleteSections(ids: string[], removeContent: boolean): void {
    void this.project.execute(
      [{ type: 'deleteSections', sectionIds: ids, removeContent }],
      removeContent ? 'Delete Section and Content' : 'Delete Section'
    );
  }

  protected deleteMarker(marker: Marker): void {
    void this.project.execute([{ type: 'deleteMarkers', markerIds: [marker.id] }], 'Delete Marker');
  }

  protected ramp(index: number): void {
    if (index < this.settings().tempoEvents.length - 1) {
      void this.project.execute(
        [{ type: 'setTempoEvents', events: toggleRamp(this.settings().tempoEvents, index) }],
        'Tempo Curve'
      );
    }
  }

  protected removeTempo(index: number): void {
    void this.project.execute(
      [{ type: 'setTempoEvents', events: removeTempoPoint(this.settings().tempoEvents, index) }],
      'Delete Tempo Point'
    );
  }

  protected removeEvent(target: { lane: 'signature' | 'key'; index: number }): void {
    const settings = this.settings();
    void this.project.execute(
      [
        target.lane === 'signature'
          ? { type: 'setSignatures', events: removeEvent(settings.signatures, target.index) }
          : { type: 'setKeys', events: removeEvent(settings.keys, target.index) }
      ],
      target.lane === 'signature' ? 'Delete Signature' : 'Delete Key'
    );
  }

  /** Arrows change a tempo point by 1 bpm (⇧: 10), R toggles its ramp, Delete removes it. */
  protected tempoKey(index: number, event: KeyboardEvent): void {
    const events = this.settings().tempoEvents;
    const point = events[index];
    if (!point) {
      return;
    }
    const step = event.shiftKey ? 10 : 1;
    let next: TempoEvent[] | null = null;
    if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
      next = moveTempoPoint(events, index, point.tick, point.bpm + (event.key === 'ArrowUp' ? step : -step));
    } else if (event.key === 'Delete' || event.key === 'Backspace') {
      next = removeTempoPoint(events, index);
    } else if (event.key.toLowerCase() === 'r') {
      next = toggleRamp(events, index);
    }
    if (next) {
      event.preventDefault();
      event.stopPropagation();
      void this.project.execute([{ type: 'setTempoEvents', events: next }], 'Tempo Change');
    }
  }

  /** A lane's top within the global tracks. */
  private laneTop(lane: GlobalLane): number {
    let top = 0;
    for (const l of this.lanes()) {
      if (l === lane) {
        return top;
      }
      top += GlobalLaneHeights[l];
    }
    return top;
  }
}
