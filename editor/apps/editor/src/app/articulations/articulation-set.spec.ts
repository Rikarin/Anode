import type { ArticulationSet } from '@anode/api-client';
import { checkArticulationSet } from '../core/fake/fake-phase7';
import {
  addArticulation,
  articulationSetProblems,
  cleanArticulationSet,
  emptyArticulationSet,
  loadInto,
  newArticulationSetId,
  newOutput,
  newSwitch,
  nextArticulationId,
  parsePitch,
  pitchLabel,
  removeArticulation,
  saveArticulationSetCommands,
  withOutputKind
} from './articulation-set';

function keyswitched(): ArticulationSet {
  return {
    id: 'ars_strings',
    name: 'Sampler keyswitches',
    articulations: [
      { id: 1, name: 'Sine', symbol: '~' },
      { id: 2, name: 'Square', symbol: null }
    ],
    switches: [{ articulationId: 2, kind: 'controlChange', number: 32, value: 2 }],
    outputs: [
      { articulationId: 1, kind: 'note', number: 24, value: 100 },
      { articulationId: 2, kind: 'note', number: 25, value: 100 }
    ]
  };
}

/** Whether the fake Server (the contract's checks) accepts a set. */
function serverAccepts(set: ArticulationSet): boolean {
  try {
    checkArticulationSet(set);
    return true;
  } catch {
    return false;
  }
}

