import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { SmartControl, SmartControlsLayout } from '@anode/api-client';
import { AnKnob, AnXyPad, type XyValue } from '@anode/audio-ui';
import { provideIcons } from '@ng-icons/core';
import { matAutoFixHighRound, matExpandMoreRound, matInfoRound } from '@ng-icons/material-icons/round';
import { XuiIconImports } from '@xui/icon';
import { XuiMenuImports } from '@xui/menu';
import { XuiTooltipImports } from '@xui/tooltip';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { SmartControlsInspector } from './smart-controls-inspector';
import { SmartControlsStore } from './smart-controls.store';
import { knobCount } from './smart-math';

const Layouts: readonly { value: SmartControlsLayout; label: string }[] = [
  { value: 'knobs8', label: '8 Knobs' },
  { value: 'knobs12', label: '12 Knobs' }
];

/**
 * Smart Controls (Logic's, B) for the selected track: the layout's knobs in two rows, the two switches and the XY pad
 * (`padX`, `padY`), with the inspector beside them when toggled. The header holds the track's name, the layout picker,
 * the inspector toggle, automatic mapping, Compare and the arpeggiator button (docs/07-phase-6.md). Clicking (or
 * focusing) a control selects it for the inspector. A track whose Smart Controls are null shows the 8-knob layout; the
 * first command sets them up.
 */
