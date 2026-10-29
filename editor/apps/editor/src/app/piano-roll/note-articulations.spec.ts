import { TestBed } from '@angular/core/testing';
import type { MidiRegion } from '@anode/api-client';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { NeutralPlayback } from '../midi/midi-math';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import {
  articulationCommands,
  articulationGroups,
  articulationIdsOf,
  articulationName,
  articulationText,
  editNoteField
} from './articulation-edit';
import { ArticulationLane, NoteInspector } from './note-articulations';
import { placeNotes } from './note-edit';

const region: MidiRegion = {
  id: 'mid_1',
  trackId: 'trk_1',
  name: 'R',
  startTick: 3840,
  lengthTicks: 3840,
  playback: NeutralPlayback,
  notes: [
    { id: 1, pitch: 60, velocity: 100, channel: 1, startTick: 0, lengthTicks: 480, articulationId: 0 },
    { id: 2, pitch: 64, velocity: 90, channel: 1, startTick: 0, lengthTicks: 960, articulationId: 2 },
    { id: 3, pitch: 67, velocity: 80, channel: 1, startTick: 960, lengthTicks: 240, articulationId: 1 }
  ],
  controls: [],
  muted: false
};

const articulations = [
  { id: 1, name: 'Sine', symbol: '~' },
  { id: 2, name: 'Square', symbol: null }
];

describe('Piano Roll articulations: maths', () => {
  it('groups notes by start and labels their articulations', () => {
    const groups = articulationGroups([region]);
    expect(groups.map(g => [g.start, g.length, g.notes.map(n => n.id)])).toEqual([
      [3840, 960, [1, 2]],
      [4800, 240, [3]]
    ]);
    const first = groups[0]?.notes ?? [];
    expect(articulationIdsOf(first)).toEqual([0, 2]);
    expect(articulationText([0, 2], articulations)).toBe('*');
    expect(articulationText([1], articulations)).toBe('~');
    expect(articulationText([2], articulations)).toBe('Square');
    expect(articulationText([0], articulations)).toBe('–');
    expect(articulationText([9], articulations)).toBe('#9');
    expect(articulationName([0, 2], articulations)).toBe('Mixed');
    expect(articulationName([0], articulations)).toBe('None');
    expect(articulationName([9], articulations)).toBe('Articulation 9');
  });

  it('sets articulations with editNotes, leaving notes that have it', () => {
    expect(articulationCommands([region], new Set([1, 2, 3]), 2)).toEqual([
      {
        type: 'editNotes',
        regionId: 'mid_1',
        changes: [
          { id: 1, articulationId: 2 },
          { id: 3, articulationId: 2 }
        ]
      }
    ]);
    expect(articulationCommands([region], new Set([2]), 2)).toEqual([]);
    expect(articulationCommands([region], new Set([3]), 0)).toEqual([
      { type: 'editNotes', regionId: 'mid_1', changes: [{ id: 3, articulationId: 0 }] }
    ]);
  });

  it('edits the selection from the first note’s field', () => {
    const notes = placeNotes([region]).filter(n => n.id !== 2);
    expect(editNoteField(notes, 'position', 3840 + 240).map(n => n.start)).toEqual([4080, 5040]);
    expect(editNoteField(notes, 'position', 0).map(n => n.start)).toEqual([0, 960]);
    expect(editNoteField(notes, 'position', 3840)).toEqual([]);
    expect(editNoteField(notes, 'length', 120).map(n => n.length)).toEqual([120, 120]);
    expect(editNoteField(notes, 'pitch', 62).map(n => n.pitch)).toEqual([62, 69]);
    expect(editNoteField(notes, 'pitch', 127).map(n => n.pitch)).toEqual([120, 127]);
    expect(editNoteField(notes, 'velocity', 127).map(n => n.velocity)).toEqual([127, 107]);
    expect(editNoteField([], 'velocity', 1)).toEqual([]);
  });
});

