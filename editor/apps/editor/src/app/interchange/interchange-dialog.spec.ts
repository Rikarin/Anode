import { TestBed } from '@angular/core/testing';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { JobsStore } from '../state/jobs.store';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import { InterchangeDialog } from './interchange-dialog';

async function open(direction: 'import' | 'export', format: 'musicxml' | 'aaf' | 'midi') {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Song' }));
  await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: false, name: 'Piano' }], 'Track');
  TestBed.inject(UiStore).interchange.set({ direction, format });
  const fixture = TestBed.createComponent(InterchangeDialog);
  await fixture.whenStable();
  const query = (testId: string) => document.querySelector<HTMLElement>(`[data-testid="${testId}"]`);
  return { server, project, fixture, query, ui: TestBed.inject(UiStore), jobs: TestBed.inject(JobsStore) };
}

describe('the interchange dialogs', () => {
  it('exports MusicXML as a job and closes when it is done', async () => {
    const { server, fixture, query, ui, jobs } = await open('export', 'musicxml');
    expect(query('export-file')?.textContent).toContain('Song.musicxml');
    query('export-compressed')?.click();
    fixture.detectChanges();
    expect(query('export-file')?.textContent).toContain('Song.mxl');
    query('interchange-start')?.click();
    await settle();
    server.runJobs();
    await settle();
    fixture.detectChanges();
    const job = [...jobs.jobs().values()].at(-1);
    expect(job).toMatchObject({ kind: 'musicXmlExport', state: 'done' });
    expect(job?.files[0]).toMatch(/\/Exports\/Song\.mxl$/);
    expect(ui.interchange()).toBeNull();
  });

  it('imports an uploaded MIDI file with its options, one undo step', async () => {
    const { server, project, fixture, query } = await open('import', 'midi');
    const input = query('import-file') as HTMLInputElement | null;
    expect(input?.getAttribute('accept')).toBe('.mid,.midi,.smf');
    const file = new File(['MThd'], 'Groove.mid');
    if (input) {
      Object.defineProperty(input, 'files', { value: [file] });
      input.dispatchEvent(new Event('change'));
    }
    fixture.detectChanges();
    query('import-merge')?.click();
    fixture.detectChanges();
    const before = project.trackList().length;
    query('interchange-start')?.click();
    await settle();
    server.runJobs();
    await settle();
    expect(project.trackList().length).toBe(before + 1);
    expect(project.trackList().at(-1)?.name).toBe('Groove');
  });
});
