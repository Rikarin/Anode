import type { ControllerAssignments } from '@anode/api-client';
import {
  addAssignment,
  addMode,
  blankAssignment,
  deleteAssignment,
  deleteMode,
  deleteZone,
  describeInput,
  describeMessage,
  findAssignment,
  idsOf,
  learnKey,
  learnTargetAt,
  moveAssignment,
  newId,
  nextName,
  parseLearnKey,
  renameMode,
  retarget,
  sameTarget,
  setActiveMode,
  updateAssignment,
  withZone
} from './assignments';

const Empty: ControllerAssignments = { zones: [] };

function twoModes(): ControllerAssignments {
  let a = withZone(Empty);
  const zone = a.zones[0];
  if (!zone) {
    throw new Error('no zone');
  }
  a = addMode(a, zone.id, 'Mode 2');
  return a;
}

describe('controller assignments (editing)', () => {
  it('describes inputs and messages as the window shows them', () => {
    expect(describeInput({ port: 'anode:in', kind: 'controlChange', channel: 1, number: 21 }, () => 'Anode In')).toBe(
      'CC 21 · Ch 1 · Anode In'
    );
    expect(describeInput({ port: null, kind: 'note', channel: null, number: 60 })).toBe('Note C3 · Any Ch · Any port');
    expect(describeInput({ kind: 'controlChange14', number: 7 })).toBe('CC 7/39 · Any Ch · Any port');
    const input = { kind: 'controlChange' as const, number: 21 };
    expect(describeMessage({ port: 'p', status: 0xb0, data1: 21, data2: 64, input })).toBe('CC 21 = 64 · Ch 1');
    expect(describeMessage({ port: 'p', status: 0x99, data1: 36, data2: 100, input })).toBe(
      'Note C1 on, velocity 100 · Ch 10'
    );
    expect(describeMessage({ port: 'p', status: 0xe0, data1: 0, data2: 64, input })).toBe('Pitch Bend 0 · Ch 1');
  });

  it('makes a zone on first use and ids unique across everything', () => {
    const a = withZone(Empty);
    expect(a.zones).toHaveLength(1);
    expect(a.zones[0]?.modes).toHaveLength(1);
    expect(a.zones[0]?.activeModeId).toBe(a.zones[0]?.modes[0]?.id);
    // A random source that gives a used id first: newId draws again.
    const taken = { zones: [{ id: 'cas_000000', name: 'Z', modes: [], activeModeId: '' }] };
    const draws = [0, 0.5];
    const id = newId('cas', taken, () => draws.shift() ?? 0.5);
    expect(id).toMatch(/^cas_[0-9a-z]{6}$/);
    expect(idsOf(taken).has(id)).toBe(false);
  });

  it('adds, updates, moves and deletes assignments', () => {
    let a = twoModes();
    const [first, second] = a.zones[0]?.modes ?? [];
    a = addAssignment(a, blankAssignment('cas_1', 'Fader'));
    expect(findAssignment(a, 'cas_1')?.mode.id).toBe(first?.id);
    a = updateAssignment(a, 'cas_1', { mode: 'relativeTwosComplement', min: 0.25 });
    expect(findAssignment(a, 'cas_1')?.assignment).toEqual(
      expect.objectContaining({ name: 'Fader', mode: 'relativeTwosComplement', min: 0.25, max: 1 })
    );
    a = moveAssignment(a, 'cas_1', second?.id ?? '');
    expect(findAssignment(a, 'cas_1')?.mode.id).toBe(second?.id);
    expect(a.zones[0]?.modes[0]?.assignments).toEqual([]);
    a = deleteAssignment(a, 'cas_1');
    expect(findAssignment(a, 'cas_1')).toBeNull();
  });

  it('switches, renames and deletes modes, keeping one per zone', () => {
    let a = twoModes();
    const zone = a.zones[0];
    const [first, second] = zone?.modes ?? [];
    a = setActiveMode(a, zone?.id ?? '', second?.id ?? '');
    expect(a.zones[0]?.activeModeId).toBe(second?.id);
    a = renameMode(a, second?.id ?? '', '  Mixer  ');
    expect(a.zones[0]?.modes[1]?.name).toBe('Mixer');
    expect(renameMode(a, second?.id ?? '', '   ')).toBe(a);
    // A switch to the second mode goes with it; the active mode falls back to the first.
    a = addAssignment(
      a,
      {
        ...blankAssignment('cas_s'),
        target: { kind: 'zoneMode', zoneId: zone?.id ?? null, modeId: second?.id ?? null }
      },
      first?.id
    );
    a = deleteMode(a, second?.id ?? '');
    expect(a.zones[0]?.modes.map(m => m.id)).toEqual([first?.id]);
    expect(a.zones[0]?.activeModeId).toBe(first?.id);
    expect(findAssignment(a, 'cas_s')).toBeNull();
    expect(deleteMode(a, first?.id ?? '')).toEqual(a);
    expect(deleteZone(a, zone?.id ?? '').zones).toEqual([]);
  });

  it('names the next of a series', () => {
    expect(nextName('Zone', [])).toBe('Zone 1');
    expect(nextName('Mode', ['Mode 1', 'Mode 3'])).toBe('Mode 2');
  });

  it('retargets with each kind’s defaults', () => {
    const target = { kind: 'parameter' as const, trackId: 'trk_1', address: 'pan' };
    expect(retarget(target, 'trackSwitch', Empty)).toEqual({ kind: 'trackSwitch', trackId: 'trk_1', switch: 'mute' });
    expect(retarget(target, 'transport', Empty)).toEqual({ kind: 'transport', transport: 'play' });
    expect(retarget(target, 'smartControl', Empty)).toEqual({ kind: 'smartControl', trackId: null, address: 'knob1' });
  });

  it('reads the learn target of a clicked control', () => {
    const target = { kind: 'parameter' as const, trackId: 'trk_2', address: 'send:snd_1' };
    expect(parseLearnKey(learnKey(target))).toEqual(target);
    expect(parseLearnKey('not json')).toBeNull();
    expect(sameTarget(target, { ...target })).toBe(true);
    expect(
      sameTarget({ kind: 'parameter', trackId: null, address: 'volume' }, { kind: 'parameter', address: 'volume' })
    ).toBe(true);

    document.body.innerHTML = `
      <div data-learn-track="trk_2">
        <an-fader><span id="cap"></span></an-fader>
        <an-pan-knob><span id="pan"></span></an-pan-knob>
        <div data-learn='${learnKey(target)}'><span id="send"></span></div>
        <span id="name"></span>
      </div>`;
    const at = (id: string) => learnTargetAt(document.getElementById(id));
    expect(at('cap')).toEqual({ kind: 'parameter', trackId: 'trk_2', address: 'volume' });
    expect(at('pan')).toEqual({ kind: 'parameter', trackId: 'trk_2', address: 'pan' });
    expect(at('send')).toEqual(target);
    expect(at('name')).toBeNull();
  });
});
