import { TestBed } from '@angular/core/testing';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { TrackInspector } from '../inspector/track-inspector';
import { MidiLibraryStore } from '../state/midi-library.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';
import { ArticulationSetEditor } from './articulation-set-editor';

async function setUp() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Articulations' }));
  await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: false }], 'Track');
  const trackId = project.trackIds()[0] ?? '';
  TestBed.inject(SelectionStore).selectTrack(trackId);
  return { server, project, trackId, ui: TestBed.inject(UiStore) };
}

async function openEditor(id: string) {
  TestBed.inject(UiStore).articulationSetEditor.set(id);
  const fixture = TestBed.createComponent(ArticulationSetEditor);
  fixture.autoDetectChanges();
  await settle();
  await fixture.whenStable();
  const element = fixture.nativeElement as HTMLElement;
  const all = (testId: string) => [...element.querySelectorAll<HTMLElement>(`[data-testid="${testId}"]`)];
  const one = (testId: string, index = 0) => {
    const found = all(testId)[index];
    if (!found) {
      throw new Error(`No ${testId} #${index}`);
    }
    return found;
  };
  const input = (testId: string, index = 0) => one(testId, index) as HTMLInputElement;
  const select = (testId: string, index = 0) => one(testId, index) as HTMLSelectElement;
  const button = (testId: string, index = 0) => one(testId, index) as HTMLButtonElement;
  const type = async (field: HTMLInputElement | HTMLSelectElement, value: string) => {
    field.value = value;
    field.dispatchEvent(new Event('change'));
    await fixture.whenStable();
  };
  const click = async (testId: string, index = 0) => {
    one(testId, index).click();
    await settle();
    await fixture.whenStable();
  };
  const tab = async (name: string) => {
    const found = [...element.querySelectorAll<HTMLElement>('[role="tab"]')].find(t => t.textContent.trim() === name);
    found?.click();
    await fixture.whenStable();
  };
  return { fixture, element, all, one, input, select, button, type, click, tab };
}

