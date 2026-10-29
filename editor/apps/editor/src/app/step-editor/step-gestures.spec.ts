import type { MidiRegion } from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import { DefaultLaneSet } from '../midi-edit/lanes';
import {
  barKeyCommand,
  currentLaneSet,
  isUnstored,
  laneBars,
  laneTestId,
  newLane,
  newLaneSetId,
  newSetName,
  strokeCommands,
  strokePoints,
  tickAtX,
  valueAtY,
  withLane,
  withoutLane,
  withResolution,
  xAtTick,
  yAtValue
} from './step-gestures';

const scale = { originTick: 0, pixelsPerTick: 0.1, height: 64 };
const meter = MeterMap.constant(4, 4);

function region(start = 0): MidiRegion {
  return {
    id: start ? 'mid_2' : 'mid_1',
    trackId: 'trk_1',
    name: 'Riff',
    startTick: start,
    lengthTicks: 3840,
    playback: {} as MidiRegion['playback'],
    muted: false,
    notes: [0, 960, 1920, 2880].map((tick, i) => ({
      id: start + i + 1,
      pitch: 60 + i,
      velocity: 50,
      channel: 1,
      startTick: tick,
      lengthTicks: 240,
      articulationId: 0
    })),
    controls: [
      { id: start + 100, kind: 'controlChange', channel: 2, number: 74, value: 10, tick: 0 },
      { id: start + 101, kind: 'controlChange', channel: 2, number: 74, value: 20, tick: 250 },
      { id: start + 102, kind: 'controlChange', channel: 2, number: 1, value: 30, tick: 0 }
    ]
  };
}

const cc74 = { kind: 'controlChange' as const, number: 74, name: 'CC 74', resolutionTicks: 240 };
const velocity = { kind: 'velocity' as const, number: null, name: 'Velocity', resolutionTicks: 240 };

