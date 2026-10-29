import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import type { ParameterInfo } from '@anode/api-client';
import { formatParameter, fromNormal, toNormal } from '@anode/audio-ui';
import { PanelParameter } from './panel-parameter';
import { panelBinding } from './panel-patterns';
import type { PluginPanelHost } from './plugin-panel';

/** A tap as the panel shows it: its parameters (pan and cutoff when the plugin has them). */
export interface DelayTap {
  readonly number: number;
  readonly time: ParameterInfo;
  readonly level: ParameterInfo;
  readonly pan: ParameterInfo | undefined;
  readonly cutoff: ParameterInfo | undefined;
}

/** The timeline's span: the latest tap plus a quarter, rounded up to a tidy length (at least half a second). */
export function tapSpan(latest: number): number {
  const target = Math.max(0.5, latest * 1.25);
  const tidy = [0.5, 1, 1.5, 2, 3, 4, 5, 6, 8, 10, 15, 20, 30];
  return tidy.find(t => t >= target) ?? Math.ceil(target);
}

/** How many taps sound: the tap count parameter's value when there is one (within the taps the plugin exposes). */
export function tapsShown(count: ParameterInfo | undefined, value: number, available: number): number {
  if (!count) {
    return available;
  }
  return Math.min(available, Math.max(Math.min(1, available), Math.round(value)));
}

const range = (p: ParameterInfo) => ({ min: p.min, max: p.max, steps: p.steps });

/**
 * The Delay Designer's panel: a tap timeline (time across, level up) with each tap a bar to drag, an ARIA slider for
 * its level (↑/↓) whose ←/→ move it in time (⇧ finer); under it a pan lane (each tap's pan from the middle, left up,
 * right down); "Add Tap" and "Remove Tap" when the plugin has a tap count; and a row of knobs per tap.
 */
@Component({
  selector: 'an-delay-taps-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PanelParameter],
  host: { class: 'flex flex-col gap-2', role: 'group', 'aria-label': 'Delay taps', 'data-testid': 'delay-taps-panel' },
  template: `
    <div class="flex items-center gap-2">
      <span class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">Taps</span>
      <span class="readout text-foreground text-[11px]">{{ taps().length }}</span>
      @if (count(); as c) {
        <span class="flex-1"></span>
        <div class="bezel-group text-foreground h-6 text-[11px] font-semibold" role="group" aria-label="Tap count">
          <button
            type="button"
            class="px-2 disabled:opacity-35"
            data-testid="delay-remove-tap"
            [disabled]="taps().length <= 1"
            (click)="setCount(c, taps().length - 1)"
          >
            Remove Tap
          </button>
          <button
            type="button"
            class="px-2 disabled:opacity-35"
            data-testid="delay-add-tap"
            [disabled]="taps().length >= maxTaps()"
            (click)="setCount(c, taps().length + 1)"
          >
            Add Tap
          </button>
        </div>
      }
    </div>
    <div class="well relative h-28 rounded-[5px]" role="group" aria-label="Tap timeline" data-testid="delay-timeline">
      @for (tick of ticks(); track tick) {
        <span
          class="border-hairline absolute inset-y-0 border-s"
          [style.left.%]="(tick / span()) * 100"
          aria-hidden="true"
        ></span>
      }
      @for (tap of taps(); track tap.number) {
        <span
          class="bg-primary focus-visible:outline-focus absolute bottom-0 w-1.5 -translate-x-1/2 cursor-grab touch-none rounded-t-[2px] outline-offset-2 focus-visible:outline-2"
          role="slider"
          tabindex="0"
          data-testid="delay-tap"
          [attr.aria-label]="'Tap ' + tap.number"
          [attr.aria-valuemin]="tap.level.min"
          [attr.aria-valuemax]="tap.level.max"
          [attr.aria-valuenow]="round(value(tap.level))"
          [attr.aria-valuetext]="describe(tap)"
          [style.left.%]="(value(tap.time) / span()) * 100"
          [style.height.%]="levelNormal(tap) * 100"
          [style.min-height.px]="3"
          (pointerdown)="grab($event)"
          (pointermove)="drag($event, tap)"
          (pointerup)="release($event)"
          (pointercancel)="release($event)"
          (keydown)="key($event, tap)"
        ></span>
      }
      <span class="text-foreground-muted readout absolute end-1 top-0.5 text-[10px]" aria-hidden="true">{{
        spanText()
      }}</span>
    </div>
    @if (hasPan()) {
      <div class="well relative h-6 rounded-[5px]" aria-hidden="true">
        <span class="border-hairline-strong absolute inset-x-0 top-1/2 border-t"></span>
        @for (tap of taps(); track tap.number) {
          @if (tap.pan; as pan) {
            <span
              class="bg-knob-arc absolute w-1 -translate-x-1/2"
              [style.left.%]="(value(tap.time) / span()) * 100"
              [style.top.%]="panTop(pan)"
              [style.height.%]="panHeight(pan)"
            ></span>
          }
        }
      </div>
    }
    <div class="flex flex-col gap-1" role="list" aria-label="Tap settings">
      @for (tap of taps(); track tap.number) {
        <div class="flex items-end gap-1" role="listitem" [attr.aria-label]="'Tap ' + tap.number">
          <span class="text-foreground-muted w-10 self-center text-[10px] font-semibold tracking-wide uppercase"
            >Tap {{ tap.number }}</span
          >
          <an-panel-parameter [host]="host()" [parameter]="tap.time" label="Time" />
          <an-panel-parameter [host]="host()" [parameter]="tap.level" label="Level" />
          @if (tap.pan; as pan) {
            <an-panel-parameter [host]="host()" [parameter]="pan" label="Pan" />
          }
          @if (tap.cutoff; as cutoff) {
            <an-panel-parameter [host]="host()" [parameter]="cutoff" label="Cutoff" />
          }
        </div>
      }
    </div>
  `
})
export class DelayTapsPanel {
  readonly host = input.required<PluginPanelHost>();

