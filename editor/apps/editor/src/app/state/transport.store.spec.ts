import { TestBed } from '@angular/core/testing';
import { Ppq, TransportStore, barsBeats, clockTime } from './transport.store';

describe('barsBeats', () => {
  const fourFour = { numerator: 4, denominator: 4 };

  it('counts from 1 1 1 1 at the start', () => {
    expect(barsBeats(0, fourFour)).toEqual({ bar: 1, beat: 1, division: 1, tick: 1 });
  });

  it('splits ticks into bars, beats, 16ths and ticks', () => {
    const ticks = Ppq * 4 + Ppq * 2 + (Ppq / 4) * 3 + 10; // bar 2, beat 3, 4th sixteenth, tick 11
    expect(barsBeats(ticks, fourFour)).toEqual({ bar: 2, beat: 3, division: 4, tick: 11 });
  });

  it('uses the denominator for the beat length', () => {
    const sixEight = { numerator: 6, denominator: 8 };
    expect(barsBeats((Ppq / 2) * 6, sixEight)).toEqual({ bar: 2, beat: 1, division: 1, tick: 1 });
  });
});

describe('clockTime', () => {
  it('formats hours, minutes, seconds and milliseconds', () => {
    expect(clockTime(3_723.456)).toBe('01:02:03.456');
  });
});

describe('TransportStore', () => {
  it('advances the playhead at the tempo while playing and keeps it when stopped', () => {
    const transport = TestBed.inject(TransportStore);
    transport.play(1_000);
    expect(transport.positionAt(1_500)).toBe(Ppq); // 120 bpm: a quarter note every 500 ms

    transport.stop(2_000);
    expect(transport.playing()).toBe(false);
    expect(transport.positionAt(9_000)).toBe(Ppq * 2);
  });

  it('returns to the start when stopped twice', () => {
    const transport = TestBed.inject(TransportStore);
    transport.play(0);
    transport.stop(1_000);
    transport.stop(1_000);
    expect(transport.positionAt(1_000)).toBe(0);
  });

  it('records by playing, and stops recording on stop', () => {
    const transport = TestBed.inject(TransportStore);
    transport.record(0);
    expect(transport.playing()).toBe(true);
    expect(transport.recording()).toBe(true);
    transport.stop(10);
    expect(transport.recording()).toBe(false);
  });
});
