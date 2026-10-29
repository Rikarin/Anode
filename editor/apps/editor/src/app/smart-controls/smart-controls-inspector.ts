import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { SmartMapping } from '@anode/api-client';
import { AnMappingGraph, type MappingPoint } from '@anode/audio-ui';
import { provideIcons } from '@ng-icons/core';
import { matAddRound, matCloseRound, matExpandMoreRound, matSwapVertRound } from '@ng-icons/material-icons/round';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiMenuImports } from '@xui/menu';
import { XuiTooltipImports } from '@xui/tooltip';
import { SmartControlsStore } from './smart-controls.store';

let nextId = 0;

const Band =
  'text-foreground border-hairline bg-surface-raised truncate border-y px-3 py-1 text-[11px] font-bold shadow-[inset_0_1px_0_var(--highlight)]';

/**
 * Logic's Smart Controls inspector: the selected control's name (Enter renames it) and its parameter mappings, each
 * with its target (a menu to retarget it), its range in the parameter's unit, invert, the mapping graph and a remove
 * button; "Add Mapping" opens the parameter picker (the instrument, each insert, each MIDI FX slot, Main). Every
 * change sends the control's whole mapping list.
 */
@Component({
  selector: 'an-smart-controls-inspector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnMappingGraph, XuiIconImports, XuiInputImports, XuiMenuImports, XuiTooltipImports],
  providers: [provideIcons({ matAddRound, matCloseRound, matExpandMoreRound, matSwapVertRound })],
  host: {
    class: 'border-hairline bg-surface flex w-72 shrink-0 flex-col overflow-y-auto border-e',
    role: 'region',
    'aria-label': 'Smart Controls Inspector',
    'data-testid': 'smart-inspector'
  },
  template: `
    @if (control(); as c) {
      <h3 class="${Band}">{{ c.name }}</h3>
      <div class="grid grid-cols-[4.5rem_1fr] items-center gap-x-2 gap-y-1 px-3 py-2 text-[11px]">
        <label class="text-foreground-muted" [for]="nameId">Name</label>
        <input
          #name
          xuiInput
          class="h-6 px-1 text-xs"
          data-testid="smart-control-name"
          [id]="nameId"
          [value]="c.name"
          (keydown.enter)="rename(name.value)"
          (keydown.escape)="name.value = c.name; name.blur()"
          (blur)="rename(name.value)"
        />
      </div>
      <h3 class="${Band}">Parameter Mapping</h3>
      <div class="flex flex-col gap-2 px-3 py-2" role="list" aria-label="Mappings">
        @for (mapping of c.mappings; track $index; let i = $index) {
          <div
            class="border-hairline bg-surface-raised flex flex-col gap-1.5 rounded-[6px] border p-2"
            role="listitem"
            data-testid="smart-mapping"
            [attr.data-target]="mapping.target"
            [attr.aria-label]="'Mapping to ' + targetName(mapping)"
          >
            <div class="flex items-center gap-1">
              <button
                type="button"
                class="bezel text-foreground flex h-6 min-w-0 flex-1 items-center gap-1 px-1.5 text-[11px] font-semibold"
                data-testid="smart-mapping-target"
                [attr.aria-label]="'Target of mapping ' + (i + 1) + ': ' + targetName(mapping)"
                [xuiMenuTriggerFor]="picker"
                [xuiMenuTriggerData]="{ $implicit: i }"
              >
                <span class="min-w-0 flex-1 truncate text-start">{{ targetName(mapping) }}</span>
                <ng-icon xui name="matExpandMoreRound" size="14px" />
              </button>
              <button
                type="button"
                class="bezel text-foreground grid h-6 w-7 place-items-center"
                aria-label="Invert"
                data-testid="smart-mapping-invert"
                xuiTooltip="Invert the mapping"
                [attr.aria-pressed]="mapping.invert"
                [class.text-lit]="mapping.invert"
                (click)="update(i, { invert: !mapping.invert })"
              >
                <ng-icon xui name="matSwapVertRound" size="14px" />
              </button>
              <button
                type="button"
                class="text-foreground-muted hover:text-error grid size-6 place-items-center"
                data-testid="smart-mapping-remove"
                [attr.aria-label]="'Remove mapping to ' + targetName(mapping)"
                (click)="remove(i)"
              >
                <ng-icon xui name="matCloseRound" size="14px" />
              </button>
            </div>
            <div class="flex items-start gap-2">
              <div class="flex w-20 shrink-0 flex-col gap-1 text-[10px]">
                <label class="text-foreground-muted flex flex-col gap-0.5">
                  Max
                  <input
                    #max
                    xuiInput
                    type="number"
                    class="readout h-6 px-1 text-xs"
                    aria-label="Maximum"
                    step="any"
                    [value]="round(mapping.max)"
                    (keydown.enter)="max.blur()"
                    (change)="setRange(i, mapping, 'max', max.value)"
                  />
                </label>
                <label class="text-foreground-muted flex flex-col gap-0.5">
                  Min
                  <input
                    #min
                    xuiInput
                    type="number"
                    class="readout h-6 px-1 text-xs"
                    aria-label="Minimum"
                    step="any"
                    [value]="round(mapping.min)"
                    (keydown.enter)="min.blur()"
                    (change)="setRange(i, mapping, 'min', min.value)"
                  />
                </label>
                <span class="text-foreground-muted readout truncate" [attr.title]="rangeText(mapping)">{{
                  rangeText(mapping)
                }}</span>
              </div>
              <an-mapping-graph
                data-testid="mapping-graph"
                [width]="148"
                [height]="88"
                [label]="'Graph of ' + targetName(mapping)"
                [graph]="mapping.graph"
                [invert]="mapping.invert"
                (committed)="setGraph(i, $event)"
              />
            </div>
          </div>
        } @empty {
          <p class="text-foreground-muted text-xs" role="listitem">
            {{ c.name }} is not mapped. Add a mapping to control a parameter with it.
          </p>
        }
        <button
          type="button"
          class="bezel text-foreground flex h-6.5 items-center justify-center gap-1 px-2 text-[11px] font-semibold"
          data-testid="smart-add-mapping"
          [xuiMenuTriggerFor]="picker"
          [xuiMenuTriggerData]="{ $implicit: -1 }"
        >
          <ng-icon xui name="matAddRound" size="14px" />
          Add Mapping
        </button>
      </div>
    }

    <ng-template #picker let-index>
      <xui-menu class="max-h-96 min-w-56 overflow-y-auto" aria-label="Parameters">
        @for (group of groups(); track group.group; let first = $first) {
          <xui-menu-divider [title]="group.group" [class.mt-0]="first" />
          @for (t of group.targets; track t.target) {
            <button
              xuiMenuItem
              type="button"
              [attr.data-target]="t.target"
              [selected]="index >= 0 && control()?.mappings?.[index]?.target === t.target"
              (click)="pick(index, t.target)"
            >
              {{ t.name }}
            </button>
          }
        } @empty {
          <span class="text-foreground-muted px-3 py-1 text-xs">This track has no parameters to map.</span>
        }
      </xui-menu>
    </ng-template>
  `
})
export class SmartControlsInspector {
  protected readonly store = inject(SmartControlsStore);
  readonly trackId = input.required<string>();

