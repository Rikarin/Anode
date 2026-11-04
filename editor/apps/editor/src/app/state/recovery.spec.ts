import { TestBed } from '@angular/core/testing';
import type { FakeAnodeServer } from '../core/fake/fake-server';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { ProjectChooser } from '../pages/project-chooser';
import { ProjectSession } from './project-session';
import { ProjectStore } from './project.store';

const Path = '/music/Song.anode';

describe('Autosave and crash recovery', () => {
  /** A saved project with one edit since, autosaved, and a Server that then died. */
  async function crashed(): Promise<{ server: FakeAnodeServer; session: ProjectSession; project: ProjectStore }> {
    const server = setUpFakeServer();
    const session = TestBed.inject(ProjectSession);
    const project = TestBed.inject(ProjectStore);
    await session.create({ name: 'Song', directory: '/music' });
    await session.save();
    await project.execute([{ type: 'addTrack', stereo: false }], 'New Track');
    expect(server.autosave()).toBe(true);
    server.crash();
    await settle();
    expect(project.isOpen()).toBe(false);
    return { server, session, project };
  }

  it('notes the autosave on the unsaved edits', async () => {
    const server = setUpFakeServer();
    const session = TestBed.inject(ProjectSession);
    const project = TestBed.inject(ProjectStore);
    await session.create({ name: 'Song', directory: '/music' });
    expect(server.autosave()).toBe(false); // nothing unsaved
    await project.execute([{ type: 'addTrack', stereo: false }], 'New Track');
    expect(project.autosaved()).toBeNull();

    server.autosave();
    await settle();
    expect(project.autosaved()).not.toBeNull();
    expect(project.dirty()).toBe(true); // autosaving is not saving
  });

  it('offers the edits at the next launch and recovers them as unsaved', async () => {
    const { session, project } = await crashed();

    await session.refreshRecoverable();
    expect(session.recoverable().map(p => p.path)).toEqual([Path]);

    expect(await session.recover(Path)).toBe(true);
    expect(project.trackIds()).toHaveLength(1);
    expect(project.dirty()).toBe(true);
    expect(session.recoverable()).toEqual([]);
  });

  it('keeps the saved project as it was until the recovered edits are saved', async () => {
    const { session, project } = await crashed();

    await session.recover(Path);
    session.confirm = () => true;
    await session.close();
    await session.open(Path);

    expect(project.trackIds()).toHaveLength(0);
  });

  it('survives a second crash after recovering', async () => {
    const { server, session } = await crashed();

    await session.recover(Path);
    server.crash();
    await settle();
    await session.refreshRecoverable();

    expect(session.recoverable().map(p => p.path)).toEqual([Path]);
  });

  it('offers nothing after a save', async () => {
    const { session } = await crashed();
    await session.recover(Path);

    await session.save();
    await session.close();
    await session.refreshRecoverable();

    expect(session.recoverable()).toEqual([]);
  });

  it('asks when a project with autosaved edits is opened directly', async () => {
    const { session, project } = await crashed();
    const asked: string[] = [];
    session.confirm = message => {
      asked.push(message);
      return true;
    };

    expect(await session.open(`${Path} `)).toBe(true);

    expect(asked).toHaveLength(1);
    expect(asked[0]).toContain('“Song”');
    expect(project.trackIds()).toHaveLength(1);
    expect(project.dirty()).toBe(true);
  });

  it('keeps refused edits in a backup when the saved version is opened instead', async () => {
    const { server, session, project } = await crashed();
    session.confirm = () => false;

    await session.open(Path);

    expect(project.trackIds()).toHaveLength(0);
    expect(await server.recoverableProjects()).toEqual([]);
    await session.refreshVersions();
    expect(await session.revertToBackup(session.backups()[0]?.id ?? '')).toBe(true);
    expect(project.trackIds()).toHaveLength(1);
  });

  it('keeps discarded edits in a backup', async () => {
    const { session, project } = await crashed();
    await session.refreshRecoverable();

    expect(await session.discardRecovery(Path)).toBe(true);

    expect(session.recoverable()).toEqual([]);
    await session.open(Path);
    expect(project.trackIds()).toHaveLength(0);
    await session.refreshVersions();
    await session.revertToBackup(session.backups()[0]?.id ?? '');
    expect(project.trackIds()).toHaveLength(1);
  });

  it('closes the project when told to discard and quit, so nothing is offered next time', async () => {
    const server = setUpFakeServer();
    const session = TestBed.inject(ProjectSession);
    const project = TestBed.inject(ProjectStore);
    await session.create({ name: 'Song', directory: '/music' });
    await project.execute([{ type: 'addTrack', stereo: false }], 'New Track');

    session.confirm = () => false;
    expect(await session.mayQuit()).toBe(false);
    expect(project.isOpen()).toBe(true);

    session.confirm = () => true;
    expect(await session.mayQuit()).toBe(true);
    expect(await server.recoverableProjects()).toEqual([]);
    expect(server.requests).toContain('CloseProject');
  });

  it('lets the project chooser recover or discard', async () => {
    const { session, project } = await crashed();
    const chooser = TestBed.createComponent(ProjectChooser);
    await settle();
    chooser.detectChanges();
    const element = chooser.nativeElement as HTMLElement;

    expect(element.querySelector('[data-testid="recovery"]')?.textContent).toContain('Song');
    element.querySelector<HTMLButtonElement>('[data-testid="recover-Song"]')?.click();
    await settle();

    expect(project.trackIds()).toHaveLength(1);
    expect(session.recoverable()).toEqual([]);
    chooser.detectChanges();
    expect(element.querySelector('[data-testid="recovery"]')).toBeNull();
  });
});