describe('Articulation Set editor', () => {
  it('builds a keyswitch set, saves it into the project and assigns it to the track it was opened from', async () => {
    const { project, trackId, ui } = await setUp();
    const w = await openEditor('new');
    await w.type(w.input('articulation-set-name'), 'Sampler KS');
    await w.click('articulation-add');
    await w.click('articulation-add');
    await w.type(w.input('articulation-name', 0), 'Sine');
    await w.type(w.input('articulation-symbol', 0), '~');
    await w.type(w.input('articulation-name', 1), 'Square');

    await w.tab('Outputs');
    await w.click('output-add');
    await w.click('output-add');
    expect(w.all('output-number').map(f => (f as HTMLInputElement).value)).toEqual(['C0', 'C♯0']);
    await w.type(w.input('output-number', 1), 'D0');
    expect(w.input('output-number', 1).value).toBe('D0');
    await w.type(w.input('output-value', 1), '127');

    await w.tab('Input Switches');
    await w.click('switch-add');
    await w.type(w.select('switch-articulation'), '2');
    await w.type(w.select('switch-kind'), 'controlChange');
    await w.type(w.input('switch-number'), '32');
    await w.type(w.input('switch-value'), '5');

    expect(w.all('articulation-set-problems')).toEqual([]);
    expect(w.all('articulation-set-delete')).toEqual([]);
    await w.click('articulation-set-save');
    const set = project.articulationSetList()[0];
    expect(set).toMatchObject({
      name: 'Sampler KS',
      articulations: [
        { id: 1, name: 'Sine', symbol: '~' },
        { id: 2, name: 'Square', symbol: null }
      ],
      switches: [{ articulationId: 2, kind: 'controlChange', number: 32, value: 5 }],
      outputs: [
        { articulationId: 1, kind: 'note', number: 24, value: 100 },
        { articulationId: 2, kind: 'note', number: 26, value: 127 }
      ]
    });
    expect(set?.id).toMatch(/^ars_/);
    expect(project.track(trackId)?.articulationSetId).toBe(set?.id);
    expect(ui.articulationSetEditor()).toBe(set?.id);
    // Now it edits the saved set: Delete shows, and a second save does not assign again.
    expect(w.all('articulation-set-delete')).toHaveLength(1);
  });

  it('shows the contract’s problems and refuses to save until they are fixed', async () => {
    const { project } = await setUp();
    const w = await openEditor('new');
    await w.click('articulation-add');
    await w.click('articulation-add');
    await w.type(w.input('articulation-id', 1), '1');
    expect(w.one('articulation-set-problems').textContent).toContain('id 1 is used twice');
    expect(w.button('articulation-set-save').disabled).toBe(true);
    expect(w.button('articulation-set-save-library').disabled).toBe(true);
    await w.type(w.input('articulation-id', 1), '300');
    expect(w.one('articulation-set-problems').textContent).toContain('the id must be 1…254');
    await w.type(w.input('articulation-id', 1), '7');
    await w.tab('Outputs');
    await w.click('output-add');
    await w.type(w.select('output-kind'), 'channel');
    expect(w.all('output-number')).toEqual([]);
    await w.type(w.input('output-value'), '17');
    expect(w.one('articulation-set-problems').textContent).toContain('value must be 1…16');
    await w.type(w.input('output-value'), '2');
    expect(w.all('articulation-set-problems')).toEqual([]);
    await w.click('articulation-set-save');
    expect(project.articulationSetList()[0]?.outputs).toEqual([
      { articulationId: 1, kind: 'channel', number: 0, value: 2 }
    ]);
  });

  it('saves to and loads from the library, and deletes a set from the project', async () => {
    const { project, trackId } = await setUp();
    await project.execute(
      [
        {
          type: 'setArticulationSet',
          set: {
            id: 'ars_1',
            name: 'Strings',
            articulations: [{ id: 1, name: 'Legato' }],
            switches: [],
            outputs: [{ articulationId: 1, kind: 'controlChange', number: 58, value: 10 }]
          }
        },
        { type: 'assignArticulationSet', trackId, setId: 'ars_1' }
      ],
      'Set'
    );
    const w = await openEditor('ars_1');
    expect(w.input('articulation-set-name').value).toBe('Strings');
    await w.click('articulation-set-save-library');
    const library = TestBed.inject(MidiLibraryStore);
    expect(library.articulationSets().map(s => s.name)).toEqual(['Strings']);

    // A new set loads the library's content under its own id.
    TestBed.inject(UiStore).articulationSetEditor.set('new');
    await w.fixture.whenStable();
    expect(w.all('articulation-row')).toEqual([]);
    await w.type(w.select('articulation-set-load-library'), 'ars_1');
    expect(w.input('articulation-name').value).toBe('Legato');

    TestBed.inject(UiStore).articulationSetEditor.set('ars_1');
    await w.fixture.whenStable();
    await w.click('articulation-set-delete');
    expect(project.articulationSets().size).toBe(0);
    expect(project.track(trackId)?.articulationSetId ?? null).toBeNull();
    expect(TestBed.inject(UiStore).articulationSetEditor()).toBeNull();
  });

  it('gives a track a set from the inspector, and opens the editor', async () => {
    const { project, trackId, ui } = await setUp();
    await project.execute(
      [
        {
          type: 'setArticulationSet',
          set: { id: 'ars_1', name: 'Brass', articulations: [], switches: [], outputs: [] }
        }
      ],
      'Set'
    );
    const fixture = TestBed.createComponent(TrackInspector);
    fixture.componentRef.setInput('track', project.track(trackId));
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const select = element.querySelector<HTMLSelectElement>('[data-testid="articulation-set-select"]');
    expect([...(select?.options ?? [])].map(o => o.text.trim())).toEqual(['None', 'Brass']);
    const edit = element.querySelector<HTMLButtonElement>('[data-testid="articulation-set-edit"]');
    expect(edit?.disabled).toBe(true);
    if (select) {
      select.value = 'ars_1';
      select.dispatchEvent(new Event('change'));
    }
    await settle();
    expect(project.track(trackId)?.articulationSetId).toBe('ars_1');
    fixture.componentRef.setInput('track', project.track(trackId));
    await fixture.whenStable();
    edit?.click();
    expect(ui.articulationSetEditor()).toBe('ars_1');
    element.querySelector<HTMLButtonElement>('[data-testid="articulation-set-new"]')?.click();
    expect(ui.articulationSetEditor()).toBe('new');
    if (select) {
      select.value = '';
      select.dispatchEvent(new Event('change'));
    }
    await settle();
    expect(project.track(trackId)?.articulationSetId ?? null).toBeNull();
  });
});