  protected readonly nameId = `an-smart-control-name-${nextId++}`;
  protected readonly control = computed(() => this.store.control(this.trackId(), this.store.selected()));
  protected readonly groups = computed(() => this.store.targets(this.trackId()));

  protected targetName(mapping: SmartMapping): string {
    const info = this.store.target(this.trackId(), mapping.target);
    return info ? `${info.name} (${info.group})` : mapping.target;
  }

  protected rangeText(mapping: SmartMapping): string {
    const info = this.store.target(this.trackId(), mapping.target);
    return info ? `${info.format(mapping.min)} – ${info.format(mapping.max)}` : '';
  }

  protected round(value: number): number {
    return Math.round(value * 1000) / 1000;
  }

  protected rename(name: string): void {
    void this.store.rename(this.trackId(), this.store.selected(), name);
  }

  protected pick(index: number, target: string): void {
    if (index < 0) {
      void this.store.addMapping(this.trackId(), this.store.selected(), target);
    } else {
      this.update(index, { target });
    }
  }

  protected update(index: number, change: Partial<SmartMapping>): void {
    void this.store.updateMapping(this.trackId(), this.store.selected(), index, change);
  }

  protected setRange(index: number, mapping: SmartMapping, end: 'min' | 'max', text: string): void {
    const value = Number(text);
    if (text.trim() === '' || !Number.isFinite(value) || value === mapping[end]) {
      return;
    }
    // Within the parameter's own range.
    const info = this.store.target(this.trackId(), mapping.target);
    const lo = info ? Math.min(info.min, info.max) : -Infinity;
    const hi = info ? Math.max(info.min, info.max) : Infinity;
    this.update(index, { [end]: Math.min(hi, Math.max(lo, value)) });
  }

  protected setGraph(index: number, graph: MappingPoint[]): void {
    this.update(index, { graph: graph.map(p => ({ x: p.x, y: p.y })) });
  }

  protected remove(index: number): void {
    void this.store.removeMapping(this.trackId(), this.store.selected(), index);
  }
}
