import type { ControlEvent } from '@anode/api-client';
import { DefaultLaneSet, drawControls, inLane, laneRange, lineSteps, lineValue, lineVelocities } from './lanes';

describe('Step Editor lanes and the line tool', () => {
  it('has the documented default set', () => {
    expect(DefaultLaneSet.lanes.map(l => [l.kind, l.number ?? null, l.resolutionTicks])).toEqual([
      ['velocity', null, 240],
      ['controlChange', 1, 240],
      ['pitchBend', null, 240],
      ['channelPressure', null, 240],
      ['program', null, 240]
    ]);
  });

  it('interpolates a line on the lane’s grid, rounded and clamped', () => {
    // A bar of 1/16s from 0 to 127, both ends included: 17 steps.
    const steps = lineSteps({ tick: 0, value: 0 }, { tick: 3840, value: 127 }, 240, [0, 127]);
    expect(steps).toHaveLength(17);
    expect(steps[0]).toEqual({ tick: 0, value: 0 });
    expect(steps[8]).toEqual({ tick: 1920, value: 64 });
    expect(steps[16]).toEqual({ tick: 3840, value: 127 });
    // Either direction; steps start on the grid at or after the line's start.
    expect(lineSteps({ tick: 1000, value: 100 }, { tick: 100, value: 10 }, 240, [0, 127])).toEqual([
      { tick: 240, value: 24 },
      { tick: 480, value: 48 },
      { tick: 720, value: 72 },
      { tick: 960, value: 96 }
    ]);
    expect(lineSteps({ tick: 0, value: -9000 }, { tick: 480, value: 9000 }, 240, laneRange('pitchBend'))).toEqual([
      { tick: 0, value: -8192 },
      { tick: 240, value: 0 },
      { tick: 480, value: 8191 }
    ]);
    expect(lineValue({ tick: 5, value: 9 }, { tick: 5, value: 1 }, 5)).toBe(9);
  });

  it('gives a velocity ramp to the notes under the line', () => {
    const notes = [0, 240, 480, 720, 960].map((startTick, i) => ({ id: i + 1, startTick }));
    expect(lineVelocities(notes, { tick: 0, value: 20 }, { tick: 720, value: 110 })).toEqual([
      { id: 1, velocity: 20 },
      { id: 2, velocity: 50 },
      { id: 3, velocity: 80 },
      { id: 4, velocity: 110 }
    ]);
  });

  it('replaces the lane’s events in each drawn step, and only that lane’s', () => {
    const existing: ControlEvent[] = [
      { id: 1, kind: 'controlChange', channel: 1, number: 74, value: 10, tick: 250 },
      { id: 2, kind: 'controlChange', channel: 1, number: 1, value: 10, tick: 250 },
      { id: 3, kind: 'controlChange', channel: 1, number: 74, value: 10, tick: 2000 },
      { id: 4, kind: 'pitchBend', channel: 1, number: 0, value: 10, tick: 250 }
    ];
    const lane = { kind: 'controlChange' as const, number: 74, resolutionTicks: 240 };
    expect(existing.filter(e => inLane(lane, e)).map(e => e.id)).toEqual([1, 3]);
    const draw = drawControls(
      lane,
      existing,
      lineSteps({ tick: 0, value: 0 }, { tick: 480, value: 100 }, 240, [0, 127])
    );
    expect(draw.deleteIds).toEqual([1]);
    expect(draw.add).toEqual([
      { kind: 'controlChange', channel: 1, number: 74, value: 0, tick: 0 },
      { kind: 'controlChange', channel: 1, number: 74, value: 50, tick: 240 },
      { kind: 'controlChange', channel: 1, number: 74, value: 100, tick: 480 }
    ]);
    const program = drawControls({ kind: 'program', number: null, resolutionTicks: 960 }, existing, [
      { tick: 10, value: 5 }
    ]);
    expect(program.add).toEqual([{ kind: 'programChange', channel: 1, number: 0, value: 5, tick: 10 }]);
    expect(
      drawControls({ kind: 'velocity', number: null, resolutionTicks: 240 }, existing, [{ tick: 0, value: 1 }])
    ).toEqual({
      deleteIds: [],
      add: []
    });
  });
});
