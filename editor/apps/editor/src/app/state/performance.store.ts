import { Injectable, computed, inject, signal } from '@angular/core';
import type { PerformanceStatus } from '@anode/api-client';
import { EventStream } from '../core/events';

/** How long the overload light stays on after one. */
export const OverloadHoldMs = 3_000;

/**
 * The engine's load as the Server reports it (`performance` events, a few a second): the audio thread's load, the
 * machine's cores, the disk thread's, and overloads. Overloads count since the app connected or {@link reset}; a new one
 * lights the overload light for a while.
 */
@Injectable({ providedIn: 'root' })
export class PerformanceStore {
  readonly status = signal<PerformanceStatus | null>(null);
  /** When the last overload arrived (performance.now()), null: none since the reset. */
  private readonly overloadAt = signal<number | null>(null);
  private readonly base = signal({ overloads: 0, underruns: 0 });
  private seen: { overloads: number; underruns: number } | null = null;
  private now = 0;

  /** Dropouts and starved tracks since the reset. */
  readonly overloads = computed(() => Math.max(0, (this.status()?.overloads ?? 0) - this.base().overloads));
  readonly underruns = computed(() => Math.max(0, (this.status()?.underruns ?? 0) - this.base().underruns));

  /** The overload light: on for a while after each. */
  readonly overloaded = signal(false);

  constructor() {
    inject(EventStream).events$.subscribe(message => {
      if (message.type === 'performance') {
        this.apply(message.performance);
      }
    });
  }

  apply(status: PerformanceStatus, now = performance.now()): void {
    this.now = now;
    const seen = this.seen;
    if (seen === null) {
      // What happened before this client connected is not news: it starts from here.
      this.base.set({ overloads: status.overloads, underruns: status.underruns });
    } else if (status.overloads < seen.overloads) {
      this.base.set({ overloads: 0, underruns: 0 }); // the host restarted
    } else if (status.overloads > seen.overloads || status.underruns > seen.underruns) {
      this.overloadAt.set(now);
    }
    this.seen = { overloads: status.overloads, underruns: status.underruns };
    this.status.set(status);
    const at = this.overloadAt();
    this.overloaded.set(at !== null && this.now - at < OverloadHoldMs);
  }

  /** Clears the counts and the light. */
  reset(): void {
    this.base.set({ overloads: this.seen?.overloads ?? 0, underruns: this.seen?.underruns ?? 0 });
    this.overloadAt.set(null);
    this.overloaded.set(false);
  }
}
