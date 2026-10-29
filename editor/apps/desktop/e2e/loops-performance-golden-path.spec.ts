import type { ElectronApplication, Locator, Page } from '@playwright/test';
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { expect, test } from './fixtures';

/** Workstream B's test content pack (`scripts/build-content.mjs`, docs/07-phase-6.md). */
const TestPack = resolve(__dirname, '../../../../engine/artifacts/content/TestPack.anodepack');

const Rate = 48_000;
const Bpm = 110;
const Beat = 60 / Bpm;
const Sixteenth = Beat / 4;

// ---- reading bounces (as in advanced-audio-golden-path.spec.ts)

/** A WAV's samples, mixed to mono (16/24-bit PCM or 32-bit float). */
function readWav(path: string): { sampleRate: number; samples: Float32Array } {
  const bytes = readFileSync(path);
  let offset = 12;
  let format = 1;
  let channels = 1;
  let bits = 16;
  let sampleRate = Rate;
  let data: Buffer | null = null;
  while (offset + 8 <= bytes.length) {
    const id = bytes.toString('ascii', offset, offset + 4);
    const size = bytes.readUInt32LE(offset + 4);
    if (id === 'fmt ') {
      format = bytes.readUInt16LE(offset + 8);
      channels = bytes.readUInt16LE(offset + 10);
      sampleRate = bytes.readUInt32LE(offset + 12);
      bits = bytes.readUInt16LE(offset + 22);
    } else if (id === 'data') {
      data = bytes.subarray(offset + 8, offset + 8 + size);
    }
    offset += 8 + size + (size % 2);
  }
  if (!data) {
    throw new Error(`${path} has no data chunk`);
  }
  const step = bits / 8;
  const frames = Math.floor(data.length / (step * channels));
  const samples = new Float32Array(frames);
  for (let f = 0; f < frames; f++) {
    let sum = 0;
    for (let c = 0; c < channels; c++) {
      const i = (f * channels + c) * step;
      sum +=
        format === 3
          ? data.readFloatLE(i)
          : bits === 24
            ? data.readIntLE(i, 3) / 8_388_608
            : data.readInt16LE(i) / 32_768;
    }
    samples[f] = sum / channels;
  }
  return { sampleRate, samples };
}

/**
 * Onsets in seconds: where the 1 ms energy jumps well above the 20 ms before it (silence before the file's start; at most
 * one per 60 ms), each placed at the first sample within 10 dB of the hit's peak (not at a stretcher's pre-echo). With `emphasis`, the energy is
 * the first difference's, which weights a hit's attack over the low tail of the one before (a hat on a kick's boom), and
 * a 6 dB jump counts (a ratchet on its own hit's tail).
 */
function onsets(path: string, emphasis = false): number[] {
  const { sampleRate, samples } = readWav(path);
  if (emphasis) {
    for (let i = samples.length - 1; i > 0; i--) {
      samples[i] = (samples[i] ?? 0) - (samples[i - 1] ?? 0);
    }
  }
  const floor = emphasis ? 1e-6 : 1e-4;
  const jump = emphasis ? 4 : 8;
  const window = Math.round(sampleRate / 1000);
  const energy: number[] = [];
  for (let i = 0; i + window <= samples.length; i += window) {
    let sum = 0;
    for (let k = 0; k < window; k++) {
      sum += (samples[i + k] ?? 0) ** 2;
    }
    energy.push(sum / window);
  }
  const found: number[] = [];
  for (let i = 0; i < energy.length; i++) {
    let before = 0;
    for (let k = Math.max(0, i - 20); k < i; k++) {
      before += energy[k] ?? 0;
    }
    before /= 20;
    const now = energy[i] ?? 0;
    if (now > floor && now > jump * before && (found.length === 0 || i / 1000 - (found.at(-1) ?? 0) > 0.06)) {
      // To the sample: the first one from the millisecond before within 10 dB of the hit's peak (its next 20 ms).
      const from = Math.max(0, (i - 1) * window);
      const to = Math.min(samples.length, (i + 20) * window);
      let peak = 0;
      for (let n = from; n < to; n++) {
        peak = Math.max(peak, Math.abs(samples[n] ?? 0));
      }
      let at = from;
      while (at < to && Math.abs(samples[at] ?? 0) < peak / Math.sqrt(10)) {
        at++;
      }
      found.push(at / sampleRate);
    }
  }
  return found;
}

/** The pitch (Hz) between two times, by autocorrelation over 40…1 000 Hz. */
function pitchAt(path: string, from: number, to: number): number {
  const { sampleRate, samples } = readWav(path);
  const a = Math.round(from * sampleRate);
  const n = Math.round((to - from) * sampleRate);
  const r = (lag: number) => {
    let sum = 0;
    for (let i = 0; i < n - lag; i++) {
      sum += (samples[a + i] ?? 0) * (samples[a + i + lag] ?? 0);
    }
    return sum;
  };
  let best = 0;
  let bestLag = 0;
  for (let lag = Math.floor(sampleRate / 1000); lag <= Math.ceil(sampleRate / 40); lag++) {
    const sum = r(lag);
    if (sum > best) {
      best = sum;
      bestLag = lag;
    }
  }
  const [l, c, h] = [r(bestLag - 1), r(bestLag), r(bestLag + 1)];
  const shift = (l - h) / (2 * (l - 2 * c + h));
  return sampleRate / (bestLag + (Number.isFinite(shift) ? shift : 0));
}

/** How far (cents) a frequency lies from the nearest pitch of pitch class `pc` (0 = C). */
function centsFromPitchClass(hz: number, pc: number): number {
  const midi = 69 + 12 * Math.log2(hz / 440);
  const off = (((midi - pc) % 12) + 12) % 12;
  return 100 * Math.min(off, 12 - off);
}

