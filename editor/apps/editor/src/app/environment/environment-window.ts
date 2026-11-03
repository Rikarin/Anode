import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import {
  type EnvironmentObject,
  type EnvironmentObjectKind,
  EnvironmentObjectKinds,
  type LiveMidiEvent
} from '@anode/api-client';
import { AnPianoKeyboard } from '@anode/audio-ui';
import { AnEnvironmentFader, AnEnvironmentMonitor, AnEnvironmentNode } from '@anode/audio-ui/lazy';
import { provideIcons } from '@ng-icons/core';
import { matAddRound, matDeleteOutlineRound } from '@ng-icons/material-icons/round';
import { XuiIconImports } from '@xui/icon';
import { type XuiGraphConnection, type XuiGraphEdge, XuiNodeGraphImports } from '@xui/node-graph';
import { FloatingWindow } from '../shell/floating-window';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import { EnvironmentGraphStore } from './environment-graph.store';
import { EnvironmentInspector } from './environment-inspector';
import { describeEvent } from './environment-model';

/** The object palette, in Logic's order: inputs, sources and views, processors, destinations. */
const Palette: readonly EnvironmentObjectKind[] = [
  'physicalInput',
  'keyboard',
  'fader',
  'monitor',
  'transformer',
  'delayLine',
  'arpeggiator',
  'chordMemorizer',
  'channelSplitter',
  'sequencerInput',
  'trackInput',
  'midiPort',
  'controlTarget'
];

/** Each kind's accent, from the track palette: inputs green, views orange, processors blue and violet, outputs red. */
const Accents: Record<EnvironmentObjectKind, string> = {
  physicalInput: 'var(--track-5)',
  sequencerInput: 'var(--track-1)',
  keyboard: 'var(--track-5)',
  monitor: 'var(--track-3)',
  fader: 'var(--track-3)',
  transformer: 'var(--track-8)',
  delayLine: 'var(--track-8)',
  arpeggiator: 'var(--track-10)',
  chordMemorizer: 'var(--track-10)',
  channelSplitter: 'var(--track-8)',
  midiPort: 'var(--track-1)',
  trackInput: 'var(--track-1)',
  controlTarget: 'var(--track-1)'
};

/**
 * The MIDI Environment window (ADR-0015, Logic's Environment; ⌘0): a `glass-bar` row of layers as a `bezel-group`
 * radiogroup (double-click renames; + adds; the bin deletes the layer shown), the object palette on the left, the
 * layer's canvas (`@xui/node-graph`: objects as nodes, cables as edges, drag from an outlet to an inlet; refused
 * cables never land) and the inspector on the right. Monitors show their last events and faders their value as the
 * host reports them; on-screen keyboards and faders play live MIDI into their object. Lazy-loaded with the node graph.
 */
