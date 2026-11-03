import type { ElectronApplication, Page } from '@playwright/test';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { expect, test } from '../fixtures';
import { amplitude, db, noise, readWav, rms, sine, writeWav } from '../phase10/helpers';
import {
  centsFrom,
  decayTime,
  evenHarmonicsDb,
  integratedLufs,
  interauralDifferences,
  pitchOf,
  thd,
  truePeakDbtp
} from './helpers';

/*
 * Phase 11's exit criterion (docs/12-phase-11.md): a song gets its chords from what is played, players that follow
 * them, a master that meets a loudness target, the T3 instruments and effects, and an Environment patch between the
 * keyboard and a track — on the null device, against the real app.
 *
 * It runs every workstream together (D's generator, styles and Chord ID; E's T3 DSP, saturation, down-mixer, binaural
 * insert and Mastering Assistant; A's Environment graph and format-changing inserts; B's model, compiler and jobs) with
 * the test pack installed (the Drummer kit, a tape set; the global setup builds it). The signal checks are ./helpers.ts (BS.1770 loudness and true peak, harmonics, a pitch tracker, the decay
 * time, interaural differences). The model is set up through the API where the step is not about the UI; what the
 * step tests goes through the window: the chord track and its popover, the Session Player Editor, the Mastering
 * Assistant window, the Environment window.
 */

/** A value the test needs to be there. */
function must<T>(value: T | null | undefined): T {
  if (value === null || value === undefined) {
    throw new Error('Expected a value.');
  }
  return value;
}

const TestPack = resolve(__dirname, '../../../../../engine/artifacts/content/TestPack.anodepack');
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
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

interface PitchName {
  step: string;
  alter: number;
}
interface NoteShape {
  pitch: number;
  velocity: number;
  startTick: number;
  lengthTicks: number;
}
interface ProjectShape {
  path: string;
  tracks: {
    id: string;
    name: string;
    kind: string;
    parentId: string | null;
    drumMachine: { pads: { note: number; trackId: string }[] } | null;
    instrument: { parameters: Record<string, number> } | null;
    strip: { volumeDb: number; inserts: { id: string; pluginId: string; parameters: Record<string, number> }[] };
  }[];
  master: { inserts: { id: string; pluginId: string; mastering?: unknown }[] };
  midiRegions: {
    id: string;
    trackId: string;
    startTick: number;
    notes: NoteShape[];
    pitchSource: string;
    key: unknown;
  }[];
  patternRegions: {
    id: string;
    trackId: string;
    startTick: number;
    lengthTicks: number;
    pattern: { stepTicks: number; rows: { note: number; steps: { on: boolean; offset: number }[] }[] };
  }[];
  regions: { id: string; trackId: string; startTick: number; fileId: string; pitchSource: string }[];
  arrangement: { id: string; name: string; startTick: number; lengthTicks: number }[];
  chordTrack: {
    chords: { id: string; tick: number; root: PitchName; quality: string; bass: PitchName | null }[];
    scales: unknown[];
  };
  sessionPlayerRegions: {
    id: string;
    trackId: string;
    name: string;
    startTick: number;
    lengthTicks: number;
    seed: number;
    parameters: Record<string, unknown> & { pad: { complexity: number; intensity: number } };
  }[];
  environment: {
    layers: { id: string; name: string }[];
    objects: { id: string; kind: string; name: string; parameters: Record<string, number> }[];
    cables: { id: string; fromObjectId: string; toObjectId: string }[];
  };
}
interface JobShape {
  id: string;
  state: string;
  files: string[];
}

const project = (window: Page) => api<ProjectShape>(window, 'GET', '/project');
/** Edits through the API; the editor hears of them over its socket, and an edit of its own before that would be refused. */
async function commands(window: Page, list: unknown[], label = 'Test'): Promise<void> {
  await api(window, 'POST', '/project/commands', { commands: list, label });
  await window.waitForTimeout(150);
}
const transport = (window: Page, command: 'play' | 'stop' | 'record' | 'locate', tick?: number) =>
  api<{ state: string; positionTick: number }>(window, 'POST', '/transport', { command, tick });
const instrumentOf = async (window: Page, trackId: string) =>
  (await project(window)).tracks.find(t => t.id === trackId)?.instrument;
const notesOf = async (window: Page, regionId: string) =>
  (await api<{ hash: string; notes: NoteShape[] }>(window, 'GET', `/project/regions/${regionId}/session-player`)).notes;

async function finished(window: Page, job: JobShape): Promise<JobShape> {
  let current = job;
  await expect
    .poll(
      async () => {
        current = await api<JobShape>(window, 'GET', `/jobs/${job.id}`);
        return current.state;
      },
      { timeout: 180_000 }
    )
    .toBe('done');
  return current;
}

