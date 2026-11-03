import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { ParameterInfo } from '@anode/api-client';
import { PanelParameter } from './panel-parameter';
import { panelBinding } from './panel-patterns';
import type { PluginPanelHost } from './plugin-panel';

/** A place along the object, 0…1, from a pointer over the drawing (its padded span: 5 % to 95 %). */
export function placeAt(clientX: number, box: { left: number; width: number }): number {
  if (box.width <= 0) {
    return 0;
  }
  const x = (clientX - box.left) / box.width;
  return Math.round(Math.min(1, Math.max(0, (x - 0.05) / 0.9)) * 1000) / 1000;
}

/** Where a place is drawn across the well, in %: the object spans 5 % to 95 %. */
export function placePercent(position: number): number {
  return 5 + Math.min(1, Math.max(0, position)) * 90;
}

/** A position after a key: ←/→ (↑/↓) by 1 % (⇧: 0.1 %), Page keys by 10 %, Home/End the ends; null for other keys. */
export function stepPosition(value: number, key: string, fine: boolean): number | null {
  const step = { ArrowRight: 1, ArrowUp: 1, ArrowLeft: -1, ArrowDown: -1, PageUp: 10, PageDown: -10 }[key];
  const next =
    step !== undefined ? value + step * (fine ? 0.001 : 0.01) : key === 'Home' ? 0 : key === 'End' ? 1 : null;
  return next === null ? null : Math.round(Math.min(1, Math.max(0, next)) * 1000) / 1000;
}

/** A position's parameter value (its range may be other than 0…1). */
function toValue(parameter: ParameterInfo, position: number): number {
  return parameter.min + position * (parameter.max - parameter.min);
}

function toPosition(parameter: ParameterInfo, value: number): number {
  const span = parameter.max - parameter.min;
  return span === 0 ? 0 : (value - parameter.min) / span;
}

/** A marker on the object: an exciter (above it) or a pickup (below it). */
interface Marker {
  id: string;
  kind: 'exciter' | 'pickup';
  label: string;
  /** What the marker is ("Pick", "Bow"; "Pickup A"). */
  detail: string;
  /** Its tag on the drawing ("1 Pick", "A"). */
  short: string;
  parameter: ParameterInfo;
  off: boolean;
}

/**
 * The physical model's panel (Sculpture's object view): the object drawn as a string, bar, membrane or tube (by its
 * Object parameter, the material's stiffness thickening it), its exciters as markers above it and its two pickups
 * below, each an ARIA slider along the object (drag, or ←/→ by 1 %, ⇧ finer, Home/End); an exciter that is off is
 * dimmed. Under it the object, the material, the pickups' spread, the body and each exciter's kind and strength.
 */
@Component({
  selector: 'an-physical-model-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PanelParameter],
  host: {
    class: 'flex flex-col gap-2',
    role: 'group',
    'aria-label': 'Object and pickups',
    'data-testid': 'physical-model-panel'
  },
  template: `
    <div class="well relative h-24 rounded-[6px]" data-testid="physical-object">
      <svg class="absolute inset-0 size-full" viewBox="0 0 100 40" preserveAspectRatio="none" aria-hidden="true">
        @switch (object()) {
          @case (1) {
            <rect
              x="5"
              [attr.y]="20 - thickness()"
              width="90"
              [attr.height]="thickness() * 2"
              rx="0.6"
              class="fill-foreground-muted"
            />
          }
          @case (2) {
            <ellipse
              cx="50"
              cy="20"
              rx="45"
              [attr.ry]="5 + thickness() * 2"
              class="fill-foreground-muted"
              fill-opacity="0.35"
            />
            <ellipse
              cx="50"
              cy="20"
              rx="45"
              [attr.ry]="5 + thickness() * 2"
              class="stroke-foreground fill-none"
              stroke-width="0.4"
            />
          }
          @case (3) {
            <path
              class="stroke-foreground fill-none"
              stroke-width="0.6"
              [attr.d]="
                'M5,' +
                (14 - thickness()) +
                ' H95 M5,' +
                (26 + thickness()) +
                ' H95 M5,' +
                (14 - thickness()) +
                ' V' +
                (26 + thickness())
              "
            />
          }
          @default {
            <line
              x1="5"
              y1="20"
              x2="95"
              y2="20"
              class="stroke-foreground"
              [attr.stroke-width]="0.4 + thickness() / 2"
            />
          }
        }
      </svg>
      @for (marker of markers(); track marker.id) {
        <span
          class="focus-visible:outline-focus absolute h-9 w-1 -translate-x-1/2 cursor-ew-resize touch-none rounded-full outline-offset-2 focus-visible:outline-2"
          role="slider"
          tabindex="0"
          [class.bg-meter-mid]="marker.kind === 'exciter'"
          [class.bg-primary]="marker.kind === 'pickup'"
          [class.top-1]="marker.kind === 'exciter'"
          [class.bottom-1]="marker.kind === 'pickup'"
          [class.opacity-35]="marker.off"
          [attr.data-testid]="marker.kind"
          [attr.aria-label]="marker.label + ' position'"
          aria-valuemin="0"
          aria-valuemax="100"
          [attr.aria-valuenow]="percent(marker)"
          [attr.aria-valuetext]="percent(marker) + ' %, ' + marker.detail"
          [style.left.%]="placePercent(position(marker))"
          (pointerdown)="grab($event)"
          (pointermove)="drag($event, marker)"
          (pointerup)="release($event)"
          (pointercancel)="release($event)"
          (keydown)="key($event, marker)"
        >
          <span
            class="text-foreground absolute start-2 text-[10px] font-semibold whitespace-nowrap"
            [class.top-0]="marker.kind === 'exciter'"
            [class.bottom-0]="marker.kind === 'pickup'"
            aria-hidden="true"
            >{{ marker.short }}</span
          >
        </span>
      }
    </div>
    <div class="flex flex-wrap items-end gap-x-1 gap-y-2">
      @for (parameter of material(); track parameter.id) {
        <an-panel-parameter [host]="host()" [parameter]="parameter" />
      }
    </div>
    <div class="flex flex-wrap items-start gap-1">
      @for (exciter of exciters(); track exciter.number) {
        <div
          class="border-hairline bg-surface-raised flex flex-col items-center gap-1 rounded-[6px] border px-1 py-1.5"
          role="group"
          [attr.aria-label]="'Exciter ' + exciter.number"
        >
          <span class="text-foreground text-[11px] font-semibold">Exciter {{ exciter.number }}</span>
          @if (exciter.type; as type) {
            <an-panel-parameter [host]="host()" [parameter]="type" label="Kind" />
          }
          @if (exciter.strength; as strength) {
            <an-panel-parameter [host]="host()" [parameter]="strength" label="Strength" />
          }
        </div>
      }
    </div>
  `
})
export class PhysicalModelPanel {
  readonly host = input.required<PluginPanelHost>();