describe('Piano Roll articulations: lane and note inspector', () => {
  async function setUp() {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Notes' }));
    await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: false }], 'Track');
    const trackId = project.trackIds()[0] ?? '';
    await project.execute(
      [
        { type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: 3840 },
        {
          type: 'setArticulationSet',
          set: { id: 'ars_1', name: 'KS', articulations, switches: [], outputs: [] }
        },
        { type: 'assignArticulationSet', trackId, setId: 'ars_1' }
      ],
      'Setup'
    );
    const regionId = [...project.midiRegions().keys()][0] ?? '';
    await project.execute(
      [
        {
          type: 'addNotes',
          regionId,
          notes: [
            { pitch: 60, velocity: 100, channel: 1, startTick: 0, lengthTicks: 480 },
            { pitch: 64, velocity: 100, channel: 1, startTick: 960, lengthTicks: 480 }
          ]
        }
      ],
      'Notes'
    );
    const selection = TestBed.inject(SelectionStore);
    selection.select([regionId]);
    const notes = () => project.midiRegions().get(regionId)?.notes ?? [];
    return { project, selection, notes, regionId };
  }

  it('gives the selected notes an articulation, position, pitch and velocity from the note inspector', async () => {
    const { selection, notes } = await setUp();
    const fixture = TestBed.createComponent(NoteInspector);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const field = (id: string) => element.querySelector(`[data-testid="${id}"]`) as HTMLInputElement;
    const select = element.querySelector('[data-testid="note-articulation"]') as HTMLSelectElement;
    expect(field('note-inspector-count').textContent).toContain('No note selected');
    expect(select.disabled).toBe(true);

    selection.selectNotes(notes().map(n => n.id));
    await fixture.whenStable();
    expect(field('note-inspector-count').textContent).toContain('2 notes');
    expect(field('note-position').value).toBe('1 1 1 1');
    expect(field('note-pitch').value).toBe('C3');
    expect([...select.options].filter(o => !o.hidden).map(o => o.text.trim())).toEqual(['None', '~ Sine', 'Square']);
    select.value = '2';
    select.dispatchEvent(new Event('change'));
    await settle();
    expect(notes().map(n => n.articulationId)).toEqual([2, 2]);

    const commit = async (id: string, value: string) => {
      const input = field(id);
      input.value = value;
      input.dispatchEvent(new Event('change'));
      await settle();
      await fixture.whenStable();
    };
    await commit('note-position', '1 2');
    await commit('note-pitch', 'D3');
    await commit('note-velocity', '80');
    await commit('note-length', '0 0 1 0');
    expect(notes().map(n => [n.startTick, n.pitch, n.velocity, n.lengthTicks])).toEqual([
      [960, 62, 80, 240],
      [1920, 66, 80, 240]
    ]);
    await commit('note-pitch', 'nonsense');
    expect(field('note-pitch').value).toBe('D3');

    // One note differs: the select says "Mixed".
    selection.selectNotes([notes()[0]?.id ?? 0]);
    await fixture.whenStable();
    select.value = '1';
    select.dispatchEvent(new Event('change'));
    await settle();
    selection.selectNotes(notes().map(n => n.id));
    await fixture.whenStable();
    expect(select.options[select.selectedIndex]?.text.trim()).toBe('Mixed');
  });

  it('shows a chip per note start in the articulation lane and sets its notes’ articulation from its menu', async () => {
    const { selection, notes } = await setUp();
    const fixture = TestBed.createComponent(ArticulationLane);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const chips = () => [...element.querySelectorAll<HTMLButtonElement>('[data-testid="articulation-chip"]')];
    expect(chips().map(c => c.textContent.trim())).toEqual(['–', '–']);
    expect(chips()[1]?.getAttribute('aria-label')).toBe('Articulation of E3 at 1 2 1 1: None');
    chips()[1]?.click();
    await fixture.whenStable();
    expect([...selection.notes()]).toEqual([notes()[1]?.id]);
    const choice = [...document.querySelectorAll<HTMLElement>('[data-testid="articulation-choice"]')].find(
      c => c.dataset['articulation'] === '1'
    );
    expect(choice).toBeDefined();
    choice?.click();
    await settle();
    await fixture.whenStable();
    expect(notes().map(n => n.articulationId)).toEqual([0, 1]);
    expect(chips().map(c => c.textContent.trim())).toEqual(['–', '~']);
  });
});
