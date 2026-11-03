import { padKey, padPosition, padRound, padText, padValueAt, padZone } from './session-pad-math';

describe('the Session Player pad', () => {
  const box = { left: 10, top: 20, width: 200, height: 100 };

  it('reads complexity to the right and intensity up, clamped and in hundredths', () => {
    expect(padValueAt(10, 120, box)).toEqual({ complexity: 0, intensity: 0 });
    expect(padValueAt(210, 20, box)).toEqual({ complexity: 1, intensity: 1 });
    expect(padValueAt(60, 45, box)).toEqual({ complexity: 0.25, intensity: 0.75 });
    expect(padValueAt(-50, 500, box)).toEqual({ complexity: 0, intensity: 0 });
    expect(padValueAt(77.7, 70, box).complexity).toBe(0.34);
    expect(padValueAt(0, 0, { ...box, width: 0 })).toEqual({ complexity: 0.5, intensity: 0.5 });
  });

  it('places a value back where it was read', () => {
    const value = padValueAt(160, 30, box);
    const { left, bottom } = padPosition(value);
    expect(box.left + (left / 100) * box.width).toBeCloseTo(160);
    expect(box.top + (1 - bottom / 100) * box.height).toBeCloseTo(30);
  });

  it('steps by 5 % (1 % fine) from the keyboard, Home and End to the corners', () => {
    const value = { complexity: 0.5, intensity: 0.5 };
    expect(padKey(value, 'ArrowRight', false)).toEqual({ complexity: 0.55, intensity: 0.5 });
    expect(padKey(value, 'ArrowDown', true)).toEqual({ complexity: 0.5, intensity: 0.49 });
    expect(padKey({ complexity: 0.98, intensity: 0 }, 'ArrowRight', false)).toEqual({ complexity: 1, intensity: 0 });
    expect(padKey(value, 'Home', false)).toEqual({ complexity: 0, intensity: 0 });
    expect(padKey(value, 'End', false)).toEqual({ complexity: 1, intensity: 1 });
    expect(padKey(value, 'a', false)).toBeNull();
  });

  it('names its zones and reads out its value', () => {
    expect(padZone({ complexity: 0.1, intensity: 0.9 })).toBe('simple, loud');
    expect(padZone({ complexity: 0.5, intensity: 0.5 })).toBe('moderate, medium');
    expect(padText(padRound({ complexity: 0.456, intensity: 0.7 }))).toBe(
      'Complexity 46 %, intensity 70 % (moderate, loud)'
    );
  });
});