describe('articulation sets', () => {
  it('makes new sets with ars_ ids and adds articulations with the lowest free id', () => {
    expect(newArticulationSetId()).toMatch(/^ars_[0-9a-f]{12}$/);
    let set = emptyArticulationSet('ars_x');
    expect(articulationSetProblems(set)).toEqual([]);
    set = addArticulation(addArticulation(set));
    expect(set.articulations.map(a => [a.id, a.name])).toEqual([
      [1, 'Articulation 1'],
      [2, 'Articulation 2']
    ]);
    set = { ...set, articulations: [{ id: 2, name: 'Two' }] };
    expect(nextArticulationId(set)).toBe(1);
    expect(addArticulation(set).articulations.map(a => a.id)).toEqual([1, 2]);
    const full = { ...set, articulations: Array.from({ length: 254 }, (_, i) => ({ id: i + 1, name: `A${i}` })) };
    expect(nextArticulationId(full)).toBeNull();
    expect(addArticulation(full)).toBe(full);
  });

  it('removes an articulation with the switches and outputs that named it', () => {
    const set = removeArticulation(keyswitched(), 1);
    expect(set.articulations.map(a => a.id)).toEqual([1]);
    expect(set.switches).toEqual([]);
    expect(set.outputs.map(o => o.number)).toEqual([24]);
  });

  it('adds switches and keyswitch outputs on free keys', () => {
    const set = keyswitched();
    expect(
      newOutput({ ...set, outputs: [set.outputs[0] ?? { articulationId: 1, kind: 'note', number: 24, value: 1 }] })
    ).toEqual({ articulationId: 2, kind: 'note', number: 25, value: 100 });
    expect(newOutput(set)).toEqual({ articulationId: 1, kind: 'note', number: 26, value: 100 });
    expect(newSwitch(set)).toEqual({ articulationId: 1, kind: 'note', number: 0, value: null });
  });

  it('moves an output’s datum into the range of its new kind', () => {
    const note = { articulationId: 1, kind: 'note' as const, number: 24, value: 100 };
    expect(withOutputKind(note, 'channel')).toEqual({ articulationId: 1, kind: 'channel', number: 0, value: 1 });
    expect(withOutputKind(note, 'program')).toEqual({ articulationId: 1, kind: 'program', number: 0, value: 100 });
    expect(withOutputKind({ ...note, value: 0, kind: 'controlChange' }, 'note').value).toBe(100);
  });

  it('validates as the Server does', () => {
    const good = keyswitched();
    expect(articulationSetProblems(good)).toEqual([]);
    const cases: [ArticulationSet, string][] = [
      [{ ...good, articulations: [...good.articulations, { id: 1, name: 'Again' }] }, 'id 1 is used twice'],
      [{ ...good, articulations: [{ id: 0, name: 'Zero' }, ...good.articulations] }, 'the id must be 1…254'],
      [{ ...good, articulations: [{ id: 255, name: 'Big' }, ...good.articulations] }, 'the id must be 1…254'],
      [{ ...good, switches: [{ articulationId: 9, kind: 'note', number: 0 }] }, 'Switch 1 names no articulation'],
      [{ ...good, switches: [{ articulationId: 1, kind: 'note', number: 128 }] }, 'the key must be 0…127'],
      [{ ...good, switches: [{ articulationId: 1, kind: 'controlChange', number: 1, value: 200 }] }, 'value must be'],
      [{ ...good, outputs: [{ articulationId: 1, kind: 'note', number: 24, value: 0 }] }, 'value must be 1…127'],
      [{ ...good, outputs: [{ articulationId: 1, kind: 'channel', number: 0, value: 17 }] }, 'value must be 1…16'],
      [{ ...good, outputs: [{ articulationId: 1, kind: 'program', number: 0, value: 128 }] }, 'value must be 0…127'],
      [
        { ...good, outputs: [{ articulationId: 3, kind: 'controlChange', number: 1, value: 1 }] },
        'names no articulation'
      ]
    ];
    for (const [set, problem] of cases) {
      const problems = articulationSetProblems(set);
      expect(problems.join(' ')).toContain(problem);
      // Every problem the Server would refuse is caught here too.
      expect(serverAccepts(set)).toBe(false);
    }
    expect(articulationSetProblems({ ...good, name: ' ' })).toEqual(['The set needs a name.']);
    expect(articulationSetProblems({ ...good, id: 'x' })).toEqual(['The set’s id must start with ars_.']);
    expect(serverAccepts(good)).toBe(true);
  });

  it('saves a set (and assigns a new one to its track) in one undo step', () => {
    const set = {
      ...keyswitched(),
      name: ' Keys ',
      articulations: [
        { id: 2, name: 'B', symbol: ' ' },
        { id: 1, name: 'A' }
      ]
    };
    const [save, assign] = saveArticulationSetCommands(set, 'trk_1');
    expect(save).toMatchObject({
      type: 'setArticulationSet',
      set: {
        name: 'Keys',
        articulations: [
          { id: 1, name: 'A', symbol: null },
          { id: 2, name: 'B', symbol: null }
        ]
      }
    });
    expect(assign).toEqual({ type: 'assignArticulationSet', trackId: 'trk_1', setId: 'ars_strings' });
    expect(saveArticulationSetCommands(set, null)).toHaveLength(1);
    expect(
      cleanArticulationSet({ ...set, outputs: [{ articulationId: 1, kind: 'program', number: 9, value: 3 }] }).outputs
    ).toEqual([{ articulationId: 1, kind: 'program', number: 0, value: 3 }]);
  });

  it('loads a library set into the set being edited, keeping its id', () => {
    const loaded = loadInto(emptyArticulationSet('ars_mine'), keyswitched());
    expect(loaded.id).toBe('ars_mine');
    expect(loaded.outputs).toEqual(keyswitched().outputs);
  });

  it('reads pitches as Logic names them (C3 = 60) or as numbers', () => {
    expect(parsePitch('C3')).toBe(60);
    expect(parsePitch('c0')).toBe(24);
    expect(parsePitch('C#0')).toBe(25);
    expect(parsePitch('D♭0')).toBe(25);
    expect(parsePitch('C-2')).toBe(0);
    expect(parsePitch('G8')).toBe(127);
    expect(parsePitch('A8')).toBeNull();
    expect(parsePitch('61')).toBe(61);
    expect(parsePitch('128')).toBeNull();
    expect(parsePitch('H2')).toBeNull();
    expect(pitchLabel(24)).toBe('C0 (24)');
  });
});
