import { TempoMap } from './tempo-map';

describe('TempoMap', () => {
  it('converts at one tempo', () => {
    const map = TempoMap.constant(120);
    expect(map.secondsAt(960)).toBeCloseTo(0.5);
    expect(map.tickAt(2)).toBeCloseTo(3840);
    expect(map.bpmAt(10_000)).toBe(120);
    expect(map.constant).toBe(true);
  });

  it('holds a step until the next event', () => {
    const map = new TempoMap([
      { tick: 0, bpm: 120, curve: 'step' },
      { tick: 3840, bpm: 60, curve: 'step' }
    ]);
    expect(map.secondsAt(3840)).toBeCloseTo(2);
    expect(map.secondsAt(4800)).toBeCloseTo(3);
    expect(map.tickAt(3)).toBeCloseTo(4800);
    expect(map.bpmAt(3839)).toBe(120);
    expect(map.bpmAt(3840)).toBe(60);
  });

  it('ramps linearly in BPM over ticks, its seconds the exact integral', () => {
    const map = new TempoMap([
      { tick: 0, bpm: 60, curve: 'ramp' },
      { tick: 9600, bpm: 180, curve: 'step' }
    ]);
    expect(map.bpmAt(4800)).toBeCloseTo(120);
    // Numeric integration of 60 / (PPQ × bpm(t)).
    let seconds = 0;
    const steps = 96_000;
    for (let i = 0; i < steps; i++) {
      const t = ((i + 0.5) / steps) * 9600;
      seconds += (9600 / steps) * (60 / (960 * map.bpmAt(t)));
    }
    expect(map.secondsAt(9600)).toBeCloseTo(seconds, 6);
    expect(map.secondsAt(9600)).toBeCloseTo(10 * (60 / 120) * Math.log(3), 6);
  });

  it('round-trips and is monotonic', () => {
    const map = new TempoMap([
      { tick: 0, bpm: 90, curve: 'ramp' },
      { tick: 1920, bpm: 140, curve: 'step' },
      { tick: 5760, bpm: 70, curve: 'ramp' },
      { tick: 7680, bpm: 70, curve: 'ramp' },
      { tick: 9600, bpm: 200, curve: 'step' }
    ]);
    let last = -1;
    for (let tick = 0; tick <= 12_000; tick += 37) {
      const s = map.secondsAt(tick);
      expect(s).toBeGreaterThan(last);
      last = s;
      expect(map.tickAt(s)).toBeCloseTo(tick, 6);
    }
  });

  it('adds a first event at 0 when the track has none there', () => {
    const map = new TempoMap([{ tick: 960, bpm: 100, curve: 'step' }]);
    expect(map.events[0]).toEqual({ tick: 0, bpm: 100, curve: 'step' });
  });

  it('converts audio frames', () => {
    const map = TempoMap.constant(120);
    expect(map.framesBetween(0, 960, 48_000)).toBeCloseTo(24_000);
    expect(map.tickAfterFrames(960, 48_000, 48_000)).toBeCloseTo(2880);
  });
});