@Component({
  selector: 'an-smart-controls-pane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnKnob, AnXyPad, SmartControlsInspector, XuiIconImports, XuiMenuImports, XuiTooltipImports],
  providers: [provideIcons({ matAutoFixHighRound, matExpandMoreRound, matInfoRound })],
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Smart Controls' },
  template: `
    @if (track(); as t) {
      <div class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2" role="toolbar" aria-label="Smart Controls tools">
        <button
          type="button"
          class="bezel text-foreground grid h-6.5 w-7 place-items-center"
          aria-label="Smart Controls Inspector"
          data-testid="smart-inspector-toggle"
          xuiTooltip="Show the Smart Controls inspector"
          [attr.aria-pressed]="store.inspectorShown()"
          [class.text-lit]="store.inspectorShown()"
          (click)="store.inspectorShown.set(!store.inspectorShown())"
        >
          <ng-icon xui name="matInfoRound" size="16px" />
        </button>
        <span class="text-foreground min-w-0 truncate text-xs font-semibold" data-testid="smart-track-name">{{
          t.name
        }}</span>
        <button
          type="button"
          class="bezel text-foreground flex h-6.5 items-center gap-1 px-2 text-[11px] font-semibold"
          data-testid="smart-layout"
          [attr.aria-label]="'Layout: ' + layoutLabel()"
          [xuiMenuTriggerFor]="layoutMenu"
        >
          {{ layoutLabel() }}
          <ng-icon xui name="matExpandMoreRound" size="14px" />
        </button>
        <ng-template #layoutMenu>
          <xui-menu aria-label="Smart Controls layout">
            @for (layout of layouts; track layout.value) {
              <button
                xuiMenuItem
                type="button"
                role="menuitemradio"
                [attr.aria-checked]="controls().layout === layout.value"
                [selected]="controls().layout === layout.value"
                (click)="store.setLayout(t.id, layout.value)"
              >
                {{ layout.label }}
              </button>
            }
          </xui-menu>
        </ng-template>
        <span class="flex-1"></span>
        <button
          type="button"
          class="bezel text-foreground flex h-6.5 items-center gap-1 px-2 text-[11px] font-semibold"
          data-testid="smart-auto-map"
          xuiTooltip="Map the knobs to the plugins' main parameters"
          (click)="store.autoMap(t.id)"
        >
          <ng-icon xui name="matAutoFixHighRound" size="14px" />
          Auto Map
        </button>
        <button
          type="button"
          class="bezel text-foreground h-6.5 px-2 text-[11px] font-semibold disabled:opacity-35"
          data-testid="smart-compare"
          title="Compare with the saved values"
          [attr.aria-pressed]="controls().comparing"
          [class.text-lit]="controls().comparing"
          [disabled]="!store.canCompare(t.id)"
          (click)="store.toggleCompare(t.id)"
        >
          Compare
        </button>
        <button
          type="button"
          class="bezel text-foreground h-6.5 px-2 text-[11px] font-semibold disabled:opacity-35"
          data-testid="smart-arpeggiator"
          [attr.aria-pressed]="!!controls().arpeggiator"
          [class.text-lit]="!!controls().arpeggiator"
          [disabled]="controls().arpeggiator === null || controls().arpeggiator === undefined"
          (click)="store.toggleArpeggiator(t.id)"
        >
          Arpeggiator
        </button>
      </div>
      <div class="flex min-h-0 flex-1">
        @if (store.inspectorShown()) {
          <an-smart-controls-inspector [trackId]="t.id" />
        }
        <div class="flex min-w-0 flex-1 items-center justify-center overflow-auto p-3">
          <div
            class="grid items-start gap-x-1 gap-y-3"
            role="grid"
            [attr.aria-label]="'Smart Controls of ' + t.name"
            [style.grid-template-columns]="'repeat(' + perRow() + ', auto) auto auto'"
          >
            @for (row of rows(); track $index; let r = $index) {
              <div class="contents" role="row">
                @for (knob of row; track knob.id) {
                  <an-knob
                    class="rounded-[6px] py-1"
                    role="gridcell"
                    data-testid="smart-knob"
                    [attr.data-control]="knob.id"
                    [attr.aria-selected]="store.selected() === knob.id"
                    [class.bg-muted]="store.selected() === knob.id"
                    [label]="knob.name"
                    [min]="0"
                    [max]="1"
                    [defaultValue]="0"
                    [format]="formatter(t.id, knob.id)"
                    [value]="store.value(t.id, knob.id)"
                    (pointerdown)="select(knob.id)"
                    (focusin)="select(knob.id)"
                    (valueChange)="store.set(t.id, knob.id, $event)"
                    (committed)="store.release(t.id, knob.id)"
                  />
                }
                @if (switches()[r]; as sw) {
                  <div
                    class="flex h-full flex-col items-center justify-center rounded-[6px] px-1"
                    role="gridcell"
                    [attr.aria-selected]="store.selected() === sw.id"
                    [class.bg-muted]="store.selected() === sw.id"
                    (pointerdown)="select(sw.id)"
                    (focusin)="select(sw.id)"
                  >
                    <button
                      type="button"
                      class="bezel text-foreground h-6.5 max-w-24 min-w-16 truncate px-2 text-[11px] font-semibold"
                      data-testid="smart-switch"
                      [attr.data-control]="sw.id"
                      [attr.aria-pressed]="store.value(t.id, sw.id) >= 0.5"
                      [class.text-lit]="store.value(t.id, sw.id) >= 0.5"
                      (click)="toggle(t.id, sw)"
                    >
                      {{ sw.name }}
                    </button>
                  </div>
                }
                @if (r === 0) {
                  <div
                    class="row-span-2 flex flex-col items-center gap-1 rounded-[6px] px-2 py-1"
                    role="gridcell"
                    [attr.aria-selected]="store.selected() === 'padX' || store.selected() === 'padY'"
                    [class.bg-muted]="store.selected() === 'padX' || store.selected() === 'padY'"
                  >
                    <an-xy-pad
                      data-testid="smart-pad"
                      label="Pad"
                      [size]="96"
                      [xLabel]="padX()?.name ?? 'Pad X'"
                      [yLabel]="padY()?.name ?? 'Pad Y'"
                      [value]="padValue()"
                      (valueChange)="movePad(t.id, $event)"
                      (committed)="releasePad(t.id)"
                      (pointerdown)="select(store.selected() === 'padY' ? 'padY' : 'padX')"
                    />
                    <div
                      class="bezel-group text-foreground h-5 text-[10px] font-semibold"
                      role="group"
                      aria-label="Pad axis"
                    >
                      @for (axis of padAxes; track axis.id) {
                        <button
                          type="button"
                          class="px-1.5"
                          [attr.aria-label]="'Select ' + axis.label"
                          [attr.aria-pressed]="store.selected() === axis.id"
                          [class.text-lit]="store.selected() === axis.id"
                          (click)="select(axis.id)"
                        >
                          {{ axis.short }}
                        </button>
                      }
                    </div>
                  </div>
                }
              </div>
            }
          </div>
        </div>
      </div>
    } @else {
      <p class="text-foreground-muted p-3 text-xs">Select a track to see its Smart Controls.</p>
    }
  `
})
export class SmartControlsPane {
  protected readonly project = inject(ProjectStore);
  protected readonly store = inject(SmartControlsStore);
  private readonly selection = inject(SelectionStore);

