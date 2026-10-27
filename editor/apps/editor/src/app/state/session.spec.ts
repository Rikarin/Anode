import { TestBed } from '@angular/core/testing';
import { ApiError } from '@anode/api-client';
import { encodePeaks } from '@anode/timeline';
import { ANODE_API } from '../core/api';
import { silentWav } from '../core/fake/fake-audio';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { AudioImport, addedTrackId, isWav } from '../tracks/audio-import';
import { HistoryStore } from './history.store';
import { MasterMeter, MeterBus } from './meters';
import { MixerStore } from './mixer.store';
import { PeaksCache } from './peaks';
import { ProjectSession } from './project-session';
import { ProjectStore } from './project.store';
import { SelectionStore } from './selection.store';

describe('ProjectSession', () => {
  it('creates, saves, closes and reopens a project from the recent list', async () => {
    setUpFakeServer();
    const session = TestBed.inject(ProjectSession);
    const project = TestBed.inject(ProjectStore);

    expect(await session.create({ name: 'Song', directory: '/music' })).toBe(true);
    expect(project.info()?.path).toBe('/music/Song.anode');
    await project.execute([{ type: 'addTrack', stereo: false }], 'New Track');
    expect(project.dirty()).toBe(true);

    expect(await session.save()).toBe(true);
    expect(project.dirty()).toBe(false);
    expect(await session.close()).toBe(true);
    expect(project.isOpen()).toBe(false);
    await settle();
    expect(session.recent().map(p => p.name)).toEqual(['Song']);

    expect(await session.open('/music/Song.anode')).toBe(true);
    expect(project.trackIds()).toHaveLength(1);
  });

  it('asks before dropping unsaved edits, and keeps them when told no', async () => {
    setUpFakeServer();
    const session = TestBed.inject(ProjectSession);
    const project = TestBed.inject(ProjectStore);
    await session.create({ name: 'Draft' });
    await project.execute([{ type: 'addTrack', stereo: false }], 'New Track');
    const asked: string[] = [];
    session.confirm = message => {
      asked.push(message);
      return false;
    };

    expect(await session.close()).toBe(false);
    expect(session.mayDiscard('quit')).toBe(false);
    expect(project.isOpen()).toBe(true);
    expect(asked[0]).toBe('“Draft” has unsaved changes. Discard them and close it?');
  });
});

describe('HistoryStore', () => {
  it('follows the revision and names what undo and redo will do', async () => {
    setUpFakeServer();
    const session = TestBed.inject(ProjectSession);
    const project = TestBed.inject(ProjectStore);
    const history = TestBed.inject(HistoryStore);
    await session.create({ name: 'History' });
    await project.execute([{ type: 'addTrack', stereo: false }], 'New Track');
    TestBed.tick();
    await settle();
    expect(history.undoLabel()).toBe('New Track');
    expect(history.canRedo()).toBe(false);

    await history.undo();
    TestBed.tick();
    await settle();
    expect(history.canUndo()).toBe(false);
    expect(history.redoLabel()).toBe('New Track');
  });
});

describe('MixerStore', () => {
  it('shows a dragged value at once and sends at most one setStrip at a time per control', async () => {
    const server = setUpFakeServer();
    const session = TestBed.inject(ProjectSession);
    const project = TestBed.inject(ProjectStore);
    const mixer = TestBed.inject(MixerStore);
    await session.create({ name: 'Mix' });
    await project.execute([{ type: 'addTrack', stereo: false }], 'New Track');
    const trackId = project.trackIds()[0] ?? '';
    const apply = vi.spyOn(server, 'applyCommands');

    void mixer.set(trackId, 'volumeDb', -1);
    void mixer.set(trackId, 'volumeDb', -2);
    void mixer.set(trackId, 'volumeDb', -3);
    expect(mixer.value(trackId, 'volumeDb')).toBe(-3);
    await settle();

    // The first value went at once; -2 was replaced by -3 while it was in flight.
    expect(apply.mock.calls.map(([batch]) => batch.commands[0])).toEqual([
      { type: 'setStrip', trackId, volumeDb: -1 },
      { type: 'setStrip', trackId, volumeDb: -3 }
    ]);
    expect(project.track(trackId)?.strip.volumeDb).toBe(-3);

    mixer.toggleMute(null);
    mixer.toggleSolo(trackId);
    await settle();
    expect(project.master().mute).toBe(true);
    expect(project.track(trackId)?.strip.solo).toBe(true);
  });

  it('keeps meter levels outside signals, per track and for the master', () => {
    setUpFakeServer();
    const meters = TestBed.inject(MeterBus);
    meters.apply([{ trackId: 'trk_1', peakLeft: 0.5, peakRight: 0.25 }], { trackId: '', peakLeft: 1, peakRight: 1 });
    expect(meters.level('trk_1')).toEqual({ left: 0.5, right: 0.25 });
    expect(meters.level(MasterMeter).left).toBe(1);
    expect(meters.level('trk_unknown')).toEqual({ left: 0, right: 0 });
  });
});

