import { MeterMap } from '@anode/timeline';
import { formatLength, formatPosition, parseLength, parsePosition } from './position';

describe('bar.beat.division.tick positions', () => {
  const four = MeterMap.constant(4, 4);

  it('formats project ticks as Logic counts them, from 1', () => {
    expect(formatPosition(0, four)).toBe('1 1 1 1');
    expect(formatPosition(959, four)).toBe('1 1 4 240');
    expect(formatPosition(960, four)).toBe('1 2 1 1');
    expect(formatPosition(3840 + 480 + 1, four)).toBe('2 1 3 2');
  });

  it('parses what it formats, with any separator, and fills left-out fields with 1', () => {
    for (const tick of [0, 1, 239, 240, 961, 3840, 12_345, 99_999]) {
      expect(parsePosition(formatPosition(tick, four), four)).toBe(tick);
    }
    expect(parsePosition('2.1.3.2', four)).toBe(3840 + 480 + 1);
    expect(parsePosition('3', four)).toBe(7680);
    expect(parsePosition('3:2', four)).toBe(7680 + 960);
    expect(parsePosition(' 1  1 1 1 ', four)).toBe(0);
  });

  it('carries fields past their range over, as Logic does', () => {
    expect(parsePosition('1 5 1 1', four)).toBe(3840);
    expect(parsePosition('1 1 5 1', four)).toBe(960);
  });

  it('refuses what is not a position', () => {
    for (const text of ['', 'a', '1 2 3 4 5', '0 1 1 1', '1 -1', '1.5x']) {
      expect(parsePosition(text, four), text).toBeNull();
    }
  });

  it('follows the signature track', () => {
    const meter = new MeterMap([
      { tick: 0, numerator: 4, denominator: 4 },
      { tick: 3840, numerator: 3, denominator: 4 },
      { tick: 3840 + 2880, numerator: 6, denominator: 8 }
    ]);
    expect(formatPosition(3840 + 2880, meter)).toBe('3 1 1 1');
    expect(parsePosition('3 1 1 1', meter)).toBe(6720);
    // A 6/8 beat is an eighth: two sixteenths.
    expect(parsePosition('3 2 2 1', meter)).toBe(6720 + 480 + 240);
    expect(formatPosition(6720 + 480 + 240, meter)).toBe('3 2 2 1');
  });

  it('formats and parses lengths from 0', () => {
    expect(formatLength(480, four)).toBe('0 0 2 0');
    expect(formatLength(3840 + 960 + 1, four)).toBe('1 1 0 1');
    expect(parseLength('0 0 2 0', four)).toBe(480);
    expect(parseLength('0 1', four)).toBe(960);
    expect(parseLength('240', four)).toBe(240);
    expect(parseLength('x', four)).toBeNull();
    for (const length of [1, 240, 959, 3841, 10_000]) {
      expect(parseLength(formatLength(length, four), four)).toBe(length);
    }
  });

  it('takes another division', () => {
    expect(formatPosition(480, four, { division: 8 })).toBe('1 1 2 1');
    expect(parsePosition('1 1 2 1', four, { division: 8 })).toBe(480);
  });
});