/** The farthest any onset lies from a grid of `step` seconds, in milliseconds (there must be onsets to measure). */
function gridErrorMs(times: readonly number[], step: number): number {
  expect(times.length).toBeGreaterThan(4);
  return 1000 * Math.max(...times.map(t => Math.abs(t / step - Math.round(t / step)) * step));
}

/** The farthest an onset lies from its nearest expected time (and vice versa), in milliseconds. */
function matchErrorMs(actual: readonly number[], expected: readonly number[]): number {
  const nearest = (t: number, list: readonly number[]) => Math.min(...list.map(x => Math.abs(x - t)));
  return 1000 * Math.max(...actual.map(t => nearest(t, expected)), ...expected.map(t => nearest(t, actual)));
}

/** Records what the exit criterion measured as a test annotation, and returns it. */
function measured(what: string, value: number, unit: string): number {
  test.info().annotations.push({ type: 'measured', description: `${what}: ${value.toFixed(3)} ${unit}` });
  return value;
}

// ---- driving the app

// Lane geometry at the default view: the ruler is 24 px, lanes 72 px, a bar 192 px at the default zoom.
const Ruler = 24;
const Lane = 72;

async function regionCount(window: Page): Promise<number> {
  const label = await window.getByRole('img', { name: /^Tracks area:/ }).getAttribute('aria-label');
  return Number(/(\d+) regions?/.exec(label ?? '')?.[1] ?? 0);
}

async function trackCount(window: Page): Promise<number> {
  const label = await window.getByRole('img', { name: /^Tracks area:/ }).getAttribute('aria-label');
  return Number(/on (\d+) tracks?/.exec(label ?? '')?.[1] ?? 0);
}

/** The Key column of the Loop Browser's rows. */
function rowKeys(rows: Locator): Promise<string[]> {
  return rows.evaluateAll(list => list.map(row => (row.children[5]?.textContent ?? '').trim()));
}

/** Answers the next native open dialog with `path`. */
async function answerDialog(app: ElectronApplication, path: string): Promise<void> {
  await app.evaluate(({ dialog }, file) => {
    dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [file] });
  }, path);
}

/** Solos exactly one track (by name) for a bounce; `''` unsolos all. */
async function soloOnly(window: Page, names: readonly string[], solo: string): Promise<void> {
  for (const name of names) {
    // The track header's key (the Mixer, when shown, has one too).
    const key = window.locator('an-track-header').getByRole('button', { name: `Solo ${name}`, exact: true });
    const on = (await key.getAttribute('aria-pressed')) === 'true';
    if (on !== (name === solo)) {
      await key.click();
    }
  }
}

/** Bounces bars 1…`endBar` to `folder/name.wav` and returns the path. */
async function bounce(window: Page, folder: string, name: string, endBar: number): Promise<string> {
  await window.keyboard.press('ControlOrMeta+b');
  await expect(window.getByTestId('bounce-dialog')).toBeVisible();
  await window.getByTestId('bounce-name').fill(name);
  await window.getByTestId('bounce-folder').fill(folder);
  await window.getByTestId('bounce-end').locator('input').fill(String(endBar));
  await window.getByTestId('bounce-start-button').click();
  await expect(window.getByTestId('bounce-dialog')).toBeHidden({ timeout: 60_000 });
  const path = join(folder, `${name}.wav`);
  expect(existsSync(path)).toBe(true);
  return path;
}

/** The name of the track with number `n` (its header is "Track n: <name>"). */
async function trackName(window: Page, n: number): Promise<string> {
  const label = await window.getByRole('group', { name: new RegExp(`^Track ${n}: `) }).getAttribute('aria-label');
  return (label ?? '').replace(/^Track \d+: /, '');
}

/** The track headers' names, by number ("Track n: <name>"). */
async function trackNames(window: Page): Promise<Map<number, string>> {
  const labels = await window
    .locator('an-track-header')
    .evaluateAll(list => list.map(header => header.getAttribute('aria-label') ?? ''));
  return new Map(
    labels.map(label => {
      const [, n, name] = /^Track (\d+): (.*)$/.exec(label) ?? [];
      return [Number(n), name ?? ''] as const;
    })
  );
}

/**
 * A new software instrument track: its number and name, once it is the selected track (the inspector shows it). It
 * goes after the selected track (the DMD's pads are tracks too), so it is found by its header.
 */
async function addInstrumentTrack(window: Page): Promise<[number, string]> {
  const before = await trackNames(window);
  await window.getByTestId('add-track-menu').click();
  await window.getByTestId('add-instrument-track').click();
  await expect.poll(async () => (await trackNames(window)).size).toBe(before.size + 1);
  const known = new Set(before.values());
  const [number, name] = [...(await trackNames(window))].find(([, n]) => !known.has(n)) ?? [0, ''];
  expect(name).not.toBe('');
  await expect(window.getByRole('group', { name: `Track ${number}: ${name}`, exact: true })).toHaveClass(
    /\bbg-muted\b/
  );
  return [number, name];
}

/**
 * Scrolls the Tracks area as a person does (the wheel over the lanes, which the headers follow) until `target` is
 * inside it (lanes off screen are not drawn, so it may not exist until then); returns the area's box.
 */
async function revealInTracks(
  window: Page,
  target: Locator
): Promise<{ x: number; y: number; width: number; height: number }> {
  const timeline = await window.locator('an-timeline').boundingBox();
  if (!timeline) {
    throw new Error('The Tracks area is not on screen.');
  }
  await window.mouse.move(timeline.x + timeline.width / 2, timeline.y + timeline.height / 2);
  for (let i = 0; i < 30; i++) {
    const box = (await target.count()) > 0 ? await target.boundingBox() : null;
    if (box && box.y >= timeline.y + Ruler && box.y + box.height <= timeline.y + timeline.height) {
      return timeline;
    }
    await window.mouse.wheel(0, box && box.y < timeline.y + Ruler ? -Lane : Lane);
    await window.waitForTimeout(100);
  }
  throw new Error('Could not scroll the Tracks area to show it.');
}

