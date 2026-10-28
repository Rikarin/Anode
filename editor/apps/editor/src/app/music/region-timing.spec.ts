import type { FileTempo, FlexMarker, SignatureEvent, TempoEvent } from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import { isWarped, nextLine, RegionTiming, type TimedRegion, type TimingContext } from './region-timing';
import { rampBpm, TempoMap } from './tempo-map';

const Sr = 48_000;

function context(tempo: TempoMap = TempoMap.constant(120), meter = MeterMap.constant(4, 4)): TimingContext {
  return { tempo, meter, sampleRate: Sr };
}

function region(overrides: Partial<TimedRegion> = {}): TimedRegion {
  return {
    startTick: 0,
    fileOffset: 0,
    length: 96_000,
    follow: 'off',
    flexMarkers: [],
    transpose: 0,
    fineTune: 0,
    pitchNotes: [],
    ...overrides
  };
}

/** A file at 100 bpm: a beat is 28 800 frames at 48 kHz. */
const Beat = 28_800;
const fileTempo = (beats: number[], downbeats: number[]): FileTempo => ({
  bpm: 100,
  variable: false,
  beats,
  downbeats
});

describe('RegionTiming', () => {
  it('says which regions are warped', () => {
    expect(isWarped(region(), false)).toBe(false);
    expect(isWarped(region(), true)).toBe(true);
    expect(isWarped(region({ follow: 'on' }), false)).toBe(true);
    expect(isWarped(region({ transpose: 2 }), false)).toBe(true);
    expect(isWarped(region({ fineTune: -5 }), false)).toBe(true);
  });

  it('plays an unwarped region 1:1 in time', () => {
    const t = new RegionTiming(region({ startTick: 960, fileOffset: 1000, length: Sr }), false, null, context());
    expect(t.warped).toBe(false);
    expect(t.endTick).toBeCloseTo(2880);
    expect(t.tickAt(1000 + Sr / 2)).toBeCloseTo(1920);
    expect(t.frameAt(1920)).toBeCloseTo(1000 + Sr / 2);
  });

  it('follows the tempo track in time, as Phase 4 did, when unwarped under a ramp', () => {
    const tempo = new TempoMap([
      { tick: 0, bpm: 60, curve: 'ramp', shape: 0 },
      { tick: 9600, bpm: 180, curve: 'step', shape: 0 }
    ]);
    const t = new RegionTiming(region({ startTick: 960, length: 3 * Sr }), false, null, context(tempo));
    expect(t.endTick).toBeCloseTo(tempo.tickAfterFrames(960, 3 * Sr, Sr), 6);
  });

  it('maps a file beat to a quarter note with follow on, whatever the project tempo', () => {
    const tempo = fileTempo([0, Beat, 2 * Beat, 3 * Beat], [0]);
    for (const project of [TempoMap.constant(120), TempoMap.constant(87)]) {
      const t = new RegionTiming(
        region({ startTick: 3840, length: 4 * Beat, follow: 'on' }),
        false,
        tempo,
        context(project)
      );
      expect(t.endTick).toBeCloseTo(3840 + 4 * 960);
      expect(t.tickAt(Beat)).toBeCloseTo(3840 + 960);
      expect(t.frameAt(3840 + 1920)).toBeCloseTo(2 * Beat);
    }
  });

  it('falls back to time when follow is on but the file has no tempo', () => {
    const t = new RegionTiming(region({ follow: 'on', length: Sr }), false, null, context());
    expect(t.warped).toBe(true);
    expect(t.endTick).toBeCloseTo(1920);
  });

  it('puts downbeats on bars with follow bars', () => {
    // Downbeats a little late (117 000 rather than 115 200), then on time.
    const tempo = fileTempo([], [0, 117_000, 230_400]);
    const t = new RegionTiming(region({ length: 300_000, follow: 'bars' }), false, tempo, context());
    expect(t.tickAt(117_000)).toBeCloseTo(3840);
    expect(t.tickAt(58_500)).toBeCloseTo(1920);
    expect(t.tickAt(230_400)).toBeCloseTo(7680);
    // After the last downbeat, the follow-on rate: 69 600 frames are 2 320 ticks at 100 bpm.
    expect(t.endTick).toBeCloseTo(10_000);
    expect(t.frameAt(t.tickAt(150_000))).toBeCloseTo(150_000);
  });

  it('puts beats on beats with follow bars and beats, from the line after the start', () => {
    // The region starts mid-bar; its first beat would land at 960 + 1000 → snapped to 1920 (a beat line).
    const tempo = fileTempo([30_000, 58_800, 87_600], [30_000]);
    const t = new RegionTiming(
      region({ startTick: 960, length: 100_000, follow: 'barsAndBeats' }),
      false,
      tempo,
      context()
    );
    expect(t.tickAt(30_000)).toBeCloseTo(1920);
    expect(t.tickAt(58_800)).toBeCloseTo(2880);
    expect(t.tickAt(87_600)).toBeCloseTo(3840);
  });

  it('scales the base map linearly between flex markers, keeping the start and end', () => {
    // 2 s at 120 bpm is 3 840 ticks; the middle frame moves from 1 920 to 1 440.
    const markers: FlexMarker[] = [{ fileFrame: 48_000, tick: 1440 }];
    const t = new RegionTiming(region({ flexMarkers: markers }), true, null, context());
    expect(t.endTick).toBeCloseTo(3840);
    expect(t.tickAt(48_000)).toBeCloseTo(1440);
    expect(t.tickAt(24_000)).toBeCloseTo(720);
    expect(t.tickAt(72_000)).toBeCloseTo(2640);
    expect(t.frameAt(2640)).toBeCloseTo(72_000);
    expect(t.frameAt(720)).toBeCloseTo(24_000);
  });

  it('ignores flex markers while the region is unwarped, and markers outside it', () => {
    const markers: FlexMarker[] = [
      { fileFrame: 48_000, tick: 1440 },
      { fileFrame: 200_000, tick: 9000 }
    ];
    expect(new RegionTiming(region({ flexMarkers: markers }), false, null, context()).tickAt(48_000)).toBeCloseTo(1920);
    expect(new RegionTiming(region({ flexMarkers: markers }), true, null, context()).knots).toHaveLength(3);
  });

  it('gives drawing points along the map', () => {
    const t = new RegionTiming(region({ flexMarkers: [{ fileFrame: 48_000, tick: 1440 }] }), true, null, context());
    const points = t.drawPoints(480);
    expect(points[0]).toEqual({ tick: 0, frame: 0 });
    expect(points.at(-1)?.frame).toBe(96_000);
    expect(points.some(p => p.tick === 1440 && p.frame === 48_000)).toBe(true);
  });

  it('finds the next bar or beat line', () => {
    const meter = new MeterMap([
      { tick: 0, numerator: 4, denominator: 4 },
      { tick: 3840, numerator: 3, denominator: 4 }
    ] satisfies SignatureEvent[]);
    expect(nextLine(meter, 0, false)).toBe(3840);
    expect(nextLine(meter, 3840, false)).toBe(3840 + 2880);
    expect(nextLine(meter, 100, true)).toBe(960);
    expect(nextLine(meter, 3840 + 1920, true)).toBe(3840 + 2880);
  });
});

