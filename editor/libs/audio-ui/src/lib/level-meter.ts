import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** dBFS at the bottom of the scale: anything quieter shows as empty. */
export const MeterFloorDb = -60;

/** How far up the meter `db` sits, from 0 (the floor or below) to 1 (0 dBFS or above). */
export function meterFraction(db: number, floorDb = MeterFloorDb): number {
  if (!Number.isFinite(db)) {
    return 0;
  }

  return Math.min(1, Math.max(0, (db - floorDb) / -floorDb));
}

/**
 * A vertical level meter for one channel: a placeholder for the canvas meters the mixer will use, which draw from
 * telemetry in animation frames rather than through inputs (docs/01-implementation-plan.md §6.2).
 */
@Component({
  selector: 'an-level-meter',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'well relative block w-2 overflow-hidden rounded-[2px]',
    role: 'meter',
    '[attr.aria-label]': 'label()',
    '[attr.aria-valuemin]': 'floorDb()',
    'aria-valuemax': '0',
    '[attr.aria-valuenow]': 'clampedDb()'
  },
  template: `
    <div class="meter-segments absolute inset-px">
      <div class="bg-meter-unlit absolute inset-0"></div>
      <div class="absolute inset-x-0 bottom-0" [class]="fill()" [style.height.%]="fraction() * 100"></div>
    </div>
  `
})
export class AnLevelMeter {
  /** The level in dBFS. */
  readonly db = input(-Infinity);
  /** The bottom of the scale in dBFS. */
  readonly floorDb = input(MeterFloorDb);
  /** The accessible name, e.g. the channel's. */
  readonly label = input('Level');

  protected readonly fraction = computed(() => meterFraction(this.db(), this.floorDb()));
  protected readonly clampedDb = computed(() => Math.round(Math.min(0, Math.max(this.floorDb(), this.db()))));
  protected readonly fill = computed(() =>
    this.db() >= 0 ? 'bg-meter-high' : this.db() > -6 ? 'bg-meter-mid' : 'bg-meter-low'
  );
}
