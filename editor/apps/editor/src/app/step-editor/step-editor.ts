import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  type ElementRef,
  inject,
  Injector,
  signal,
  viewChild
} from '@angular/core';
import type { Lane, LaneKind, LaneSet } from '@anode/api-client';
import { AnStepLane, type StepBarKey, type StepLaneStroke, type StepLaneTool } from '@anode/audio-ui/lazy';
import { provideIcons } from '@ng-icons/core';
import {
  matAddRound,
  matCloseRound,
  matEditRound,
  matExpandMoreRound,
  matShowChartRound,
  matZoomInRound,
  matZoomOutRound
} from '@ng-icons/material-icons/round';
import { XuiIconImports } from '@xui/icon';
import { XuiMenuImports } from '@xui/menu';
import { XuiNonIdealStateImports } from '@xui/non-ideal-state';
import { XuiTooltipImports } from '@xui/tooltip';
import { laneRange } from '../midi-edit/lanes';
import { gridLabel, QuantizeGrids } from '../midi/midi-math';
import { PianoRollStore } from '../state/piano-roll.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import {
  barKeyCommand,
  currentLaneSet,
  isUnstored,
  laneBars,
  LaneHeaderWidth,
  LaneHeight,
  LaneKinds,
  laneTestId,
  needsNumber,
  newLane,
  newLaneSetId,
  newSetName,
  strokeCommands,
  strokeLabel,
  strokePoints,
  withLane,
  withoutLane,
  withResolution,
  xAtTick,
  yAtValue
} from './step-gestures';

const Tools: readonly { value: StepLaneTool; label: string; icon: string }[] = [
  { value: 'pencil', label: 'Pencil', icon: 'matEditRound' },
  { value: 'line', label: 'Line', icon: 'matShowChartRound' }
];

/** The shortest gap between drawn grid lines, in CSS pixels: finer grids are left out. */
const MinGridPx = 6;

/**
 * The Step Editor (Logic's), an editor of the Editors pane: lanes of the shown regions' events (velocity, any CC,
 * pitch bend, pressure, program, a note's own velocity) as value bars, on the Piano Roll's time axis (its zoom and
 * scroll), under a ruler. The lanes come from a lane set: the project's (chosen in the lane set popup) or the default
 * set until the first change stores it. The pencil sets the value of each grid step it passes (the lane's
 * resolution); the line draws a straight line from point to point, one value per step. Each stroke is one undo step.
 */
