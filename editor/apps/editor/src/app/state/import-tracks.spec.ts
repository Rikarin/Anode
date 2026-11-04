import { TestBed } from '@angular/core/testing';
import type { FakeAnodeServer } from '../core/fake/fake-server';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { Notify } from '../core/notify';
import { ProjectSession } from './project-session';
import { ProjectStore } from './project.store';

const Source = '/music/Source.anode';
const Bar = 3_840;

describe('Import from Project', () => {
  /** A saved source with an audio-less track, a send to an aux and a lane, and an open target with its own aux on bus 1. */
  async function projects(): Promise<{ server: FakeAnodeServer; session: ProjectSession; project: ProjectStore }> {
    const server = setUpFakeServer();
    const session = TestBed.inject(ProjectSession);
    const project = TestBed.inject(ProjectStore);
    await session.create({ name: 'Source', directory: '/music' });
    await project.execute(
      [
        { type: 'addTrack', name: 'Gtr', stereo: false },
        { type: 'addAux', bus: 1, name: 'Verb' }
      ],
      'Tracks'
    );
    const [gtr] = [...project.tracks().keys()];
    await project.execute(
      [
        { type: 'addSend', trackId: gtr ?? '', bus: 1 },
        {
          type: 'setAutomation',
          trackId: gtr ?? '',
          target: 'volume',
          points: [
            { tick: 0, value: -12, curve: 0 },
            { tick: Bar, value: 0, curve: 0 }
          ]
        }
      ],
      'Routing'
    );
    await session.save();
    await session.create({ name: 'Target', directory: '/music' });
    await project.execute(
      [
        { type: 'addTrack', name: 'Vox', stereo: false },
        { type: 'addAux', bus: 1, name: 'Hall' }
      ],
      'Tracks'
    );
    return { server, session, project };
  }

  async function withJobs(server: FakeAnodeServer, action: Promise<boolean>): Promise<boolean> {
    await settle();
    server.runJobs(100);
    await settle();
    return action;
  }

  it('reads a project without opening it', async () => {
    const { session, project } = await projects();

    const contents = await session.projectContents(Source);

    expect(contents?.tracks.map(t => t.name)).toEqual(['Gtr', 'Verb']);
    expect(contents?.tracks[0]?.sends).toBe(1);
    expect(project.info()?.name).toBe('Target');
  });

  it('brings a track with its aux, on a bus of its own, as one undo step', async () => {
    const { server, session, project } = await projects();
    const contents = await session.projectContents(Source);

    const done = await withJobs(
      server,
      session.importTracks({ path: Source, trackIds: [contents?.tracks[0]?.id ?? ''] })
    );

    expect(done).toBe(true);
    await settle();
    const tracks = [...project.tracks().values()];
    expect(tracks.map(t => t.name)).toEqual(['Vox', 'Hall', 'Gtr', 'Verb']);
    expect(tracks[2]?.strip.sends[0]?.bus).toBe(tracks[3]?.inputBus);
    expect(tracks[3]?.inputBus).not.toBe(1);
    expect(project.automationList().map(l => l.trackId)).toEqual([tracks[2]?.id]);

    await project.undo();
    expect([...project.tracks().values()].map(t => t.name)).toEqual(['Vox', 'Hall']);
  });

  it('leaves out what is left out', async () => {
    const { server, session, project } = await projects();
    const contents = await session.projectContents(Source);

    await withJobs(
      server,
      session.importTracks({
        path: Source,
        trackIds: [contents?.tracks[0]?.id ?? ''],
        sends: false,
        automation: false
      })
    );

    await settle();
    const tracks = [...project.tracks().values()];
    expect(tracks.map(t => t.name)).toEqual(['Vox', 'Hall', 'Gtr']);
    expect(tracks[2]?.strip.sends).toEqual([]);
    expect(project.automationList()).toEqual([]);
  });

  it('reports a project that is not there, and a job that fails, and changes nothing', async () => {
    const { server, session, project } = await projects();
    const error = vi.spyOn(TestBed.inject(Notify), 'error');

    expect(await session.projectContents('/music/Gone.anode')).toBeNull();
    expect(error).toHaveBeenCalled();

    server.failNextJob('The disk is full.');
    const contents = await session.projectContents(Source);
    const done = await withJobs(
      server,
      session.importTracks({ path: Source, trackIds: [contents?.tracks[0]?.id ?? ''] })
    );

    expect(done).toBe(false);
    expect(error).toHaveBeenCalledWith(expect.stringContaining('The disk is full.'));
    expect(project.tracks().size).toBe(2);
  });
});