  private readonly binding = computed(() => panelBinding(this.host().plugin, 'delayTaps'));
  protected readonly count = computed(() => this.binding()?.roles['count']);
  private readonly available = computed<DelayTap[]>(() => {
    const series = this.binding()?.series ?? {};
    const times = series['time'] ?? [];
    const levels = series['level'] ?? [];
    return times.slice(0, levels.length).map((time, i) => ({
      number: i + 1,
      time,
      level: levels[i] ?? time,
      pan: series['pan']?.[i],
      cutoff: series['cutoff']?.[i]
    }));
  });
  protected readonly maxTaps = computed(() => Math.min(this.available().length, this.count()?.max ?? Infinity));
  protected readonly taps = computed(() => {
    const count = this.count();
    const shown = tapsShown(count, count ? this.host().value(count.id) : 0, this.available().length);
    return this.available().slice(0, shown);
  });
  protected readonly hasPan = computed(() => this.taps().some(t => !!t.pan));
  /** Kept still while a tap is dragged, so the timeline does not rescale under the pointer. */
  private readonly frozenSpan = signal<number | null>(null);
  /** Times in milliseconds are scaled to seconds for the span and the key steps. */
  private readonly timeScale = computed(() => (this.taps()[0]?.time.unit === 'ms' ? 1000 : 1));
  protected readonly span = computed(
    () =>
      this.frozenSpan() ??
      tapSpan(Math.max(0, ...this.taps().map(t => this.value(t.time))) / this.timeScale()) * this.timeScale()
  );
  protected readonly ticks = computed(() => {
    const span = this.span() / this.timeScale();
    const step = span <= 1 ? 0.125 : span <= 4 ? 0.5 : span <= 10 ? 1 : 5;
    return Array.from({ length: Math.floor(span / step) - 1 }, (_, i) => (i + 1) * step * this.timeScale());
  });
  protected readonly spanText = computed(() => formatParameter(this.span() / this.timeScale(), 's'));

  private pointer: number | null = null;

  protected value(parameter: ParameterInfo): number {
    return this.host().value(parameter.id);
  }

