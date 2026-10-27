import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { TransportStore, barsBeats, clockTime } from '../state/transport.store';

/**
 * The LCD in the control bar: the playhead as bars/beats/divisions/ticks and as clock time, plus tempo and time
 * signature. It reads the playhead from its own animation frames while playing, not through change detection.
 */
@Component({
  selector: 'an-lcd',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'an-lcd bg-surface-inset border-border flex items-center gap-4 rounded border px-3 py-1 font-mono',
    role: 'group',
    'aria-label': 'Position'
  },
  template: `
    <div class="flex flex-col items-end leading-tight">
      <span class="text-sm" data-testid="lcd-bars">{{ bars() }}</span>
      <span class="text-foreground-muted text-[10px]" data-testid="lcd-time">{{ time() }}</span>
    </div>
    <div class="border-border flex flex-col items-end border-s ps-4 leading-tight">
      <span class="text-sm">{{ transport.tempo() }}</span>
      <span class="text-foreground-muted text-[10px]">bpm</span>
    </div>
    <div class="flex flex-col items-end leading-tight">
      <span class="text-sm">{{ transport.signature().numerator }}/{{ transport.signature().denominator }}</span>
      <span class="text-foreground-muted text-[10px]">time</span>
    </div>
  `
})
export class Lcd {
  protected readonly transport = inject(TransportStore);
  protected readonly bars = signal('1 1 1 1');
  protected readonly time = signal(clockTime(0));

  constructor() {
    let frame = 0;
    const draw = () => {
      const ticks = this.transport.positionAt(performance.now());
      const { bar, beat, division, tick } = barsBeats(ticks, this.transport.signature());
      this.bars.set(`${bar} ${beat} ${division} ${String(tick).padStart(3, ' ')}`);
      this.time.set(clockTime(ticks / this.transport.ticksPerSecond()));
      frame = requestAnimationFrame(draw);
    };
    frame = requestAnimationFrame(draw);
    inject(DestroyRef).onDestroy(() => cancelAnimationFrame(frame));
  }
}
