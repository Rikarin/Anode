import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import type { ParameterInfo, PluginInfo } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import {
  matAddRound,
  matArrowDownwardRound,
  matArrowUpwardRound,
  matCloseRound,
  matPlayArrowRound,
  matStopRound
} from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiMenuImports } from '@xui/menu';
import { isActive, JobsStore } from '../state/jobs.store';
import { LanesStore } from '../state/lanes.store';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TracksViewStore } from '../state/tracks-view.store';
import { TransportStore } from '../state/transport.store';
import { UiStore } from '../state/ui.store';
import {
  type ChainEntry,
  chainEntry,
  processingEffects,
  processRequest,
  processTarget,
  type ProcessTarget
} from './processing';

/**
 * Logic's Selection-Based Processing (⌥X): a chain of built-in effects run over the Marquee tool's range of the audio
 * regions under it (or the selected regions, whole). Each effect shows its parameters; Apply runs the chain on the
 * Server as a job, and the processed part becomes a new file and region (one undo step). Preview plays the source over
 * the range: the chain is heard once applied.
 */
@Component({
  selector: 'an-processing-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiIconImports, XuiInputImports, XuiMenuImports],
  providers: [
    provideIcons({
      matAddRound,
      matArrowDownwardRound,
      matArrowUpwardRound,
      matCloseRound,
      matPlayArrowRound,
      matStopRound
    })
  ],
  template: `
    <xui-dialog
      [(open)]="ui.processingOpen"
      title="Selection-Based Processing"
      size="lg"
      [canOutsideClickClose]="false"
    >
      <xui-dialog-body class="flex flex-col gap-4 text-xs" data-testid="processing-dialog">
        @if (!target()) {
          <p class="text-foreground-muted">
            Select audio regions, or a range of them with the Marquee tool, to process.
          </p>
        } @else {
          <div class="grid grid-cols-[7rem_auto_auto_auto_1fr] items-center gap-x-3 gap-y-2.5">
            <span class="text-foreground-muted">Range</span>
            <label class="text-foreground-muted flex items-center gap-2">
              Start bar
              <input
                #start
                xuiInput
                type="number"
                class="readout h-7 w-20 text-xs"
                step="0.25"
                min="1"
                data-testid="processing-start"
                [value]="startBar()"
                (change)="setBar('start', start.valueAsNumber)"
              />
            </label>
            <label class="text-foreground-muted flex items-center gap-2">
              End bar
              <input
                #end
                xuiInput
                type="number"
                class="readout h-7 w-20 text-xs"
                step="0.25"
                min="1"
                data-testid="processing-end"
                [value]="endBar()"
                (change)="setBar('end', end.valueAsNumber)"
              />
            </label>
            <label class="text-foreground flex items-center gap-2">
              <input
                type="checkbox"
                class="accent-(--color-primary)"
                data-testid="processing-include-tail"
                [checked]="includeTail()"
                (change)="includeTail.set(!includeTail())"
              />
              Include Tail
            </label>
            <span class="text-foreground-muted truncate text-end">{{ regionCount() }}</span>
          </div>
        }

        <div class="flex items-center gap-2">
          <span class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">Effect Chain</span>
          <button
            xuiButton
            type="button"
            size="sm"
            variant="outline"
            class="ms-auto"
            data-testid="processing-add-effect"
            [xuiMenuTriggerFor]="effectsMenu"
          >
            <ng-icon xui name="matAddRound" size="sm" />
            Add Effect
          </button>
          <ng-template #effectsMenu>
            <xui-menu class="max-h-96 min-w-44 overflow-y-auto" aria-label="Built-in effects">
              @for (effect of effects(); track effect.id) {
                <button xuiMenuItem type="button" (click)="add(effect)">{{ effect.name }}</button>
              }
            </xui-menu>
          </ng-template>
        </div>
        <ol
          class="border-hairline bg-surface-inset flex max-h-80 flex-col overflow-y-auto rounded-md border"
          aria-label="Effect chain"
          data-testid="processing-chain"
        >
          @for (entry of chain(); track entry.key; let i = $index; let last = $last) {
            @if (plugin(entry.pluginId); as p) {
              <li class="border-hairline flex flex-col gap-2 border-b px-3 py-2 last:border-b-0">
                <div class="flex items-center gap-1">
                  <span class="text-foreground-subtle readout w-4 text-[11px]">{{ i + 1 }}</span>
                  <span class="text-foreground flex-1 text-[12px] font-semibold">{{ p.name }}</span>
                  <button
                    xuiButton
                    type="button"
                    variant="ghost"
                    size="sm"
                    class="h-6 w-6 px-0"
                    [disabled]="i === 0"
                    [attr.aria-label]="'Move ' + p.name + ' up'"
                    (click)="move(i, -1)"
                  >
                    <ng-icon xui name="matArrowUpwardRound" size="sm" />
                  </button>
                  <button
                    xuiButton
                    type="button"
                    variant="ghost"
                    size="sm"
                    class="h-6 w-6 px-0"
                    [disabled]="last"
                    [attr.aria-label]="'Move ' + p.name + ' down'"
                    (click)="move(i, 1)"
                  >
                    <ng-icon xui name="matArrowDownwardRound" size="sm" />
                  </button>
                  <button
                    xuiButton
                    type="button"
                    variant="ghost"
                    size="sm"
                    class="h-6 w-6 px-0"
                    [attr.aria-label]="'Remove ' + p.name"
                    (click)="remove(i)"
                  >
                    <ng-icon xui name="matCloseRound" size="sm" />
                  </button>
                </div>
                <div class="grid grid-cols-[repeat(auto-fill,minmax(10rem,1fr))] gap-x-3 gap-y-1.5">
                  @for (param of p.parameters; track param.id) {
                    <div class="text-foreground-muted flex items-center justify-between gap-2">
                      <span class="truncate" [attr.title]="param.name">{{ param.name }}</span>
                      @if (param.valueNames?.length) {
                        <select
                          #choice
                          class="border-border bg-surface-inset text-foreground h-6 rounded-[5px] border px-1 text-xs"
                          [attr.data-testid]="'processing-param-' + p.id + '-' + param.id"
                          [attr.aria-label]="p.name + ' ' + param.name"
                          (change)="set(i, param.id, choice.selectedIndex)"
                        >
                          @for (name of param.valueNames; track $index) {
                            <option [selected]="entry.values[param.id] === $index">{{ name }}</option>
                          }
                        </select>
                      } @else {
                        <span class="flex items-center gap-1">
                          <input
                            #number
                            xuiInput
                            type="number"
                            class="readout h-6 w-20 text-xs"
                            [attr.data-testid]="'processing-param-' + p.id + '-' + param.id"
                            [attr.aria-label]="p.name + ' ' + param.name"
                            [min]="param.min"
                            [max]="param.max"
                            [step]="stepOf(param)"
                            [value]="entry.values[param.id]"
                            (change)="set(i, param.id, number.valueAsNumber, param)"
                          />
                          <span class="w-6 text-[10px]">{{ param.unit }}</span>
                        </span>
                      }
                    </div>
                  }
                </div>
              </li>
            }
          } @empty {
            <li class="text-foreground-muted px-3 py-4 text-center">Add the effects to run, in order.</li>
          }
        </ol>
        <p class="text-foreground-muted">
          Preview plays the source over the range; the effects are heard once applied (Undo takes them off).
        </p>
        @if (job(); as j) {
          <div class="flex items-center gap-3" data-testid="processing-progress">
            <div
              class="well h-2 flex-1 overflow-hidden rounded-full"
              role="progressbar"
              aria-label="Processing progress"
              aria-valuemin="0"
              aria-valuemax="100"
              [attr.aria-valuenow]="percent()"
            >
              <div class="bg-primary h-full transition-[width] duration-200" [style.width.%]="percent()"></div>
            </div>
            <span class="readout text-foreground w-20 text-end">{{
              j.state === 'failed' ? 'Failed' : percent() + ' %'
            }}</span>
          </div>
        }
      </xui-dialog-body>
      <xui-dialog-footer>
        <button
          xuiButton
          type="button"
          variant="outline"
          class="me-auto"
          data-testid="processing-preview"
          [attr.aria-pressed]="previewing()"
          [disabled]="!target()"
          (click)="preview()"
        >
          <ng-icon xui [name]="previewing() ? 'matStopRound' : 'matPlayArrowRound'" size="sm" />
          {{ previewing() ? 'Stop' : 'Preview' }}
        </button>
        <button xuiButton type="button" variant="ghost" (click)="ui.processingOpen.set(false)">Cancel</button>
        <button
          xuiButton
          type="button"
          data-testid="processing-apply"
          [disabled]="running() || !target() || chain().length === 0"
          (click)="apply()"
        >
          Apply
        </button>
      </xui-dialog-footer>
    </xui-dialog>
  `
})
export class ProcessingDialog {
  protected readonly ui = inject(UiStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly view = inject(TracksViewStore);
  private readonly lanes = inject(LanesStore);
  private readonly plugins = inject(PluginsStore);
  private readonly jobs = inject(JobsStore);
  private readonly transport = inject(TransportStore);

  protected readonly effects = computed(() => processingEffects(this.plugins.plugins()));
  protected readonly chain = signal<readonly ChainEntry[]>([]);
  protected readonly includeTail = signal(false);
  /** What is processed, taken when the window opens; its range is editable. */
  protected readonly target = signal<ProcessTarget | null>(null);
  protected readonly previewing = signal(false);
  private nextKey = 1;

  protected readonly startBar = computed(() => this.bar(this.target()?.startTick ?? 0));
  protected readonly endBar = computed(() => this.bar(this.target()?.endTick ?? 0));
  protected readonly regionCount = computed(() => {
    const n = this.target()?.regionIds.length ?? 0;
    return `${n} ${n === 1 ? 'region' : 'regions'}`;
  });

  protected readonly job = this.jobs.processJob;
  protected readonly running = computed(() => isActive(this.job()));
  protected readonly percent = computed(() => Math.round((this.job()?.progress ?? 0) * 100));

  constructor() {
    effect(() => {
      if (this.ui.processingOpen()) {
        untracked(() => {
          if (!this.running()) {
            this.jobs.processId.set(null);
            this.target.set(
              processTarget(
                this.view.marquee(),
                this.lanes.rows(),
                this.project.regionList(),
                this.selection.regions(),
                region => this.project.regionEnd(region)
              )
            );
          }
        });
      } else {
        untracked(() => this.stopPreview());
      }
    });
    effect(() => {
      if (this.job()?.state === 'done') {
        untracked(() => {
          this.ui.processingOpen.set(false);
          this.jobs.processId.set(null);
        });
      }
    });
  }

  protected plugin(id: string): PluginInfo | undefined {
    return this.plugins.plugin(id);
  }

  protected stepOf(param: ParameterInfo): number {
    return param.steps > 1 ? (param.max - param.min) / (param.steps - 1) : (param.max - param.min) / 200 || 0.01;
  }

  protected add(plugin: PluginInfo): void {
    this.chain.update(chain => [...chain, chainEntry(plugin, this.nextKey++)]);
  }

  protected remove(index: number): void {
    this.chain.update(chain => chain.filter((_, i) => i !== index));
  }

  protected move(index: number, by: number): void {
    this.chain.update(chain => {
      const next = [...chain];
      const [entry] = next.splice(index, 1);
      if (entry) {
        next.splice(Math.max(0, Math.min(next.length, index + by)), 0, entry);
      }
      return next;
    });
  }

  protected set(index: number, parameterId: string, value: number, param?: ParameterInfo): void {
    if (!Number.isFinite(value)) {
      return;
    }
    const clamped = param ? Math.min(param.max, Math.max(param.min, value)) : value;
    this.chain.update(chain =>
      chain.map((entry, i) => (i === index ? { ...entry, values: { ...entry.values, [parameterId]: clamped } } : entry))
    );
  }

  /** Bar (from 1, two decimals) of a tick. */
  private bar(tick: number): number {
    return Math.round((this.project.meter().barAt(tick) + 1) * 100) / 100;
  }

  protected setBar(which: 'start' | 'end', bar: number): void {
    const target = this.target();
    if (!target || !Number.isFinite(bar) || bar < 1) {
      return;
    }
    const tick = Math.round(this.project.meter().barStart(bar - 1));
    this.target.set(which === 'start' ? { ...target, startTick: tick } : { ...target, endTick: tick });
  }

  protected preview(): void {
    if (this.previewing()) {
      this.stopPreview();
      return;
    }
    const target = this.target();
    if (target) {
      this.transport.locate(target.startTick);
      this.transport.play();
      this.previewing.set(true);
    }
  }

  private stopPreview(): void {
    if (this.previewing()) {
      this.previewing.set(false);
      if (this.transport.playing()) {
        this.transport.stop();
      }
    }
  }

  protected async apply(): Promise<void> {
    const target = this.target();
    if (!target || target.endTick <= target.startTick) {
      return;
    }
    this.stopPreview();
    await this.jobs.process(processRequest(this.chain(), this.plugins.plugins(), target, this.includeTail()));
  }
}
