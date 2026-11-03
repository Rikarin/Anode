import { TestBed } from '@angular/core/testing';
import type { Movie } from '@anode/api-client';
import { ConnectionService } from '../core/connection';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { MarkerList } from '../lists/marker-list';
import { ProjectStore } from '../state/project.store';
import { TransportStore } from '../state/transport.store';
import { UiStore } from '../state/ui.store';
import { MovieMedia } from './media-urls';
import { MovieDialogHost } from './movie-dialog';
import { MovieLane } from './movie-lane';
import { MovieSettingsWindow } from './movie-settings-window';
import { MovieThumbnailCache, ThumbnailHeight } from './movie-thumbnails';
import { MovieWindow } from './movie-window';

const Bar = 3840;

const picture: Movie = {
  path: 'Movies/Picture.mov',
  name: 'Picture',
  inProject: true,
  startTick: Bar,
  offsetSeconds: 0,
  frameRate: { numerator: 25, denominator: 1 },
  timecodeStartSeconds: 3600,
  durationSeconds: 60,
  frames: 1500,
  width: 1280,
  height: 720,
  codec: 'h264',
  audio: { codec: 'aac', channels: 2, sampleRate: 48_000 }
};

async function setUp(movie: Movie | null = picture) {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Film' }));
  if (movie) {
    await project.execute([{ type: 'setMovie', movie }], 'Movie');
    await settle();
  }
  const query = (testId: string, root: ParentNode = document) =>
    root.querySelector<HTMLElement>(`[data-testid="${testId}"]`);
  return { server, project, query, ui: TestBed.inject(UiStore), transport: TestBed.inject(TransportStore) };
}

function type(element: HTMLElement | null, value: string, event: 'input' | 'change' = 'input'): void {
  const input = element as HTMLInputElement | null;
  if (input) {
    input.value = value;
    input.dispatchEvent(new Event(event));
  }
}