describe('PeaksCache', () => {
  it('fetches each file and level once, and retries peaks that are not ready yet', async () => {
    const peaks = vi.fn<(fileId: string, framesPerPeak: number) => Promise<ArrayBuffer>>();
    peaks.mockRejectedValueOnce(new ApiError(404, 'Not yet.'));
    peaks.mockResolvedValue(encodePeaks({ channels: 1, framesPerPeak: 256, firstPeak: 0, data: new Int16Array(8) }));
    TestBed.configureTestingModule({ providers: [{ provide: ANODE_API, useValue: { peaks } }] });
    const cache = TestBed.inject(PeaksCache);
    cache.delay = () => Promise.resolve();

    const [a, b] = await Promise.all([cache.get('aud_1', 256), cache.get('aud_1', 256)]);

    expect(a?.peakCount).toBe(4);
    expect(b).toBe(a);
    expect(peaks).toHaveBeenCalledTimes(2); // one 404, one success, shared by both requests
    expect(cache.levelFor(5000)).toBe(4096);
  });

  it('gives up on other errors without caching the failure', async () => {
    const peaks = vi.fn().mockRejectedValue(new ApiError(500, 'Broken.'));
    TestBed.configureTestingModule({ providers: [{ provide: ANODE_API, useValue: { peaks } }] });
    const cache = TestBed.inject(PeaksCache);
    expect(await cache.get('aud_1', 64)).toBeNull();
    await settle();
    expect(cache.peek('aud_1', 64)).toBeUndefined();
  });
});

describe('AudioImport', () => {
  it('uploads dropped WAVs in a browser and places them one after the other on the track', async () => {
    const server = setUpFakeServer();
    const session = TestBed.inject(ProjectSession);
    const project = TestBed.inject(ProjectStore);
    await session.create({ name: 'Import' });
    await project.execute([{ type: 'addTrack', stereo: false }], 'New Track');
    const trackId = project.trackIds()[0] ?? '';
    const files = [new File([silentWav(48_000)], 'a.wav'), new File([silentWav(24_000)], 'b.wav')];

    await TestBed.inject(AudioImport).importFiles(files, trackId, 960);

    const regions = project.regionList().sort((x, y) => x.startTick - y.startTick);
    expect(regions.map(r => [r.name, r.startTick])).toEqual([
      ['a', 960],
      ['b', 960 + 1920]
    ]);
    expect((await server.history()).undo[0]?.label).toBe('Add 2 Regions');
  });

  it('makes a track for a drop below the last one', async () => {
    setUpFakeServer();
    const session = TestBed.inject(ProjectSession);
    const project = TestBed.inject(ProjectStore);
    await session.create({ name: 'New track' });

    await TestBed.inject(AudioImport).importFiles([new File([silentWav(4_800)], 'Kick.wav')], null, 0);

    expect(project.trackList().map(t => t.name)).toEqual(['Kick']);
    expect(project.regionList()).toHaveLength(1);
    expect(isWav({ name: 'x.WAV' })).toBe(true);
    expect(isWav({ name: 'x.mp3', type: 'audio/mpeg' })).toBe(false);
    expect(addedTrackId(null, new Set())).toBeNull();
  });
});

describe('SelectionStore', () => {
  it('drops regions that leave the project', () => {
    setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    const selection = TestBed.inject(SelectionStore);
    selection.select(['rgn_gone']);
    selection.toggle('rgn_other');
    expect(selection.regions().size).toBe(2);
    project.regions.set(new Map());
    TestBed.tick();
    expect(selection.hasRegions()).toBe(false);
  });
});