/** A bounce of the mix (or the stem of a track), read back. */
async function bounce(window: Page, folder: string, name: string, endTick: number, trackIds: string[] | null = null) {
  const job = await api<JobShape>(window, 'POST', '/project/bounce', {
    fileName: name,
    directory: folder,
    startTick: 0,
    endTick,
    source: trackIds ? 'stems' : 'mix',
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
  console.warn(`measured ${step}: ${what}`);
}

// ---- music

const pc = (p: PitchName) =>
  (must(({ c: 0, d: 2, e: 4, f: 5, g: 7, a: 9, b: 11 } as Record<string, number>)[p.step]) + p.alter + 12) % 12;
const name = (step: string, alter = 0): PitchName => ({ step, alter });

/** The golden path's eight known chords, a bar each, voiced for Chord ID (the lowest note the bass). */
const Progression: { root: PitchName; quality: string; bass: PitchName | null; pitches: number[] }[] = [
  { root: name('c'), quality: '', bass: null, pitches: [48, 60, 64, 67] },
  { root: name('a'), quality: 'm7', bass: null, pitches: [45, 57, 60, 64, 67] },
  { root: name('f'), quality: 'maj7', bass: null, pitches: [41, 57, 60, 64, 65] },
  { root: name('g'), quality: '7', bass: null, pitches: [43, 59, 62, 65, 67] },
  { root: name('e'), quality: 'm', bass: null, pitches: [40, 59, 64, 67] },
  { root: name('a'), quality: '7', bass: null, pitches: [45, 61, 64, 67, 69] },
  { root: name('d'), quality: 'm7', bass: null, pitches: [38, 57, 60, 62, 65] },
  { root: name('g'), quality: '', bass: name('b'), pitches: [47, 55, 62, 67] }
];
const Qualities: Record<string, number[]> = {
  '': [0, 4, 7],
  m: [0, 3, 7],
  '7': [0, 4, 7, 10],
  maj7: [0, 4, 7, 11],
  m7: [0, 3, 7, 10],
  dim: [0, 3, 6],
  m7b5: [0, 3, 6, 10],
  aug: [0, 4, 8],
  sus2: [0, 2, 7],
  sus4: [0, 5, 7],
  '6': [0, 4, 7, 9],
  m6: [0, 3, 7, 9],
  '9': [0, 4, 7, 10, 14]
};
const tonesOf = (root: PitchName, quality: string) => new Set((Qualities[quality] ?? []).map(i => (pc(root) + i) % 12));

/** How many of the eight known chords the chord track holds right (root, and quality and bass when asked), each within an eighth of its bar. */
function rightChords(state: ProjectShape, qualities = true, basses = true): number {
  return Progression.filter((known, bar) =>
    state.chordTrack.chords.some(
      c =>
        Math.abs(c.tick - bar * Bar) <= Ppq / 2 &&
        pc(c.root) === pc(known.root) &&
        (!qualities || c.quality === known.quality) &&
        (!basses || (c.bass ? pc(c.bass) : null) === (known.bass ? pc(known.bass) : null))
    )
  ).length;
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

async function newProject(window: Page, folder: string, projectName: string): Promise<void> {
  await window.getByTestId('new-project-name').fill(projectName);
  await window.getByTestId('new-project-folder').fill(folder);
  await window.getByTestId('create-project').click();
  await expect(window.getByTestId('project-name')).toHaveText(projectName);
}

/** An instrument track playing `pluginId`, with an empty MIDI region of `bars` bars at the start. */
async function instrumentTrack(window: Page, trackName: string, pluginId: string, bars: number) {
  await commands(window, [{ type: 'addTrack', kind: 'instrument', stereo: true, name: trackName, pluginId }]);
  const trackId = (await project(window)).tracks.find(t => t.name === trackName)?.id ?? '';
  await commands(window, [{ type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: bars * Bar }]);
  const regionId = (await project(window)).midiRegions.find(r => r.trackId === trackId)?.id ?? '';
  return { trackId, regionId };
}

const Ruler = 24;
const Lane = 72;

/** A point on the lane of the track named `trackName`, `x` pixels from the timeline's left (scrolled into view). */
async function lanePoint(window: Page, trackName: string, x = 8): Promise<{ x: number; y: number }> {
  const area = window.locator('an-timeline');
  const timeline = await area.boundingBox();
  if (!timeline) {
    throw new Error('The Tracks area is not on screen.');
  }
  const header = window.getByRole('group', { name: new RegExp(`^Track \\d+: ${trackName}$`) });
  await window.mouse.move(timeline.x + timeline.width / 2, timeline.y + timeline.height / 2);
  for (let i = 0; i < 30; i++) {
    const box = (await header.count()) > 0 ? await header.boundingBox() : null;
    if (box && box.y >= timeline.y + Ruler && box.y + box.height <= timeline.y + timeline.height) {
      return { x, y: box.y - timeline.y + 6 };
    }
    await window.mouse.wheel(0, box && box.y < timeline.y + Ruler ? -Lane : Lane);
    await window.waitForTimeout(100);
  }
  throw new Error(`Could not scroll the Tracks area to ${trackName}.`);
}

/** Scrolls the Tracks area back to the song's start (a bounce's position scrolls it; lanes draw what is in view). */
async function scrollToStart(window: Page): Promise<void> {
  const timeline = await window.locator('an-timeline').boundingBox();
  if (timeline) {
    await window.mouse.move(timeline.x + timeline.width / 2, timeline.y + timeline.height / 2);
    await window.mouse.wheel(-100_000, 0);
    await window.waitForTimeout(150);
  }
}

/** Selects the region at the start of a track's lane (its name bar), so the editors follow it. */
async function selectRegion(window: Page, trackName: string): Promise<void> {
  await scrollToStart(window);
  await window.locator('an-timeline').click({ position: await lanePoint(window, trackName) });
}

// ---- the golden path

test.describe('Phase 11: generative and analysis', () => {
  test.setTimeout(1_200_000);
  test.use({ actionTimeout: 30_000 });

  test('identifies chords, plays Session Players that follow them, masters to a target, plays the T3 instruments and effects, and patches the Environment', async ({
    app,
    window
  }) => {
    const folder = mkdtempSync(join(tmpdir(), 'anode-phase11-'));
    try {
      await api(window, 'POST', '/library/packs', { path: TestPack });
      await newProject(window, folder, 'Generative');
      await commands(window, [
        { type: 'addSection', name: 'Verse', startTick: 0, lengthTicks: 4 * Bar },
        { type: 'addSection', name: 'Chorus', startTick: 4 * Bar, lengthTicks: 4 * Bar }
      ]);

      // ---- 1. Chord ID and the chord track
      const keys = await instrumentTrack(window, 'Keys', 'anode.electric-piano', 8);
      await commands(window, [
        {
          type: 'addNotes',
          regionId: keys.regionId,
          notes: Progression.flatMap((chord, bar) =>
            chord.pitches.map(pitch => ({
              pitch,
              velocity: 90,
              channel: 1,
              startTick: bar * Bar,
              lengthTicks: Bar - Ppq / 8
            }))
          )
        }
      ]);
      // Chord ID from the region's menu in the Tracks area; the chord track shows what it writes.
      await selectRegion(window, 'Keys');
      await window.locator('an-timeline').click({ button: 'right', position: await lanePoint(window, 'Keys') });
      await window.getByTestId('chord-id').click();
      await expect(window.getByTestId('global-chord')).toBeVisible();
      await expect.poll(async () => rightChords(await project(window)), { timeout: 120_000 }).toBeGreaterThanOrEqual(7);
      let state = await project(window);
      measured(
        '1 chord ID (MIDI)',
        `${rightChords(state)} of 8 right: ${state.chordTrack.chords.map(c => c.quality).join(' ')}`
      );
      await expect(window.getByTestId('global-chord').locator('[data-chord="Am7"]')).toBeVisible();

      // The same on an audio bounce of the region played by the Electric Piano: roots and qualities.
      await commands(window, [{ type: 'deleteChords', chordIds: state.chordTrack.chords.map(c => c.id) }]);
      const piano = await bounce(window, folder, 'Keys bounce', 8 * Bar, [keys.trackId]);
      const bounced = join(folder, 'Keys bounce.wav');
      writeWav(bounced, piano.channels[0] ?? new Float32Array(), piano.sampleRate);
      const file = await api<{ id: string }>(window, 'POST', '/project/files/import', { path: bounced });
      await commands(window, [{ type: 'addTrack', kind: 'audio', stereo: false, name: 'Keys Audio' }]);
      state = await project(window);
      const keysAudio = state.tracks.find(t => t.name === 'Keys Audio')?.id ?? '';
      await commands(window, [
        { type: 'addRegion', trackId: keysAudio, fileId: file.id, startTick: 0 },
        { type: 'setStrip', trackId: keys.trackId, mute: true }
      ]);
      const audioRegion = (await project(window)).regions.find(r => r.trackId === keysAudio)?.id ?? '';
      await finished(
        window,
        await api<JobShape>(window, 'POST', '/project/chords/detect', {
          regionIds: [audioRegion],
          write: 'replace',
          detectKeys: true
        })
      );
      state = await project(window);
      expect(rightChords(state, true, false)).toBeGreaterThanOrEqual(7);
      measured('1 chord ID (audio)', `${rightChords(state, true, false)} of 8 roots and qualities right`);

      // Edit a chord on the chord track (its popover): one undo step.
      await scrollToStart(window);
      // (The audio's slash basses are not part of its limit: match the symbol's start.)
      const edited = window.getByTestId('global-chord').locator('[data-chord^="Em"]');
      const symbol = await edited.getAttribute('data-chord');
      await edited.click();
      await window.getByTestId('chord-quality').selectOption('m7');
      await expect(window.getByTestId('global-chord').locator('[data-chord^="Em7"]')).toBeVisible();
      await window.getByRole('button', { name: 'Undo' }).click();
      await expect(window.getByTestId('global-chord').locator(`[data-chord="${symbol}"]`)).toBeVisible();
      await window.keyboard.press('Escape');

      // ---- 2. Pitch Source
      const bass = await instrumentTrack(window, 'Bass', 'anode.synth', 8);
      await commands(window, [
        {
          type: 'addNotes',
          regionId: bass.regionId,
          notes: Array.from({ length: 8 }, (_, bar) =>
            [0, 1, 2, 3].map(beat => ({
              pitch: 36 + (beat === 2 ? 7 : 0),
              velocity: 100,
              channel: 1,
              startTick: bar * Bar + beat * Ppq,
              lengthTicks: Ppq - 60
            }))
          ).flat()
        },
        { type: 'setRegionKey', regionIds: [bass.regionId], key: { tonic: name('c'), mode: 'major' } }
      ]);
      await selectRegion(window, 'Bass');
      await expect(window.getByText('Region: Bass')).toBeVisible();
      await window.getByTestId('region-pitch-source').selectOption('root');
      await expect
        .poll(async () => (await project(window)).midiRegions.find(r => r.id === bass.regionId)?.pitchSource)
        .toBe('root');
      // Root mode: each bar's notes move by its chord's root interval (−6…+5) — heard on a bounce of the bass alone.
      const rootBounce = await bounce(window, folder, 'Bass root', 8 * Bar, [bass.trackId]);
      for (const [bar, chord] of Progression.entries()) {
        const up = ((pc(chord.root) + 6) % 12) - 6;
        const start = Math.round(((bar * Bar) / Ppq) * (Rate / 2)); // 120 bpm: half a second a beat
        const hz = pitchOf(rootBounce.channels[0] ?? new Float32Array(), Rate, start + 12000, start + 22000);
        expect(Math.abs(centsFrom(hz, 36 + up))).toBeLessThan(25);
      }
      // Chords mode: every note on a chord tone.
      await window.getByTestId('region-pitch-source').selectOption('chords');
      await expect
        .poll(async () => (await project(window)).midiRegions.find(r => r.id === bass.regionId)?.pitchSource)
        .toBe('chords');
      const chordBounce = await bounce(window, folder, 'Bass chords', 8 * Bar, [bass.trackId]);
      const followingChords = (await project(window)).chordTrack.chords;
      for (const [bar, chord] of [...followingChords].sort((a, b) => a.tick - b.tick).entries()) {
        const tones = tonesOf(chord.root, chord.quality).add(pc(chord.bass ?? chord.root));
        const nearest = [0, -1, 1, -2, 2, -3, 3, -4, 4, -5, 5, -6, 6].map(d => 36 + d).find(p => tones.has(p % 12));
        const start = Math.round(((bar * Bar) / Ppq) * (Rate / 2));
        const hz = pitchOf(chordBounce.channels[0] ?? new Float32Array(), Rate, start + 12000, start + 22000);
        expect(Math.abs(centsFrom(hz, must(nearest)))).toBeLessThan(25);
      }
      // An audio region in C following the chord track: its pitch moves by the root interval within 5 cents.
      const tone = join(folder, 'C tone.wav');
      writeWav(tone, sine(130.8128, -12, 16, Rate), Rate);
      const toneFile = await api<{ id: string }>(window, 'POST', '/project/files/import', { path: tone });
      await commands(window, [{ type: 'addTrack', kind: 'audio', stereo: false, name: 'Tone' }]);
      const toneTrack = (await project(window)).tracks.find(t => t.name === 'Tone')?.id ?? '';
      await commands(window, [{ type: 'addRegion', trackId: toneTrack, fileId: toneFile.id, startTick: 0 }]);
      const toneRegion = (await project(window)).regions.find(r => r.trackId === toneTrack)?.id ?? '';
      await commands(window, [
        { type: 'setRegionKey', regionIds: [toneRegion], key: { tonic: name('c'), mode: 'major' } },
        { type: 'setPitchSource', regionIds: [toneRegion], mode: 'root' }
      ]);
      const followed = await bounce(window, folder, 'Tone root', 8 * Bar, [toneTrack]);
      let worstCents = 0;
      for (const [bar, chord] of Progression.entries()) {
        const up = ((pc(chord.root) + 6) % 12) - 6;
        const start = bar * 2 * Rate;
        const hz = pitchOf(followed.channels[0] ?? new Float32Array(), Rate, start + 9600, start + 72000, 80, 400);
        const cents = centsFrom(hz, 48 + up);
        expect(Math.abs(cents)).toBeLessThan(5);
        worstCents = Math.max(worstCents, Math.abs(cents));
      }
      measured('2 pitch source', `MIDI and audio follow the eight chords; audio worst ${worstCents.toFixed(2)} cents`);

      // ---- 3. Drummer
      await window.getByTestId('add-track-menu').click();
      await window.getByTestId('add-drummer').click();
      await expect.poll(async () => (await project(window)).tracks.some(t => t.kind === 'drummer')).toBe(true);
      state = await project(window);
      const drummer = must(state.tracks.find(t => t.kind === 'drummer'));
      // Pick a style in the Session Player Editor: two regions, one per section; one 8-bar region over both is generated.
      const drumRegions = state.sessionPlayerRegions.filter(r => r.trackId === drummer.id);
      expect([...drumRegions].sort((a, b) => a.startTick - b.startTick).map(r => r.name)).toEqual(['Verse', 'Chorus']);
      await commands(window, [
        { type: 'deleteRegions', regionIds: drumRegions.map(r => r.id) },
        { type: 'addSessionPlayerRegion', trackId: drummer.id, startTick: 0, lengthTicks: 8 * Bar, parameters: null }
      ]);
      const drums = must((await project(window)).sessionPlayerRegions.find(r => r.trackId === drummer.id));
      await selectRegion(window, drummer.name);
      await window.getByRole('tab', { name: 'Editors' }).click();
      await expect(window.getByRole('region', { name: 'Session Player Editor' })).toBeVisible();
      // The style browser: the library's first drum style (D's styles have generic names).
      await window.getByRole('listbox', { name: 'Styles' }).getByRole('option').first().click();
      const hitsPerBar = (notes: NoteShape[], bar: number) =>
        notes.filter(n => n.startTick >= bar * Bar && n.startTick < (bar + 1) * Bar).length;
      // Complexity at three settings: the hits per bar rise; intensity: the mean velocity rises.
      const pad = window.getByRole('slider', { name: drums.name });
      const hits: number[] = [];
      for (const complexity of [0.1, 0.5, 0.9]) {
        await pad.focus();
        await window.keyboard.press('Home');
        for (let i = 0; i < Math.round(complexity / 0.05); i++) {
          await window.keyboard.press('ArrowRight');
        }
        await expect
          .poll(
            async () =>
              (await project(window)).sessionPlayerRegions.find(r => r.id === drums.id)?.parameters.pad.complexity
          )
          .toBeCloseTo(complexity, 2);
        hits.push(hitsPerBar(await notesOf(window, drums.id), 1));
      }
      expect(hits[0]).toBeLessThan(must(hits[1]));
      expect(hits[1]).toBeLessThan(must(hits[2]));
      const velocities: number[] = [];
      for (const intensity of [0.1, 0.5, 0.9]) {
        await commands(window, [
          { type: 'setSessionPlayerRegion', regionId: drums.id, intensity, drums: null, bass: null, keys: null }
        ]);
        const notes = await notesOf(window, drums.id);
        velocities.push(notes.reduce((s, n) => s + n.velocity, 0) / notes.length);
      }
      expect(velocities[0]).toBeLessThan(must(velocities[1]));
      expect(velocities[1]).toBeLessThan(must(velocities[2]));
      measured(
        '3 drummer',
        `hits per bar ${hits.join(' < ')}; mean velocity ${velocities.map(v => v.toFixed(1)).join(' < ')}`
      );
      // The last bar of the first section holds a fill: more hits than its groove bars, toms or snare rolls.
      let notes = await notesOf(window, drums.id);
      expect(hitsPerBar(notes, 3)).toBeGreaterThan(
        Math.max(hitsPerBar(notes, 0), hitsPerBar(notes, 1), hitsPerBar(notes, 2))
      );
      expect(
        notes.some(n => n.startTick >= 3 * Bar && n.startTick < 4 * Bar && [41, 45, 47, 50].includes(n.pitch))
      ).toBe(true);
      // Same parameters and seed: the same notes; Regenerate: different notes.
      expect(await notesOf(window, drums.id)).toEqual(notes);
      await window.getByTestId('session-player-regenerate').click();
      await expect.poll(async () => JSON.stringify(await notesOf(window, drums.id))).not.toBe(JSON.stringify(notes));
      // Follow a rhythm source (the bass track): at least 75 % of kicks within a sixteenth of a bass onset.
      await window.getByTestId('session-player-rhythm-source').selectOption(bass.trackId);
      await expect
        .poll(
          async () =>
            (
              (await project(window)).sessionPlayerRegions.find(r => r.id === drums.id)?.parameters['drums'] as {
                rhythmSourceTrackId: string | null;
              }
            ).rhythmSourceTrackId
        )
        .toBe(bass.trackId);
      await expect
        .poll(async () => {
          const bassOnsets = must((await project(window)).midiRegions.find(r => r.id === bass.regionId)).notes.map(
            n => n.startTick
          );
          const kicks = (await notesOf(window, drums.id)).filter(n => n.pitch === 36);
          return (
            kicks.filter(k => bassOnsets.some(o => Math.abs(o - k.startTick) <= Ppq / 4)).length /
            Math.max(1, kicks.length)
          );
        })
        .toBeGreaterThanOrEqual(0.75);
      // The kit is multi-output: the kick plays on its own strip.
      state = await project(window);
      const kickPad = must(
        must(must(state.tracks.find(t => t.id === drummer.id)).drumMachine).pads.find(p => p.note === 36)
      );
      const kickOnly = await bounce(window, folder, 'Kick', 8 * Bar, [kickPad.trackId]);
      expect(db(rms(kickOnly.channels[0] ?? new Float32Array()))).toBeGreaterThan(-60);
      // Convert to MIDI region and to a pattern region: the notes are the generated notes.
      notes = await notesOf(window, drums.id);
      await commands(window, [{ type: 'copyRegions', regionIds: [drums.id], offsetTicks: 8 * Bar }]);
      const copy = must(
        (await project(window)).sessionPlayerRegions.find(r => r.trackId === drummer.id && r.startTick === 8 * Bar)
      );
      const copyNotes = await notesOf(window, copy.id);
      await window.getByTestId('session-player-convert-midi').click();
      await expect.poll(async () => (await project(window)).midiRegions.some(r => r.trackId === drummer.id)).toBe(true);
      const converted = must((await project(window)).midiRegions.find(r => r.trackId === drummer.id));
      expect(converted.notes.map(n => [n.pitch, n.startTick, n.velocity])).toEqual(
        notes.map(n => [n.pitch, n.startTick, n.velocity])
      );
      await finished(
        window,
        await api<JobShape>(window, 'POST', `/project/regions/${copy.id}/session-player/convert`, { target: 'pattern' })
      );
      await expect
        .poll(async () => (await project(window)).patternRegions.filter(r => r.trackId === drummer.id).length)
        .toBe(2);
      // Eight bars of 16ths make two four-bar pattern regions; their steps are the generated notes, in place.
      const hit = (pitch: number, tick: number) => `${pitch}@${tick}`;
      const stepped = (await project(window)).patternRegions
        .filter(r => r.trackId === drummer.id)
        .flatMap(r =>
          r.pattern.rows.flatMap(row =>
            row.steps.flatMap((step, s) =>
              step.on
                ? [
                    hit(
                      row.note,
                      r.startTick - 8 * Bar + s * r.pattern.stepTicks + Math.round(step.offset * r.pattern.stepTicks)
                    )
                  ]
                : []
            )
          )
        );
      const generatedHits = new Set(copyNotes.map(n => hit(n.pitch, n.startTick)));
      expect(stepped.every(h => generatedHits.has(h))).toBe(true);
      // Two hits of one drum in a step are one step: the louder (the pattern's grid is the limit).
      expect(stepped.length).toBeGreaterThanOrEqual(0.95 * generatedHits.size);
      measured(
        '3 drummer',
        `fill at the section end, deterministic, regenerates, follows the bass, multi-output; converts to MIDI (${converted.notes.length} notes) and to 2 pattern regions (${stepped.length} of ${generatedHits.size} hits)`
      );

      // ---- 4. Bass and Keyboard Players following the chord track
      await window.getByTestId('add-track-menu').click();
      await window.getByTestId('add-bass').click();
      await window.getByTestId('add-track-menu').click();
      await window.getByTestId('add-keyboard').click();
      await expect
        .poll(
          async () =>
            (await project(window)).tracks.filter(t => t.kind === 'bassPlayer' || t.kind === 'keyboardPlayer').length
        )
        .toBe(2);
      state = await project(window);
      const chords = state.chordTrack.chords;
      for (const region of state.sessionPlayerRegions.filter(
        r => state.tracks.find(t => t.id === r.trackId)?.kind === 'bassPlayer'
      )) {
        const generated = await notesOf(window, region.id);
        const range = region.parameters['bass'] as { lowNote: number; highNote: number };
        expect(generated.every(n => n.pitch >= range.lowNote && n.pitch <= range.highNote)).toBe(true);
        for (const chord of chords.filter(
          c => c.tick >= region.startTick && c.tick < region.startTick + region.lengthTicks
        )) {
          const down = generated.find(n => Math.abs(region.startTick + n.startTick - chord.tick) <= Ppq / 8);
          expect(down && down.pitch % 12).toBe(pc(chord.bass ?? chord.root));
        }
      }
      let worstVoice = 0;
      let voiceMoves = 0;
      for (const region of state.sessionPlayerRegions.filter(
        r => state.tracks.find(t => t.id === r.trackId)?.kind === 'keyboardPlayer'
      )) {
        const generated = await notesOf(window, region.id);
        let passing = 0;
        for (const [i, chord] of chords.entries()) {
          const end = chords[i + 1]?.tick ?? chord.tick + Bar;
          const voiced = generated.filter(
            n => region.startTick + n.startTick >= chord.tick && region.startTick + n.startTick < end
          );
          const tones = tonesOf(chord.root, chord.quality).add(pc(chord.bass ?? chord.root)); // the slash bass counts
          passing += voiced.filter(n => !tones.has(n.pitch % 12)).length;
        }
        // Voice leading: each chord's voicing is the right hand's chord tones while it lasts, struck together or (this
        // style) arpeggiated; of voicings of the same size on neighbouring chords, no voice moves by more than a fifth.
        const range = region.parameters['keys'] as { lowNote: number };
        const voicings = chords.map((chord, i) => {
          const end = chords[i + 1]?.tick ?? chord.tick + Bar;
          const tones = tonesOf(chord.root, chord.quality).add(pc(chord.bass ?? chord.root));
          const heard = generated
            .filter(n => n.pitch >= range.lowNote && tones.has(n.pitch % 12))
            .filter(n => region.startTick + n.startTick >= chord.tick && region.startTick + n.startTick < end)
            .map(n => n.pitch);
          return [...new Set(heard)].sort((x, y) => x - y);
        });
        for (const [k, voicing] of voicings.entries()) {
          const before = voicings[k - 1];
          if (before?.length && before.length === voicing.length) {
            voiceMoves++;
            voicing.forEach((p, v) => {
              worstVoice = Math.max(worstVoice, Math.abs(p - must(before[v])));
            });
          }
        }
        expect(passing / Math.max(1, generated.length)).toBeLessThan(0.1);
      }
      expect(voiceMoves).toBeGreaterThan(0);
      expect(worstVoice).toBeLessThanOrEqual(7);
      measured(
        '4 players',
        `bass roots on every chord change, in range; keys chord tones, worst voice move ${worstVoice} semitones of ${voiceMoves} chord changes`
      );

      // ---- 5. Mastering Assistant: analyse; Clean and Valve at −14 LUFS within 0.5 LU, true peak ≤ −1 dBTP
      // The song is its eight bars: the pattern conversion's copy after them goes, or the analysis would hear it too.
      await commands(window, [
        {
          type: 'deleteRegions',
          regionIds: (await project(window)).patternRegions.filter(r => r.trackId === drummer.id).map(r => r.id)
        }
      ]);
      await menu(app, 'masteringAssistant');
      await window.getByTestId('mastering-analyse').click();
      await expect(window.getByTestId('mastering-analysis')).toBeVisible({ timeout: 180_000 });
      const songEnd = 8 * Bar;
      let processed = 0;
      for (const character of ['clean', 'valve']) {
        await window.getByTestId(`mastering-character-${character}`).click();
        const master = await bounce(window, folder, `Master ${character}`, songEnd);
        const lufs = integratedLufs(master.channels, master.sampleRate);
        const peak = truePeakDbtp(master.channels);
        expect(Math.abs(lufs + 14)).toBeLessThanOrEqual(0.5);
        expect(peak).toBeLessThanOrEqual(-1);
        processed = lufs;
        measured('5 mastering', `${character}: ${lufs.toFixed(2)} LUFS, ${peak.toFixed(2)} dBTP`);
      }
      // Valve's even harmonics on a test tone exceed Clean's by at least 6 dB.
      const testTone = join(folder, 'Mastering tone.wav');
      writeWav(testTone, sine(200, -10, 4, Rate), Rate);
      const testFile = await api<{ id: string }>(window, 'POST', '/project/files/import', { path: testTone });
      await commands(window, [{ type: 'addTrack', kind: 'audio', stereo: false, name: 'Test Tone' }]);
      const testTrack = (await project(window)).tracks.find(t => t.name === 'Test Tone')?.id ?? '';
      await commands(window, [
        { type: 'addRegion', trackId: testTrack, fileId: testFile.id, startTick: 0 },
        { type: 'setStrip', trackId: testTrack, solo: true }
      ]);
      const evens: Record<string, number> = {};
      for (const character of ['clean', 'valve']) {
        await window.getByTestId(`mastering-character-${character}`).click();
        // Through the master (a stem would not reach the assistant): the tone alone, past the assistant's settling.
        const out = await bounce(window, folder, `Tone ${character}`, 2 * Bar);
        evens[character] = evenHarmonicsDb((out.channels[0] ?? new Float32Array()).subarray(Rate), 200, out.sampleRate);
      }
      await commands(window, [{ type: 'setStrip', trackId: testTrack, solo: false, mute: true }]);
      expect(must(evens['valve']) - must(evens['clean'])).toBeGreaterThanOrEqual(6);
      // The level-matched bypass is within 1 LU of the processed loudness.
      await window.getByTestId('mastering-bypass').click();
      const bypassed = await bounce(window, folder, 'Master bypassed', songEnd);
      const bypassedLufs = integratedLufs(bypassed.channels, bypassed.sampleRate);
      expect(Math.abs(bypassedLufs - processed)).toBeLessThanOrEqual(1);
      await window.getByTestId('mastering-bypass').click();
      measured(
        '5 mastering',
        `even harmonics: Valve ${evens['valve']?.toFixed(1)} dB, Clean ${evens['clean']?.toFixed(1)} dB; bypass ${bypassedLufs.toFixed(2)} LUFS against ${processed.toFixed(2)}`
      );

      await window.getByTestId('mastering-window-close').click();

      // ---- 6. T3 instruments and effects
      const model = await instrumentTrack(window, 'Model', 'anode.physical-model', 2);
      for (const pitch of [36, 48, 60, 72, 84]) {
        await commands(window, [
          {
            type: 'addNotes',
            regionId: model.regionId,
            notes: [{ pitch, velocity: 100, channel: 1, startTick: 0, lengthTicks: Bar }]
          }
        ]);
        const out = await bounce(window, folder, `Model ${pitch}`, 2 * Bar, [model.trackId]);
        const hz = 440 * 2 ** ((pitch - 69) / 12);
        const cents = centsFrom(
          pitchOf(out.channels[0] ?? new Float32Array(), Rate, 4800, 28800, hz / 1.2, hz * 1.2),
          pitch
        );
        expect(Math.abs(cents)).toBeLessThan(5);
        await commands(window, [
          {
            type: 'deleteNotes',
            regionId: model.regionId,
            noteIds: must((await project(window)).midiRegions.find(r => r.id === model.regionId)).notes.map(
              n => (n as unknown as { id: number }).id
            )
          }
        ]);
      }
      // The custom panel binds the host's parameters: a pickup moved on the drawing moves pickup1Position.
      await selectRegion(window, 'Model');
      const pickup = window.getByTestId('physical-model-panel').getByRole('slider', { name: 'Pickup A position' });
      await pickup.focus();
      await window.keyboard.press('ArrowRight');
      await expect
        .poll(async () => (await instrumentOf(window, model.trackId))?.parameters['pickup1Position'])
        .toBeCloseTo(21, 3);
      // Lower damping, longer decay (the −60 dB time at least 1.5×): a struck string, the damping its own parameter.
      const decays: number[] = [];
      const struck = await instrumentTrack(window, 'Struck', 'anode.physical-model', 6);
      await commands(window, [
        { type: 'setParameter', trackId: struck.trackId, parameterId: 'exciter1Type', value: 3 },
        {
          type: 'addNotes',
          regionId: struck.regionId,
          notes: [{ pitch: 60, velocity: 100, channel: 1, startTick: 0, lengthTicks: 11 * Ppq * 2 }]
        }
      ]);
      for (const damping of [60, 40]) {
        await commands(window, [
          { type: 'setParameter', trackId: struck.trackId, parameterId: 'damping', value: damping }
        ]);
        const out = await bounce(window, folder, `Struck ${damping}`, 6 * Bar, [struck.trackId]);
        decays.push(must(decayTime(out.channels[0] ?? new Float32Array(), Rate)));
      }
      expect(must(decays[1]) / must(decays[0])).toBeGreaterThanOrEqual(1.5);
      measured(
        '6 physical model',
        `C2–C6 within 5 cents; −60 dB in ${decays.map(d => d.toFixed(2)).join(' s at 60 % damping, ')} s at 40 %`
      );
      // The tape-replay instrument: the test pack's strings over a few keys; a held note stops at its tape's length.
      const tape = await instrumentTrack(window, 'Tape', 'anode.tape-replay', 6);
      await commands(window, [
        { type: 'applyPatch', trackId: tape.trackId, patchId: 'tape_strings' },
        { type: 'setParameter', trackId: tape.trackId, parameterId: 'wow', value: 0 },
        { type: 'setParameter', trackId: tape.trackId, parameterId: 'flutter', value: 0 },
        {
          type: 'addNotes',
          regionId: tape.regionId,
          notes: [{ pitch: 57, velocity: 100, channel: 1, startTick: 0, lengthTicks: 20 * Ppq }]
        }
      ]);
      const tapeOut = await bounce(window, folder, 'Tape', 6 * Bar, [tape.trackId]);
      const tapeLeft = tapeOut.channels[0] ?? new Float32Array();
      expect(db(rms(tapeLeft, 7 * Rate, 7.9 * Rate))).toBeGreaterThan(-50);
      expect(db(rms(tapeLeft, 8.3 * Rate, 9 * Rate))).toBeLessThan(-60);
      const tapeCents = centsFrom(pitchOf(tapeLeft, Rate, Rate, 2 * Rate, 150, 300), 57);
      expect(Math.abs(tapeCents)).toBeLessThan(5);
      measured(
        '6 tape',
        `stops by 8.3 s (${db(rms(tapeLeft, 8.3 * Rate, 9 * Rate)).toFixed(1)} dB after it), A3 ${tapeCents.toFixed(2)} cents`
      );
      // Sample Alchemy: the test sample (a harmonic tone on C4) played on G4 in the additive and granular modes.
      const sampleTone = sine(261.626, -9, 3, Rate).map(
        (v, i) => v + 0.5 * 0.35 * Math.sin((2 * Math.PI * 523.251 * i) / Rate)
      );
      const samplePath = join(folder, 'Alchemy sample.wav');
      writeWav(samplePath, sampleTone, Rate);
      const alchemy = await instrumentTrack(window, 'Alchemy', 'anode.sample-alchemy', 2);
      await commands(window, [
        { type: 'loadInstrumentFile', trackId: alchemy.trackId, path: samplePath },
        {
          type: 'addNotes',
          regionId: alchemy.regionId,
          notes: [{ pitch: 67, velocity: 100, channel: 1, startTick: 0, lengthTicks: Bar + Bar / 2 }]
        }
      ]);
      const alchemyCents: string[] = [];
      for (const [mode, modeName] of ['additive', 'granular'].entries()) {
        await commands(window, [{ type: 'setParameter', trackId: alchemy.trackId, parameterId: 'mode', value: mode }]);
        const out = await bounce(window, folder, `Alchemy ${modeName}`, 2 * Bar, [alchemy.trackId]);
        const cents = centsFrom(pitchOf(out.channels[0] ?? new Float32Array(), Rate, Rate / 2, 2 * Rate, 300, 600), 67);
        expect(Math.abs(cents)).toBeLessThan(5);
        alchemyCents.push(`${modeName} ${cents.toFixed(2)}`);
      }
      measured('6 sample alchemy', `G4 from a C4 sample, cents off: ${alchemyCents.join(', ')}`);
      // Saturation: THD rises with drive (three settings, monotone), on the test tone's stem.
      await commands(window, [
        { type: 'setStrip', trackId: testTrack, mute: false },
        { type: 'addInsert', trackId: testTrack, pluginId: 'anode.saturation' }
      ]);
      const saturationId = must(
        must((await project(window)).tracks.find(t => t.id === testTrack)).strip.inserts.find(
          i => i.pluginId === 'anode.saturation'
        )
      ).id;
      const saturation: number[] = [];
      for (const drive of [10, 50, 90]) {
        await commands(window, [
          { type: 'setInsertParameter', trackId: testTrack, insertId: saturationId, parameterId: 'drive', value: drive }
        ]);
        const out = await bounce(window, folder, `Saturation ${drive}`, 2 * Bar, [testTrack]);
        saturation.push(thd(out.channels[0] ?? new Float32Array(), 200, Rate));
      }
      expect(saturation[0]).toBeLessThan(must(saturation[1]));
      expect(saturation[1]).toBeLessThan(must(saturation[2]));
      measured('6 saturation', `THD ${saturation.map(t => (t * 100).toFixed(2) + ' %').join(' < ')}`);
      await commands(window, [{ type: 'setStrip', trackId: testTrack, mute: true }]);

      // The Down Mixer on a 5.1 bus to stereo: C and Ls take BS.775's −3 dB, measured on the bus's stem at its output.
      const toneOnBus = async (trackName: string, samples: Float32Array, bus: number, angle: number) => {
        const path = join(folder, `${trackName}.wav`);
        writeWav(path, samples, Rate);
        const imported = await api<{ id: string }>(window, 'POST', '/project/files/import', { path });
        await commands(window, [{ type: 'addTrack', kind: 'audio', stereo: false, name: trackName }]);
        const trackId = must((await project(window)).tracks.find(t => t.name === trackName)).id;
        await commands(window, [
          { type: 'addRegion', trackId, fileId: imported.id, startTick: 0 },
          { type: 'setOutput', trackId, output: `bus:${bus}` },
          { type: 'setPanner', trackId, mode: 'surround', angle, diversity: 1 }
        ]);
      };
      const surroundBus = async (bus: number, trackName: string, inputFormat: string, pluginId: string) => {
        await commands(window, [{ type: 'addAux', bus, name: trackName }]);
        const trackId = must((await project(window)).tracks.find(t => t.name === trackName)).id;
        await commands(window, [
          { type: 'setStripFormat', trackId, inputFormat },
          { type: 'addInsert', trackId, pluginId }
        ]);
        return trackId;
      };
      const downMixBus = await surroundBus(11, 'Down Mix Bus', 'surround51', 'anode.down-mixer');
      await toneOnBus('Down Mix Centre', sine(500, -12, 4, Rate), 11, 0);
      await toneOnBus('Down Mix Surround', sine(1000, -12, 4, Rate), 11, 110);
      const downMixed = await bounce(window, folder, 'Down Mixed', Bar, [downMixBus]);
      expect(downMixed.channels).toHaveLength(2);
      const mid = (c: Float32Array | undefined) => (c ?? new Float32Array()).subarray(Rate / 2, Rate + Rate / 2);
      const centreLeft = db(amplitude(mid(downMixed.channels[0]), 500, Rate)) + 12;
      const centreRight = db(amplitude(mid(downMixed.channels[1]), 500, Rate)) + 12;
      const surroundLeft = db(amplitude(mid(downMixed.channels[0]), 1000, Rate)) + 12;
      const surroundRight = db(amplitude(mid(downMixed.channels[1]), 1000, Rate)) + 12;
      expect(Math.abs(centreLeft + 3.01)).toBeLessThan(0.1);
      expect(Math.abs(centreRight + 3.01)).toBeLessThan(0.1);
      expect(Math.abs(surroundLeft + 3.01)).toBeLessThan(0.1);
      expect(surroundRight).toBeLessThan(-60);
      measured(
        '6 down mixer',
        `C → L ${centreLeft.toFixed(2)}, R ${centreRight.toFixed(2)} dB; Ls → L ${surroundLeft.toFixed(2)} dB, R off`
      );
      // Binaural Post-Processing on a 7.1.4 bus: noise in the side surround, 90° left.
      const binauralBus = await surroundBus(12, 'Binaural Bus', 'surround714', 'anode.binaural');
      await toneOnBus('Binaural Noise', noise(-12, 4, Rate), 12, 90);
      const ears = await bounce(window, folder, 'Binaural', Bar, [binauralBus]);
      expect(ears.channels).toHaveLength(2);
      const { ild, itdMs } = interauralDifferences(mid(ears.channels[0]), mid(ears.channels[1]), Rate);
      expect(ild).toBeGreaterThanOrEqual(6);
      expect(itdMs).toBeGreaterThanOrEqual(0.5);
      expect(itdMs).toBeLessThanOrEqual(0.8);
      measured('6 binaural', `ILD ${ild.toFixed(1)} dB, ITD ${itdMs.toFixed(3)} ms`);

      // ---- 7. Environment: physical input → transformer (+12) → arpeggiator (up, 1/16) → a track's input; a monitor
      await menu(app, 'environment');
      await expect(window.getByTestId('environment-canvas')).toBeVisible();
      await expect(window.getByTestId('environment-node-Physical Input')).toBeVisible();
      await window.getByTestId('environment-palette-transformer').click();
      await window.getByTestId('environment-palette-arpeggiator').click();
      await window.getByTestId('environment-palette-trackInput').click();
      await window.getByTestId('environment-palette-monitor').click();
      await window.getByTestId('environment-palette-fader').click();
      await window.getByTestId('environment-palette-controlTarget').click();
      state = await project(window);
      const object = (kind: string) => must(state.environment.objects.find(o => o.kind === kind));
      await commands(window, [
        {
          type: 'setEnvironmentObject',
          objectId: object('transformer').id,
          conditions: [{ field: 'status', op: 'inside', a: 128, b: 159 }],
          operations: [{ field: 'pitch', op: 'add', a: 12, b: 0 }]
        },
        { type: 'setEnvironmentObject', objectId: object('trackInput').id, trackId: keys.trackId },
        {
          type: 'setEnvironmentObject',
          objectId: object('controlTarget').id,
          trackId: keys.trackId,
          address: 'volume'
        },
        { type: 'setEnvironmentObject', objectId: object('fader').id, parameters: { message: 0, number: 7 } },
        { type: 'disconnectEnvironment', cableIds: state.environment.cables.map(c => c.id) },
        {
          type: 'connectEnvironment',
          fromObjectId: object('physicalInput').id,
          outlet: 0,
          toObjectId: object('transformer').id,
          inlet: 0
        },
        {
          type: 'connectEnvironment',
          fromObjectId: object('transformer').id,
          outlet: 0,
          toObjectId: object('arpeggiator').id,
          inlet: 0
        },
        {
          type: 'connectEnvironment',
          fromObjectId: object('arpeggiator').id,
          outlet: 0,
          toObjectId: object('trackInput').id,
          inlet: 0
        },
        {
          type: 'connectEnvironment',
          fromObjectId: object('transformer').id,
          outlet: 0,
          toObjectId: object('monitor').id,
          inlet: 0
        },
        {
          type: 'connectEnvironment',
          fromObjectId: object('fader').id,
          outlet: 0,
          toObjectId: object('controlTarget').id,
          inlet: 0
        }
      ]);
      // Play a chord through live MIDI while recording: the region holds it an octave up, on the 1/16 grid.
      await commands(window, [
        { type: 'setTrack', trackId: keys.trackId, recordArmed: true },
        { type: 'setStrip', trackId: keys.trackId, mute: false }
      ]);
      await transport(window, 'locate', 16 * Bar);
      await transport(window, 'record');
      await api(window, 'POST', '/midi/live', {
        trackId: keys.trackId,
        events: [60, 64, 67].map(data1 => ({ status: 0x90, data1, data2: 100 }))
      });
      // Note 72, an octave over 60, which Anode names C4 (Logic's C3 for 60).
      await expect(window.getByTestId('environment-node-Monitor')).toContainText('Note On C4');
      await window.waitForTimeout(2000);
      await api(window, 'POST', '/midi/live', {
        trackId: keys.trackId,
        events: [60, 64, 67].map(data1 => ({ status: 0x80, data1, data2: 0 }))
      });
      await transport(window, 'stop');
      await expect
        .poll(
          async () =>
            (await project(window)).midiRegions.some(r => r.trackId === keys.trackId && r.startTick >= 15 * Bar),
          { timeout: 30_000 }
        )
        .toBe(true);
      state = await project(window);
      const recorded = state.midiRegions
        .filter(r => r.trackId === keys.trackId && r.startTick >= 15 * Bar)
        .flatMap(r => r.notes.map(n => ({ ...n, startTick: r.startTick + n.startTick })));
      expect(new Set(recorded.map(n => n.pitch))).toEqual(new Set([72, 76, 79]));
      expect(recorded.every(n => n.startTick % (Ppq / 4) === 0)).toBe(true);
      // A fader object (CC 7 to that track's volume) moved on screen changes the strip's volume.
      const before = must(state.tracks.find(t => t.id === keys.trackId)).strip.volumeDb;
      const stripFader = window
        .getByRole('group', { name: /^Track \d+: Keys$/ })
        .getByRole('slider')
        .first();
      const shown = await stripFader.getAttribute('aria-valuetext');
      const fader = window.getByTestId('environment-node-Fader').getByRole('slider');
      await fader.focus();
      await window.keyboard.press('End');
      await expect
        .poll(async () => must((await project(window)).tracks.find(t => t.id === keys.trackId)).strip.volumeDb)
        .not.toBe(before);
      await expect(stripFader).not.toHaveAttribute('aria-valuetext', shown ?? '');
      const moved = must((await project(window)).tracks.find(t => t.id === keys.trackId)).strip.volumeDb;
      measured(
        '7 environment',
        `recorded ${recorded.length} notes (${[...new Set(recorded.map(n => n.pitch))].join(', ')}) on 16ths; the fader moved the strip ${before} → ${moved.toFixed(1)} dB`
      );

      // ---- 8. Save, close and reopen: everything is intact; the Session Player regions regenerate identically
      const saved = await project(window);
      const generated = await Promise.all(saved.sessionPlayerRegions.map(r => notesOf(window, r.id)));
      await menu(app, 'saveProject');
      await expect(window.getByRole('img', { name: 'Unsaved changes' })).toBeHidden();
      await menu(app, 'closeProject');
      await expect(window.getByTestId('new-project-name')).toBeVisible();
      await api(window, 'POST', '/projects/open', { path: saved.path });
      const reopened = await project(window);
      expect(reopened.chordTrack).toEqual(saved.chordTrack);
      expect(reopened.environment).toEqual(saved.environment);
      expect(reopened.sessionPlayerRegions).toEqual(saved.sessionPlayerRegions);
      expect(reopened.midiRegions.map(r => [r.id, r.pitchSource, r.key])).toEqual(
        saved.midiRegions.map(r => [r.id, r.pitchSource, r.key])
      );
      expect(reopened.master.inserts.find(i => i.pluginId === 'anode.mastering-assistant')?.mastering).toEqual(
        saved.master.inserts.find(i => i.pluginId === 'anode.mastering-assistant')?.mastering
      );
      expect(await Promise.all(reopened.sessionPlayerRegions.map(r => notesOf(window, r.id)))).toEqual(generated);
      measured(
        '8 reopen',
        `${reopened.chordTrack.chords.length} chords, ${reopened.sessionPlayerRegions.length} Session Player regions (same notes), the Environment and the assistant as saved`
      );
    } finally {
      if (!process.env['ANODE_E2E_KEEP']) {
        rmSync(folder, { recursive: true, force: true });
      } else {
        console.warn(`Kept ${folder}`);
      }
    }
  });
});
