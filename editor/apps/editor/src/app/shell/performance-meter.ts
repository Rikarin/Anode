import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { XuiPopoverImports } from '@xui/popover';
import { PerformanceStore } from '../state/performance.store';

/** A load as a percentage, for reading. */
export function percent(load: number): string {
  return `${Math.round(load * 100)} %`;
}

/** The meter colour of a load: green, yellow towards the limit, red at it. */
export function loadClass(load: number): string {
  return load >= 0.9 ? 'bg-meter-high' : load >= 0.7 ? 'bg-meter-mid' : 'bg-meter-low';
}

/**
 * The control bar's load meter: a bar per core, the disk's bar and the overload light, all from the Server's `performance`
 * events. A click opens the figures behind them, with the audio thread's load and the overload counts.
 */
@Component({
  selector: 'an-performance-meter',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiPopoverImports],
  template: `
    <button
      type="button"
      class="bezel flex h-6.5 items-center gap-1.5 px-1.5"
      data-testid="performance-meter"
      placement="bottom-end"
      [attr.aria-label]="summary()"
      [attr.title]="summary()"
      [xuiPopover]="details"
    >
      <span class="well flex h-4.5 items-end gap-px rounded-[2px] p-px" data-testid="core-bars" aria-hidden="true">
        @for (load of bars(); track $index) {
          <span class="bg-meter-unlit relative block h-full w-1 overflow-hidden">
            <span class="absolute inset-x-0 bottom-0 block" [class]="tone(load)" [style.height.%]="load * 100"></span>
          </span>
        }
      </span>
      <span class="well flex h-4.5 items-end rounded-[2px] p-px" data-testid="disk-bar" aria-hidden="true">
        <span class="bg-meter-unlit relative block h-full w-1.5 overflow-hidden">
          <span
            class="absolute inset-x-0 bottom-0 block"
            [class]="tone(diskLoad())"
            [style.height.%]="diskLoad() * 100"
          ></span>
        </span>
      </span>
      <span
        class="size-2 rounded-full shadow-[0_0_0_1px_var(--hairline)] transition-colors duration-100"
        data-testid="overload-light"
        aria-hidden="true"
        [attr.data-on]="store.overloaded() ? '' : null"
        [class]="store.overloaded() ? 'bg-meter-high shadow-[0_0_4px_var(--meter-high)]' : 'bg-meter-unlit'"
      ></span>
    </button>
    <ng-template #details>
      <div class="flex w-56 flex-col gap-2 p-2 text-xs" data-testid="performance-details">
        <dl class="grid grid-cols-[1fr_auto] gap-x-3 gap-y-1">
          <dt class="text-foreground-muted">Audio thread</dt>
          <dd class="readout text-foreground text-end" data-testid="audio-load">{{ audioText() }}</dd>
          <dt class="text-foreground-muted">Peak</dt>
          <dd class="readout text-foreground text-end" data-testid="audio-peak">{{ peakText() }}</dd>
          <dt class="text-foreground-muted">Disk</dt>
          <dd class="readout text-foreground text-end" data-testid="disk-load">{{ diskText() }}</dd>
          <dt class="text-foreground-muted">Overloads</dt>
          <dd class="readout text-foreground text-end" data-testid="overload-count">{{ store.overloads() }}</dd>
          <dt class="text-foreground-muted">Underruns</dt>
          <dd class="readout text-foreground text-end" data-testid="underrun-count">{{ store.underruns() }}</dd>
        </dl>
        @if (cores().length > 0) {
          <div class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">Cores</div>
          <ul class="grid grid-cols-4 gap-x-2 gap-y-0.5" data-testid="core-list">
            @for (load of cores(); track $index) {
              <li class="readout text-foreground text-end">{{ text(load) }}</li>
            }
          </ul>
        } @else {
          <p class="text-foreground-muted">This system does not report its cores.</p>
        }
        <button
          type="button"
          class="bezel text-foreground h-6 self-end px-2 text-[11px] font-semibold"
          data-testid="performance-reset"
          (click)="store.reset()"
        >
          Reset
        </button>
      </div>
    </ng-template>
  `
})
export class PerformanceMeter {
  protected readonly store = inject(PerformanceStore);

  protected readonly tone = loadClass;
  protected readonly text = percent;
  protected readonly cores = computed(() => this.store.status()?.coreLoads ?? []);
  /** A bar per core, or the audio thread's alone where the system does not report them. */
  protected readonly bars = computed(() =>
    this.cores().length > 0 ? this.cores() : [Math.min(1, this.store.status()?.audioLoad ?? 0)]
  );
  protected readonly diskLoad = computed(() => Math.min(1, this.store.status()?.diskLoad ?? 0));
  protected readonly audioText = computed(() => percent(this.store.status()?.audioLoad ?? 0));
  protected readonly peakText = computed(() => percent(this.store.status()?.audioPeakLoad ?? 0));
  protected readonly diskText = computed(() => percent(this.diskLoad()));
  protected readonly summary = computed(() => {
    const status = this.store.status();
    if (!status) {
      return 'Load: no engine';
    }
    const overloads = this.store.overloads();
    return `Audio ${percent(status.audioLoad)}, disk ${percent(status.diskLoad)}, ${overloads} overloads`;
  });
}