describe('Step Editor gestures', () => {
  it('maps ticks to x and values to y, both ways', () => {
    const at = { originTick: 960, pixelsPerTick: 0.1 };
    expect(xAtTick(1920, at)).toBe(96);
    expect(tickAtX(96, at)).toBe(1920);
    expect(yAtValue(127, 64, [0, 127])).toBe(4);
    expect(yAtValue(0, 64, [0, 127])).toBe(60);
    expect(valueAtY(4, 64, [0, 127])).toBe(127);
    expect(valueAtY(0, 64, [0, 127])).toBe(127);
    expect(valueAtY(60, 64, [0, 127])).toBe(0);
    expect(valueAtY(64, 64, [1, 127])).toBe(1);
    expect(valueAtY(32, 64, [-8192, 8191])).toBe(0);
    for (const value of [1, 40, 100, 127]) {
      expect(valueAtY(yAtValue(value, 64, [1, 127]), 64, [1, 127])).toBe(value);
    }
  });

  it('draws a lane’s bars: beams for controllers, thin bars for velocities', () => {
    const selected = { notes: new Set([2]), controls: new Set<number>() };
    const cc = laneBars(cc74, [region()], scale, 1000, meter, selected);
    expect(cc.map(b => [b.x, b.width, b.value])).toEqual([
      [0, 23, 10],
      [25, 23, 20]
    ]);
    expect(cc[0]?.ref).toEqual({ kind: 'control', id: 100, regionId: 'mid_1', value: 10 });
    expect(cc[1]?.valueText).toBe('20 at 1 1 2 11');
    const notes = laneBars(velocity, [region()], scale, 1000, meter, selected);
    expect(notes.map(b => [b.x, b.width, b.selected])).toEqual([
      [-1, 3, false],
      [95, 3, true],
      [191, 3, false],
      [287, 3, false]
    ]);
    // Out of view: left out.
    expect(laneBars(velocity, [region()], scale, 100, meter, selected)).toHaveLength(2);
  });

  it('turns a line on a controller lane into one event per step, replacing the lane’s events there', () => {
    // A ramp over the first bar's first beat: 0 → 120 from tick 0 to 960.
    const points = strokePoints(
      [
        { x: 0, y: yAtValue(0, 64, [0, 127]) },
        { x: 96, y: yAtValue(120, 64, [0, 127]) }
      ],
      cc74,
      scale
    );
    expect(points).toEqual([
      { tick: 0, value: 0 },
      { tick: 960, value: 120 }
    ]);
    expect(strokeCommands(cc74, 'line', points, [region()])).toEqual([
      { type: 'deleteControls', regionId: 'mid_1', eventIds: [100, 101] },
      {
        type: 'addControls',
        regionId: 'mid_1',
        events: [0, 240, 480, 720, 960].map((tick, i) => ({
          kind: 'controlChange',
          channel: 2,
          number: 74,
          value: i * 30,
          tick
        }))
      }
    ]);
  });

  it('paints one value per grid step with the pencil, the last value in a step winning', () => {
    const commands = strokeCommands(
      cc74,
      'pencil',
      [
        { tick: 500, value: 10 },
        { tick: 600, value: 50 },
        { tick: 800, value: 70 }
      ],
      [region()]
    );
    expect(commands).toEqual([
      {
        type: 'addControls',
        regionId: 'mid_1',
        events: [
          { kind: 'controlChange', channel: 2, number: 74, value: 50, tick: 480 },
          { kind: 'controlChange', channel: 2, number: 74, value: 70, tick: 720 }
        ]
      }
    ]);
  });

  it('splits a stroke across regions, relative to each', () => {
    const regions = [region(), region(3840)];
    const commands = strokeCommands(
      { kind: 'pitchBend', number: null, resolutionTicks: 960 },
      'line',
      [
        { tick: 2880, value: 0 },
        { tick: 4800, value: 8000 }
      ],
      regions
    );
    expect(commands).toEqual([
      {
        type: 'addControls',
        regionId: 'mid_1',
        events: [{ kind: 'pitchBend', channel: 1, number: 0, value: 0, tick: 2880 }]
      },
      {
        type: 'addControls',
        regionId: 'mid_2',
        events: [
          { kind: 'pitchBend', channel: 1, number: 0, value: 4000, tick: 0 },
          { kind: 'pitchBend', channel: 1, number: 0, value: 8000, tick: 960 }
        ]
      }
    ]);
  });

  it('sets note velocities along a line, and per step with the pencil', () => {
    // A ramp over the bar: 1 at tick 0 to 127 at tick 3840; notes at 0, 960, 1920, 2880.
    expect(
      strokeCommands(
        velocity,
        'line',
        [
          { tick: 0, value: 1 },
          { tick: 3840, value: 127 }
        ],
        [region()]
      )
    ).toEqual([
      {
        type: 'editNotes',
        regionId: 'mid_1',
        changes: [
          { id: 1, velocity: 1 },
          { id: 2, velocity: 33 },
          { id: 3, velocity: 64 },
          { id: 4, velocity: 96 }
        ]
      }
    ]);
    expect(strokeCommands(velocity, 'pencil', [{ tick: 1000, value: 110 }], [region()])).toEqual([
      { type: 'editNotes', regionId: 'mid_1', changes: [{ id: 2, velocity: 110 }] }
    ]);
    // A note lane edits only its pitch.
    expect(
      strokeCommands(
        { kind: 'note', number: 62, resolutionTicks: 240 },
        'line',
        [
          { tick: 0, value: 100 },
          { tick: 3840, value: 100 }
        ],
        [region()]
      )
    ).toEqual([{ type: 'editNotes', regionId: 'mid_1', changes: [{ id: 3, velocity: 100 }] }]);
  });

  it('changes a bar’s value from the keyboard, within the lane’s range', () => {
    expect(barKeyCommand(cc74, { kind: 'control', id: 5, regionId: 'r', value: 126 }, 10)).toEqual({
      type: 'setControls',
      regionId: 'r',
      changes: [{ id: 5, value: 127 }]
    });
    expect(barKeyCommand(velocity, { kind: 'note', id: 1, regionId: 'r', value: 1 }, -1)).toBeNull();
    expect(barKeyCommand(velocity, { kind: 'note', id: 1, regionId: 'r', value: 64 }, -1)).toEqual({
      type: 'editNotes',
      regionId: 'r',
      changes: [{ id: 1, velocity: 63 }]
    });
  });

  it('edits lane sets', () => {
    expect(currentLaneSet([], null)).toBe(DefaultLaneSet);
    const mine = { id: 'lns_a', name: 'Mine', lanes: [] };
    expect(currentLaneSet([mine], null)).toBe(mine);
    expect(currentLaneSet([mine], 'lns_x')).toBe(mine);
    expect(isUnstored(DefaultLaneSet, [mine])).toBe(true);
    expect(newLaneSetId()).toMatch(/^lns_[a-z0-9]+$/);
    expect(newSetName([mine, { ...mine, name: 'Lane Set 2' }])).toBe('Lane Set 3');
    const lane = newLane('controlChange', 74);
    expect(lane).toEqual({ kind: 'controlChange', number: 74, name: 'CC 74', resolutionTicks: 240 });
    expect(newLane('pitchBend', 74)).toMatchObject({ number: null, name: 'Pitch Bend' });
    const set = withLane(mine, lane);
    expect(withResolution(set, 0, 480).lanes[0]?.resolutionTicks).toBe(480);
    expect(withoutLane(set, 0).lanes).toEqual([]);
    expect(laneTestId(lane)).toBe('step-lane-controlChange-74');
    expect(laneTestId(velocity)).toBe('step-lane-velocity');
  });
});