/** Track `n`'s lane in the Tracks area, scrolled into view: its top and height in the window (lanes line up with headers). */
async function laneOf(window: Page, n: number): Promise<{ x: number; top: number; height: number }> {
  const header = window.getByRole('group', { name: new RegExp(`^Track ${n}: `) });
  const timeline = await revealInTracks(window, header);
  const box = await header.boundingBox();
  if (!box) {
    throw new Error(`Track ${n} is not on screen.`);
  }
  return { x: timeline.x, top: box.y, height: box.height };
}

/** Drags a Loop Browser row onto a target (HTML5 drag and drop, which Chromium runs for Playwright). */
async function dragLoop(row: Locator, target: Locator, position?: { x: number; y: number }): Promise<void> {
  await row.dragTo(target, position ? { targetPosition: position } : {});
}

/** Presses a step of a Step Sequencer row in On/Off mode. */
async function lightSteps(window: Page, row: string, steps: readonly number[]): Promise<void> {
  for (const step of steps) {
    await window.getByRole('button', { name: `${row} step ${step}`, exact: true }).click();
  }
}

/** Changes a step's value in a value mode by `presses` arrow presses (negative: down). */
async function nudgeStep(window: Page, mode: string, row: string, step: number, presses: number): Promise<void> {
  await window.getByTestId('step-edit-mode').getByRole('radio', { name: mode }).click();
  const slider = window.getByRole('slider', { name: `${row} step ${step} ${mode.toLowerCase()}`, exact: true });
  await slider.focus();
  for (let i = 0; i < Math.abs(presses); i++) {
    await window.keyboard.press(presses > 0 ? 'ArrowUp' : 'ArrowDown');
  }
}

/** Holds a key of the on-screen keyboard for `ms`. */
async function holdKey(window: Page, name: string, ms: number): Promise<void> {
  await window.getByRole('tab', { name: 'Keyboard' }).click();
  const key = window.getByRole('button', { name, exact: true });
  await key.dispatchEvent('pointerdown', { button: 0, pointerId: 1 });
  await window.waitForTimeout(ms);
  await key.dispatchEvent('pointerup', { button: 0, pointerId: 1 });
}

/** Adds an Audio FX insert to the first empty slot inside `scope`, from its category menu. */
async function addInsert(window: Page, scope: Locator, category: string, plugin: RegExp): Promise<void> {
  await scope.getByTestId('insert-empty').first().click();
  // A submenu opens on hover (a click on its item toggles it shut again).
  await window.getByRole('menuitem', { name: category, exact: true }).hover();
  await window.getByRole('menuitem', { name: plugin }).first().click();
}

