import { StepChordCollector, stepLength } from './step-input';

describe('step input', () => {
  it('lengthens a dotted step by half and shortens a triplet to two thirds', () => {
    expect(stepLength({ noteValueTicks: 240, dotted: false, triplet: false })).toBe(240);
    expect(stepLength({ noteValueTicks: 480, dotted: true, triplet: false })).toBe(720);
    expect(stepLength({ noteValueTicks: 480, dotted: false, triplet: true })).toBe(320);
    expect(stepLength({ noteValueTicks: 960, dotted: true, triplet: true })).toBe(960);
  });

  it('makes one step of the keys down since the last, sent at the first release, at the highest velocity', () => {
    const keys = new StepChordCollector();
    keys.noteOn(60, 90);
    expect(keys.noteOff(60)).toEqual({ pitches: [60], velocity: 90 });

    // A chord: three keys down, the first release sends it, the others do nothing.
    keys.noteOn(64, 70);
    keys.noteOn(60, 100);
    keys.noteOn(67, 80);
    expect(keys.noteOff(64)).toEqual({ pitches: [60, 64, 67], velocity: 100 });
    expect(keys.noteOff(60)).toBeNull();
    expect(keys.noteOff(67)).toBeNull();

    // Legato: a key held over the next is in neither step twice.
    keys.noteOn(62, 50);
    keys.noteOn(65, 60);
    expect(keys.noteOff(62)).toEqual({ pitches: [62, 65], velocity: 60 });
    keys.noteOn(69, 40);
    expect(keys.noteOff(65)).toBeNull();
    expect(keys.noteOff(69)).toEqual({ pitches: [69], velocity: 40 });

    keys.noteOn(72, 10);
    keys.reset();
    expect(keys.noteOff(72)).toBeNull();
  });
});
