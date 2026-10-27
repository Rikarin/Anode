import { TestBed } from '@angular/core/testing';
import { EventStream } from '../core/events';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { Notify } from '../core/notify';
import { ProjectStore } from './project.store';
import { Ppq, TransportStore, barsBeats, clockTime, parseSignature } from './transport.store';

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

describe('clockTime and parseSignature', () => {
  it('formats hours, minutes, seconds and milliseconds', () => {
    expect(clockTime(3_723.456)).toBe('01:02:03.456');
  });

  it('reads a time signature', () => {
    expect(parseSignature(' 6 / 8 ')).toEqual({ numerator: 6, denominator: 8 });
    expect(parseSignature('7/5')).toBeNull();
    expect(parseSignature('four')).toBeNull();
  });
});

describe('TransportStore', () => {
  async function setUp() {
    const server = setUpFakeServer();
    await server.createProject({ name: 'Transport' });
    const project = TestBed.inject(ProjectStore);
    await project.refetch();
    return { server, project, transport: TestBed.inject(TransportStore) };
  }

  it('extrapolates the playhead from the last frame at the tempo, and holds it when stopped', async () => {
    const { transport } = await setUp();
    transport.applyStatus({ state: 'playing', positionTick: 960, positionSamples: 24_000, sampleRate: 48_000 }, 1_000);
    expect(transport.positionAt(1_500)).toBe(960 + Ppq); // 120 bpm: a quarter note every 500 ms
    expect(transport.secondsAt(1_500)).toBe(1);

    transport.applyStatus(
      { state: 'stopped', positionTick: 1_920, positionSamples: 48_000, sampleRate: 48_000 },
      2_000
    );
    expect(transport.playing()).toBe(false);
    expect(transport.positionAt(9_000)).toBe(1_920);
  });

  it('wraps at the cycle end while cycling', async () => {
    const { project, transport } = await setUp();
    await project.execute([{ type: 'setCycle', enabled: true, startTick: 0, endTick: 4 * Ppq }], 'Set Cycle');
    transport.applyStatus({ state: 'playing', positionTick: 3 * Ppq, positionSamples: 0, sampleRate: 48_000 }, 0);
    expect(transport.positionAt(1_000)).toBe(Ppq); // 3 + 2 beats wraps to beat 1
  });

  it('follows transport frames from the Server', async () => {
    const { transport } = await setUp();
    TestBed.inject(EventStream).dispatch({
      type: 'transport',
      transport: { state: 'recording', positionTick: 480, positionSamples: 12_000, sampleRate: 48_000 }
    });
    expect(transport.recording()).toBe(true);
    expect(transport.anchorTicks()).toBe(480);
  });

  it('plays at once and then follows the answer; stop twice goes back to the start', async () => {
    const { server, transport } = await setUp();
    transport.play(0);
    expect(transport.playing()).toBe(true);
    await settle();
    expect(server.requests).toContain('ControlTransport');

    transport.stop();
    await settle();
    expect(transport.playing()).toBe(false);
    transport.stop();
    await settle();
    expect(transport.anchorTicks()).toBe(0);
  });

  it('shows stopped again when the Server refuses to record', async () => {
    const { transport } = await setUp();
    transport.record(0);
    expect(transport.recording()).toBe(true);
    await settle();
    expect(transport.recording()).toBe(false);
    expect(TestBed.inject(Notify).last()).toBe('Could not record: Record-enable a track first.');
  });

  it('edits the tempo, signature, cycle and metronome as labelled commands', async () => {
    const { server, transport } = await setUp();
    transport.setTempo(98);
    transport.setSignature({ numerator: 3, denominator: 4 });
    transport.toggleCycle();
    transport.toggleMetronome();
    transport.toggleCountIn();
    await settle();
    expect(transport.tempo()).toBe(98);
    expect(transport.signature()).toEqual({ numerator: 3, denominator: 4 });
    expect(transport.cycle()).toBe(true);
    expect((await server.history()).undo.map(s => s.label)).toEqual([
      'Count-in On',
      'Metronome On',
      'Cycle On',
      'Time Signature Change',
      'Tempo Change'
    ]);
  });
});