describe('Tempo curve shapes', () => {
  const ramp = (shape: number): TempoEvent[] => [
    { tick: 0, bpm: 60, curve: 'ramp', shape },
    { tick: 9600, bpm: 180, curve: 'step', shape: 0 }
  ];

  it('follows the fade formula', () => {
    expect(rampBpm(60, 180, 0.5, 0)).toBeCloseTo(120);
    expect(rampBpm(60, 180, 0.5, 0.5)).toBeCloseTo(60 + 120 * Math.SQRT1_2);
    expect(rampBpm(60, 180, 0.5, -0.5)).toBeCloseTo(60 + 120 * 0.25);
    expect(rampBpm(60, 180, 0, 1)).toBe(60);
    expect(rampBpm(60, 180, 1, -1)).toBe(180);
  });

  for (const shape of [-1, -0.4, 0.3, 1]) {
    it(`integrates a ramp of shape ${shape} and inverts it`, () => {
      const map = new TempoMap(ramp(shape));
      expect(map.bpmAt(4800)).toBeCloseTo(rampBpm(60, 180, 0.5, shape));
      let seconds = 0;
      const steps = 200_000;
      for (let i = 0; i < steps; i++) {
        const t = ((i + 0.5) / steps) * 9600;
        seconds += (9600 / steps) * (60 / (960 * map.bpmAt(t)));
      }
      expect(map.secondsAt(9600)).toBeCloseTo(seconds, 5);
      for (const tick of [100, 2400, 7000, 9600, 12_000]) {
        expect(map.tickAt(map.secondsAt(tick))).toBeCloseTo(tick, 4);
      }
    });
  }

  it('keeps the linear ramp exact when the shape is 0', () => {
    expect(new TempoMap(ramp(0)).secondsAt(9600)).toBeCloseTo(10 * 0.5 * Math.log(3), 9);
  });
});