describe('the movie (Phase 10, docs/11-phase-10.md)', () => {
  it('opens a movie from a path on the Server, as a job, and shows it in the movie window', async () => {
    const { server, project, query, ui } = await setUp(null);
    ui.movieDialog.set('open');
    const fixture = TestBed.createComponent(MovieDialogHost);
    await fixture.whenStable();
    expect((query('movie-start-job') as HTMLButtonElement).disabled).toBe(true);
    type(query('movie-path-field'), '/Users/anode/Movies/Trailer.mp4');
    fixture.detectChanges();
    query('movie-start-job')?.click();
    await settle();
    server.runJobs();
    await settle();
    fixture.detectChanges();
    expect(project.movie()).toMatchObject({ name: 'Trailer', path: 'Movies/Trailer.mp4', startTick: 0 });
    expect(ui.movieDialog()).toBeNull();
    expect(ui.movieWindowOpen()).toBe(true);
  });

  it('exports audio to a movie in its container, and detects scene cuts as scene markers', async () => {
    const { server, project, query, ui } = await setUp({ ...picture, path: 'Movies/Picture.mp4' });
    ui.movieDialog.set('exportAudio');
    const fixture = TestBed.createComponent(MovieDialogHost);
    await fixture.whenStable();
    expect(query('movie-export-file')?.textContent).toContain('Picture with Mix.mp4');
    // PCM goes in a MOV only: an MP4 offers AAC.
    expect(query('movie-export-codec')?.textContent).not.toContain('PCM');
    query('movie-start-job')?.click();
    await settle();
    server.runJobs();
    await settle();
    expect(ui.movieDialog()).toBeNull();

    ui.movieDialog.set('detectScenes');
    fixture.detectChanges();
    await fixture.whenStable();
    query('movie-start-job')?.click();
    await settle();
    server.runJobs();
    await settle();
    const scenes = project.markers().filter(m => m.kind === 'scene');
    expect(scenes.length).toBeGreaterThan(0);
    expect(scenes[0]?.name).toBe('Scene 1');

    // The Marker List marks them.
    const list = TestBed.createComponent(MarkerList);
    await list.whenStable();
    expect(query('marker-row-Scene 1', list.nativeElement as HTMLElement)?.dataset['kind']).toBe('scene');
  });

  it('shows the playhead frame and its timecode, after locates (the fake Server shows frame images)', async () => {
    const { project, query, transport } = await setUp();
    const fixture = TestBed.createComponent(MovieWindow);
    await fixture.whenStable();
    const window = fixture.componentInstance;
    const root = fixture.nativeElement as HTMLElement;
    // No stream (the fake Server): the frame's image instead of a video.
    expect(query('movie-video', root)).toBeNull();
    transport.locate(Bar + 960); // half a second into the movie
    window.step();
    fixture.detectChanges();
    expect(query('movie-frame', root)?.textContent).toBe('12');
    expect(query('movie-timecode', root)?.textContent).toBe('01:00:00:12');
    expect((query('movie-frame-image', root) as HTMLImageElement | null)?.src).toMatch(/^data:image\/svg\+xml/);
    transport.locate(0);
    window.step();
    fixture.detectChanges();
    expect(query('movie-outside', root)?.textContent).toContain('Before the movie');
    expect(project.movie()?.name).toBe('Picture');
  });

  it('moves the movie in Movie Settings', async () => {
    const { project, query } = await setUp();
    const fixture = TestBed.createComponent(MovieSettingsWindow);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(query('movie-frame-rate', root)?.textContent).toContain('25 fps');
    expect(query('movie-timecode-start', root)?.textContent).toBe('01:00:00:00');
    type(query('movie-start', root), '3 1 1 1', 'change');
    await settle();
    expect(project.movie()?.startTick).toBe(2 * Bar);
    type(query('movie-offset', root), '00:00:01:12', 'change');
    await settle();
    expect(project.movie()?.offsetSeconds).toBeCloseTo(1.48);
    type(query('movie-offset', root), '600', 'change');
    await settle();
    fixture.detectChanges();
    expect(root.querySelector('[role="alert"]')?.textContent).toContain('inside the movie');
    expect(project.movie()?.offsetSeconds).toBeCloseTo(1.48);
  });

  it('asks the Server for the thumbnails the movie track shows, in batches, and shows them when cached', async () => {
    const { server, query } = await setUp();
    const cache = TestBed.inject(MovieThumbnailCache);
    const fixture = TestBed.createComponent(MovieLane);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('[data-pending="true"]').length).toBeGreaterThan(0);
    await cache.flush();
    await settle();
    server.runJobs();
    await settle();
    fixture.detectChanges();
    const shown = [...root.querySelectorAll<HTMLImageElement>('img[data-frame]')];
    expect(shown.length).toBeGreaterThan(0);
    expect(shown[0]?.dataset['frame']).toBe('0');
    expect((await server.movieThumbnails(ThumbnailHeight)).frames).toContain(0);
    expect(query('movie-lane')).not.toBeNull();
    // Media URLs: the fake Server's frames, and no stream.
    const media = TestBed.inject(MovieMedia);
    expect(media.streamUrl('x')).toBeNull();
    expect(media.frameUrl(3, 64)).toMatch(/^data:image/);
  });
});

describe('the movie media URLs on a real Server', () => {
  it('carries the token as access_token, for the desktop app and LAN browsers alike', () => {
    TestBed.inject(ConnectionService).connection.set({ baseUrl: 'http://studio.local:5190', token: 'secret' });
    const media = TestBed.inject(MovieMedia);
    expect(media.streamUrl('Movies/Picture.mov')).toBe(
      'http://studio.local:5190/api/v1/project/movie/stream?v=Movies%2FPicture.mov&access_token=secret'
    );
    expect(media.frameUrl(42, 64)).toBe(
      'http://studio.local:5190/api/v1/project/movie/frames/42?height=64&access_token=secret'
    );
  });
});
