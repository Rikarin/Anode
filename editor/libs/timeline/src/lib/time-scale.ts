/**
 * The horizontal scale every timeline view shares: musical ticks to CSS pixels and back, at a zoom (pixels per quarter
 * note) and a scroll offset (the tick at the left edge).
 */
export class TimeScale {
  constructor(
    /** Ticks per quarter note (the project's time base). */
    readonly ppq: number,
    /** Pixels one quarter note spans. */
    readonly pixelsPerQuarter: number,
    /** The tick at x = 0. */
    readonly originTick = 0
  ) {
    if (!(ppq > 0) || !(pixelsPerQuarter > 0)) {
      throw new RangeError('A time scale needs a positive PPQ and zoom.');
    }
  }

  get pixelsPerTick(): number {
    return this.pixelsPerQuarter / this.ppq;
  }

  toPixels(tick: number): number {
    return (tick - this.originTick) * this.pixelsPerTick;
  }

  toTicks(x: number): number {
    return this.originTick + x / this.pixelsPerTick;
  }

  /** The same scale scrolled so that `tick` is at the left edge. */
  scrolledTo(tick: number): TimeScale {
    return new TimeScale(this.ppq, this.pixelsPerQuarter, Math.max(0, tick));
  }

  /** The same scale zoomed by `factor`, keeping the tick under `anchorX` where it is (pinch and ⌘-scroll zoom). */
  zoomed(factor: number, anchorX = 0): TimeScale {
    const anchor = this.toTicks(anchorX);
    const zoom = this.pixelsPerQuarter * factor;
    return new TimeScale(this.ppq, zoom, Math.max(0, anchor - anchorX / (zoom / this.ppq)));
  }
}