@Component({
  selector: 'an-environment-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AnEnvironmentFader,
    AnEnvironmentMonitor,
    AnEnvironmentNode,
    AnPianoKeyboard,
    EnvironmentInspector,
    FloatingWindow,
    XuiIconImports,
    XuiNodeGraphImports
  ],
  providers: [EnvironmentGraphStore, provideIcons({ matAddRound, matDeleteOutlineRound })],
  template: `
    <an-floating-window
      title="MIDI Environment"
      testId="environment"
      width="min(72rem, 94vw)"
      [origin]="{ x: 120, y: 80 }"
      (closed)="ui.environmentOpen.set(false)"
    >
      <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2">
        <span class="text-foreground-muted text-[11px] font-semibold">Layer</span>
        <div class="bezel-group flex h-6.5" role="radiogroup" aria-label="Layers" data-testid="environment-layers">
          @for (layer of store.layers(); track layer.id) {
            @if (renaming() === layer.id) {
              <input
                #name
                class="bg-surface-inset text-foreground w-24 px-2 text-[11px] font-semibold"
                aria-label="Layer name"
                [value]="layer.name"
                (keydown.enter)="rename(layer.id, name.value)"
                (keydown.escape)="renaming.set(null)"
                (blur)="rename(layer.id, name.value)"
              />
            } @else {
              <button
                type="button"
                role="radio"
                class="px-2.5 text-[11px] font-semibold"
                [class.text-lit]="layer.id === store.layerId()"
                [attr.aria-checked]="layer.id === store.layerId()"
                [attr.data-testid]="'environment-layer-' + layer.name"
                (click)="store.showLayer(layer.id)"
                (dblclick)="renaming.set(layer.id)"
              >
                {{ layer.name }}
              </button>
            }
          }
          <button
            type="button"
            class="px-1.5"
            aria-label="New layer"
            data-testid="environment-new-layer"
            (click)="store.addLayer()"
          >
            <ng-icon xui name="matAddRound" size="sm" />
          </button>
        </div>
        <button
          type="button"
          class="bezel h-6.5 px-1.5 disabled:opacity-35"
          aria-label="Delete layer"
          [disabled]="!store.layerId()"
          (click)="deleteLayer()"
        >
          <ng-icon xui name="matDeleteOutlineRound" size="sm" />
        </button>
        <span class="text-foreground-muted readout ms-auto text-[11px]">
          {{ store.objects().length }} objects · {{ store.edges().length }} cables
        </span>
      </div>
      @if (store.problem(); as problem) {
        <p class="border-primary bg-primary/15 text-foreground border-b px-3 py-1 text-xs" role="status">
          {{ problem }}
        </p>
      }
      <div class="flex h-[32rem] min-h-0">
        <div
          class="border-hairline bg-surface flex w-36 shrink-0 flex-col overflow-y-auto border-e py-1"
          role="group"
          aria-label="New object"
        >
          <span class="text-foreground-muted px-3 py-1 text-[10px] font-semibold tracking-wide uppercase"
            >New Object</span
          >
          @for (kind of palette; track kind) {
            <button
              type="button"
              class="text-foreground hover:bg-hover-overlay focus-visible:outline-focus flex h-7 items-center gap-2 px-3 text-start text-xs focus-visible:outline-2 disabled:opacity-35"
              [attr.data-testid]="'environment-palette-' + kind"
              [disabled]="store.addProblem(kind) !== null"
              (click)="store.addObject(kind)"
            >
              <span class="size-2 shrink-0 rounded-full" [style.background]="accents[kind]"></span>
              {{ kinds[kind].name }}
            </button>
          }
        </div>
        <xui-node-graph
          class="bg-surface-sunken min-w-0 flex-1"
          background="dots"
          routing="bezier"
          snapToGrid
          [gridSize]="20"
          [edges]="store.edges()"
          [isValidConnection]="valid"
          [attr.aria-label]="layerName()"
          data-testid="environment-canvas"
          (connect)="store.connect($event)"
          (nodeMove)="store.move($event)"
          (edgeClick)="selectCable($event)"
          (deleteSelection)="deleteEdges($event.edges)"
          (keydown.delete)="deleteObject()"
          (keydown.backspace)="deleteObject()"
          (pointerdown)="clearSelection($event)"
        >
          @for (o of store.objects(); track o.id) {
            <an-environment-node
              [nodeId]="o.id"
              [name]="o.name"
              [kindName]="kinds[o.kind].name"
              [inlets]="kinds[o.kind].inlets"
              [outlets]="kinds[o.kind].outlets"
              [position]="{ x: o.x, y: o.y }"
              [width]="o.kind === 'keyboard' ? 280 : o.kind === 'channelSplitter' ? 180 : 160"
              [accent]="accents[o.kind]"
              [selected]="selectedId() === o.id"
              (pressed)="select(o)"
            >
              @switch (o.kind) {
                @case ('monitor') {
                  <an-environment-monitor [label]="o.name + ' events'" [lines]="monitorLines(o.id)" />
                }
                @case ('fader') {
                  <an-environment-fader
                    [label]="o.name + ' value'"
                    [value]="store.faderValue(o)"
                    (valueChange)="store.moveFader(o, $event)"
                  />
                }
                @case ('keyboard') {
                  <div class="text-foreground px-2 pb-2" xuiGraphNoDrag>
                    <an-piano-keyboard
                      class="h-14"
                      [label]="o.name + ' keys'"
                      [from]="60"
                      [octaves]="2"
                      [keyWidth]="16"
                      (noteOn)="key(o, $event.pitch, true)"
                      (noteOff)="key(o, $event, false)"
                    />
                  </div>
                }
                @default {
                  @if (summary(o); as text) {
                    <p class="text-foreground-muted truncate px-2 pb-2 text-[10px]">{{ text }}</p>
                  }
                }
              }
            </an-environment-node>
          }
          <xui-graph-controls />
        </xui-node-graph>
        <an-environment-inspector [object]="store.selectedObject()" />
      </div>
    </an-floating-window>
  `
})
export class EnvironmentWindow {
  protected readonly ui = inject(UiStore);
  protected readonly store = inject(EnvironmentGraphStore);
  private readonly project = inject(ProjectStore);

