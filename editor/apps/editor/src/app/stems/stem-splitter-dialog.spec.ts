import { TestBed } from '@angular/core/testing';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import { StemSplitterDialog, stemRequest, stemsOf } from './stem-splitter-dialog';

async function setUp() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Stems' }));
  await project.execute([{ type: 'addTrack', stereo: true, name: 'Mix' }], 'Track');
  const trackId = project.trackList()[0]?.id ?? '';
  const file = await server.importAudioFile('/Users/me/Music/Mix.wav');
  await project.refetch();
  await project.execute([{ type: 'addRegion', trackId, fileId: file.id, startTick: 0 }], 'Region');
  const regionId = project.regionList()[0]?.id ?? '';
  const ui = TestBed.inject(UiStore);
  ui.stemSplitter.set({ regionId });
  const fixture = TestBed.createComponent(StemSplitterDialog);
  await fixture.whenStable();
  const query = (id: string) => document.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const stable = async () => {
    await settle();
    await fixture.whenStable();
  };
  return { server, project, ui, fixture, query, stable, regionId };
}

describe('Stem Splitter', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('asks for four, six or chosen stems', () => {
    expect(stemsOf('four', new Set())).toEqual(['vocals', 'drums', 'bass', 'other']);
    expect(stemsOf('six', new Set())).toEqual(['vocals', 'drums', 'bass', 'guitar', 'piano', 'other']);
    expect(stemsOf('custom', new Set(['other', 'drums']))).toEqual(['drums', 'other']);
    expect(stemRequest('four', new Set(), true)).toEqual({ stems: null, keepSource: true });
    expect(stemRequest('custom', new Set(['vocals']), false)).toEqual({ stems: ['vocals'], keepSource: false });
  });

  it('splits with progress, the model download first, and closes when the stems are tracks', async () => {
    const { server, project, ui, query, stable } = await setUp();
    expect(query('stem-splitter')?.textContent).toContain('Mix');
    (query('stem-set-custom') as HTMLInputElement | null)?.click();
    await stable();
    (query('stem-vocals') as HTMLInputElement | null)?.click(); // off
    (query('stem-guitar') as HTMLInputElement | null)?.click(); // on
    await stable();
    (query('stem-split') as HTMLButtonElement | null)?.click();
    await stable();
    server.runJobs(1);
    await stable();
    expect(query('stem-stage')?.textContent).toContain('Downloading the six-stem model');
    expect((query('stem-split') as HTMLButtonElement | null)?.disabled).toBe(true);
    expect(query('stem-stop')).not.toBeNull();
    server.runJobs();
    await stable();
    expect(ui.stemSplitter()).toBeNull();
    expect(project.trackList().map(t => t.name)).toEqual([
      'Mix',
      'Mix – Drums',
      'Mix – Bass',
      'Mix – Guitar',
      'Mix – Other'
    ]);
  });

  it('cannot split with no stem chosen', async () => {
    const { query, stable } = await setUp();
    (query('stem-set-custom') as HTMLInputElement | null)?.click();
    await stable();
    for (const stem of ['vocals', 'drums', 'bass', 'other']) {
      (query(`stem-${stem}`) as HTMLInputElement | null)?.click();
    }
    await stable();
    expect((query('stem-split') as HTMLButtonElement | null)?.disabled).toBe(true);
  });
});