  protected round(value: number): number {
    return Math.round(value * 100) / 100;
  }

  protected levelNormal(tap: DelayTap): number {
    return toNormal(this.value(tap.level), range(tap.level));
  }

  protected describe(tap: DelayTap): string {
    const parts = [
      formatParameter(this.value(tap.time), tap.time.unit, tap.time.steps),
      formatParameter(this.value(tap.level), tap.level.unit, tap.level.steps)
    ];
    if (tap.pan) {
      parts.push(`pan ${formatParameter(this.value(tap.pan), tap.pan.unit, tap.pan.steps)}`);
    }
    return parts.join(', ');
  }

  /** The pan lane: left pans rise from the middle, right pans fall from it. */
  protected panTop(pan: ParameterInfo): number {
    const p = this.panUnit(pan);
    return p < 0 ? 50 + p * 50 : 50;
  }

  protected panHeight(pan: ParameterInfo): number {
    return Math.max(4, Math.abs(this.panUnit(pan)) * 50);
  }

  /** Pan as −1…1 whatever its range. */
  private panUnit(pan: ParameterInfo): number {
    const span = pan.max - pan.min;
    return span === 0 ? 0 : ((this.value(pan) - pan.min) / span) * 2 - 1;
  }

  protected setCount(count: ParameterInfo, taps: number): void {
    const next = Math.min(this.maxTaps(), Math.max(count.min, taps));
    this.host().set(count.id, next);
  }

  protected grab(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    const bar = event.currentTarget as HTMLElement;
    bar.setPointerCapture(event.pointerId);
    bar.focus();
    this.pointer = event.pointerId;
    this.frozenSpan.set(this.span());
  }

  protected drag(event: PointerEvent, tap: DelayTap): void {
    if (this.pointer !== event.pointerId) {
      return;
    }
    const box = (event.currentTarget as HTMLElement).parentElement?.getBoundingClientRect();
    if (!box || box.width <= 0 || box.height <= 0) {
      return;
    }
    const x = Math.min(1, Math.max(0, (event.clientX - box.left) / box.width));
    const y = Math.min(1, Math.max(0, 1 - (event.clientY - box.top) / box.height));
    this.setTime(tap, x * this.span());
    this.setLevel(tap, fromNormal(y, range(tap.level)));
  }

  protected release(event: PointerEvent): void {
    if (this.pointer === event.pointerId) {
      this.pointer = null;
      this.frozenSpan.set(null);
    }
  }

  protected key(event: KeyboardEvent, tap: DelayTap): void {
    const fine = event.shiftKey;
    const level = this.levelNormal(tap);
    const levelStep = fine ? 0.01 : 0.05;
    const timeStep = (fine ? 0.001 : 0.01) * this.timeScale();
    switch (event.key) {
      case 'ArrowUp':
        this.setLevel(tap, fromNormal(level + levelStep, range(tap.level)));
        break;
      case 'ArrowDown':
        this.setLevel(tap, fromNormal(level - levelStep, range(tap.level)));
        break;
      case 'ArrowRight':
        this.setTime(tap, this.value(tap.time) + timeStep);
        break;
      case 'ArrowLeft':
        this.setTime(tap, this.value(tap.time) - timeStep);
        break;
      case 'Home':
        this.setLevel(tap, tap.level.min);
        break;
      case 'End':
        this.setLevel(tap, tap.level.max);
        break;
      default:
        return;
    }
    event.preventDefault();
  }

  private setTime(tap: DelayTap, seconds: number): void {
    const next =
      Math.round((Math.min(tap.time.max, Math.max(tap.time.min, seconds)) * 1000) / this.timeScale()) /
      (1000 / this.timeScale());
    if (next !== this.value(tap.time)) {
      this.host().set(tap.time.id, next);
    }
  }

  private setLevel(tap: DelayTap, level: number): void {
    const next = Math.round(Math.min(tap.level.max, Math.max(tap.level.min, level)) * 100) / 100;
    if (next !== this.value(tap.level)) {
      this.host().set(tap.level.id, next);
    }
  }
}