  protected readonly layouts = Layouts;
  protected readonly padAxes = [
    { id: 'padX', label: 'Pad X', short: 'X' },
    { id: 'padY', label: 'Pad Y', short: 'Y' }
  ];

  /** The selected track, unless it is one without Smart Controls (a VCA). */
  protected readonly track = computed(() => {
    const id = this.selection.track();
    const track = id ? this.project.track(id) : undefined;
    return track && track.kind !== 'vca' ? track : undefined;
  });
  protected readonly controls = computed(() => this.store.controls(this.track()?.id ?? ''));
  protected readonly layoutLabel = computed(
    () => Layouts.find(l => l.value === this.controls().layout)?.label ?? '8 Knobs'
  );
  protected readonly perRow = computed(() => knobCount(this.controls().layout) / 2);
  /** The knobs in two rows. */
  protected readonly rows = computed(() => {
    const knobs = this.controls().controls.filter(c => c.kind === 'knob');
    const half = Math.ceil(knobs.length / 2);
    return [knobs.slice(0, half), knobs.slice(half)];
  });
  protected readonly switches = computed(() => this.controls().controls.filter(c => c.kind === 'switch'));
  protected readonly padX = computed(() => this.controls().controls.find(c => c.id === 'padX'));
  protected readonly padY = computed(() => this.controls().controls.find(c => c.id === 'padY'));
  protected readonly padValue = computed<XyValue>(() => {
    const id = this.track()?.id ?? '';
    return { x: this.store.value(id, 'padX'), y: this.store.value(id, 'padY') };
  });

  private readonly formatters = new Map<string, (value: number) => string>();

  /** A stable formatter per control, so the knob's input does not change on every check. */
  protected formatter(trackId: string, controlId: string): (value: number) => string {
    const k = `${trackId}|${controlId}`;
    let f = this.formatters.get(k);
    if (!f) {
      f = value => this.store.format(trackId, controlId, value);
      this.formatters.set(k, f);
    }
    return f;
  }

  protected select(controlId: string): void {
    if (this.store.selected() !== controlId) {
      this.store.selected.set(controlId);
    }
  }

  protected toggle(trackId: string, control: SmartControl): void {
    this.select(control.id);
    void this.store.set(trackId, control.id, this.store.value(trackId, control.id) >= 0.5 ? 0 : 1);
    this.store.release(trackId, control.id);
  }

  protected movePad(trackId: string, value: XyValue): void {
    if (value.x !== this.store.value(trackId, 'padX')) {
      void this.store.set(trackId, 'padX', value.x);
    }
    if (value.y !== this.store.value(trackId, 'padY')) {
      void this.store.set(trackId, 'padY', value.y);
    }
  }

  protected releasePad(trackId: string): void {
    this.store.release(trackId, 'padX');
    this.store.release(trackId, 'padY');
  }
}