/**
 * Parity with the Model's RegionTiming (workstream B): the shared vectors in
 * engine/tests/Anode.Model.Tests/RegionTimingVectors.json, when the file is there. Each case has its inputs and the
 * expected start and end sample, end tick, and the tick and sample of some frames; ticks and samples match within 1e-6
 * (relative 1e-9 on ramps).
 */
interface Vector {
  name: string;
  sampleRate: number;
  tempoEvents: TempoEvent[];
  signatures: SignatureEvent[];
  flexOn: boolean;
  fileTempo: FileTempo | null;
  region: TimedRegion;
  expected: {
    warped: boolean;
    startSample: number;
    endSample: number;
    endTick: number;
    points: { frame: number; tick: number; sample: number }[];
  };
}

async function loadVectors(): Promise<Vector[] | null> {
  const proc = (globalThis as { process?: { cwd(): string; env: Record<string, string | undefined> } }).process;
  if (!proc) {
    return null;
  }
  try {
    const fsName = 'node:fs';
    const fs = (await import(/* @vite-ignore */ fsName)) as {
      existsSync(path: string): boolean;
      readFileSync(path: string, encoding: string): string;
    };
    const root = proc.cwd().replace(/\/editor(\/.*)?$/, '');
    const path =
      proc.env['ANODE_REGION_TIMING_VECTORS'] ?? `${root}/engine/tests/Anode.Model.Tests/RegionTimingVectors.json`;
    if (!fs.existsSync(path)) {
      return null;
    }
    const parsed = JSON.parse(fs.readFileSync(path, 'utf8')) as Vector[] | { cases: Vector[] };
    return Array.isArray(parsed) ? parsed : parsed.cases;
  } catch {
    return null;
  }
}

function near(actual: number, expected: number, what: string): void {
  const tolerance = Math.max(1e-6, 1e-9 * Math.abs(expected));
  expect(Math.abs(actual - expected), `${what}: ${actual} vs ${expected}`).toBeLessThanOrEqual(tolerance);
}

describe('RegionTiming parity with the Model', async () => {
  const vectors = await loadVectors();
  it.skipIf(vectors === null)('matches every shared vector', () => {
    for (const v of vectors ?? []) {
      const tempo = new TempoMap(v.tempoEvents);
      const timing = new RegionTiming(v.region, v.flexOn, v.fileTempo, {
        tempo,
        meter: new MeterMap(v.signatures),
        sampleRate: v.sampleRate
      });
      const sample = (tick: number) => tempo.secondsAt(tick) * v.sampleRate;
      expect(timing.warped, v.name).toBe(v.expected.warped);
      near(timing.endTick, v.expected.endTick, `${v.name} end tick`);
      // The start and end are whole project samples, as the compiler places them.
      near(Math.round(sample(timing.startTick)), v.expected.startSample, `${v.name} start sample`);
      near(Math.round(sample(timing.endTick)), v.expected.endSample, `${v.name} end sample`);
      for (const point of v.expected.points) {
        const tick = timing.tickAt(point.frame);
        near(tick, point.tick, `${v.name} tick @${point.frame}`);
        near(sample(tick), point.sample, `${v.name} sample @${point.frame}`);
      }
    }
  });
});
