import { TestBed } from '@angular/core/testing';
import type { FakeAnodeServer } from '../core/fake/fake-server';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { Notify } from '../core/notify';
import { ProjectSession } from './project-session';
import { ProjectStore } from './project.store';

const Path = '/music/Song.anode';

describe('Save As and Save a Copy', () => {
  async function saved(): Promise<{ server: FakeAnodeServer; session: ProjectSession; project: ProjectStore }> {
    const server = setUpFakeServer();
    const session = TestBed.inject(ProjectSession);
    const project = TestBed.inject(ProjectStore);
    await session.create({ name: 'Song', directory: '/music' });
    await session.save();
    await project.execute([{ type: 'addTrack', name: 'Unsaved', stereo: false }], 'New Track');
    return { server, session, project };
  }

  /** Runs what is queued while `action` waits for its job to end. */
  async function withJobs(server: FakeAnodeServer, action: Promise<boolean>): Promise<boolean> {
    await settle();
    server.runJobs(100);
    await settle();
    return action;
  }

  it('goes on in the new package, with its edits saved and the original as it was', async () => {
    const { server, session, project } = await saved();

    expect(await withJobs(server, session.saveAs('/music/Second'))).toBe(true);

    await settle();
    expect(project.info()?.name).toBe('Second');
    expect(project.info()?.path).toBe('/music/Second.anode');
    expect(project.dirty()).toBe(false);
    expect([...project.tracks().values()].map(t => t.name)).toEqual(['Unsaved']);
    expect(session.recent().map(p => p.path)).toContain('/music/Second.anode');
    expect(session.recent().map(p => p.path)).toContain(Path);

    await session.open(Path);
    await settle();
    expect(project.tracks().size).toBe(0);
  });

  it('keeps undo across Save As', async () => {
    const { server, session, project } = await saved();

    await withJobs(server, session.saveAs('/music/Second.anode'));
    await settle();
    await project.undo();

    expect(project.info()?.path).toBe('/music/Second.anode');
    expect(project.tracks().size).toBe(0);
  });

  it('writes a copy and stays where it was, unsaved edits and all', async () => {
    const { server, session, project } = await saved();
    const id = project.info()?.id;

    expect(await withJobs(server, session.saveCopy('/music/Copy'))).toBe(true);

    await settle();
    expect(project.info()?.path).toBe(Path);
    expect(project.info()?.id).toBe(id);
    expect(project.dirty()).toBe(true);
    await project.undo();
    await session.save();
    await session.open('/music/Copy.anode');
    await settle();
    expect([...project.tracks().values()].map(t => t.name)).toEqual(['Unsaved']);
  });

  it('refuses a place that is taken, and says so', async () => {
    const { session, project } = await saved();
    const error = vi.spyOn(TestBed.inject(Notify), 'error');

    expect(await session.saveAs(Path)).toBe(false);

    expect(error).toHaveBeenCalled();
    expect(project.info()?.path).toBe(Path);
    expect(project.dirty()).toBe(true);
  });

  it('moves no snapshot: the original offers nothing after Save As, and autosaves go to the new package', async () => {
    const { server, session, project } = await saved();
    expect(server.autosave()).toBe(true);

    await withJobs(server, session.saveAs('/music/Second'));
    await settle();
    await session.refreshRecoverable();
    expect(session.recoverable()).toEqual([]);

    await project.execute([{ type: 'addTrack', name: 'After', stereo: false }], 'New Track');
    expect(server.autosave()).toBe(true);
    server.crash();
    await settle();
    await session.refreshRecoverable();
    expect(session.recoverable().map(p => p.path)).toEqual(['/music/Second.anode']);
  });

  it('reports a failed job and stays where it was', async () => {
    const { server, session, project } = await saved();
    server.failNextJob('The disk is full.');
    const error = vi.spyOn(TestBed.inject(Notify), 'error');

    expect(await withJobs(server, session.saveAs('/music/Second'))).toBe(false);

    await settle();
    expect(error).toHaveBeenCalledWith(expect.stringContaining('The disk is full.'));
    expect(project.info()?.path).toBe(Path);
    expect(project.dirty()).toBe(true);
    await session.open('/music/Second.anode');
    expect(project.info()?.path).toBe(Path);
  });
});
