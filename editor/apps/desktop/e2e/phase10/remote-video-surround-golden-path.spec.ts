import {
  type Browser,
  type BrowserContext,
  chromium,
  devices,
  type ElectronApplication,
  type Page
} from '@playwright/test';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from '../fixtures';
import {
  amplitude,
  bestLag,
  browseAnode,
  db,
  earRender,
  earValidate,
  extractAudio,
  interaural,
  makeTestMovie,
  noise,
  onsets,
  probeStreams,
  readWav,
  rms,
  rmsPerSecond,
  sine,
  toneOf,
  videoMd5,
  writeWav
} from './helpers';

/*
 * Phase 10's exit criterion (docs/11-phase-10.md): a tablet runs the session from the live room and hears it; a movie
 * plays locked to the transport and gets its soundtrack back; a project mixes in surround and immersive formats,
 * monitors binaurally and exports ADM BWF that an independent renderer agrees with — on the null device, against the
 * real app.
 *
 * The helpers (./helpers.ts): `dns-sd` (macOS) for discovery; the media helper (ANODE_MEDIA_HELPER,
 * `scripts/media-check.mjs`: E's test movie, the pinned FFprobe and FFmpeg); the `ear` helper (ANODE_EAR_HELPER,
 * `scripts/ear-check.py`, in the venv `scripts/setup-ear.py` makes). The global setup fetches FFmpeg, makes the movie
 * and sets up `ear` when they are missing. The remote runs in a second browser (Playwright's Chromium, a phone viewport
 * with touch) against the desktop app's Server on the LAN, by the name DNS-SD gives (plain HTTP: the WebAssembly Opus
 * decoder and the script player), then on 127.0.0.1 (a secure context: WebCodecs and the AudioWorklet). Its listen-in
 * capture hook is `window.anodeListenIn` (remote/listen-in/listen-in.store.ts). Each check's measurement is an
 * annotation (`measured`) and a console line. `ANODE_E2E_KEEP=1` keeps the scratch folder.
 */

const Ppq = 960;
const Bar = 4 * Ppq;
const Rate = 48_000;

// ---- the Server's API (setting up and checking the model; the UI is what is tested)

interface Connection {
  baseUrl: string;
  token: string | null;
}

async function connection(window: Page): Promise<Connection> {
  return window.evaluate(() =>
    (window as unknown as { anode: { connection(): Promise<Connection> } }).anode.connection()
  );
}

