import { scriptParameters, scriptTraces } from '../core/fake/fake-phase7';
import { FactoryScripts } from './factory-scripts';
import { OctaveEchoScript, runScript } from './testing';

describe('Scripter factory presets', () => {
  it('has about ten, with unique ids and names', () => {
    expect(FactoryScripts.length).toBeGreaterThanOrEqual(10);
    expect(new Set(FactoryScripts.map(s => s.id)).size).toBe(FactoryScripts.length);
    expect(new Set(FactoryScripts.map(s => s.name)).size).toBe(FactoryScripts.length);
  });

  it.each(FactoryScripts.map(s => [s.name, s] as const))(
    '%s runs against the API subset and declares parameters the host can read',
    (_name, script) => {
      const run = runScript(script.source);
      run.handle('NoteOn', { pitch: 60, velocity: 90 });
      run.handle('NoteOn', { pitch: 64, velocity: 90 });
      run.handle('ControlChange', { number: 1, value: 64 });
      run.process(0, 1);
      run.handle('NoteOff', { pitch: 60, velocity: 0 });
      run.handle('NoteOff', { pitch: 64, velocity: 0 });
      expect(run.sent.length).toBeGreaterThan(0);
      for (const event of run.sent) {
        if (event.pitch !== undefined) {
          expect(event.pitch).toBeGreaterThanOrEqual(0);
          expect(event.pitch).toBeLessThanOrEqual(127);
        }
      }
      // What the script declares is what a compile reports (the fake reads it as the host would).
      expect(scriptParameters(script.source).map(p => p.name)).toEqual(run.parameters.map(p => p.name));
      expect(run.parameters.length).toBeGreaterThan(0);
    }
  );

  it('transposes by the slider, and releases the note it started', () => {
    const transposer = FactoryScripts.find(s => s.id === 'factory.transposer')?.source ?? '';
    const run = runScript(transposer, { Semitones: 7 });
    run.handle('NoteOn', { pitch: 60 });
    run.handle('NoteOff', { pitch: 60 });
    expect(run.sent.map(e => [e.kind, e.pitch])).toEqual([
      ['NoteOn', 67],
      ['NoteOff', 67]
    ]);
  });

  it('arpeggiates held keys on the beat grid while playing', () => {
    const arp = FactoryScripts.find(s => s.id === 'factory.arpeggio')?.source ?? '';
    const run = runScript(arp);
    run.handle('NoteOn', { pitch: 64 });
    run.handle('NoteOn', { pitch: 60 });
    run.process(0, 1);
    // 1/16 by default: the held keys in turn, up.
    expect(
      run.sent
        .filter(e => e.kind === 'NoteOn')
        .slice(0, 2)
        .map(e => [e.pitch, e.beat])
    ).toEqual([
      [60, 0],
      [64, 0.25]
    ]);
  });

  it('traces from the Trace demo', () => {
    const demo = FactoryScripts.find(s => s.id === 'factory.trace-demo')?.source ?? '';
    const run = runScript(demo);
    run.handle('NoteOn', { pitch: 60, velocity: 100 });
    expect(run.traces[0]).toBe('Trace Demo ready');
    expect(run.traces.length).toBeGreaterThan(1);
    expect(scriptTraces(demo)).toContain('Trace Demo ready');
  });

  it('the golden path’s script sends each note and the octave above it 100 ms later', () => {
    const run = runScript(OctaveEchoScript);
    run.handle('NoteOn', { pitch: 60, velocity: 100 });
    run.handle('NoteOff', { pitch: 60, velocity: 0 });
    expect(run.sent).toEqual([
      { kind: 'NoteOn', pitch: 60, velocity: 100 },
      { kind: 'NoteOn', pitch: 72, velocity: 100, ms: 100 },
      { kind: 'NoteOff', pitch: 60, velocity: 0 },
      { kind: 'NoteOff', pitch: 72, velocity: 0, ms: 100 }
    ]);
    expect(run.traces).toEqual(['Octave echo ready']);
    expect(scriptParameters(OctaveEchoScript)).toEqual([
      {
        id: 'Interval',
        name: 'Interval',
        min: 0,
        max: 24,
        default: 12,
        unit: '',
        steps: 24,
        valueNames: null,
        macro: null
      }
    ]);
  });
});
