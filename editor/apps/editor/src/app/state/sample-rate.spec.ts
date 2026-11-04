import type { Project } from '@anode/api-client';
import { TestBed } from '@angular/core/testing';
import { ANODE_API } from '../core/api';
import type { FakeAnodeServer } from '../core/fake/fake-server';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { Notify } from '../core/notify';
import { ProjectSession } from './project-session';
import { ProjectStore } from './project.store';

describe('Changing the sample rate', () => {
  async function demo(): Promise<{ server: FakeAnodeServer; session: ProjectSession; project: ProjectStore }> {
    const server = setUpFakeServer({ demo: true });
    const session = TestBed.inject(ProjectSession);
    const project = TestBed.inject(ProjectStore);
    project.load(server.current as Project);
    await session.save();
    return { server, session, project };
  }

  async function withJobs(server: FakeAnodeServer, action: Promise<boolean>): Promise<boolean> {
    await settle();
    server.runJobs(100);
    await settle();
    return action;
  }

  it('converts: frames scale, files are new ones, ticks stay, and it reopens saved without undo', async () => {
    const { server, session, project } = await demo();
    const rate = project.info()?.sampleRate ?? 0;
    const target = rate === 48_000 ? 96_000 : 48_000;
    const ratio = target / rate;
    const region = [...project.regions().values()][0];
    const file = [...project.files().values()][0];
    await project.execute([{ type: 'addTrack', name: 'Unsaved', stereo: false }], 'New Track');

    expect(await withJobs(server, session.changeSampleRate(target, 'convert'))).toBe(true);

    await settle();
    expect(project.info()?.sampleRate).toBe(target);
    expect(project.dirty()).toBe(false);
    const moved = project.regions().get(region?.id ?? '');
    expect(moved?.length).toBe(Math.round((region?.length ?? 0) * ratio));
    expect(moved?.startTick).toBe(region?.startTick);
    expect(moved?.fileId).not.toBe(region?.fileId);
    expect(project.files().get(moved?.fileId ?? '')?.sampleRate).toBe(target);
    expect(project.files().has(file?.id ?? '')).toBe(false);
    expect((await TestBed.inject(ANODE_API).history()).undo).toEqual([]);
  });

  it('keeps the samples: frames stay and the tempo moves with the rate', async () => {
    const { server, session, project } = await demo();
    const rate = project.info()?.sampleRate ?? 0;
    const target = rate === 48_000 ? 96_000 : 48_000;
    const region = [...project.regions().values()][0];
    const tempo = project.settings().tempo;

    await withJobs(server, session.changeSampleRate(target, 'keepSamples'));

    await settle();
    expect(project.regions().get(region?.id ?? '')?.length).toBe(region?.length);
    expect(project.settings().tempo).toBeCloseTo((tempo * target) / rate, 6);
  });

  it('keeps a backup that goes back to the old rate', async () => {
    const { server, session, project } = await demo();
    const rate = project.info()?.sampleRate ?? 0;
    const target = rate === 48_000 ? 96_000 : 48_000;

    await withJobs(server, session.changeSampleRate(target, 'convert'));
    await settle();
    await session.refreshVersions();
    const backup = session.backups()[0];
    expect(backup).toBeDefined();

    await session.revertToBackup(backup?.id ?? '');
    await settle();
    expect(project.info()?.sampleRate).toBe(rate);
  });

  it('refuses while files are offline, says why, and changes nothing', async () => {
    const server = setUpFakeServer({ demo: true, offline: true });
    const session = TestBed.inject(ProjectSession);
    const project = TestBed.inject(ProjectStore);
    project.load(server.current as Project);
    const rate = project.info()?.sampleRate ?? 0;
    const error = vi.spyOn(TestBed.inject(Notify), 'error');

    expect(await session.changeSampleRate(rate === 48_000 ? 96_000 : 48_000, 'convert')).toBe(false);

    expect(error).toHaveBeenCalledWith(expect.stringContaining('offline'));
    expect(project.info()?.sampleRate).toBe(rate);
    expect(server).toBeDefined();
  });

  it('leaves the project as it was when the job fails', async () => {
    const { server, session, project } = await demo();
    const rate = project.info()?.sampleRate ?? 0;
    await project.execute([{ type: 'addTrack', name: 'Unsaved', stereo: false }], 'New Track');
    server.failNextJob('The converter gave up.');

    expect(await withJobs(server, session.changeSampleRate(rate === 48_000 ? 96_000 : 48_000, 'convert'))).toBe(false);

    await settle();
    expect(project.info()?.sampleRate).toBe(rate);
    expect(project.dirty()).toBe(true);
    expect((await TestBed.inject(ANODE_API).history()).undo.length).toBeGreaterThan(0);
  });
});
