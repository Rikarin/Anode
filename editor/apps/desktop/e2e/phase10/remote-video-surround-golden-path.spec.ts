import {
  type Browser,
  type BrowserContext,
  chromium,
  devices,
  type ElectronApplication,
  type Page
} from '@playwright/test';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from '../fixtures';
import {
  bestLag,
  browseAnode,
  db,
  earRender,
  earValidate,
  extractAudio,
  interaural,
  makeTestMovie,
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
 * Written by the editor workstream (C) as `test.fixme`: it needs every workstream merged and the INTEGRATOR's helpers
 * (./helpers.ts): `dns-sd` (macOS) for discovery; E's media helper (ANODE_MEDIA_HELPER, `scripts/media-check.mjs`) for
 * the test movie, FFprobe and FFmpeg; D's `ear` helper (ANODE_EAR_HELPER, `scripts/ear-check.py`). The remote runs in a
 * second browser (Playwright's Chromium, a phone viewport with touch) against the desktop app's Server on the LAN.
 * Its listen-in capture hook is `window.anodeListenIn` (remote/listen-in/listen-in.store.ts).
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
  return (await response.json()) as T;
}

interface ProjectShape {
  path: string;
  settings: { surroundFormat: string; tempo: number };
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
}

const project = (window: Page) => api<ProjectShape>(window, 'GET', '/project');
const commands = (window: Page, list: unknown[], label = 'Test') =>
  api(window, 'POST', '/project/commands', { commands: list, label });
const transport = (window: Page, command: 'play' | 'stop' | 'record' | 'locate', tick?: number) =>
  api<{ state: string; positionTick: number; positionSamples: number }>(window, 'POST', '/transport', {
    command,
    tick
  });

async function finished(window: Page, job: JobShape): Promise<JobShape> {
  let current = job;
  await expect
    .poll(
      async () => {
        current = await api<JobShape>(window, 'GET', `/jobs/${job.id}`);
        return current.state;
      },
      { timeout: 120_000 }
    )
    .toBe('done');
  return current;
}

/** A multichannel bounce of the mix (one interleaved WAV in the master's layout), read back. */
async function bounce(window: Page, folder: string, name: string, endTick: number, trackIds: string[] | null = null) {
  const job = await api<JobShape>(window, 'POST', '/project/bounce', {
    fileName: name,
    directory: folder,
    startTick: 0,
    endTick,
    source: 'mix',
    trackIds,
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

/** A 10 s 1 kHz test tone at −12 dBFS as an audio track at bar 1. */
async function toneTrack(window: Page, folder: string, name: string): Promise<string> {
  const path = join(folder, `${name}.wav`);
  writeWav(path, sine(1000, -12, 10, Rate), Rate);
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
}

/** Turns the desktop app's network access on and opens the remote from what DNS-SD found, with the pairing token. */
async function openRemote(window: Page, projectName: string): Promise<Remote> {
  const status = await window.evaluate(async () => {
    const network = (
      window as unknown as {
        anode: { network: { apply(n: { enabled: boolean; port: number }): Promise<{ token: string; port: number }> } };
      }
    ).anode.network;
    return network.apply({ enabled: true, port: 0 });
  });
  // The window reloads on the new Server: wait for it to come back.
  await expect(window.getByTestId('project-name')).toBeVisible({ timeout: 90_000 });
  const found = await browseAnode(projectName);
  expect(found.port).toBe(status.port);
  expect(found.txt['path']).toBe('/api/v1');
  measured('1 remote', `found ${found.name} at ${found.host}:${found.port}, TXT ${JSON.stringify(found.txt)}`);

  const browser = await chromium.launch();
  const context = await browser.newContext({ ...devices['iPhone 13'], hasTouch: true, isMobile: true });
  const page = await context.newPage();
  // Pairing as today: the token in the address (the QR code's URL), then the remote route.
  await page.goto(`http://${found.host}:${found.port}/?token=${encodeURIComponent(status.token)}#/remote`);
  await expect(page.getByTestId('remote-project')).toHaveText(projectName, { timeout: 30_000 });
  return { browser, context, page };
}

async function remoteTab(remote: Remote, name: string): Promise<void> {
  await remote.page.getByTestId(`remote-tab-${name}`).tap();
}

// ---- the golden path

test.describe('Phase 10: remote, video and surround', () => {
  test.setTimeout(900_000);

  test.fixme('runs the session from a remote that hears it, locks a movie to the transport and mixes in surround, binaurally and to ADM BWF', async ({
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
      await commands(window, [{ type: 'createDrumMachine', trackId: drumsId, name: 'Drums' }, { type: 'addScene' }]);
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
        { type: 'setTrack', trackId: drumsId, recordArmed: true }
      ]);

      // ---- 1. Remote: discovery, pairing, transport, a fader, a Live Loops cell, drum pads recorded.
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
      const remoteDb = Number(await fader.getAttribute('aria-valuenow'));
      await window.getByRole('tab', { name: 'Mixer' }).click();
      await expect(window.getByRole('slider', { name: 'Tone volume' })).toHaveAttribute(
        'aria-valuenow',
        String(remoteDb)
      );

      await remoteTab(remote, 'loops');
      await remote.page.getByTestId('remote-cell').first().tap();
      await transport(window, 'play', 0);
      await expect
        .poll(async () => window.getByTestId('live-loops-cell').first().getAttribute('data-state'))
        .toBe('playing');
      await transport(window, 'stop');

      await api(window, 'PUT', '/project/selection', { trackId: drumsId });
      await remoteTab(remote, 'play');
      await remote.page.getByTestId('play-drums').tap();
      await transport(window, 'record', 0);
      for (const name of [/Kick/, /Snare/, /Kick/, /Hat/]) {
        await remote.page.getByRole('button', { name }).first().tap();
        await remote.page.waitForTimeout(250);
      }
      await transport(window, 'stop');
      state = await project(window);
      const hits = state.midiRegions.filter(r => r.trackId === drumsId).flatMap(r => r.notes.map(n => n.pitch));
      expect(hits).toHaveLength(4);
      measured('1 remote', `drum hits recorded ${JSON.stringify(hits)}`);

      // ---- 2. Listen-in: the 1 kHz tone heard on the remote, at its frequency and level, < 500 ms behind.
      await remote.page.getByTestId('listen-in').tap();
      await expect(remote.page.getByTestId('listen-in-status')).toContainText(/Listening|Playing/, { timeout: 15_000 });
      await transport(window, 'play', 0);
      await remote.page.evaluate(() =>
        (window as unknown as { anodeListenIn: { startCapture(s: number): void } }).anodeListenIn.startCapture(2)
      );
      await remote.page.waitForTimeout(3000);
      const now = await api<{ positionSamples: number }>(window, 'GET', '/transport');
      const heard = await remote.page.evaluate(() => {
        const c = (
          window as unknown as {
            anodeListenIn: {
              capture(): {
                samples: Float32Array;
                transportSample: number;
                sampleRate: number;
                projectSampleRate: number;
              };
            };
          }
        ).anodeListenIn.capture();
        return { samples: Array.from(c.samples), transportSample: c.transportSample, sampleRate: c.sampleRate };
      });
      await transport(window, 'stop');
      const steady = Float32Array.from(heard.samples.slice(Rate / 4));
      const toneHeard = toneOf(steady, heard.sampleRate);
      expect(Math.abs(toneHeard.hz - 1000)).toBeLessThan(2);
      // The tone track's fader moved in step 1: compare with its level at the fader.
      const fadedDb = -12 + ((await project(window)).tracks.find(t => t.id === tone)?.strip.volumeDb ?? 0);
      expect(Math.abs(toneHeard.peakDb - fadedDb)).toBeLessThan(1);
      const latency = await remote.page.getByTestId('listen-in-latency').textContent();
      const behindMs = Number(/(\d+)\s*ms/.exec(latency ?? '')?.[1] ?? NaN);
      expect(behindMs).toBeLessThan(500);
      measured(
        '2 listen-in',
        `${toneHeard.hz.toFixed(2)} Hz at ${toneHeard.peakDb.toFixed(2)} dBFS (expected ${fadedDb.toFixed(2)}), ` +
          `${behindMs} ms behind; the capture began at transport sample ${heard.transportSample}, the Server at ${now.positionSamples}`
      );
      await remote.page.getByTestId('listen-in').tap();

      // ---- 3. Video: the test movie (25 fps, 12 s, cuts at frames 50, 125, 200; a beep every second).
      const moviePath = join(folder, 'Test Movie.mov');
      const movie = makeTestMovie(moviePath, 12, 25, [50, 125, 200]);
      await answerDialogs(app, moviePath);
      await menu(app, 'openMovie');
      await window.getByTestId('movie-choose').click();
      await window.getByTestId('movie-start-job').click();
      await expect(window.getByTestId('movie-window')).toBeVisible({ timeout: 60_000 });
      const frameShown = async () =>
        window
          .getByTestId('movie-video')
          .evaluate(
            (video: HTMLVideoElement, fps: number) =>
              new Promise<number>(resolve =>
                video.requestVideoFrameCallback((_, meta) => resolve(Math.floor(meta.mediaTime * fps + 1e-6)))
              ),
            movie.frameRate
          );
      const expectedFrame = async () => {
        const t = await api<{ positionSamples: number }>(window, 'GET', '/transport');
        const m = (await project(window)).movie;
        // At 120 bpm with no varispeed: seconds = samples / rate; the movie's own start is its startTick.
        const startSeconds = ((m?.startTick ?? 0) / Ppq) * 0.5;
        return Math.floor((t.positionSamples / Rate - startSeconds + (m?.offsetSeconds ?? 0)) * movie.frameRate);
      };
      for (const tick of [0, 3 * Ppq, 7 * Ppq + 120, 2 * Bar]) {
        await transport(window, 'locate', tick);
        await expect.poll(async () => Math.abs((await frameShown()) - (await expectedFrame()))).toBeLessThanOrEqual(1);
      }
      await transport(window, 'play', 0);
      for (let i = 0; i < 4; i++) {
        await window.waitForTimeout(1000);
        expect(Math.abs((await frameShown()) - (await expectedFrame()))).toBeLessThanOrEqual(1);
      }
      await transport(window, 'stop');

      // Thumbnails on the movie track; Detect Scene Cuts at the known cuts within a frame.
      await expect(window.getByTestId('movie-lane').locator('img').first()).toBeVisible({ timeout: 60_000 });
      await menu(app, 'detectSceneCuts');
      await window.getByTestId('movie-start-job').click();
      await expect
        .poll(async () => (await project(window)).markers.filter(m => m.kind === 'scene').length, { timeout: 60_000 })
        .toBe(3);
      const framesPerTick = movie.frameRate / (2 * Ppq); // 120 bpm: 2 beats a second
      const scenes = (await project(window)).markers.filter(m => m.kind === 'scene').map(m => m.tick * framesPerTick);
      scenes.forEach((frame, i) => expect(Math.abs(frame - (movie.sceneCutFrames[i] ?? 0))).toBeLessThanOrEqual(1));

      // Import Audio from Movie: the beeps on their frames within a millisecond (a bounce of the new track).
      const checkBeeps = async (label: string, startSeconds: number) => {
        await menu(app, 'importMovieAudio');
        await expect.poll(async () => (await project(window)).tracks.some(t => t.name === 'Test Movie')).toBe(true);
        state = await project(window);
        const audioTrack = state.tracks.filter(t => t.name === 'Test Movie').at(-1)?.id ?? '';
        // The new track alone (the tone track is muted too).
        const mixdown = await bounce(window, folder, `beeps-${label}`, 16 * Bar, [audioTrack]);
        const found = onsets(mixdown.channels[0] ?? new Float32Array(), mixdown.sampleRate).map(
          s => s / mixdown.sampleRate
        );
        movie.beepSeconds.forEach((beep, i) =>
          expect(Math.abs((found[i] ?? 0) - (beep + startSeconds)) * 1000).toBeLessThan(1)
        );
        measured('3 video', `${label}: ${found.length} beeps, first at ${found[0]?.toFixed(4)} s`);
      };
      await commands(window, [{ type: 'setStrip', trackId: tone, mute: true }]);
      await checkBeeps('at bar 1', 0);
      // With the movie's first frame at bar 2 the same holds, two seconds later.
      await commands(window, [{ type: 'setMoviePosition', startTick: Bar, offsetSeconds: 0 }]);
      await checkBeeps('at bar 2', 2);

      // Export Audio to Movie: the bounce aligned to the frames, the video stream copied.
      await menu(app, 'exportMovieAudio');
      await window.getByTestId('movie-export-name').fill('Test Movie with Mix');
      await window.getByTestId('movie-export-folder').fill(folder);
      await window.getByTestId('movie-start-job').click();
      const exported = join(folder, 'Test Movie with Mix.mov');
      await expect.poll(() => probeStreams(exported).length, { timeout: 120_000 }).toBeGreaterThan(1);
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

      // ---- 4. Surround: 7.1.4; a mono track's puck at Ls; an object from front left to rear right overhead.
      await menu(app, 'surroundSettings');
      await window.getByRole('radio', { name: /7\.1\.4/ }).click();
      await expect.poll(async () => (await project(window)).settings.surroundFormat).toBe('surround714');
      await commands(window, [{ type: 'setStrip', trackId: tone, mute: false }]);
      // The puck at Ls (110°) as a point; the LFE send at 0 dB.
      await commands(window, [{ type: 'setPanner', trackId: tone, angle: 110, diversity: 1, lfeLevelDb: 0 }]);
      const bed = await bounce(window, folder, 'bed', 4 * Bar);
      const levels = bed.channels.map(c => db(rms(c)));
      const ls = levels[4] ?? -200; // 7.1.4 order: L R C LFE Lss(M+090) Rss Lrs(M+135) Rrs …; Ls at 110° is between
      const loudest = Math.max(...levels.filter((_, i) => i !== 3));
      expect(loudest - ls).toBeLessThan(3);
      for (const [i, level] of levels.entries()) {
        if (i !== 3 && i !== 4 && i !== 6) {
          expect(ls - level).toBeGreaterThanOrEqual(20);
        }
      }
      expect(levels[3]).toBeGreaterThan(-60); // the LFE's low-passed copy (of a 1 kHz tone: low, but there)
      measured('4 surround', `bed levels ${levels.map(l => l.toFixed(1)).join(' ')}`);

      await commands(window, [
        { type: 'setPanner', trackId: tone, mode: 'object', azimuth: 30, objectElevation: 0, distance: 1 },
        {
          type: 'setAutomation',
          trackId: tone,
          target: 'object:azimuth',
          points: [
            { tick: 0, value: 30, curve: 0 },
            { tick: 4 * Bar, value: -135, curve: 0 }
          ]
        },
        {
          type: 'setAutomation',
          trackId: tone,
          target: 'object:elevation',
          points: [
            { tick: 0, value: 0, curve: 0 },
            { tick: 4 * Bar, value: 30, curve: 0 }
          ]
        }
      ]);
      const moving = await bounce(window, folder, 'object', 4 * Bar);
      const left = rmsPerSecond(moving.channels[0] ?? new Float32Array(), Rate);
      const topRearRight = rmsPerSecond(moving.channels[11] ?? new Float32Array(), Rate);
      expect((left[0] ?? -200) - (topRearRight[0] ?? -200)).toBeGreaterThan(10);
      expect((topRearRight.at(-1) ?? -200) - (left.at(-1) ?? -200)).toBeGreaterThan(10);

      // ---- 5. Binaural: a source at 90° left reaches the left ear ≥ 6 dB louder, the right 0.5–0.8 ms later.
      await commands(window, [
        { type: 'setAutomation', trackId: tone, target: 'object:azimuth', points: [] },
        { type: 'setAutomation', trackId: tone, target: 'object:elevation', points: [] },
        { type: 'setPanner', trackId: tone, azimuth: 90, objectElevation: 0 }
      ]);
      await api(window, 'PUT', '/settings/monitoring', { binaural: true, downmixTo: null, speakerMap: [] });
      // INTEGRATOR: the monitoring render only reaches a bounce that asks for it (ADR-0012); A's bounce option for the
      // monitoring render, or the device tap on the null device, gives the two ears.
      const ears = await bounce(window, folder, 'binaural', 2 * Bar);
      const binaural = interaural(ears.channels[0] ?? new Float32Array(), ears.channels[1] ?? new Float32Array(), Rate);
      expect(binaural.ildDb).toBeGreaterThanOrEqual(6);
      expect(binaural.itdMs).toBeGreaterThanOrEqual(0.5);
      expect(binaural.itdMs).toBeLessThanOrEqual(0.8);
      measured('5 binaural', `ILD ${binaural.ildDb.toFixed(1)} dB, ITD ${binaural.itdMs.toFixed(3)} ms`);
      await api(window, 'PUT', '/settings/monitoring', { binaural: false, downmixTo: null, speakerMap: [] });

      // ---- 6. ADM BWF: bed plus objects, valid per BS.2076 (ear), and ear's 7.1.4 render matches Anode's within 1 dB.
      await menu(app, 'exportAdm');
      await window.getByTestId('export-name').fill('Mix');
      await window.getByTestId('export-folder').fill(folder);
      await window.getByTestId('interchange-start').click();
      const adm = join(folder, 'Mix.wav');
      await expect(window.getByTestId('interchange-dialog')).toBeHidden({ timeout: 120_000 });
      earValidate(adm);
      const ours = await bounce(window, folder, 'anode-714', 4 * Bar);
      const theirs = earRender(adm, '4+7+0', join(folder, 'ear-714.wav'));
      // ear renders in BS.2051 order for 4+7+0 (M+030 M-030 M+000 LFE1 M+090 M-090 M+135 M-135 U+045 U-045 U+135 U-135).
      ours.channels.forEach((channel, i) => {
        const a = rmsPerSecond(channel, Rate);
        const b = rmsPerSecond(theirs.channels[i] ?? new Float32Array(), Rate);
        a.forEach((level, s) => {
          if (level > -60) {
            expect(Math.abs(level - (b[s] ?? -200))).toBeLessThan(1);
          }
        });
      });
      measured('6 adm', `ear validated Mix.wav and its 4+7+0 render matched Anode's per second within 1 dB`);

      // ---- 7. Save, close and reopen: everything is intact.
      const before = await project(window);
      await window.keyboard.press('ControlOrMeta+s');
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
    } finally {
      await remote?.context.close();
      await remote?.browser.close();
      rmSync(folder, { recursive: true, force: true });
    }
  });
});
