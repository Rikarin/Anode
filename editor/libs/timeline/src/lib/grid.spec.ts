import { barMultiple, GridLevel, gridLines, gridStep } from './grid';

describe('gridStep', () => {
  const ppq = 960;

  it('picks the finest musical division at least the minimum spacing apart', () => {
    expect(gridStep(ppq, 4, 40 / ppq, 8)).toBe(240); // a beat is 40 px: sixteenths are 10 px
    expect(gridStep(ppq, 4, 20 / ppq, 8)).toBe(480); // eighths
    expect(gridStep(ppq, 4, 10 / ppq, 8)).toBe(960); // beats
  });

  it('steps by powers of two bars when zoomed out', () => {
    expect(gridStep(ppq, 4, 4 / ppq, 8)).toBe(3840); // a bar is 16 px
    expect(gridStep(ppq, 4, 0.5 / ppq, 8)).toBe(4 * 3840); // a bar is 2 px
  });
});

describe('barMultiple', () => {
  it('is the smallest power of two reaching the spacing', () => {
    expect(barMultiple(50, 40)).toBe(1);
    expect(barMultiple(15, 40)).toBe(4);
  });
});

describe('gridLines', () => {
  it('lists every line in range, aligned to the step, with its weight', () => {
    const lines = gridLines(900, 4000, 480, 960, 4);
    expect(lines.map(line => line.tick)).toEqual([960, 1440, 1920, 2400, 2880, 3360, 3840]);
    expect(lines.map(line => line.level)).toEqual([
      GridLevel.Beat,
      GridLevel.Division,
      GridLevel.Beat,
      GridLevel.Division,
      GridLevel.Beat,
      GridLevel.Division,
      GridLevel.Bar
    ]);
  });

  it('starts at zero, never before', () => {
    expect(gridLines(-5000, 0, 960, 960, 4)).toEqual([{ tick: 0, level: GridLevel.Bar }]);
  });
});