async function api<T>(window: Page, method: 'GET' | 'POST' | 'PUT', path: string, body?: unknown): Promise<T> {
  const { baseUrl, token } = await connection(window);
  const response = await fetch(`${baseUrl.replace(/\/$/, '')}/api/v1${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? null : JSON.stringify(body)
  });
  if (!response.ok) {
    throw new Error(`${method} ${path}: ${response.status} ${await response.text()}`);
  }
  const text = await response.text(); // 204 No Content has none
  return (text ? JSON.parse(text) : undefined) as T;
}

interface ProjectShape {
  path: string;
  settings: { surroundFormat: string; tempo: number; endTick?: number };
  tracks: {
    id: string;
    name: string;
    kind: string;
    strip: { volumeDb: number; outputFormat: string; panner: { mode: string } };
  }[];
  midiRegions: { trackId: string; notes: { pitch: number }[] }[];
  regions: { trackId: string; startTick: number; fileId: string }[];
  markers: { tick: number; kind: string }[];
  liveLoops: { scenes: { id: string }[]; cells: { id: string; trackId: string; sceneId: string }[] };
  movie?: { startTick: number; offsetSeconds: number; frames: number } | null;
  automation: { trackId: string; target: string }[];
}
interface JobShape {
  id: string;
  state: string;
  files: string[];
  message?: string | null;
}

const project = (window: Page) => api<ProjectShape>(window, 'GET', '/project');
const commands = (window: Page, list: unknown[], label = 'Test') =>
  api(window, 'POST', '/project/commands', { commands: list, label });
const transport = (window: Page, command: 'play' | 'stop' | 'record' | 'locate', tick?: number) =>
  api<{ state: string; positionTick: number; positionSamples: number }>(window, 'POST', '/transport', {
    command,
    tick
  });

/** Waits for a job to finish: its result, or the reason it failed. */
async function finished(window: Page, job: JobShape): Promise<JobShape> {
  let current = job;
  await expect
    .poll(
      async () => {
        current = await api<JobShape>(window, 'GET', `/jobs/${job.id}`);
        return current.state === 'failed' || current.state === 'cancelled' ? 'done' : current.state;
      },
      { timeout: 120_000 }
    )
    .toBe('done');
  expect(current.state, current.message ?? '').toBe('done');
  return current;
}

/**
 * A multichannel bounce of the mix (one interleaved WAV in the master's layout, read back in its layout's order), of
 * the tracks given alone (a stem each: the first is read), or of what the monitoring sends the device.
 */
async function bounce(
  window: Page,
  folder: string,
  name: string,
  endTick: number,
  trackIds: string[] | null = null,
  monitoring = false
) {
  const job = await api<JobShape>(window, 'POST', '/project/bounce', {
    fileName: name,
    directory: folder,
    startTick: 0,
    endTick,
    source: trackIds ? 'stems' : 'mix',
    trackIds,
    monitoring,
    format: 'wav',
    bitDepth: 32,
    dither: 'none',
    normalize: 'off',
    loudnessLufs: -14,
    peakCeilingDb: -1,
    realtime: false
  });
  const done = await finished(window, job);
  return readWav(done.files[0] ?? '');
}

function measured(step: string, what: string): void {
  test.info().annotations.push({ type: 'measured', description: `${step}: ${what}` });
  console.warn(`measured ${step}: ${what}`);
}

// ---- driving the desktop app

async function menu(app: ElectronApplication, command: string): Promise<void> {
  await app.evaluate(({ Menu }, id) => {
    const item = Menu.getApplicationMenu()?.getMenuItemById(id);
    if (!item) {
      throw new Error(`No menu item ${id}`);
    }
    (item.click as () => void)();
  }, `anode:${command}`);
}

async function answerDialogs(app: ElectronApplication, open: string | null): Promise<void> {
  await app.evaluate(({ dialog }, file) => {
    if (file) {
      dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [file] });
    }
  }, open);
}

async function newProject(window: Page, folder: string, name: string): Promise<void> {
  await window.getByTestId('new-project-name').fill(name);
  await window.getByTestId('new-project-folder').fill(folder);
  await window.getByTestId('create-project').click();
  await expect(window.getByTestId('project-name')).toHaveText(name);
}

/** A mono audio track at bar 1 playing `samples` (a 10 s 1 kHz test tone at −12 dBFS unless given). */
async function toneTrack(
  window: Page,
  folder: string,
  name: string,
  samples: Float32Array = sine(1000, -12, 10, Rate)
): Promise<string> {
  const path = join(folder, `${name}.wav`);
  writeWav(path, samples, Rate);
  const file = await api<{ id: string; frames: number }>(window, 'POST', '/project/files/import', { path });
  await commands(window, [{ type: 'addTrack', kind: 'audio', stereo: false, name }]);
  const trackId = (await project(window)).tracks.find(t => t.name === name)?.id ?? '';
  await commands(window, [{ type: 'addRegion', trackId, fileId: file.id, startTick: 0 }]);
  return trackId;
}

// ---- the remote (a second browser: a phone in portrait, with touch)

interface Remote {
  browser: Browser;
  context: BrowserContext;
  page: Page;
  /** The address it opened (the QR code's: the token in the query, then the route). */
  url: string;
}

/** A TCP port nothing listens on. */
function freePort(): Promise<number> {
  return new Promise((resolvePort, reject) => {
    const probe = createServer();
    probe.once('error', reject);
    probe.listen(0, '0.0.0.0', () => {
      const address = probe.address();
      probe.close(() => resolvePort(typeof address === 'object' && address ? address.port : 0));
    });
  });
}

/** Turns the desktop app's network access on and opens the remote from what DNS-SD found, with the pairing token. */
async function openRemote(window: Page, projectName: string): Promise<Remote> {
  const port = await freePort();
  const status = await window.evaluate(async wanted => {
    const network = (
      window as unknown as {
        anode: { network: { apply(n: { enabled: boolean; port: number }): Promise<{ token: string; port: number }> } };
      }
    ).anode.network;
    return network.apply({ enabled: true, port: wanted });
  }, port);
  // The window reloads on the new Server: wait for it to come back.
  await expect(window.getByTestId('project-name')).toBeVisible({ timeout: 90_000 });
  const found = await browseAnode(projectName);
  expect(found.port).toBe(status.port);
  expect(found.txt['path']).toBe('/api/v1');
  measured('1 remote', `found ${found.name} at ${found.host}:${found.port}, TXT ${JSON.stringify(found.txt)}`);

  const browser = await chromium.launch();
  const context = await browser.newContext({ ...devices['iPhone 13'], hasTouch: true, isMobile: true });
  context.setDefaultTimeout(30_000); // an action that cannot happen fails rather than waiting out the test
  const page = await context.newPage();
  // Pairing as today: the token in the address (the QR code's URL), then the remote route.
  const url = `http://${found.host}:${found.port}/?token=${encodeURIComponent(status.token)}#/remote`;
  await page.goto(url);
  await expect(page.getByTestId('remote-project')).toHaveText(projectName, { timeout: 30_000 });
  return { browser, context, page, url };
}

/** Two seconds of what the remote hears (listen-in on, the transport playing), with the decoder it used. */
async function hear(
  page: Page
): Promise<{ samples: number[]; transportSample: number; sampleRate: number; decoder: string | null }> {
  await page.evaluate(() =>
    (window as unknown as { anodeListenIn: { startCapture(s: number): void } }).anodeListenIn.startCapture(2)
  );
  await page.waitForTimeout(3000);
  return page.evaluate(() => {
    const hook = (
      window as unknown as {
        anodeListenIn: {
          capture(): { samples: Float32Array; transportSample: number; sampleRate: number };
          decoder(): string | null;
        };
      }
    ).anodeListenIn;
    const c = hook.capture();
    return {
      samples: Array.from(c.samples),
      transportSample: c.transportSample,
      sampleRate: c.sampleRate,
      decoder: hook.decoder()
    };
  });
}

async function remoteTab(remote: Remote, name: string): Promise<void> {
  await remote.page.getByTestId(`remote-tab-${name}`).tap();
}

// ---- the golden path

test.describe('Phase 10: remote, video and surround', () => {
  test.setTimeout(900_000);
  test.use({ actionTimeout: 30_000 });

  test('runs the session from a remote that hears it, locks a movie to the transport and mixes in surround, binaurally and to ADM BWF', async ({
    app,
    window
  }) => {
    const folder = mkdtempSync(join(tmpdir(), 'anode-phase10-'));
    let remote: Remote | null = null;
    try {
      await newProject(window, folder, 'Live Room');
      const tone = await toneTrack(window, folder, 'Tone');
      // A Drum Machine Designer track, armed, and a Live Loops cell on the tone track.
      await commands(window, [{ type: 'addTrack', kind: 'instrument', stereo: true, name: 'Drums' }]);
      let state = await project(window);
      const drumsId = state.tracks.find(t => t.name === 'Drums')?.id ?? '';
      await commands(window, [
        { type: 'createDrumMachine', trackId: drumsId, name: 'Drums' },
        { type: 'addDrumPad', trackId: drumsId, note: 36, name: 'Kick' },
        { type: 'addDrumPad', trackId: drumsId, note: 38, name: 'Snare' },
        { type: 'addDrumPad', trackId: drumsId, note: 42, name: 'Hat' },
        { type: 'addScene' }
      ]);
      state = await project(window);
      const sceneId = state.liveLoops.scenes[0]?.id ?? '';
      const toneRegion = state.regions.find(r => r.trackId === tone);
      await commands(window, [
        {
          type: 'setCell',
          trackId: tone,
          sceneId,
          content: {
            audio: { ...toneRegion, startTick: 0 },
            midi: null,
            pattern: null
          },
          lengthTicks: Bar
        },
        { type: 'setTrack', trackId: drumsId, recordArmed: true },
        // The desktop shows the grid, so its cells' states can be seen there.
        { type: 'setLiveLoops', shown: true }
      ]);

      // ---- 1. Remote: discovery, pairing, transport, a fader, a Live Loops cell, drum pads recorded.
      // Turning network access on restarts the Server: the project is saved first and opened again on the new one.
      await api(window, 'POST', '/project/save');
      remote = await openRemote(window, 'Live Room');
      await remote.page.getByTestId('remote-play').tap();
      await expect.poll(async () => (await api<{ state: string }>(window, 'GET', '/transport')).state).toBe('playing');
      await expect(window.getByRole('button', { name: 'Stop' }).first()).toBeVisible();
      await remote.page.getByTestId('remote-play').tap();
      await expect.poll(async () => (await api<{ state: string }>(window, 'GET', '/transport')).state).toBe('stopped');

      await remoteTab(remote, 'mixer');
      const fader = remote.page.getByRole('slider', { name: 'Tone volume' });
      const box = await fader.boundingBox();
      if (!box) {
        throw new Error('No fader on the remote.');
      }
      // A touch drag down the fader: the desktop's mixer shows the new level.
      await remote.page.touchscreen.tap(box.x + box.width / 2, box.y + box.height * 0.2);
      await fader.press('ArrowDown');
      await fader.press('ArrowDown');
      await expect
        .poll(async () => (await project(window)).tracks.find(t => t.id === tone)?.strip.volumeDb)
        .toBeLessThan(0);
      // Once the edits have landed, the remote, the Server and the desktop's mixer agree.
      const serverDb = async () => (await project(window)).tracks.find(t => t.id === tone)?.strip.volumeDb ?? 0;
      await expect
        .poll(async () => Number(await fader.getAttribute('aria-valuenow')) === Math.round(await serverDb()))
        .toBe(true);
      const remoteDb = Number(await fader.getAttribute('aria-valuenow'));
      await window.getByRole('tab', { name: 'Mixer' }).click();
      await expect(window.getByRole('slider', { name: 'Tone volume' })).toHaveAttribute(
        'aria-valuenow',
        String(remoteDb)
      );
      measured('1 remote', `the remote's fader to ${remoteDb} dB: the desktop's mixer shows ${remoteDb} dB`);

      await remoteTab(remote, 'loops');
      await remote.page.getByTestId('remote-cell').first().tap();
      await transport(window, 'play', 0);
      await expect
        .poll(async () => window.getByTestId('live-loops-cell').first().getAttribute('data-state'))
        .toBe('playing');
      await transport(window, 'stop');
      // The tone track plays its arrangement again (Logic's track activation: a launched cell takes the track over).
      await commands(window, [{ type: 'setTrackActivation', trackIds: [tone], liveLoopsActive: false }]);

      // The remote's track list selects the Drum Machine Designer track: its pads come up on the Play view.
      await remoteTab(remote, 'tracks');
      await remote.page.getByRole('option', { name: /Drums/ }).first().tap();
      await remoteTab(remote, 'play');
      await remote.page.getByTestId('play-drums').tap();
      await transport(window, 'record', 0);
      // Past any count-in: the take records from bar 1.
      await expect
        .poll(async () => {
          const t = await api<{ state: string; positionTick: number }>(window, 'GET', '/transport');
          return t.state === 'recording' && t.positionTick > 0;
        })
        .toBe(true);
      for (const name of [/Kick/, /Snare/, /Kick/, /Hat/]) {
        await remote.page.getByRole('button', { name }).first().tap();
        await remote.page.waitForTimeout(250);
      }
      await transport(window, 'stop');
      const recorded = async () =>
        (await project(window)).midiRegions.filter(r => r.trackId === drumsId).flatMap(r => r.notes.map(n => n.pitch));
      await expect.poll(recorded).toHaveLength(4);
      const hits = await recorded();
      expect(hits).toEqual([36, 38, 36, 42]);
      measured('1 remote', `drum hits recorded ${JSON.stringify(hits)}`);

      // ---- 2. Listen-in: the 1 kHz tone heard on the remote, at its frequency and level, < 500 ms behind.
      // The test tone alone (the drum hits just recorded stay out of the measurement).
      await commands(window, [{ type: 'setStrip', trackId: drumsId, mute: true }]);
      await remote.page.getByTestId('listen-in').tap();
      await expect(remote.page.getByTestId('listen-in-status')).toContainText(/Listening|Playing/, { timeout: 15_000 });
      await transport(window, 'play', 0);
      const heard = await hear(remote.page);
      const now = await api<{ positionSamples: number }>(window, 'GET', '/transport');
      await transport(window, 'stop');
      const steady = Float32Array.from(heard.samples.slice(Rate / 4));
      expect(steady.length).toBeGreaterThan(Rate); // 2 s captured, the first quarter second left out
      // No dropouts: no run of silence longer than a millisecond in the tone.
      let silent = 0;
      let longestSilence = 0;
      for (const v of steady) {
        silent = Math.abs(v) < 1e-4 ? silent + 1 : 0;
        longestSilence = Math.max(longestSilence, silent);
      }
      expect(longestSilence).toBeLessThan(Rate / 1000);
      const toneHeard = toneOf(steady, heard.sampleRate);
      expect(Math.abs(toneHeard.hz - 1000)).toBeLessThan(2);
      // The tone track's fader moved in step 1: compare with its level at the fader.
      const fadedDb = -12 + ((await project(window)).tracks.find(t => t.id === tone)?.strip.volumeDb ?? 0);
      expect(Math.abs(toneHeard.peakDb - fadedDb)).toBeLessThan(1);
      const latency = await remote.page.getByTestId('listen-in-latency').textContent();
      const behindMs = Number(/(\d+)\s*ms behind/.exec(latency ?? '')?.[1] ?? NaN);
      expect(behindMs).toBeLessThan(500);
      measured(
        '2 listen-in',
        `${heard.decoder}: ${toneHeard.hz.toFixed(2)} Hz at ${toneHeard.peakDb.toFixed(2)} dBFS (expected ${fadedDb.toFixed(2)}), ` +
          `${behindMs} ms behind (${latency}); the capture began at transport sample ${heard.transportSample}, the Server at ${now.positionSamples}`
      );
      await remote.page.getByTestId('listen-in').tap();

      // The remote above opened the studio by its .local name over plain HTTP, which is no secure context: no
      // WebCodecs and no AudioWorklet, so it heard through the WebAssembly decoder and the script player. On this
      // computer (127.0.0.1, a secure context) a remote has both: the same stream, the same tone.
      expect(heard.decoder).toBe('Opus (WebAssembly)');
      const secure = await remote.context.newPage();
      await secure.goto(remote.url.replace(/\/\/[^/:]+:/, '//127.0.0.1:'));
      await expect(secure.getByTestId('remote-project')).toHaveText('Live Room', { timeout: 30_000 });
      await secure.getByTestId('listen-in').tap();
      await expect(secure.getByTestId('listen-in-status')).toContainText(/Listening|Playing/, { timeout: 15_000 });
      await transport(window, 'play', 0);
      const local = await hear(secure);
      await transport(window, 'stop');
      const localTone = toneOf(Float32Array.from(local.samples.slice(Rate / 4)), local.sampleRate);
      expect(local.decoder).toBe('Opus (WebCodecs)');
      expect(Math.abs(localTone.hz - 1000)).toBeLessThan(2);
      expect(Math.abs(localTone.peakDb - fadedDb)).toBeLessThan(1);
      measured('2 listen-in', `${local.decoder}: ${localTone.hz.toFixed(2)} Hz at ${localTone.peakDb.toFixed(2)} dBFS`);
      await secure.getByTestId('listen-in').tap();
      await secure.close();

      // ---- 3. Video: the test movie (25 fps, 12 s, cuts at frames 50, 125, 200; a beep every second).
      const moviePath = join(folder, 'Test Movie.mov');
      const movie = makeTestMovie(moviePath, 12, 25, [50, 125, 200]);
      await answerDialogs(app, moviePath);
      await menu(app, 'openMovie');
      await window.getByTestId('movie-choose').click();
      await window.getByTestId('movie-start-job').click();
      await expect(window.getByTestId('movie-window')).toBeVisible({ timeout: 60_000 });
      // The frame the movie window shows: the one presented (requestVideoFrameCallback) while playing, the one the
      // element is on when paused (a paused element presents no new frames to call back for).
      const frameShown = async () =>
        window.getByTestId('movie-video').evaluate(
          (video: HTMLVideoElement, fps: number) =>
            new Promise<number>(resolve => {
              const frameOf = (seconds: number) => Math.floor(seconds * fps + 1e-6);
              if (video.paused || video.seeking) {
                resolve(frameOf(video.currentTime));
                return;
              }
              const timeout = setTimeout(() => resolve(frameOf(video.currentTime)), 500);
              video.requestVideoFrameCallback((_, meta) => {
                clearTimeout(timeout);
                resolve(frameOf(meta.mediaTime));
              });
            }),
          movie.frameRate
        );
      // A locate, once the Server's transport is there (the movie then follows it).
      const locateTo = async (tick: number) => {
        await transport(window, 'locate', tick);
        await expect
          .poll(async () =>
            Math.abs((await api<{ positionTick: number }>(window, 'GET', '/transport')).positionTick - tick)
          )
          .toBeLessThan(2);
      };
      const expectedFrame = async () => {
        const t = await api<{ positionSamples: number }>(window, 'GET', '/transport');
        const m = (await project(window)).movie;
        // At 120 bpm with no varispeed: seconds = samples / rate; the movie's own start is its startTick.
        const startSeconds = ((m?.startTick ?? 0) / Ppq) * 0.5;
        return Math.floor((t.positionSamples / Rate - startSeconds + (m?.offsetSeconds ?? 0)) * movie.frameRate);
      };
      const located: number[] = [];
      for (const tick of [0, 3 * Ppq, 7 * Ppq + 120, 2 * Bar]) {
        await locateTo(tick);
        await expect.poll(async () => Math.abs((await frameShown()) - (await expectedFrame()))).toBeLessThanOrEqual(1);
        located.push((await frameShown()) - (await expectedFrame()));
      }
      // Playing: the frame shown lies between the playhead's frames just before and just after it was read.
      await transport(window, 'play', 0);
      const playing: number[] = [];
      for (let i = 0; i < 4; i++) {
        await window.waitForTimeout(1000);
        const before = await expectedFrame();
        const shown = await frameShown();
        const after = await expectedFrame();
        expect(shown).toBeGreaterThanOrEqual(before - 1);
        expect(shown).toBeLessThanOrEqual(after + 1);
        playing.push(shown < before ? shown - before : shown > after ? shown - after : 0);
      }
      await transport(window, 'stop');
      measured(
        '3 video',
        `the movie window's frame minus the playhead's: after locates ${located.join(', ')}; playing ${playing.join(', ')} (frames)`
      );

      // Thumbnails on the movie track; Detect Scene Cuts at the known cuts within a frame.
      const globalTracks = window.getByRole('button', { name: 'Global tracks' });
      if ((await globalTracks.getAttribute('aria-pressed')) !== 'true') {
        await globalTracks.click(); // the movie track is a global track
      }
      await expect(window.getByTestId('movie-lane').locator('img').first()).toBeVisible({ timeout: 60_000 });
      measured(
        '3 video',
        `the movie track shows ${await window.getByTestId('movie-lane').locator('img').count()} thumbnails`
      );
      await menu(app, 'detectSceneCuts');
      await window.getByTestId('movie-start-job').click();
      await expect
        .poll(async () => (await project(window)).markers.filter(m => m.kind === 'scene').length, { timeout: 60_000 })
        .toBe(3);
      const framesPerTick = movie.frameRate / (2 * Ppq); // 120 bpm: 2 beats a second
      const scenes = (await project(window)).markers
        .filter(m => m.kind === 'scene')
        .map(m => m.tick * framesPerTick)
        .sort((x, y) => x - y);
      scenes.forEach((frame, i) => expect(Math.abs(frame - (movie.sceneCutFrames[i] ?? 0))).toBeLessThanOrEqual(1));
      measured(
        '3 video',
        `scene markers at frames ${scenes.map(f => f.toFixed(2)).join(', ')} (cuts at ${movie.sceneCutFrames.join(', ')})`
      );

      // Import Audio from Movie: the beeps on their frames within a millisecond (a bounce of the new track).
      const checkBeeps = async (label: string, startSeconds: number) => {
        const before = new Set((await project(window)).tracks.map(t => t.id));
        await menu(app, 'importMovieAudio');
        const added = async () => (await project(window)).tracks.find(t => !before.has(t.id))?.id ?? null;
        await expect.poll(added, { timeout: 60_000 }).not.toBeNull();
        const audioTrack = (await added()) ?? '';
        // The new track alone (the tone track is muted too).
        const mixdown = await bounce(window, folder, `beeps-${label}`, 16 * Bar, [audioTrack]);
        const found = onsets(mixdown.channels[0] ?? new Float32Array(), mixdown.sampleRate).map(
          s => s / mixdown.sampleRate
        );
        expect(found.length).toBeGreaterThanOrEqual(movie.beepSeconds.length);
        const errors = movie.beepSeconds.map((beep, i) => Math.abs((found[i] ?? 0) - (beep + startSeconds)) * 1000);
        errors.forEach(error => expect(error).toBeLessThan(1));
        measured(
          '3 video',
          `Import Audio from Movie ${label}: ${found.length} beeps, first at ${found[0]?.toFixed(5)} s, worst ${Math.max(...errors).toFixed(3)} ms off its frame`
        );
      };
      await commands(window, [{ type: 'setStrip', trackId: tone, mute: true }]);
      await checkBeeps('at bar 1', 0);
      // With the movie's first frame at bar 2 the same holds, two seconds later.
      await commands(window, [{ type: 'setMoviePosition', startTick: Bar, offsetSeconds: 0 }]);
      const offsetLocated: number[] = [];
      for (const tick of [Bar, Bar + 3 * Ppq, 3 * Bar + 120]) {
        await locateTo(tick);
        await expect.poll(async () => Math.abs((await frameShown()) - (await expectedFrame()))).toBeLessThanOrEqual(1);
        offsetLocated.push((await frameShown()) - (await expectedFrame()));
      }
      await transport(window, 'play', Bar);
      await window.waitForTimeout(1500);
      const offsetBefore = await expectedFrame();
      const offsetShown = await frameShown();
      const offsetAfter = await expectedFrame();
      await transport(window, 'stop');
      expect(offsetShown).toBeGreaterThanOrEqual(offsetBefore - 1);
      expect(offsetShown).toBeLessThanOrEqual(offsetAfter + 1);
      measured(
        '3 video',
        `with the first frame at bar 2: frame minus playhead after locates ${offsetLocated.join(', ')}; playing ${offsetShown} in [${offsetBefore}, ${offsetAfter}]`
      );
      await checkBeeps('at bar 2', 2);
      // Detect Scene Cuts again: the markers move with the movie (replacing the first ones), still within a frame.
      await menu(app, 'detectSceneCuts');
      await window.getByTestId('movie-start-job').click();
      const offsetScenesOf = async () =>
        (await project(window)).markers
          .filter(m => m.kind === 'scene')
          .map(m => (m.tick - Bar) * framesPerTick)
          .sort((x, y) => x - y);
      // The job replaces the markers: wait for the moved ones (those of the first detection sit 50 frames earlier).
      await expect
        .poll(
          async () =>
            (await offsetScenesOf()).map((frame, i) => Math.abs(frame - (movie.sceneCutFrames[i] ?? 0)) <= 1).join(),
          { timeout: 60_000 }
        )
        .toBe('true,true,true');
      const offsetScenes = await offsetScenesOf();
      measured(
        '3 video',
        `with the first frame at bar 2: scene markers at movie frames ${offsetScenes.map(f => f.toFixed(2)).join(', ')}`
      );

      // Export Audio to Movie: the bounce aligned to the frames, the video stream copied.
      await menu(app, 'exportMovieAudio');
      await window.getByTestId('movie-export-name').fill('Test Movie with Mix');
      await window.getByTestId('movie-export-folder').fill(folder);
      await window.getByTestId('movie-start-job').click();
      const exported = join(folder, 'Test Movie with Mix.mov');
      // The job writes the file when the remux is done: then it has the video and the new audio.
      await expect.poll(() => existsSync(exported), { timeout: 120_000 }).toBe(true);
      await expect
        .poll(() => (existsSync(exported) ? probeStreams(exported).length : 0), { timeout: 30_000 })
        .toBeGreaterThan(1);
      expect(videoMd5(exported)).toBe(videoMd5(moviePath));
      const soundtrack = extractAudio(exported, join(folder, 'soundtrack.wav'));
      const reference = await bounce(window, folder, 'reference', Bar + 12 * 2 * Ppq);
      const offset = Math.round(2 * Rate); // the movie starts at bar 2
      const lag = bestLag(
        (reference.channels[0] ?? new Float32Array()).subarray(offset),
        soundtrack.channels[0] ?? new Float32Array(),
        Rate / 100
      );
      expect(Math.abs(lag / Rate) * 1000).toBeLessThan(1);
      measured('3 video', `exported soundtrack ${lag} samples from the bounce; video packets unchanged`);

      // ---- 4. Surround: 7.1.4; a mono track's puck at Ls (the side surround, M+090), with its LFE send.
      await menu(app, 'surroundSettings');
      await window.getByRole('radio', { name: /7\.1\.4/ }).click();
      await expect.poll(async () => (await project(window)).settings.surroundFormat).toBe('surround714');
      // A 1 kHz tone with a 60 Hz component (for the LFE's low-pass), on the Surround Panner.
      const lowAndHigh = sine(1000, -12, 10, Rate).map(
        (v, i) => v + Math.pow(10, -12 / 20) * Math.sin((2 * Math.PI * 60 * i) / Rate)
      );
      const panned = await toneTrack(window, folder, 'Panned', lowAndHigh);
      state = await project(window);
      expect(state.tracks.find(t => t.id === panned)?.strip.panner.mode).toBe('surround');
      const others = state.tracks.filter(t => t.id !== panned).map(t => t.id);
      await commands(window, [
        ...others.map(trackId => ({ type: 'setStrip', trackId, mute: true })),
        { type: 'setPanner', trackId: panned, angle: 90, diversity: 1, lfeLevelDb: 0 }
      ]);
      const bed = await bounce(window, folder, 'bed', 2 * Bar);
      expect(bed.channels).toHaveLength(12);
      const levels = bed.channels.map(c => db(rms(c, Rate / 2, 3 * Rate)));
      const ls = levels[4] ?? -200; // 7.1.4 (ADM order): L R C LFE Ls(M+090) Rs Lrs(M+135) Rrs Ltf Rtf Ltr Rtr
      for (const [i, level] of levels.entries()) {
        if (i !== 3 && i !== 4) {
          expect(ls - level).toBeGreaterThanOrEqual(20);
        }
      }
      const lfe = bed.channels[3] ?? new Float32Array();
      const lfe60 = db(amplitude(lfe.subarray(Rate / 2, 3 * Rate), 60, Rate));
      const lfe1k = db(amplitude(lfe.subarray(Rate / 2, 3 * Rate), 1000, Rate));
      expect(lfe60).toBeGreaterThan(-13);
      expect(lfe60 - lfe1k).toBeGreaterThan(40);
      measured(
        '4 surround',
        `Ls ${ls.toFixed(1)} dB; the loudest other speaker ${Math.max(...levels.filter((_, i) => i !== 3 && i !== 4)).toFixed(1)} dB; ` +
          `LFE 60 Hz ${lfe60.toFixed(1)} dB, 1 kHz ${lfe1k.toFixed(1)} dB`
      );

      // An object (3D Object Panner) automated from front left to the top rear right over 4 s.
      await commands(window, [
        { type: 'setStrip', trackId: panned, mute: true },
        { type: 'setStrip', trackId: tone, mute: false },
        { type: 'setPanner', trackId: tone, mode: 'object', azimuth: 30, objectElevation: 0, distance: 1 },
        {
          type: 'setAutomation',
          trackId: tone,
          target: 'object:azimuth',
          points: [
            { tick: 0, value: 30, curve: 0 },
            { tick: 2 * Bar, value: -135, curve: 0 }
          ]
        },
        {
          type: 'setAutomation',
          trackId: tone,
          target: 'object:elevation',
          points: [
            { tick: 0, value: 0, curve: 0 },
            { tick: 2 * Bar, value: 30, curve: 0 }
          ]
        }
      ]);
      const moving = await bounce(window, folder, 'object', 2 * Bar);
      const at = (from: number) => moving.channels.map(c => rms(c, from, from + Rate / 10));
      const start = at(0);
      const end = at(4 * Rate - Rate / 10);
      expect(start.indexOf(Math.max(...start))).toBe(0); // L
      expect(end.indexOf(Math.max(...end))).toBe(11); // top rear right (U-135)
      expect(db(start[0] ?? 0) - db(start[11] ?? 0)).toBeGreaterThan(20);
      expect(db(end[11] ?? 0) - db(end[0] ?? 0)).toBeGreaterThan(20);
      measured(
        '4 surround',
        `object: L − Rtr ${(db(start[0] ?? 0) - db(start[11] ?? 0)).toFixed(1)} dB at the start, Rtr − L ${(db(end[11] ?? 0) - db(end[0] ?? 0)).toFixed(1)} dB at the end`
      );

      // A stereo effect (Channel EQ's output at −6 dB) on a 7.1.4 bus processes every channel; the down-mix to stereo
      // monitoring has ITU-R BS.775's coefficients (C −3 dB into L and R, Ls −6 dB into L, the top front left −3 dB).
      await commands(window, [{ type: 'addAux', bus: 7, name: 'Surround Bus' }]);
      const busId = (await project(window)).tracks.find(t => t.name === 'Surround Bus')?.id ?? '';
      await commands(window, [{ type: 'setStripFormat', trackId: busId, inputFormat: 'surround714' }]);
      const voices: [string, number, number, number][] = [
        ['Centre', 500, 0, 0],
        ['Side', 1000, 90, 0],
        ['Height', 2000, 45, 30]
      ];
      const voiceIds: string[] = [];
      for (const [name, hz, angle, elevation] of voices) {
        const id = await toneTrack(window, folder, name, sine(hz, -12, 10, Rate));
        voiceIds.push(id);
        await commands(window, [
          { type: 'setOutput', trackId: id, output: 'bus:7' },
          { type: 'setPanner', trackId: id, angle, diversity: 1, surroundElevation: elevation }
        ]);
      }
      await commands(window, [{ type: 'setStrip', trackId: tone, mute: true }]);
      const dry = await bounce(window, folder, 'bus-dry', Bar);
      await commands(window, [{ type: 'addInsert', trackId: busId, pluginId: 'anode.channel-eq' }]);
      const eqId =
        (
          await api<{ tracks: { id: string; strip: { inserts: { id: string }[] } }[] }>(window, 'GET', '/project')
        ).tracks.find(t => t.id === busId)?.strip.inserts[0]?.id ?? '';
      await commands(window, [
        { type: 'setInsertParameter', trackId: busId, insertId: eqId, parameterId: 'output', value: -6 }
      ]);
      const wet = await bounce(window, folder, 'bus-eq', Bar);
      const channelOf = { 500: 2, 1000: 4, 2000: 8 } as Record<number, number>;
      const part = (c: Float32Array | undefined) => (c ?? new Float32Array()).subarray(Rate / 2, Rate + Rate / 2);
      for (const [, hz] of voices) {
        const c = channelOf[hz] ?? 0;
        const drop = db(amplitude(part(dry.channels[c]), hz, Rate)) - db(amplitude(part(wet.channels[c]), hz, Rate));
        expect(Math.abs(drop - 6)).toBeLessThan(0.1);
      }
      await api(window, 'PUT', '/settings/monitoring', { binaural: false, downmixTo: 'stereo', speakerMap: [] });
      const downmix = await bounce(window, folder, 'downmix', Bar, null, true);
      expect(downmix.channels).toHaveLength(2);
      const coefficients: string[] = [];
      for (const [name, hz, , , left, right] of [
        ['C', 500, 0, 0, -3.01, -3.01],
        ['Ls', 1000, 0, 0, -6.02, null],
        ['Ltf', 2000, 0, 0, -3.01, null]
      ] as [string, number, number, number, number, number | null][]) {
        const source = db(amplitude(part(wet.channels[channelOf[hz] ?? 0]), hz, Rate));
        const l = db(amplitude(part(downmix.channels[0]), hz, Rate)) - source;
        const r = db(amplitude(part(downmix.channels[1]), hz, Rate)) - source;
        expect(Math.abs(l - left)).toBeLessThan(0.1);
        if (right === null) {
          expect(r).toBeLessThan(-60);
        } else {
          expect(Math.abs(r - right)).toBeLessThan(0.1);
        }
        coefficients.push(`${name} → L' ${l.toFixed(2)} dB, R' ${r < -60 ? 'off' : `${r.toFixed(2)} dB`}`);
      }
      measured('4 surround', `Channel EQ on the 7.1.4 bus: −6 dB on C, Ls, Ltf; down-mix ${coefficients.join('; ')}`);

      // ---- 5. Binaural: a source at 90° left reaches the left ear ≥ 6 dB louder, the right 0.5–0.8 ms later.
      const noiseTrack = await toneTrack(window, folder, 'Noise', noise(-12, 10, Rate));
      await commands(window, [
        ...voiceIds.map(trackId => ({ type: 'setStrip', trackId, mute: true })),
        { type: 'setPanner', trackId: noiseTrack, mode: 'object', azimuth: 90, objectElevation: 0, distance: 1 }
      ]);
      await api(window, 'PUT', '/settings/monitoring', { binaural: true, downmixTo: null, speakerMap: [] });
      const ears = await bounce(window, folder, 'binaural', Bar, null, true);
      expect(ears.channels).toHaveLength(2);
      const binaural = interaural(
        (ears.channels[0] ?? new Float32Array()).subarray(Rate / 2, Rate + Rate / 2),
        (ears.channels[1] ?? new Float32Array()).subarray(Rate / 2, Rate + Rate / 2),
        Rate
      );
      expect(binaural.ildDb).toBeGreaterThanOrEqual(6);
      expect(binaural.itdMs).toBeGreaterThanOrEqual(0.5);
      expect(binaural.itdMs).toBeLessThanOrEqual(0.8);
      measured('5 binaural', `ILD ${binaural.ildDb.toFixed(1)} dB, ITD ${binaural.itdMs.toFixed(3)} ms`);
      await api(window, 'PUT', '/settings/monitoring', { binaural: false, downmixTo: null, speakerMap: [] });

      // ---- 6. ADM BWF: the bed (the Surround Panner track) plus the objects (the moving tone and the noise), valid per
      // BS.2076 (ear), and ear's 7.1.4 render matches Anode's within 1 dB of RMS per speaker and second.
      await commands(window, [
        { type: 'setStrip', trackId: panned, mute: false },
        { type: 'setStrip', trackId: tone, mute: false },
        { type: 'setStrip', trackId: noiseTrack, volumeDb: -12 }
      ]);
      await menu(app, 'exportAdm');
      await window.getByTestId('export-name').fill('Mix');
      await window.getByTestId('export-folder').fill(folder);
      await window.getByTestId('interchange-start').click();
      const adm = join(folder, 'Mix.wav');
      await expect(window.getByTestId('interchange-dialog')).toBeHidden({ timeout: 120_000 });
      await expect.poll(() => existsSync(adm), { timeout: 120_000 }).toBe(true);
      earValidate(adm);
      const ours = await bounce(window, folder, 'anode-714', (await project(window)).settings.endTick ?? 4 * Bar);
      const theirs = earRender(adm, '4+7+0', join(folder, 'ear-714.wav'));
      expect(theirs.channels).toHaveLength(12);
      // ear renders 4+7+0 in BS.2051 order, which is the ADM order of Anode's 7.1.4.
      let worst = 0;
      let compared = 0;
      const apart: string[] = [];
      ours.channels.forEach((channel, i) => {
        const a = rmsPerSecond(channel, Rate);
        const b = rmsPerSecond(theirs.channels[i] ?? new Float32Array(), Rate);
        a.forEach((level, second) => {
          if (level > -60) {
            const difference = Math.abs(level - (b[second] ?? -200));
            worst = Math.max(worst, difference);
            compared++;
            if (difference >= 1) {
              apart.push(
                `speaker ${i} second ${second}: Anode ${level.toFixed(2)} dB, ear ${(b[second] ?? -200).toFixed(2)} dB`
              );
            }
          }
        });
      });
      expect(apart).toEqual([]);
      expect(compared).toBeGreaterThan(10);
      measured(
        '6 adm',
        `ear validated Mix.wav; its 4+7+0 render vs Anode's: ${compared} speaker-seconds, worst ${worst.toFixed(3)} dB`
      );

      // ---- 7. Save, close and reopen: everything is intact.
      const before = await project(window);
      await menu(app, 'saveProject'); // File ▸ Save (the window's focus may be in a field the shortcut skips)
      await expect(window.getByRole('img', { name: 'Unsaved changes' })).toBeHidden();
      await menu(app, 'closeProject');
      await expect(window.getByTestId('new-project-name')).toBeVisible();
      await api(window, 'POST', '/projects/open', { path: before.path });
      const after = await project(window);
      expect(after.settings.surroundFormat).toBe('surround714');
      expect(after.movie).toMatchObject({ startTick: Bar, offsetSeconds: 0, frames: movie.frames });
      expect(after.markers.filter(m => m.kind === 'scene')).toHaveLength(3);
      expect(after.tracks.map(t => [t.name, t.strip.outputFormat, t.strip.panner.mode])).toEqual(
        before.tracks.map(t => [t.name, t.strip.outputFormat, t.strip.panner.mode])
      );
      expect((await api<{ sets: unknown[] }>(window, 'GET', '/remote/pads')).sets.length).toBeGreaterThan(0);
      measured(
        '7 reopen',
        `${after.tracks.length} tracks with their formats and panners, ${after.markers.filter(m => m.kind === 'scene').length} scene markers, ` +
          `the movie at tick ${after.movie?.startTick}, ${after.settings.surroundFormat}: as saved`
      );
    } finally {
      await remote?.context.close();
      await remote?.browser.close();
      if (!process.env['ANODE_E2E_KEEP']) {
        rmSync(folder, { recursive: true, force: true });
      } else {
        console.warn(`Kept ${folder}`);
      }
    }
  });
});
