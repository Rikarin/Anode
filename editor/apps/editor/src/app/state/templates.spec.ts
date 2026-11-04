import { TestBed } from '@angular/core/testing';
import type { FakeAnodeServer } from '../core/fake/fake-server';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { Notify } from '../core/notify';
import { ProjectChooser } from '../pages/project-chooser';
import { ProjectSession } from './project-session';
import { ProjectStore } from './project.store';
import { SelectionStore } from './selection.store';
import { TemplatesStore } from './templates.store';

describe('Templates', () => {
  function stores(): {
    server: FakeAnodeServer;
    session: ProjectSession;
    project: ProjectStore;
    templates: TemplatesStore;
  } {
    const server = setUpFakeServer();
    return {
      server,
      session: TestBed.inject(ProjectSession),
      project: TestBed.inject(ProjectStore),
      templates: TestBed.inject(TemplatesStore)
    };
  }

  async function withJobs(server: FakeAnodeServer, action: Promise<boolean>): Promise<boolean> {
    await settle();
    server.runJobs(100);
    await settle();
    return action;
  }

  const names = (project: ProjectStore): string[] => [...project.tracks().values()].map(t => t.name);

  it('lists the factory templates by category and makes a project from one', async () => {
    const { session, project, templates } = stores();

    await templates.refresh();

    expect(templates.projectTemplates().map(t => t.id)).toContain('factory-songwriter');
    expect(templates.projectCategories().map(c => c.category)).toContain('Songwriting');
    await session.create({ name: 'Song', directory: '/music', template: 'factory-songwriter' });
    await settle();
    expect(names(project)).toEqual(expect.arrayContaining(['Vocal', 'Guitar', 'Keys', 'Bass', 'Reverb']));
  });

  it('shows templates in the project chooser and creates from the chosen one', async () => {
    const { project } = stores();
    const fixture = TestBed.createComponent(ProjectChooser);
    await settle();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;

    element.querySelector<HTMLButtonElement>('[data-testid="template-Voiceover"]')?.click();
    await fixture.whenStable();
    element.querySelector<HTMLButtonElement>('[data-testid="create-project"]')?.click();
    await settle();

    expect(names(project)).toEqual(expect.arrayContaining(['Voice', 'Room Tone']));
  });

  it('saves the project as a template and starts a new project from it', async () => {
    const { server, session, project, templates } = stores();
    await session.create({ name: 'Song', directory: '/music' });
    await project.execute([{ type: 'addTrack', name: 'Mine', stereo: false }], 'New Track');

    expect(await withJobs(server, templates.saveProject('My Setup', 'Band'))).toBe(true);

    expect(project.dirty()).toBe(true);
    const saved = templates.projectTemplates().find(t => !t.factory);
    expect(saved?.name).toBe('My Setup');
    await project.undo();
    await session.save();
    await session.create({ name: 'Next', directory: '/music', template: saved?.id ?? null });
    await settle();
    expect(project.info()?.name).toBe('Next');
    expect(names(project)).toEqual(['Mine']);

    await templates.deleteProject(saved?.id ?? '');
    expect(templates.projectTemplates().some(t => !t.factory)).toBe(false);
  });

  it('saves selected tracks with their aux and adds them as one undo step, on a bus of their own', async () => {
    const { server, session, project, templates } = stores();
    await session.create({ name: 'Song', directory: '/music' });
    await project.execute(
      [
        { type: 'addTrack', name: 'Lead', stereo: false },
        { type: 'addAux', bus: 1, name: 'Plate' }
      ],
      'Tracks'
    );
    const lead = [...project.tracks().keys()][0] ?? '';
    await project.execute([{ type: 'addSend', trackId: lead, bus: 1 }], 'Send');
    TestBed.inject(SelectionStore).tracks.set(new Set([lead]));

    expect(await templates.saveTracks('Chain', '')).toBe(true);
    const saved = templates.trackTemplates()[0];
    expect(saved?.tracks).toBe(2);

    await session.save();
    await session.create({ name: 'Other', directory: '/music' });
    await project.execute([{ type: 'addAux', bus: 1, name: 'Hall' }], 'Aux');
    expect(await withJobs(server, templates.apply(saved?.id ?? ''))).toBe(true);

    await settle();
    expect(names(project)).toEqual(['Hall', 'Lead', 'Plate']);
    const tracks = [...project.tracks().values()];
    expect(tracks[1]?.strip.sends[0]?.bus).toBe(tracks[2]?.inputBus);
    expect(tracks[2]?.inputBus).not.toBe(1);
    await project.undo();
    expect(names(project)).toEqual(['Hall']);
  });

  it('reports a failed save and lists nothing', async () => {
    const { server, session, templates } = stores();
    await session.create({ name: 'Song', directory: '/music' });
    server.failNextJob('The disk is full.');
    const error = vi.spyOn(TestBed.inject(Notify), 'error');

    expect(await withJobs(server, templates.saveProject('Nope', ''))).toBe(false);

    expect(error).toHaveBeenCalledWith(expect.stringContaining('The disk is full.'));
    await templates.refresh();
    expect(templates.projectTemplates().some(t => !t.factory)).toBe(false);
  });

  it('refuses a rate a template with audio cannot take', async () => {
    const { server, session, project } = stores();
    await session.create({ name: 'Song', directory: '/music', sampleRate: 48_000 });
    server.addFakeAudio();
    const templates = TestBed.inject(TemplatesStore);
    await withJobs(server, templates.saveProject('With Audio', ''));
    const saved = templates.projectTemplates().find(t => !t.factory);
    expect(saved?.hasAudio).toBe(true);

    expect(
      await session.create({ name: 'Other', directory: '/music', sampleRate: 44_100, template: saved?.id ?? null })
    ).toBe(false);
  });
});