  protected readonly letters = ['A', 'B'];
  protected readonly placePercent = placePercent;
  private readonly binding = computed(() => panelBinding(this.host().plugin, 'physicalModel'));

  /** The object's kind: 0 string, 1 bar, 2 membrane, 3 tube. */
  protected readonly object = computed(() => {
    const parameter = this.binding()?.roles['object'];
    return parameter ? Math.round(this.host().value(parameter.id) - parameter.min) : 0;
  });
  /** Stiffness thickens the drawing (0.5…3 units). */
  protected readonly thickness = computed(() => {
    const parameter = this.binding()?.roles['stiffness'];
    return 0.5 + (parameter ? toPosition(parameter, this.host().value(parameter.id)) : 0.4) * 2.5;
  });
  protected readonly material = computed(() => {
    const roles = this.binding()?.roles ?? {};
    return ['object', 'stiffness', 'damping', 'inharmonicity', 'pickupSpread', 'body', 'bodyAmount']
      .map(role => roles[role])
      .filter((p): p is ParameterInfo => !!p);
  });
  protected readonly exciters = computed(() => {
    const s = this.binding()?.series ?? {};
    return (s['exciterPosition'] ?? []).map((position, i) => ({
      number: i + 1,
      position,
      type: s['exciterType']?.[i],
      strength: s['exciterStrength']?.[i]
    }));
  });
  protected readonly markers = computed(() => {
    const exciters = this.exciters().map(e => {
      const kind = e.type ? this.typeName(e.type) : 'Exciter';
      return {
        id: e.position.id,
        kind: 'exciter' as const,
        label: `Exciter ${e.number}`,
        short: `${e.number} ${kind}`,
        detail: kind,
        parameter: e.position,
        off: !!e.type && this.host().value(e.type.id) <= e.type.min
      };
    });
    const pickups = (this.binding()?.series['pickupPosition'] ?? []).slice(0, 2).map((parameter, i) => ({
      id: parameter.id,
      kind: 'pickup' as const,
      label: `Pickup ${this.letters[i]}`,
      short: this.letters[i] ?? '',
      detail: `Pickup ${this.letters[i]}`,
      parameter,
      off: false
    }));
    return [...exciters, ...pickups];
  });

  private pointer: number | null = null;

  private typeName(parameter: ParameterInfo): string {
    const index = Math.round(this.host().value(parameter.id) - parameter.min);
    return parameter.valueNames?.[index] ?? String(index);
  }

  protected position(marker: Marker): number {
    return toPosition(marker.parameter, this.host().value(marker.parameter.id));
  }

  protected percent(marker: Marker): number {
    return Math.round(this.position(marker) * 100);
  }

  protected grab(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    const handle = event.currentTarget as HTMLElement;
    handle.setPointerCapture(event.pointerId);
    handle.focus();
    this.pointer = event.pointerId;
  }

  protected drag(event: PointerEvent, marker: Marker): void {
    const box = (event.currentTarget as HTMLElement).parentElement?.getBoundingClientRect();
    if (this.pointer === event.pointerId && box) {
      this.place(marker, placeAt(event.clientX, box));
    }
  }

  protected release(event: PointerEvent): void {
    if (this.pointer === event.pointerId) {
      this.pointer = null;
    }
  }

  protected key(event: KeyboardEvent, marker: Marker): void {
    const next = stepPosition(this.position(marker), event.key, event.shiftKey);
    if (next !== null) {
      event.preventDefault();
      this.place(marker, next);
    }
  }

  private place(marker: Marker, position: number): void {
    const value = toValue(marker.parameter, position);
    if (Math.abs(value - this.host().value(marker.parameter.id)) > 1e-9) {
      this.host().set(marker.parameter.id, value);
    }
  }
}