@Component({
  selector: 'an-step-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnStepLane, XuiIconImports, XuiMenuImports, XuiNonIdealStateImports, XuiTooltipImports],
  providers: [
    provideIcons({
      matAddRound,
      matCloseRound,
      matEditRound,
      matExpandMoreRound,
      matShowChartRound,
      matZoomInRound,
      matZoomOutRound
    })
  ],
  host: {
    class: 'flex h-full min-h-0 flex-col',
    role: 'region',
    'aria-label': 'Step Editor',
    'data-testid': 'step-editor-view'
  },
  template: `
    <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2" role="toolbar" aria-label="Step Editor tools">
      @if (renaming()) {
        <input
          #nameField
          type="text"
          class="bg-surface-inset text-foreground border-primary h-6.5 w-36 rounded-[6px] border px-2 text-[12px] outline-none"
          aria-label="Lane set name"
          data-testid="lane-set-name"
          [value]="set().name"
          (keydown.enter)="rename(nameField.value)"
          (keydown.escape)="renaming.set(false)"
          (blur)="rename(nameField.value)"
        />
      } @else {
        <button
          type="button"
          class="bezel text-foreground flex h-6.5 max-w-44 items-center gap-1 px-2 text-[11px] font-semibold"
          aria-label="Lane set"
          data-testid="lane-set-menu"
          [xuiTooltip]="'Lane set: ' + set().name"
          [xuiMenuTriggerFor]="setMenu"
        >
          <span class="text-foreground-muted">Lanes</span><span class="truncate">{{ set().name }}</span>
          <ng-icon xui name="matExpandMoreRound" size="14px" />
        </button>
      }
      <ng-template #setMenu>
        <xui-menu class="min-w-44" aria-label="Lane sets">
          @for (item of setChoices(); track item.id) {
            <button
              xuiMenuItem
              type="button"
              role="menuitemradio"
              [attr.aria-checked]="item.id === set().id"
              [attr.data-testid]="'lane-set-' + item.id"
              (click)="chosen.set(item.id)"
            >
              {{ item.name }}
            </button>
          }
          <xui-menu-divider />
          <button xuiMenuItem type="button" data-testid="lane-set-new" (click)="newSet()">New Lane Set</button>
          <button xuiMenuItem type="button" data-testid="lane-set-rename" (click)="startRename()">Rename…</button>
          <button xuiMenuItem type="button" data-testid="lane-set-delete" [disabled]="unstored()" (click)="deleteSet()">
            Delete Lane Set
          </button>
        </xui-menu>
      </ng-template>
      <div class="bezel-group text-foreground h-6.5" role="radiogroup" aria-label="Tool" data-testid="step-tools">
        @for (item of tools; track item.value) {
          <button
            type="button"
            role="radio"
            class="grid w-6.5 place-items-center"
            [class.text-lit]="tool() === item.value"
            [attr.aria-checked]="tool() === item.value"
            [attr.aria-label]="item.label"
            [attr.data-testid]="'step-tool-' + item.value"
            [attr.tabindex]="tool() === item.value ? 0 : -1"
            [xuiTooltip]="item.label + ' tool'"
            (click)="tool.set(item.value)"
            (keydown)="toolKey($event)"
          >
            <ng-icon xui [name]="item.icon" size="15px" />
          </button>
        }
      </div>
      <div class="flex items-center gap-1">
        <input
          #numberField
          type="number"
          min="0"
          max="127"
          class="bg-surface-inset text-foreground border-hairline readout h-6.5 w-12 rounded-[6px] border px-1.5 text-[12px]"
          aria-label="Lane number (controller, key or pitch)"
          data-testid="add-lane-number"
          [value]="laneNumber()"
          (input)="laneNumber.set(numberField.valueAsNumber || 0)"
        />
        <button
          type="button"
          class="bezel text-foreground flex h-6.5 items-center gap-0.5 px-2 text-[11px] font-semibold"
          aria-label="Add lane"
          data-testid="add-lane"
          xuiTooltip="Add a lane (CC, poly pressure and note lanes take the number beside)"
          [xuiMenuTriggerFor]="laneMenu"
        >
          <ng-icon xui name="matAddRound" size="14px" />Lane
        </button>
      </div>
      <ng-template #laneMenu>
        <xui-menu aria-label="Lane kinds">
          @for (kind of laneKinds; track kind.kind) {
            <button xuiMenuItem type="button" [attr.data-testid]="'add-lane-' + kind.kind" (click)="addLane(kind.kind)">
              {{ kind.label }}{{ numbered(kind.kind) ? ' ' + laneNumber() : '' }}
            </button>
          }
        </xui-menu>
      </ng-template>
      <span class="text-foreground-muted min-w-0 truncate text-xs" data-testid="step-editor-title">{{ title() }}</span>
      <div class="ms-auto flex items-center gap-1">
        <button
          type="button"
          class="bezel text-foreground grid h-6.5 w-7 place-items-center"
          aria-label="Zoom out"
          (click)="store.zoom(1 / 1.5)"
        >
          <ng-icon xui name="matZoomOutRound" size="15px" />
        </button>
        <button
          type="button"
          class="bezel text-foreground grid h-6.5 w-7 place-items-center"
          aria-label="Zoom in"
          (click)="store.zoom(1.5)"
        >
          <ng-icon xui name="matZoomInRound" size="15px" />
        </button>
      </div>
    </div>
    @if (regions().length === 0) {
      <xui-non-ideal-state
        class="min-h-0 flex-1"
        title="No MIDI region"
        description="Select MIDI regions, or an instrument or MIDI track, to draw their velocities and controllers here."
      />
    } @else {
      <div #area class="flex min-h-0 flex-1 flex-col" (wheel)="wheel($event)">
        <div class="border-hairline bg-surface-raised flex h-5 shrink-0 border-b">
          <div class="border-hairline shrink-0 border-e" [style.width.px]="headerWidth"></div>
          <div class="relative min-w-0 flex-1 overflow-hidden" aria-hidden="true" data-testid="step-ruler">
            @for (bar of bars(); track bar.number) {
              <span
                class="border-hairline-strong text-foreground-muted readout absolute top-0 h-full border-s ps-1 text-[10px] leading-5 font-semibold"
                [style.left.px]="bar.x"
              >
                {{ bar.number }}
              </span>
            }
          </div>
        </div>
        <div class="min-h-0 flex-1 overflow-y-auto">
          @for (lane of set().lanes; track $index; let i = $index) {
            <an-step-lane
              [label]="lane.name"
              [bars]="lanes()[i]?.bars ?? []"
              [tool]="tool()"
              [height]="laneHeight"
              [headerWidth]="headerWidth"
              [min]="lanes()[i]?.min ?? 0"
              [max]="lanes()[i]?.max ?? 127"
              [gridLines]="lanes()[i]?.grid ?? []"
              [barLines]="barXs()"
              [zeroY]="lanes()[i]?.zeroY ?? null"
              [testId]="testIdOf(lane)"
              (stroke)="draw(lane, $event)"
              (barKey)="barKey(i, lane, $event)"
            >
              <span class="text-foreground truncate text-[12px] font-semibold" [attr.title]="lane.name">
                {{ lane.name }}
              </span>
              <div class="flex items-center gap-1">
                <button
                  type="button"
                  class="bezel text-foreground flex h-5 items-center px-1.5 text-[10px] font-semibold"
                  [attr.aria-label]="lane.name + ' resolution: ' + gridName(lane.resolutionTicks)"
                  [attr.data-testid]="'lane-resolution-' + i"
                  [xuiMenuTriggerFor]="resolutionMenu"
                  [xuiMenuTriggerData]="{ index: i }"
                >
                  {{ shortGrid(lane.resolutionTicks) }}
                </button>
                <button
                  type="button"
                  class="bezel text-foreground grid size-5 place-items-center"
                  [attr.aria-label]="'Remove ' + lane.name + ' lane'"
                  [attr.data-testid]="'lane-remove-' + i"
                  (click)="removeLane(i)"
                >
                  <ng-icon xui name="matCloseRound" size="12px" />
                </button>
              </div>
            </an-step-lane>
          } @empty {
            <p class="text-foreground-muted p-4 text-[12px]">This lane set has no lanes. Add one with + Lane.</p>
          }
        </div>
      </div>
    }
    <ng-template #resolutionMenu let-index="index">
      <xui-menu aria-label="Lane resolution">
        @for (grid of grids; track grid.ticks) {
          <button
            xuiMenuItem
            type="button"
            role="menuitemradio"
            [attr.aria-checked]="set().lanes[index]?.resolutionTicks === grid.ticks"
            (click)="setResolution(index, grid.ticks)"
          >
            {{ grid.label }}
          </button>
        }
      </xui-menu>
    </ng-template>
  `
})
export class StepEditor {
  protected readonly store = inject(PianoRollStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly injector = inject(Injector);
  private readonly area = viewChild<ElementRef<HTMLElement>>('area');
  private readonly nameField = viewChild<ElementRef<HTMLInputElement>>('nameField');

  protected readonly tools = Tools;
  protected readonly laneKinds = LaneKinds;
  protected readonly grids = QuantizeGrids;
  protected readonly laneHeight = LaneHeight;
  protected readonly headerWidth = LaneHeaderWidth;
  protected readonly numbered = needsNumber;
  protected readonly testIdOf = laneTestId;
  protected readonly gridName = gridLabel;

  readonly tool = signal<StepLaneTool>('pencil');
  /** The lane set chosen in the popup (null: the project's first, or the default). */
  readonly chosen = signal<string | null>(null);
  /** The number "Add Lane" gives a CC, poly pressure or note lane. */
  readonly laneNumber = signal(74);
  readonly renaming = signal(false);
  /** The lanes' body width in CSS pixels. */
  private readonly width = signal(1200);

  protected readonly regions = this.store.regions;
  readonly set = computed(() => currentLaneSet(this.project.laneSetList(), this.chosen()));
  protected readonly unstored = computed(() => isUnstored(this.set(), this.project.laneSetList()));
  protected readonly setChoices = computed(() => {
    const sets = this.project.laneSetList();
    return sets.length > 0 ? sets : [this.set()];
  });
  private readonly scale = computed(() => ({
    originTick: this.store.originTick(),
    pixelsPerTick: this.store.pixelsPerTick(),
    height: LaneHeight
  }));
  /** Bar lines in view: x and number. */
  protected readonly bars = computed(() => {
    const meter = this.project.meter();
    const scale = this.scale();
    const end = scale.originTick + this.width() / scale.pixelsPerTick;
    const bars: { x: number; number: number }[] = [];
    let bar = Math.max(0, Math.floor(meter.barAt(scale.originTick)));
    for (let tick = meter.barStart(bar); tick <= end && bars.length < 2000; tick = meter.barStart(++bar)) {
      bars.push({ x: xAtTick(tick, scale), number: bar + 1 });
    }
    return bars;
  });
  protected readonly barXs = computed(() => this.bars().map(b => b.x));
  /** Each lane's bars, value range and grid. */
  protected readonly lanes = computed(() => {
    const scale = this.scale();
    const width = this.width();
    const meter = this.project.meter();
    const selected = { notes: this.selection.notes(), controls: this.selection.controls() };
    return this.set().lanes.map(lane => {
      const [min, max] = laneRange(lane.kind);
      const stepPx = lane.resolutionTicks * scale.pixelsPerTick;
      const grid: number[] = [];
      if (stepPx >= MinGridPx) {
        const first = Math.floor(scale.originTick / lane.resolutionTicks) * lane.resolutionTicks;
        for (let tick = first; xAtTick(tick, scale) <= width; tick += lane.resolutionTicks) {
          grid.push(xAtTick(tick, scale));
        }
      }
      return {
        min,
        max,
        grid,
        zeroY: lane.kind === 'pitchBend' ? yAtValue(0, LaneHeight, [min, max]) : null,
        bars: laneBars(lane, this.regions(), scale, width, meter, selected)
      };
    });
  });
  protected readonly title = computed(() => {
    const regions = this.regions();
    const track = this.store.track();
    const names = regions.length === 1 ? (regions[0]?.name ?? '') : `${regions.length} regions`;
    return regions.length === 0 ? '' : track ? `${track.name}: ${names}` : names;
  });

  constructor() {
    const resize = typeof ResizeObserver === 'function' ? new ResizeObserver(() => this.measure()) : null;
    let observed: HTMLElement | null = null;
    // The lanes area comes and goes with the regions shown: observe whichever is there.
    effect(() => {
      const element = this.area()?.nativeElement ?? null;
      if (element !== observed) {
        if (observed) {
          resize?.unobserve(observed);
        }
        if (element) {
          resize?.observe(element);
        }
        observed = element;
        afterNextRender(() => this.measure(), { injector: this.injector });
      }
    });
    inject(DestroyRef).onDestroy(() => resize?.disconnect());
  }

  private measure(): void {
    const element = this.area()?.nativeElement;
    const width = element ? element.getBoundingClientRect().width - LaneHeaderWidth : 0;
    if (width > 0) {
      this.width.set(Math.round(width));
    }
  }

  protected shortGrid(ticks: number): string {
    return gridLabel(ticks).replace(' note', '').replace(' triplet', 'T');
  }

  /** ←/→ move the checked tool, as in a radio group. */
  protected toolKey(event: KeyboardEvent): void {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') {
      return;
    }
    event.preventDefault();
    const index = Tools.findIndex(t => t.value === this.tool());
    const next = Tools[(index + (event.key === 'ArrowRight' ? 1 : Tools.length - 1)) % Tools.length];
    if (next) {
      this.tool.set(next.value);
      const radios = (event.currentTarget as HTMLElement).parentElement?.querySelectorAll<HTMLElement>(
        '[role="radio"]'
      );
      radios?.[Tools.indexOf(next)]?.focus();
    }
  }