  protected readonly palette = Palette;
  protected readonly accents = Accents;
  protected readonly kinds = EnvironmentObjectKinds;
  protected readonly renaming = signal<string | null>(null);
  protected readonly valid = (connection: XuiGraphConnection) => this.store.connectionProblem(connection) === null;
  protected readonly selectedId = computed(() => {
    const selection = this.store.selection();
    return selection?.kind === 'object' ? selection.id : null;
  });
  protected readonly layerName = computed(
    () => this.store.layers().find(l => l.id === this.store.layerId())?.name ?? 'Environment'
  );

  constructor() {
    void this.store.ensureDefault();
  }

  protected monitorLines(objectId: string): string[] {
    return (this.store.monitors().get(objectId) ?? []).map(describeEvent);
  }

  /** What an object without a body does, in a line: its port, track, parameter, rules or taps. */
  protected summary(object: EnvironmentObject): string {
    const track = (id: string | null | undefined) => (id ? (this.project.track(id)?.name ?? id) : null);
    switch (object.kind) {
      case 'physicalInput':
        return object.port ?? 'Every input';
      case 'midiPort':
        return object.port ?? 'No port';
      case 'trackInput':
        return track(object.trackId) ?? 'No track';
      case 'controlTarget':
        return `${track(object.trackId) ?? 'Master'} · ${object.address ?? 'no parameter'}`;
      case 'transformer': {
        const c = object.conditions?.length ?? 0;
        const o = object.operations?.length ?? 0;
        return `${c} condition${c === 1 ? '' : 's'}, ${o} operation${o === 1 ? '' : 's'}`;
      }
      case 'delayLine': {
        const taps = object.taps?.length ?? 0;
        return `${taps} tap${taps === 1 ? '' : 's'}`;
      }
      case 'chordMemorizer':
        return object.chord?.length ? `Chord ${object.chord.join(' ')}` : 'Chord from its parameter';
      case 'sequencerInput':
        return 'The record-enabled track';
      default:
        return '';
    }
  }

  protected select(object: EnvironmentObject): void {
    this.store.selection.set({ kind: 'object', id: object.id });
  }

  protected selectCable(edge: XuiGraphEdge): void {
    this.store.selection.set({ kind: 'cable', id: edge.id });
  }

  /** A press on the empty canvas clears the selection (on a node or edge, they select). */
  protected clearSelection(event: PointerEvent): void {
    const target = event.target as Element | null;
    if (!target?.closest('[data-xui-graph-node], path, [data-xui-graph-port]')) {
      this.store.selection.set(null);
    }
  }

  protected deleteEdges(edges: readonly string[]): void {
    if (edges.length) {
      void this.store.disconnect(edges);
      this.store.selection.set(null);
    }
  }

  protected deleteObject(): void {
    if (this.store.selection()?.kind === 'object') {
      void this.store.deleteSelection();
    }
  }

  /** The on-screen keyboard: a note on at the object's velocity, or off, on its channel. */
  protected key(object: EnvironmentObject, pitch: number, on: boolean): void {
    const channel = Math.round(object.parameters['channel'] ?? 1) - 1;
    const velocity = Math.round(object.parameters['velocity'] ?? 100);
    const event: LiveMidiEvent = on
      ? { status: 0x90 | channel, data1: pitch, data2: velocity }
      : { status: 0x80 | channel, data1: pitch, data2: 0 };
    void this.store.play(object.id, [event]);
  }

  protected rename(layerId: string, name: string): void {
    if (this.renaming() !== layerId) {
      return;
    }
    this.renaming.set(null);
    const layer = this.store.layers().find(l => l.id === layerId);
    if (name.trim() && layer && name.trim() !== layer.name) {
      void this.store.renameLayer(layerId, name.trim());
    }
  }

  protected deleteLayer(): void {
    const id = this.store.layerId();
    if (id) {
      void this.store.removeLayer(id);
    }
  }
}