// The Phase 6 exit criterion (docs/07-phase-6.md): a beat-maker's session built and performed from Anode's own content
// on the real app with the null device: the test pack, loops following tempo and key, the Drum Machine Designer, the
// Step Sequencer, MIDI FX, Smart Controls, Live Loops with Remix FX and performance recording, the T2 plugins, and
// reopening. It needs every workstream (A's launcher and MIDI FX, B's Server and the test pack, D's and E's plugins).
test('builds and performs a beat from Anode content: packs, loops, DMD, Step Sequencer, MIDI FX, Smart Controls, Live Loops, T2 sound, reopening', async ({
  app,
  window
}) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-loops-golden-'));
  const bounces = join(scratch, 'Bounces');
  const ir = resolve(__dirname, '../../../../engine/artifacts/content/TestIR.wav');
  const timeline = window.locator('an-timeline');
  try {
    expect(existsSync(TestPack)).toBe(true);
    // A 110 bpm project in D minor.
    await window.getByTestId('new-project-name').fill('Beats');
    await window.getByTestId('new-project-folder').fill(scratch);
    await window.getByTestId('create-project').click();
    await expect(window.getByTestId('project-name')).toHaveText('Beats');
    await window.getByTestId('lcd-tempo').click();
    await window.getByRole('textbox', { name: 'Tempo in bpm' }).fill(String(Bpm));
    await window.keyboard.press('Enter');
    await expect(window.getByTestId('lcd-tempo')).toHaveText(String(Bpm));
    await window.getByTestId('global-tracks').click();
    await window.getByRole('button', { name: 'Choose global tracks' }).click();
    await window.getByRole('menuitemcheckbox', { name: /Key$/ }).click();
    await window.keyboard.press('Escape');
    await window.getByRole('button', { name: /^Key C major$/ }).dblclick();
    await window.getByRole('textbox', { name: /key/i }).fill('D minor');
    await window.keyboard.press('Enter');
    await expect(window.getByTestId('lcd-key')).toContainText('D');
    await window.getByTestId('global-tracks').click();

    // 1. Content: install the test pack in the Sound Library window; the Loop Browser lists its loops; filter by the
    //    keyword buttons and by key; preview a loop in sync.
    await window.keyboard.press('o');
    await window.getByTestId('open-sound-library').click();
    await expect(window.getByTestId('sound-library-dialog')).toBeVisible();
    await answerDialog(app, TestPack);
    await window.getByTestId('install-pack-file').click();
    await expect(window.getByTestId('pack-row')).toHaveCount(1, { timeout: 30_000 });
    await window.getByTestId('close-sound-library').click();
    const rows = window.getByTestId('loop-row');
    await expect(rows.first()).toBeVisible();
    const all = await rows.count();
    const keyword = (name: string) =>
      window.getByRole('group', { name: 'Keywords' }).getByRole('button', { name, exact: true });
    await keyword('Drums').click();
    await expect(keyword('Drums')).toHaveAttribute('aria-pressed', 'true');
    await expect.poll(() => rows.count()).toBeLessThan(all);
    await window.getByTestId('loop-key').selectOption({ label: 'C' });
    await expect.poll(async () => (await rowKeys(rows)).every(key => /^Cm?$/.test(key))).toBe(true);
    // Preview plays audio loops (MIDI and pattern loops have no preview): the first audio drum loop in C.
    const drumLoop = rows.and(window.locator('[title*="Audio"]')).first();
    const drumName = (await drumLoop.getAttribute('aria-label')) ?? '';
    await drumLoop.click();
    await expect(window.getByRole('button', { name: `Preview ${drumName}` })).toHaveAttribute('aria-pressed', 'true');
    await drumLoop.click();
    await expect(window.getByTestId('loop-sync')).toContainText(String(Bpm));

    // 2. Loops: the audio drum loop (100 bpm, C) onto a new track at bar 1, and a MIDI bass loop onto another.
    await dragLoop(drumLoop, timeline, { x: 4, y: Ruler + 10 });
    await expect.poll(() => regionCount(window), { timeout: 30_000 }).toBe(1);
    const drums = await trackName(window, 1);
    await window.getByTestId('loop-reset').click();
    await keyword('Bass').click();
    const bassLoop = rows.first();
    await dragLoop(bassLoop, timeline, { x: 4, y: Ruler + Lane + 10 });
    await expect.poll(() => regionCount(window), { timeout: 30_000 }).toBe(2);
    const bass = await trackName(window, 2);
    await soloOnly(window, [drums, bass], drums);
    const drumBounce = await bounce(window, bounces, 'Drum loop', 3);
    expect(
      measured(
        'Loop follows tempo: farthest onset off the 110 bpm 16ths',
        gridErrorMs(onsets(drumBounce), Sixteenth),
        'ms'
      )
    ).toBeLessThanOrEqual(5);
    await soloOnly(window, [drums, bass], bass);
    const bassBounce = await bounce(window, bounces, 'Bass loop', 2);
    const firstNote = onsets(bassBounce)[0] ?? 0;
    const bassPitch = pitchAt(bassBounce, firstNote + 0.03, firstNote + 0.13);
    // The test pack's bass loop is in C and starts on its root: transposed to D.
    expect(measured('MIDI loop: first note off D', centsFromPitchClass(bassPitch, 2), 'cents')).toBeLessThanOrEqual(30);
    await soloOnly(window, [drums, bass], '');

    // 3. Drum Machine Designer: a DMD from the pack's kit; edit a pad's sound, give a pad Quick Sampler, and put an
    //    insert on a pad's channel strip.
    await window.getByTestId('add-track-menu').click();
    await window.getByTestId('add-drum-machine').hover();
    await window.locator('[data-kit]').first().click();
    await expect.poll(() => trackCount(window)).toBeGreaterThan(3);
    const dmd = await trackName(window, 3);
    await window.getByRole('tab', { name: 'Editors' }).click();
    await window.getByRole('tab', { name: 'Drum Machine' }).click();
    const pad = (note: number) => window.locator(`[data-testid="drum-pad"][data-note="${note}"]`);
    const padSelected = (note: number) =>
      expect(window.getByRole('gridcell').filter({ has: pad(note) })).toHaveAttribute('aria-selected', 'true');
    await pad(38).click();
    await padSelected(38);
    const decay = window.getByTestId('pad-editor').getByRole('slider', { name: 'Decay' });
    const before = await decay.getAttribute('aria-valuetext');
    await decay.focus();
    await window.keyboard.press('PageUp');
    await expect(decay).not.toHaveAttribute('aria-valuetext', before ?? '');
    // An empty pad: a new Drum Synth pad there, selected once it exists, then given Quick Sampler.
    await pad(46).click();
    await padSelected(46);
    await window.getByTestId('pad-instrument').selectOption({ label: 'Quick Sampler' });
    await expect(window.getByTestId('pad-instrument')).toHaveValue('anode.quick-sampler');
    await window.keyboard.press('x');
    const pads = window.getByRole('group', { name: `Pads of ${dmd}` });
    await addInsert(window, pads, 'Dynamics', /^Compressor$/);
    await expect(pads.getByTestId('insert-slot')).toHaveCount(1);

    // 4. Step Sequencer: a pattern region on the DMD: kick, snare and hat rows; per-step velocity, gate, repeats,
    //    chance and offset; a 7-step hat row against 16-step rows; ping-pong on the snare. A bounce has the hits where
    //    the pattern says (1 ms); converted to MIDI it plays the same.
    await window.getByRole('button', { name: 'Go to start' }).click();
    await window.getByRole('group', { name: `Track 3: ${dmd}` }).click();
    await window.getByRole('tab', { name: 'Editors' }).click();
    await window.getByRole('tab', { name: 'Step Sequencer' }).click();
    await window.getByTestId('new-pattern-region').click();
    await expect(window.getByRole('grid', { name: 'Pattern' })).toBeVisible();
    const hat = ((await window.getByRole('row', { name: /Hat/ }).first().getAttribute('aria-label')) ?? 'Hat').trim();
    await lightSteps(window, 'Kick', [1, 5, 9, 13]);
    await lightSteps(window, 'Snare', [5, 13]);
    await lightSteps(window, hat, [1, 2, 3, 4, 5, 6, 7]);
    await window.getByRole('spinbutton', { name: `${hat} length` }).fill('7');
    await window.keyboard.press('Enter');
    await window.getByRole('button', { name: 'Snare play mode' }).click();
    await window.getByRole('menuitemradio', { name: 'Ping-Pong' }).click();
    await nudgeStep(window, 'Velocity', 'Kick', 5, -10);
    await nudgeStep(window, 'Gate', 'Snare', 5, 2);
    // Repeats 2 on snare step 13: a second hit half a 16th later.
    await nudgeStep(window, 'Repeats', 'Snare', 13, 1);
    // Chance 0 on hat step 3: it never plays.
    await window.getByTestId('step-edit-mode').getByRole('radio', { name: 'Chance' }).click();
    const chance = window.getByRole('slider', { name: `${hat} step 3 chance`, exact: true });
    await chance.focus();
    await window.keyboard.press('Home');
    // Offset +50 % on kick step 9: half a 16th late.
    await window.getByTestId('step-edit-mode').getByRole('radio', { name: 'Offset' }).click();
    const offset = window.getByRole('slider', { name: 'Kick step 9 offset', exact: true });
    await offset.focus();
    await window.keyboard.press('End');
    await expect(offset).toHaveAttribute('aria-valuenow', '50');
    // Where the hits are, in 16ths of bar 1: kicks 0, 4, 8.5, 12; snares 4, 12, 12.5; hats every step of the 7-step
    // row but its third (positions 0…15 with p mod 7 ≠ 2).
    const hits = new Set<number>([0, 4, 8.5, 12, 12.5]);
    for (let p = 0; p < 16; p++) {
      if (p % 7 !== 2) {
        hits.add(p);
      }
    }
    const expected = [...hits].sort((a, b) => a - b).map(s => s * Sixteenth);
    const tracksNow = [drums, bass, dmd];
    await soloOnly(window, tracksNow, dmd);
    const patternBounce = await bounce(window, bounces, 'Pattern', 2);
    const patternHits = onsets(patternBounce, true).filter(t => t < 4 * Beat);
    expect(
      measured('Pattern: farthest hit off the pattern', matchErrorMs(patternHits, expected), 'ms')
    ).toBeLessThanOrEqual(1);
    await window.getByTestId('convert-pattern-to-midi').click();
    await expect(window.getByRole('img', { name: /^Piano Roll:/ })).toBeVisible();
    const convertedBounce = await bounce(window, bounces, 'Converted', 2);
    const convertedHits = onsets(convertedBounce, true).filter(t => t < 4 * Beat);
    expect(
      measured('Converted pattern: farthest hit off the pattern', matchErrorMs(convertedHits, patternHits), 'ms')
    ).toBeLessThanOrEqual(1);
    await soloOnly(window, tracksNow, '');

    // 5. MIDI FX: an Arpeggiator (1/16, up, 2 octaves) and a Chord Trigger on a synth track; one held note gives an
    //    onset every 16th (2 ms).
    const [synthNumber, synth] = await addInstrumentTrack(window);
    const regionsBeforeSynth = await regionCount(window);
    // A plucked sound, so each arpeggiated note is an onset: no attack, the shortest release (the arpeggiator's gate
    // leaves a gap before the next note).
    for (const [parameter, end] of [
      ['ampAttack', '0'],
      ['ampRelease', '1']
    ] as const) {
      const knob = window.locator(`[data-parameter="${parameter}"]`).getByRole('slider');
      await knob.focus();
      await window.keyboard.press('Home');
      await expect(knob).toHaveAttribute('aria-valuenow', end);
    }
    await window.keyboard.press('x');
    const synthFx = window.getByRole('group', { name: `MIDI FX of ${synth}` });
    await synthFx.getByTestId('midi-fx-empty').click();
    await window.getByRole('menuitem', { name: 'Arpeggiator' }).click();
    await synthFx.getByTestId('midi-fx-empty').click();
    await window.getByRole('menuitem', { name: 'Chord Trigger' }).click();
    await expect(synthFx.getByTestId('midi-fx-slot')).toHaveCount(2);
    await synthFx.getByRole('button', { name: new RegExp(`MIDI FX 1 on ${synth}: Arpeggiator`) }).click();
    const arp = window.getByRole('dialog', { name: `${synth}: Arpeggiator` });
    for (const [parameter, value] of [
      ['rate', '1/16'],
      ['mode', 'Up']
    ] as const) {
      await arp.locator(`[data-parameter="${parameter}"]`).locator('xui-select').click();
      await window.getByRole('option', { name: value, exact: true }).click();
      await expect(window.getByRole('listbox')).toHaveCount(0);
    }
    // The closing list hands focus back to its select: focus the knob once it has.
    const octaves = arp.locator('[data-parameter="octaves"]').getByRole('slider');
    await octaves.focus();
    await expect(octaves).toBeFocused();
    await window.keyboard.press('Home');
    await expect(octaves).toHaveAttribute('aria-valuenow', '1');
    await window.keyboard.press('ArrowUp');
    await expect(octaves).toHaveAttribute('aria-valuenow', '2');
    await arp.getByRole('button', { name: /^Close / }).click();
    // One note held for a bar: an empty region, then a long note drawn with the pencil.
    const synthLane = await laneOf(window, synthNumber);
    await window.mouse.dblclick(synthLane.x + 30, synthLane.top + synthLane.height * 0.75);
    await expect.poll(() => regionCount(window)).toBeGreaterThan(regionsBeforeSynth);
    await window.mouse.click(synthLane.x + 30, synthLane.top + 6);
    await window.getByRole('tab', { name: 'Editors' }).click();
    await window.getByRole('tab', { name: 'Piano Roll' }).click();
    await window.getByRole('radio', { name: 'Pencil' }).last().click();
    const roll = window.locator('an-piano-roll-grid');
    const rollBox = await roll.boundingBox();
    if (!rollBox) {
      throw new Error('The Piano Roll is not on screen.');
    }
    await window.mouse.move(rollBox.x + 2, rollBox.y + rollBox.height / 2);
    await window.mouse.down();
    await window.mouse.move(rollBox.x + rollBox.width * 0.9, rollBox.y + rollBox.height / 2, { steps: 8 });
    await window.mouse.up();
    const allTracks = [drums, bass, dmd, synth];
    await soloOnly(window, allTracks, synth);
    const arpBounce = await bounce(window, bounces, 'Arpeggio', 2);
    const arpOnsets = onsets(arpBounce).filter(t => t > Sixteenth / 2 && t < 3 * Beat);
    expect(
      measured('Arpeggiator: farthest onset off the 16ths', gridErrorMs(arpOnsets, Sixteenth), 'ms')
    ).toBeLessThanOrEqual(2);
    expect(arpOnsets.length).toBeGreaterThanOrEqual(10);
    await soloOnly(window, allTracks, '');

    // 6. Smart Controls: automatic mapping fills 8 knobs; knob 1 to two parameters, one inverted through a curve;
    //    the knob automated; Compare shows the saved state.
    const synthHeader = window.getByRole('group', { name: `Track ${synthNumber}: ${synth}`, exact: true });
    await revealInTracks(window, synthHeader);
    await synthHeader.click();
    await window.keyboard.press('b');
    await window.getByTestId('smart-auto-map').click();
    const knobs = window.getByTestId('smart-knob');
    await expect(knobs).toHaveCount(8);
    await window.getByTestId('smart-inspector-toggle').click();
    const knob1 = window.locator('[data-testid="smart-knob"][data-control="knob1"]');
    await knob1.click();
    const mappings = window.getByTestId('smart-mapping');
    const mapped = await mappings.count();
    await window.getByTestId('smart-add-mapping').click();
    await window
      .getByRole('menuitem', { name: /Resonance/ })
      .first()
      .click();
    await expect(mappings).toHaveCount(mapped + 1);
    if (mapped === 0) {
      await window.getByTestId('smart-add-mapping').click();
      await window
        .getByRole('menuitem', { name: /Cutoff/ })
        .first()
        .click();
      await expect(mappings).toHaveCount(2);
    }
    const second = mappings.nth(1);
    await second.getByTestId('smart-mapping-invert').click();
    await expect(second.getByTestId('smart-mapping-invert')).toHaveAttribute('aria-pressed', 'true');
    const point = second.getByTestId('mapping-graph').getByRole('slider', { name: 'Point 1' });
    await point.focus();
    await window.keyboard.press('Enter');
    await expect(second.getByTestId('mapping-graph').getByRole('slider')).toHaveCount(3);
    await second.getByTestId('mapping-graph').getByRole('slider', { name: 'Point 2' }).focus();
    await window.keyboard.press('ArrowUp');
    // Automate the knob: a lane for it (once the new lane is there), the knob's entry (the Smart Controls come last in
    // the menu, and a control mapped to Cutoff is named so, as the parameter is), two points.
    await window.keyboard.press('a');
    const laneHeaders = window.getByRole('group', { name: new RegExp(`^Automation of ${synth}: `) });
    const lanesBefore = await laneHeaders.count();
    await window.getByTestId('add-automation-lane').last().click();
    await expect(laneHeaders).toHaveCount(lanesBefore + 1);
    await laneHeaders.last().getByTestId('automation-parameter').click();
    const knobName = ((await knob1.locator('[id^="an-knob-"]').textContent()) ?? '').trim();
    expect(knobName).not.toBe('');
    await window.getByRole('menuitemradio', { name: knobName, exact: true }).last().click();
    await expect(window.locator('[data-target="smartControl:knob1"]')).toHaveCount(1);
    const lane = window.getByRole('group', { name: `${synth} ${knobName} automation`, exact: true });
    await revealInTracks(window, lane);
    const laneBox = await lane.boundingBox();
    await lane.click({ position: { x: 192, y: (laneBox?.height ?? 40) / 2 } });
    await lane.click({ position: { x: 3 * 192, y: (laneBox?.height ?? 40) / 4 } });
    await expect(lane.getByRole('slider')).toHaveCount(2);
    await window.keyboard.press('a');
    // Compare: save, turn the knob, then Compare shows the saved value.
    await window.keyboard.press('ControlOrMeta+s');
    await expect(window.getByTestId('unsaved')).toHaveCount(0);
    // The lane is the knob's own (the saved package says so): two points on `smartControl:knob1`.
    const synthFile = readdirSync(join(scratch, 'Beats.anode', 'tracks'))
      .map(file => JSON.parse(readFileSync(join(scratch, 'Beats.anode', 'tracks', file), 'utf8')))
      .find(file => file.track?.name === synth);
    expect(
      (synthFile?.automation ?? []).find((l: { target: string }) => l.target === 'smartControl:knob1')?.points
    ).toHaveLength(2);
    // Knob 1 drives two parameters, one inverted through a three-point graph.
    const knob1Saved = synthFile?.track?.smartControls?.controls?.find((c: { id: string }) => c.id === 'knob1');
    expect(knob1Saved?.mappings).toHaveLength(2);
    expect(knob1Saved?.mappings?.[1]).toMatchObject({ invert: true });
    expect(knob1Saved?.mappings?.[1]?.graph).toHaveLength(3);
    const knobSlider = knob1.getByRole('slider');
    const saved = await knobSlider.getAttribute('aria-valuetext');
    await knobSlider.focus();
    await window.keyboard.press('PageUp');
    await expect(knobSlider).not.toHaveAttribute('aria-valuetext', saved ?? '');
    await window.getByTestId('smart-compare').click();
    await expect(window.getByTestId('smart-compare')).toHaveAttribute('aria-pressed', 'true');
    await expect(knobSlider).toHaveAttribute('aria-valuetext', saved ?? '');
    await window.getByTestId('smart-compare').click();
    await expect(window.getByTestId('smart-compare')).toHaveAttribute('aria-pressed', 'false');

    // 7. Live Loops: the pattern, the drum loop, the bass loop and a recorded MIDI cell; scene 1, then a scene-2
    //    cell quantised to the next bar; Remix FX during playback; a performance recorded into the Tracks area.
    //    Scene 1 holds the drum and bass loops and the recorded cell; scene 2 the pack's pattern on the DMD, alone on
    //    its track, so its region in the performance starts where it was launched, and its first step is a kick.
    await revealInTracks(window, window.getByRole('group', { name: /^Track 1: / }));
    await window.getByTestId('live-loops-toggle').click();
    await expect(window.getByRole('grid', { name: 'Live Loops' })).toBeVisible();
    await window.getByTestId('add-scene').click();
    await window.getByTestId('add-scene').click();
    const cell = (track: string, scene: number) =>
      window.getByRole('gridcell', { name: new RegExp(`, ${track}, scene ${scene}$`) });
    const filled = (c: Locator) => expect(c).not.toHaveAttribute('data-state', 'empty');
    await window.keyboard.press('o');
    await window.getByTestId('loop-reset').click();
    await keyword('Drums').click();
    await window.getByTestId('loop-key').selectOption({ label: 'C' });
    await expect.poll(async () => (await rowKeys(rows)).every(key => /^Cm?$/.test(key))).toBe(true);
    await dragLoop(rows.and(window.locator('[title*="Audio"]')).first(), cell(drums, 1));
    await filled(cell(drums, 1));
    await window.getByTestId('loop-reset').click();
    await keyword('Bass').click();
    await expect.poll(() => rows.count()).toBeLessThan(all);
    await dragLoop(rows.first(), cell(bass, 1));
    await filled(cell(bass, 1));
    await window.getByTestId('loop-reset').click();
    await keyword('Drums').click();
    await window
      .getByRole('group', { name: 'Loop kind' })
      .getByRole('button', { name: 'Pattern', exact: true })
      .click();
    await expect
      .poll(async () =>
        (await rows.evaluateAll(list => list.map(row => row.getAttribute('title') ?? ''))).every(t =>
          t.includes('Pattern')
        )
      )
      .toBe(true);
    await dragLoop(rows.first(), cell(dmd, 2));
    await filled(cell(dmd, 2));
    await window.getByTestId('loop-reset').click();
    await window.getByRole('button', { name: 'Go to start' }).click();
    await cell(synth, 1).click({ button: 'right' });
    await window.getByRole('menuitem', { name: 'Record into Cell' }).click();
    await holdKey(window, 'C3', 400);
    await window.waitForTimeout(2 * 4 * Beat * 1000);
    await cell(synth, 1).click({ button: 'right' });
    await window.getByRole('menuitem', { name: 'Stop Recording' }).click();
    await filled(cell(synth, 1));
    await window.getByRole('button', { name: 'Go to start' }).click();
    const cells = window.getByTestId('live-loops-cell');
    await expect(cells.and(window.locator(':not([data-state="empty"])'))).toHaveCount(4);
    await window.getByTestId('live-loops-quantize').click();
    await window.getByRole('menuitemradio', { name: '1 Bar' }).click();
    const regionsBefore = await regionCount(window);
    await window.getByTestId('live-loops-record').click();
    await expect(window.getByTestId('live-loops-record')).toHaveAttribute('aria-pressed', 'true');
    await window.getByRole('button', { name: 'Launch scene 1' }).click();
    await expect(cell(drums, 1)).toHaveAttribute('data-state', 'playing', { timeout: 10_000 });
    const patternCell = cell(dmd, 2);
    await patternCell.getByRole('button').click();
    await expect(patternCell).toHaveAttribute('data-state', 'queued');
    await expect(patternCell).toHaveAttribute('data-state', 'playing', { timeout: 10_000 });
    // Remix FX: the filter and the repeater, engaged while held.
    await window.getByTestId('remix-fx-toggle').click();
    for (const pad of ['remix-pad-1', 'remix-pad-2']) {
      const box = await window.getByTestId(pad).getByRole('slider').boundingBox();
      if (!box) {
        throw new Error(`${pad} is not on screen.`);
      }
      await window.mouse.move(box.x + box.width * 0.2, box.y + box.height * 0.8);
      await window.mouse.down();
      await window.mouse.move(box.x + box.width * 0.8, box.y + box.height * 0.2, { steps: 10 });
      await window.waitForTimeout(500);
      await window.mouse.up();
    }
    await window.waitForTimeout(4 * Beat * 1000);
    await window.getByTestId('live-loops-stop').click();
    await window.getByTestId('live-loops-record').click();
    await window.getByRole('button', { name: 'Stop', exact: true }).click();
    // The performance lands in the Tracks area: regions where the cells played, and Remix FX automation.
    await expect.poll(() => regionCount(window), { timeout: 30_000 }).toBeGreaterThan(regionsBefore);
    // The scene-2 cell started on a bar: back on the Tracks area, the DMD's first hit after its converted pattern
    // (bar 1) is the performed pattern's first kick, on a bar line.
    for (const track of allTracks) {
      const activation = window.getByRole('button', { name: `Live Loops activation of ${track}` });
      if ((await activation.getAttribute('aria-pressed')) === 'true') {
        await activation.click();
      }
    }
    await soloOnly(window, allTracks, dmd);
    const performed = await bounce(window, bounces, 'Performance', 8);
    const performedHits = onsets(performed, true).filter(t => t > 4 * Beat - 0.01);
    expect(performedHits.length).toBeGreaterThan(0);
    const launchedAt = performedHits[0] ?? 0;
    const Bar = 4 * Beat;
    expect(
      measured(
        `Live Loops: the scene-2 cell (at ${launchedAt.toFixed(4)} s) off the bar`,
        1000 * Math.abs(launchedAt - Math.round(launchedAt / Bar) * Bar),
        'ms'
      )
    ).toBeLessThanOrEqual(1);
    await soloOnly(window, allTracks, '');
    await window.getByTestId('live-loops-toggle').click();

    // 8. T2 sound: the FM synth, the drawbar organ (with its rotary), the electric piano and the wavetable synth on
    //    tracks; a convolution reverb with an imported IR and a Multipressor on buses; every T2 editor opens.
    for (const [instrument, panel] of [
      [/FM/, 'fm-panel'],
      [/Organ/, 'drawbars-panel'],
      [/Electric Piano/, null],
      [/Wavetable/, null]
    ] as const) {
      await addInstrumentTrack(window);
      await window.getByTestId('instrument-picker').click();
      await window.getByRole('option', { name: instrument }).first().click();
      await expect(window.getByTestId('instrument-picker')).toContainText(instrument);
      if (panel) {
        await expect(window.getByTestId(panel)).toBeVisible();
      }
      if (panel === 'drawbars-panel') {
        // The organ's built-in rotary.
        await expect(window.getByTestId(panel).locator('[data-parameter="rotarySpeed"]')).toBeVisible();
      }
    }
    await window.keyboard.press('x');
    const synthStrip = window.getByRole('group', { name: `${synth} channel strip`, exact: true });
    await synthStrip.getByTestId('add-send').click();
    await window.getByTestId('send-new-aux').click();
    await synthStrip.getByTestId('add-send').click();
    await window.getByTestId('send-new-aux').click();
    const auxes = window.getByRole('group', { name: /^Aux \d+ channel strip$/ });
    await expect(auxes).toHaveCount(2);
    // An insert's editor, by the slot's name (the slot is there once the Server has added it).
    const editorOf = async (strip: Locator, owner: string, effect: RegExp): Promise<Locator> => {
      await strip.getByRole('button', { name: new RegExp(`^Insert \\d+ on ${owner}: .*${effect.source}`) }).click();
      const dialog = window.getByRole('dialog', { name: new RegExp(`^${owner}: .*${effect.source}`) });
      await expect(dialog).toBeVisible();
      return dialog;
    };
    const closeEditor = (dialog: Locator) => dialog.getByRole('button', { name: /^Close / }).click();
    await addInsert(window, auxes.nth(0), 'Reverb', /Space|Convolution/);
    const space = await editorOf(auxes.nth(0), 'Aux 1', /Space|Convolution/);
    await answerDialog(app, ir);
    await space.getByTestId('plugin-import-file').click();
    await expect(space.getByTestId('plugin-file')).toContainText('.wav');
    await closeEditor(space);
    await addInsert(window, auxes.nth(1), 'Dynamics', /Multipressor/);
    const multipressor = await editorOf(auxes.nth(1), 'Aux 2', /Multipressor/);
    await expect(multipressor.getByTestId('multipressor-panel')).toBeVisible();
    await closeEditor(multipressor);
    // Every T2 effect's editor opens (on the master, one at a time).
    const master = window.locator('[data-strip="master"]');
    for (const [category, effect] of [
      ['EQ', /Linear Phase EQ/],
      ['Dynamics', /Enveloper/],
      ['Specialized', /Exciter/],
      ['Specialized', /SubBass/],
      ['Delay', /Delay Designer/],
      ['Pitch', /Pitch Shifter/],
      ['Pitch', /Pitch Correction/],
      ['Modulation', /Ensemble/],
      ['Modulation', /Rotor Cabinet/],
      ['Multi Effects', /Phat FX/],
      ['Multi Effects', /Step FX/],
      ['Multi Effects', /Beat Breaker/]
    ] as const) {
      await addInsert(window, master, category, effect);
      await closeEditor(await editorOf(master, 'Stereo Out', effect));
      const slot = master.getByRole('button', { name: new RegExp(`^Insert \\d+ on Stereo Out: ${effect.source}`) });
      await slot.click({ button: 'right' });
      await window.getByRole('menuitem', { name: /^Remove / }).click();
      await expect(slot).toHaveCount(0);
    }

    // 9. Save, close and reopen: everything is intact.
    const regions = await regionCount(window);
    const tracks = await trackCount(window);
    await window.keyboard.press('ControlOrMeta+s');
    await expect(window.getByTestId('unsaved')).toHaveCount(0);
    await window.getByRole('button', { name: 'Close project' }).click();
    await window.getByTestId('recent-Beats').click();
    await expect(window.getByTestId('project-name')).toHaveText('Beats');
    await expect.poll(() => regionCount(window)).toBe(regions);
    await expect.poll(() => trackCount(window)).toBe(tracks);
    await expect(window.getByTestId('lcd-tempo')).toHaveText(String(Bpm));
    await window.getByTestId('live-loops-toggle').click();
    await expect(window.getByTestId('live-loops-cell').and(window.locator(':not([data-state="empty"])'))).toHaveCount(
      4
    );
    await window.keyboard.press('x');
    await expect(window.getByRole('group', { name: `MIDI FX of ${synth}` }).getByTestId('midi-fx-slot')).toHaveCount(2);
    await expect(window.getByRole('group', { name: `Pads of ${dmd}` }).getByTestId('insert-slot')).toHaveCount(1);
    // The synth's Smart Controls: select it by its strip (the Tracks area is small with the Mixer and Keyboard up).
    await window
      .getByRole('group', { name: `${synth} channel strip`, exact: true })
      .getByText(synth, { exact: true })
      .click();
    await expect(window.getByRole('heading', { name: `Track: ${synth}`, exact: true })).toBeVisible();
    await window.keyboard.press('b');
    await expect(window.getByTestId('smart-knob')).toHaveCount(8);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