  /** A pencil or line stroke on a lane: one undo step. */
  async draw(lane: Lane, stroke: StepLaneStroke): Promise<void> {
    const points = strokePoints(stroke.points, lane, this.scale());
    const commands = strokeCommands(lane, stroke.tool, points, this.regions());
    if (commands.length > 0) {
      await this.project.execute(commands, strokeLabel(lane, stroke.tool));
    }
  }

  protected async barKey(index: number, lane: Lane, key: StepBarKey): Promise<void> {
    const bar = this.lanes()[index]?.bars[key.index];
    const command = bar ? barKeyCommand(lane, bar.ref, key.by) : null;
    if (command) {
      await this.project.execute([command], `${lane.name} Value`);
    }
  }

  /** Scrolls along time (horizontal wheel or ⇧-wheel) and zooms (⌘/Ctrl-wheel), as the Piano Roll does. */
  protected wheel(event: WheelEvent): void {
    if (event.metaKey || event.ctrlKey) {
      event.preventDefault();
      const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
      this.store.zoom(event.deltaY < 0 ? 1.1 : 1 / 1.1, Math.max(0, event.clientX - rect.left - LaneHeaderWidth));
      return;
    }
    const dx = event.shiftKey ? event.deltaY : event.deltaX;
    if (dx) {
      event.preventDefault();
      this.store.scrollBy(dx, 0, 0);
    }
  }

