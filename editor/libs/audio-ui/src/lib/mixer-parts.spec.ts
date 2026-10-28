import { automationPointKey } from './automation-lane';
import { automationModeLabel } from './automation-mode';
import { formatReading, readingFraction } from './reading-meter';
import { sendLevelAfterKey } from './send-knob';
import { SilenceDb } from './taper';

describe('send levels from keys', () => {
  it('steps along the fader taper and reaches the ends', () => {
    const up = sendLevelAfterKey(-12, 'ArrowUp') ?? 0;
    expect(up).toBeGreaterThan(-12);
    expect(sendLevelAfterKey(-12, 'ArrowUp', true) ?? 0).toBeLessThan(up);
    expect(sendLevelAfterKey(-12, 'Home')).toBe(SilenceDb);
    expect(sendLevelAfterKey(-12, 'End')).toBe(6);
    expect(sendLevelAfterKey(SilenceDb, 'ArrowDown')).toBe(SilenceDb);
    expect(sendLevelAfterKey(-12, 'a')).toBeNull();
  });
});

describe('readings', () => {
  it('places a value in its range and formats it', () => {
    expect(readingFraction(-15, -30, 0)).toBe(0.5);
    expect(readingFraction(5, -30, 0)).toBe(1);
    expect(readingFraction(null, -30, 0)).toBe(0);
    expect(formatReading(-6.54, 'dB')).toBe('−6.5 dB');
    expect(formatReading(-0.01, 'dB')).toBe('0.0 dB');
    expect(formatReading(null, 'dB')).toBe('—');
  });
});

describe('automation parts', () => {
  it('maps handle keys', () => {
    expect(automationPointKey(2, { key: 'ArrowLeft', shiftKey: true })).toEqual({ index: 2, key: 'left', fine: true });
    expect(automationPointKey(0, { key: 'Backspace', shiftKey: false })?.key).toBe('delete');
    expect(automationPointKey(0, { key: 'x', shiftKey: false })).toBeNull();
  });

  it('names modes', () => {
    expect(automationModeLabel('latch')).toBe('Latch');
  });
});
