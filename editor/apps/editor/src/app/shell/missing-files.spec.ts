import { TestBed } from '@angular/core/testing';
import type { AudioFile } from '@anode/api-client';
import { ItemFlag } from '@anode/timeline';
import type { FakeAnodeServer } from '../core/fake/fake-server';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { OfflineFilesStore } from '../state/offline-files.store';
import { PeaksCache } from '../state/peaks';
import { ProjectSession } from '../state/project-session';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import { buildScene } from '../tracks/scene';
import { MissingFilesDialog } from './missing-files';

const Disk = '/Users/anode/Backup';

async function openWithRegion(server: FakeAnodeServer): Promise<{ file: AudioFile; path: string }> {
  await server.createProject({ name: 'Song' });
  await server.applyCommands({ commands: [{ type: 'addTrack', stereo: false }] });
  const file = await server.importAudioFile('/audio/Kick.wav');
  const trackId = server.current?.tracks[0]?.id ?? '';
  await server.applyCommands({ commands: [{ type: 'addRegion', trackId, fileId: file.id, startTick: 0 }] });
  await server.saveProject();
  return { file, path: server.current?.path ?? '' };
}

function format(file: AudioFile, change: Partial<AudioFile> = {}) {
  const { sampleRate, channels, frames, bitDepth } = { ...file, ...change };
  return { sampleRate, channels, frames, bitDepth };
}

describe('Missing Files', () => {
  let server: FakeAnodeServer;

  beforeEach(() => {
    server = setUpFakeServer();
  });

  it('opens with a project that lacks files, and not with one that does not', async () => {
    const { file, path } = await openWithRegion(server);
    const ui = TestBed.inject(UiStore);
    const session = TestBed.inject(ProjectSession);

    await session.open(path);
    expect(ui.missingFilesOpen()).toBe(false);

    server.takeOffline(file.id);
    await server.saveProject();
    await session.open(path);

    expect(ui.missingFilesOpen()).toBe(true);
    expect(TestBed.inject(ProjectStore).offlineIds().has(file.id)).toBe(true);
    expect(TestBed.inject(ProjectStore).regionList()).toHaveLength(1);
  });

  it('finds a file by name, shows what does not match, and relinks what does', async () => {
    const { file, path } = await openWithRegion(server);
    await TestBed.inject(ProjectSession).open(path);
    server.takeOffline(file.id);
    server.placeWav(`${Disk}/old/Kick.wav`, format(file, { frames: file.frames + 1 }));
    server.placeWav(`${Disk}/Kick.wav`, format(file));
    await settle();
    const store = TestBed.inject(OfflineFilesStore);
    const project = TestBed.inject(ProjectStore);

    await store.search(Disk);

    expect(store.candidates()).toHaveLength(2);
    expect(store.matches().get(file.id)?.path).toBe(`${Disk}/Kick.wav`);

    expect(await store.relinkFound()).toBe(true);
    await settle();

    expect(project.offline().files).toEqual([]);
    expect(project.files().get(file.id)).toMatchObject({ path: 'Audio Files/Kick.wav', version: file.version + 1 });
  });

  it('refuses a file of the right name with the wrong content, and leaves the project alone', async () => {
    const { file, path } = await openWithRegion(server);
    await TestBed.inject(ProjectSession).open(path);
    server.takeOffline(file.id);
    server.placeWav(`${Disk}/Kick.wav`, format(file, { sampleRate: 44_100 }));
    await settle();
    const store = TestBed.inject(OfflineFilesStore);
    const project = TestBed.inject(ProjectStore);

    await store.search(Disk);
    const offered = project.offline().files[0];

    expect(store.matches().size).toBe(0);
    expect(offered && store.problemOf(offered)).toContain('44100 Hz');
    expect(await store.relink([{ fileId: file.id, path: `${Disk}/Kick.wav` }])).toBe(false);
    await settle();
    expect(project.offlineIds().has(file.id)).toBe(true);
    expect(project.files().get(file.id)?.path).toBe(file.path);
  });

  it('goes offline again when a relink is undone', async () => {
    const { file, path } = await openWithRegion(server);
    await TestBed.inject(ProjectSession).open(path);
    server.takeOffline(file.id);
    server.placeWav(`${Disk}/Kick.wav`, format(file));
    const project = TestBed.inject(ProjectStore);
    await server.relinkFiles({ files: [{ fileId: file.id, path: `${Disk}/Kick.wav` }] });
    await settle();
    expect(project.offline().files).toEqual([]);

    await server.undo();
    await settle();

    expect(project.offlineIds().has(file.id)).toBe(true);
    expect(project.files().get(file.id)?.path).toBe(file.path);
  });

  it('keeps an offline file through a save', async () => {
    const { file, path } = await openWithRegion(server);
    server.takeOffline(file.id);

    await server.saveProject();
    const reopened = await server.openProject(path);

    expect(reopened.offline.files.map(f => f.fileId)).toEqual([file.id]);
    expect(reopened.audioFiles.map(f => f.id)).toEqual([file.id]);
    expect(reopened.regions).toHaveLength(1);
  });

  it('asks for no peaks of an offline file', async () => {
    const { file, path } = await openWithRegion(server);
    await TestBed.inject(ProjectSession).open(path);
    server.takeOffline(file.id);
    await settle();
    const peaks = vi.spyOn(server, 'peaks');

    expect(await TestBed.inject(PeaksCache).get(file.id, 256)).toBeNull();

    expect(peaks).not.toHaveBeenCalled();
  });

  it('draws regions of offline files as offline', async () => {
    const { file, path } = await openWithRegion(server);
    await TestBed.inject(ProjectSession).open(path);
    const project = TestBed.inject(ProjectStore);
    const scene = (offline: ReadonlySet<string>) =>
      buildScene({
        tracks: project.trackList(),
        regions: project.regionList(),
        framesPerTick: 25,
        beatsPerBar: 4,
        selected: new Set(),
        waveformKey: () => 0,
        offline
      });
    const id = project.regionList()[0]?.id ?? '';

    const before = scene(new Set());
    const after = scene(new Set([file.id]));

    expect((before.scene.regions?.flags[0] ?? 0) & ItemFlag.Offline).toBe(0);
    expect((after.scene.regions?.flags[0] ?? 0) & ItemFlag.Offline).toBe(ItemFlag.Offline);
    expect(after.indexOf.has(id)).toBe(true);
    expect(after.scene.regions?.names[0]).toContain('(offline)');
  });

  it('lists the files and closes when the last is relinked', async () => {
    const { file, path } = await openWithRegion(server);
    await TestBed.inject(ProjectSession).open(path);
    server.takeOffline(file.id);
    server.placeWav(`${path}/Moved/Kick.wav`, format(file));
    await settle();
    const ui = TestBed.inject(UiStore);
    ui.missingFilesOpen.set(true);
    const fixture = TestBed.createComponent(MissingFilesDialog);
    fixture.detectChanges();
    await fixture.whenStable();
    const root = document.body;
    const get = (id: string) => root.querySelector<HTMLElement>(`[data-testid="${id}"]`);

    expect(get('missing-file')?.textContent).toContain('Kick');
    expect(get('missing-file')?.textContent).toContain('Used by 1 region');

    get('missing-search')?.click();
    await settle();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(get('missing-match')?.textContent).toContain('Moved/Kick.wav');

    get('missing-relink-all')?.click();
    await settle();
    fixture.detectChanges();

    expect(ui.missingFilesOpen()).toBe(false);
    fixture.destroy();
  });
});