  /** Stores an edited set: the unstored default goes in under a new id, and stays chosen. */
  private async save(set: LaneSet, label: string): Promise<void> {
    const stored = isUnstored(set, this.project.laneSetList()) ? { ...set, id: newLaneSetId() } : set;
    this.chosen.set(stored.id);
    await this.project.execute([{ type: 'setLaneSet', set: stored }], label);
  }

  async addLane(kind: LaneKind): Promise<void> {
    const lane = newLane(kind, needsNumber(kind) ? this.laneNumber() : null);
    await this.save(withLane(this.set(), lane), `Add ${lane.name} Lane`);
  }

  async removeLane(index: number): Promise<void> {
    const lane = this.set().lanes[index];
    if (lane) {
      await this.save(withoutLane(this.set(), index), `Remove ${lane.name} Lane`);
    }
  }

  async setResolution(index: number, ticks: number): Promise<void> {
    await this.save(withResolution(this.set(), index, ticks), 'Lane Resolution');
  }

  /** A new set with the default lanes, chosen. */
  async newSet(): Promise<void> {
    const sets = this.project.laneSetList();
    const set: LaneSet = {
      id: newLaneSetId(),
      name: newSetName(sets),
      lanes: currentLaneSet([], null).lanes.map(l => ({ ...l }))
    };
    this.chosen.set(set.id);
    await this.project.execute([{ type: 'setLaneSet', set }], 'New Lane Set');
  }

  protected startRename(): void {
    this.renaming.set(true);
    afterNextRender(
      () => {
        this.nameField()?.nativeElement.focus();
        this.nameField()?.nativeElement.select();
      },
      { injector: this.injector }
    );
  }

  async rename(name: string): Promise<void> {
    if (!this.renaming()) {
      return;
    }
    this.renaming.set(false);
    const trimmed = name.trim();
    if (trimmed && trimmed !== this.set().name) {
      await this.save({ ...this.set(), name: trimmed }, 'Rename Lane Set');
    }
  }

  async deleteSet(): Promise<void> {
    const set = this.set();
    if (isUnstored(set, this.project.laneSetList())) {
      return;
    }
    this.chosen.set(null);
    await this.project.execute([{ type: 'deleteLaneSet', setId: set.id }], 'Delete Lane Set');
  }
}
