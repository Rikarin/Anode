import type { ElectronApplication, Locator, Page } from '@playwright/test';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from './fixtures';

const Rate = 48_000;

// ---- WAV fixtures, written by the spec (docs/06-phase-5.md, the exit criterion)

/** A mono 16-bit WAV of `samples` (−1…1). */
function writeWav(path: string, samples: Float32Array, sampleRate = Rate): void {
  const data = Buffer.alloc(samples.length * 2);
  samples.forEach((s, i) => data.writeInt16LE(Math.round(Math.max(-1, Math.min(1, s)) * 32_767), 2 * i));
  const header = Buffer.alloc(44);
  header.write('RIFF', 0, 'ascii');
  header.writeUInt32LE(36 + data.length, 4);
  header.write('WAVEfmt ', 8, 'ascii');
  header.writeUInt32LE(16, 16);
  header.writeUInt16LE(1, 20);
  header.writeUInt16LE(1, 22);
  header.writeUInt32LE(sampleRate, 24);
  header.writeUInt32LE(sampleRate * 2, 28);
  header.writeUInt16LE(2, 32);
  header.writeUInt16LE(16, 34);
  header.write('data', 36, 'ascii');
  header.writeUInt32LE(data.length, 40);
  writeFileSync(path, Buffer.concat([header, data]));
}

/** A deterministic noise source (xorshift), so every run writes the same files. */
function noise(seed: number): () => number {
  let x = seed || 1;
  return () => {
    x ^= x << 13;
    x ^= x >>> 17;
    x ^= x << 5;
    return ((x >>> 0) / 4_294_967_295) * 2 - 1;
  };
}

/** Where the loop's hits are, in seconds: kicks on the beats, hats on the off-beat eighths; humanised ±15 ms. */
function loopHits(bpm: number, bars: number, humanise: boolean): { at: number; kick: boolean }[] {
  const beat = 60 / bpm;
  const jitter = noise(7);
  const hits: { at: number; kick: boolean }[] = [];
  for (let b = 0; b < bars * 4; b++) {
    for (const [offset, kick] of [
      [0, true],
      [0.5, false]
    ] as const) {
      const shift = humanise && b + offset > 0 ? jitter() * 0.015 : 0;
      hits.push({ at: (b + offset) * beat + shift, kick });
    }
  }
  return hits;
}

/** A drum loop of synthesised kicks (a decaying 55 Hz sine) and hats (a short noise burst). */
function drumLoop(bpm: number, bars: number, humanise: boolean): Float32Array {
  const out = new Float32Array(Math.round(((bars * 4 * 60) / bpm) * Rate));
  const hat = noise(3);
  for (const { at, kick } of loopHits(bpm, bars, humanise)) {
    const start = Math.round(at * Rate);
    const length = Math.round((kick ? 0.15 : 0.04) * Rate);
    for (let i = 0; i < length && start + i < out.length; i++) {
      const t = i / Rate;
      const value = kick
        ? 0.8 * Math.exp(-t / 0.05) * Math.sin(2 * Math.PI * 55 * t)
        : 0.3 * Math.exp(-t / 0.01) * hat();
      out[start + i] = (out[start + i] ?? 0) + value;
    }
  }
  return out;
}

/** A sine-like note (a few harmonics) with 10 ms fades. */
function addNote(out: Float32Array, from: number, seconds: number, hz: number): void {
  const start = Math.round(from * Rate);
  const length = Math.round(seconds * Rate);
  const fade = 0.01 * Rate;
  for (let i = 0; i < length && start + i < out.length; i++) {
    const t = i / Rate;
    const envelope = Math.min(1, i / fade, (length - i) / fade);
    const phase = 2 * Math.PI * hz * t;
    out[start + i] = 0.35 * envelope * (Math.sin(phase) + 0.25 * Math.sin(2 * phase) + 0.1 * Math.sin(3 * phase));
  }
}

/** The melody's notes: C4, D4, E4, G4, A4, each a few cents off, 0.6 s each with 50 ms gaps. */
const Melody = [
  { midi: 60, cents: 7 },
  { midi: 62, cents: -6 },
  { midi: 64, cents: 4 },
  { midi: 67, cents: -8 },
  { midi: 69, cents: 5 }
];
const NoteSeconds = 0.6;
const NoteStep = 0.65;

function hz(midi: number, cents = 0): number {
  return 440 * 2 ** ((midi - 69 + cents / 100) / 12);
}

function melody(): Float32Array {
  const out = new Float32Array(Math.round((Melody.length * NoteStep + 0.5) * Rate));
  Melody.forEach((note, i) => addNote(out, 0.1 + i * NoteStep, NoteSeconds, hz(note.midi, note.cents)));
  return out;
}

/** A phrase: four notes of 0.4 s with half a second of silence between them. */
const PhraseNotes = 4;
function phrase(): Float32Array {
  const out = new Float32Array(Math.round(4.2 * Rate));
  for (let i = 0; i < PhraseNotes; i++) {
    addNote(out, 0.2 + i * 0.9, 0.4, hz(57 + 2 * i));
  }
  return out;
}

// ---- reading bounces

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
 * Onsets in seconds: where the 1 ms energy jumps well above the 20 ms before it (at most one per 60 ms), each placed at
 * the first millisecond within 10 dB of the hit's peak (its next 20 ms), so a stretcher's pre-echo, a quiet smear tens
 * of dB below the hit, does not count as the hit.
 */
function onsets(path: string): number[] {
  const { sampleRate, samples } = readWav(path);
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
  for (let i = 20; i < energy.length; i++) {
    let before = 0;
    for (let k = i - 20; k < i; k++) {
      before += energy[k] ?? 0;
    }
    before /= 20;
    const now = energy[i] ?? 0;
    if (now > 1e-4 && now > 8 * before && (found.length === 0 || i / 1000 - (found.at(-1) ?? 0) > 0.06)) {
      const peak = Math.max(...energy.slice(i, i + 20));
      let at = i;
      while ((energy[at] ?? 0) < peak / 10) {
        at++;
      }
      found.push(at / 1000);
    }
  }
  return found;
}

/** The pitch (Hz) between two times, by autocorrelation over 60…1 000 Hz. */
function pitchAt(path: string, from: number, to: number): number {
  const { sampleRate, samples } = readWav(path);
  const a = Math.round(from * sampleRate);
  const n = Math.round((to - from) * sampleRate);
  let best = 0;
  let bestLag = 0;
  for (let lag = Math.floor(sampleRate / 1000); lag <= Math.ceil(sampleRate / 60); lag++) {
    let sum = 0;
    for (let i = 0; i < n - lag; i++) {
      sum += (samples[a + i] ?? 0) * (samples[a + i + lag] ?? 0);
    }
    if (sum > best) {
      best = sum;
      bestLag = lag;
    }
  }
  // Refine the peak with a parabola through its neighbours.
  const r = (lag: number) => {
    let sum = 0;
    for (let i = 0; i < n - lag; i++) {
      sum += (samples[a + i] ?? 0) * (samples[a + i + lag] ?? 0);
    }
    return sum;
  };
  const [l, c, h] = [r(bestLag - 1), r(bestLag), r(bestLag + 1)];
  const shift = (l - h) / (2 * (l - 2 * c + h));
  return sampleRate / (bestLag + (Number.isFinite(shift) ? shift : 0));
}

function cents(a: number, b: number): number {
  return 1200 * Math.log2(a / b);
}

function frames(path: string): number {
  return readWav(path).samples.length;
}

// ---- driving the app

// Lane geometry at the default view (global tracks hidden): the ruler is 24 px, lanes 72 px, 48 px per quarter note at
// 120 bpm (a bar 192 px); a region's name bar is its top 12 px.
const Ruler = 24;
const Lane = 72;
const Bar = 192;
const Quarter = Bar / 4;

async function expectRegions(window: Page, regions: number, tracks: number): Promise<void> {
  await expect(window.getByRole('img', { name: /^Tracks area:/ })).toHaveAttribute(
    'aria-label',
    `Tracks area: ${regions} ${regions === 1 ? 'region' : 'regions'} on ${tracks} ${tracks === 1 ? 'track' : 'tracks'}`
  );
}

/** The regions the Tracks area shows (from its label). */
async function regionCount(window: Page): Promise<number> {
  const label = await window.getByRole('img', { name: /^Tracks area:/ }).getAttribute('aria-label');
  return Number(/(\d+) regions?/.exec(label ?? '')?.[1] ?? 0);
}

/** Imports a WAV onto a new audio track (the desktop app's open dialog answers with `path`). */
async function importOnNewTrack(
  window: Page,
  app: ElectronApplication,
  path: string,
  n: number,
  name: string
): Promise<Locator> {
  await window.getByTestId('add-track').click();
  const header = window.getByRole('group', { name: `Track ${n}: ${name}` });
  await header.click();
  await app.evaluate(({ dialog }, file) => {
    dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [file] });
  }, path);
  const regions = await regionCount(window);
  await window.getByRole('button', { name: 'Import audio' }).click();
  // The import analyses the file first; the region lands when it is done.
  await expect.poll(() => regionCount(window), { timeout: 30_000 }).toBe(regions + 1);
  return header;
}

/** Selects the region at the start of lane `lane` (0-based) by its name bar. */
async function selectRegion(window: Page, lane: number, x = 20): Promise<void> {
  await window.locator('an-timeline').click({ position: { x, y: Ruler + lane * Lane + 6 } });
}

/** Solos exactly one track (by name) for a bounce. */
async function soloOnly(window: Page, names: readonly string[], solo: string): Promise<void> {
  for (const name of names) {
    const key = window.getByRole('button', { name: `Solo ${name}`, exact: true });
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

/** The middle of an element, in its own coordinates. */
async function middleOf(locator: Locator): Promise<{ x: number; y: number }> {
  const box = await locator.boundingBox();
  if (!box) {
    throw new Error('The element is not on screen.');
  }
  return { x: box.width / 2, y: box.height / 2 };
}

/** The farthest any onset lies from a grid of `step` seconds, in milliseconds (there must be onsets to measure). */
function gridErrorMs(times: readonly number[], step: number): number {
  expect(times.length).toBeGreaterThan(8);
  return 1000 * Math.max(...times.map(t => Math.abs(t / step - Math.round(t / step)) * step));
}

/** Records what the exit criterion measured as a test annotation (the JSON reporter lists them), and returns it. */
function measured(what: string, value: number, unit: string): number {
  test.info().annotations.push({ type: 'measured', description: `${what}: ${value.toFixed(3)} ${unit}` });
  return value;
}

// The Phase 5 exit criterion (docs/06-phase-5.md): audio as editable in time and pitch as MIDI, on the real app with
// the null device: Smart Tempo, Follow, Flex Time, quantised audio, Flex Pitch and Flex Pitch to MIDI, Strip Silence,
// tempo curves and beat mapping, Varispeed, track alternatives, bounce in place and selection-based processing.
test('edits audio in time and pitch: Smart Tempo, Follow, Flex Time and Pitch, quantise, Strip Silence, tempo curves, beat mapping, Varispeed, alternatives, bounce in place, processing, reopening', async ({
  app,
  window
}) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-advanced-golden-'));
  const bounces = join(scratch, 'Bounces');
  const files = {
    loop: join(scratch, 'Drums 100bpm.wav'),
    humanised: join(scratch, 'Drums humanised.wav'),
    melody: join(scratch, 'Melody.wav'),
    phrase: join(scratch, 'Phrase.wav')
  };
  writeWav(files.loop, drumLoop(100, 4, false));
  writeWav(files.humanised, drumLoop(100, 4, true));
  writeWav(files.melody, melody());
  writeWav(files.phrase, phrase());
  const timeline = window.locator('an-timeline');
  const tracks = ['Audio 1', 'Audio 2', 'Audio 3', 'Audio 4'];
  try {
    // 1. Smart Tempo: a new project in adapt mode; importing the 100 bpm loop sets the project tempo.
    await window.getByTestId('new-project-name').fill('Advanced');
    await window.getByTestId('new-project-folder').fill(scratch);
    await window.getByTestId('create-project').click();
    await expect(window.getByTestId('project-name')).toHaveText('Advanced');
    await window.getByTestId('tempo-mode').click();
    await window.getByRole('menuitemradio', { name: 'Adapt Project Tempo' }).click();
    await importOnNewTrack(window, app, files.loop, 1, 'Audio 1');
    await expectRegions(window, 1, 1);
    const bpm = measured('Adapted tempo', Number(await window.getByTestId('lcd-tempo').innerText()), 'bpm');
    expect(Math.abs(bpm - 100)).toBeLessThanOrEqual(0.5);
    // Open the Smart Tempo Editor on the region and move its second downbeat a little (then keep the file tempo).
    await selectRegion(window, 0);
    await window.keyboard.press('e');
    await window.getByRole('tab', { name: 'Smart Tempo' }).click();
    const downbeat = window.locator('[data-testid="smart-tempo-beat"][data-downbeat="true"]').nth(1);
    await downbeat.focus();
    await window.keyboard.press('ArrowRight');
    await window.getByTestId('smart-tempo-keep').click();

    // 2. Follow tempo: Flex & Follow on, then 120 bpm; a bounce has its onsets on the 120 bpm grid within 5 ms.
    await selectRegion(window, 0);
    await window.getByTestId('region-follow').selectOption('on');
    await window.getByTestId('lcd-tempo').click();
    await window.getByRole('textbox', { name: 'Tempo in bpm' }).fill('120');
    await window.keyboard.press('Enter');
    await expect(window.getByTestId('lcd-tempo')).toHaveText('120');
    const followed = await bounce(window, bounces, 'Follow', 5);
    expect(
      measured('Follow: farthest onset off the 120 bpm grid', gridErrorMs(onsets(followed), 0.25), 'ms')
    ).toBeLessThanOrEqual(5);

    // 3. Flex Time: flex on (Polyphonic), drag the marker of the transient at beat 3 right by a sixteenth, then Slicing;
    //    in a bounce the onset moved by the drag (within 2 ms).
    await window.getByTestId('flex-toggle').click();
    const drums = window.getByRole('group', { name: 'Track 1: Audio 1' });
    await drums.getByTestId('track-flex').click();
    await drums.getByTestId('track-flex-mode').click();
    await window.getByRole('menuitemradio', { name: 'Polyphonic' }).click();
    const flexRegion = window.getByTestId('flex-region').first();
    const box = await flexRegion.boundingBox();
    if (!box) {
      throw new Error('The flex region is not on screen.');
    }
    await window.mouse.move(box.x + 2 * Quarter, box.y + 4);
    await window.mouse.down();
    await window.mouse.move(box.x + 2 * Quarter + Quarter / 8, box.y + 4, { steps: 4 });
    await window.mouse.move(box.x + 2 * Quarter + Quarter / 4, box.y + 4, { steps: 4 });
    await window.mouse.up();
    const marker = flexRegion.getByRole('slider', { name: 'Flex marker 1' });
    await expect(marker).toHaveAttribute('aria-valuenow', String(2 * 960 + 240));
    await drums.getByTestId('track-flex-mode').click();
    await window.getByRole('menuitemradio', { name: 'Slicing' }).click();
    const sliced = await bounce(window, bounces, 'Sliced', 5);
    // The onset at 1.0 s (beat 3 at 120 bpm) now starts 125 ms later.
    const moved = Math.min(...onsets(sliced).map(t => 1000 * Math.abs(t - 1.125)));
    expect(measured('Flex marker: the marked onset off where the drag put it', moved, 'ms')).toBeLessThanOrEqual(2);

    // 4. Quantise audio: the humanised loop to 1/16; its onsets land on the grid within 5 ms.
    await importOnNewTrack(window, app, files.humanised, 2, 'Audio 2');
    await selectRegion(window, 1);
    await expect(window.getByRole('heading', { name: 'Region: Drums humanised' })).toBeVisible();
    await window.getByTestId('quantize-audio-grid').selectOption({ label: '1/16 note' });
    await soloOnly(window, tracks.slice(0, 2), 'Audio 2');
    const quantised = await bounce(window, bounces, 'Quantised', 5);
    expect(
      measured('Quantise: farthest onset off the 1/16 grid', gridErrorMs(onsets(quantised), 0.125), 'ms')
    ).toBeLessThanOrEqual(5);

    // 5. Flex Pitch on the melody: every note to 100 %, one note +2 semitones, another's vibrato halved; a bounce,
    //    re-analysed, is within 10 cents of the targets. Then Flex Pitch to MIDI.
    await importOnNewTrack(window, app, files.melody, 3, 'Audio 3');
    await selectRegion(window, 2);
    await window.getByRole('tab', { name: 'Audio Track' }).click();
    await window.getByTestId('flex-pitch-enable').click();
    const notes = window.getByTestId('pitch-note');
    await expect(notes).toHaveCount(Melody.length, { timeout: 30_000 });
    await window.getByTestId('perfect-pitch-percent').fill('100');
    await window.getByTestId('perfect-pitch-apply').click();
    await notes.nth(1).focus();
    await window.keyboard.press('ArrowUp');
    await window.keyboard.press('ArrowUp');
    // Select the fourth note away from its hotspots (top left, bottom left, centre and right), then halve its vibrato.
    await notes
      .nth(3)
      .click({ position: { x: (await middleOf(notes.nth(3))).x / 2, y: (await middleOf(notes.nth(3))).y } });
    const vibrato = window.locator('an-pitch-note').nth(3).getByTestId('pitch-hotspot-vibrato');
    await vibrato.focus();
    for (let i = 0; i < 5; i++) {
      await window.keyboard.press('ArrowDown');
    }
    await expect(vibrato).toHaveAttribute('aria-valuenow', '50');
    await soloOnly(window, tracks.slice(0, 3), 'Audio 3');
    const pitched = await bounce(window, bounces, 'Pitched', 3);
    const targets = Melody.map((note, i) => hz(note.midi + (i === 1 ? 2 : 0)));
    targets.forEach((target, i) => {
      const from = 0.1 + i * NoteStep + 0.15;
      const off = Math.abs(cents(pitchAt(pitched, from, from + 0.3), target));
      expect(measured(`Flex Pitch: note ${i + 1} off its target`, off, 'cents')).toBeLessThanOrEqual(10);
    });
    // Flex Pitch to MIDI: a new instrument track (named as any new one) under the melody's, with a region of its notes.
    await window.getByTestId('flex-pitch-to-midi').click();
    await expectRegions(window, 4, 4);
    const midiTrack = (
      (await window.getByRole('group', { name: /^Track 4: / }).getAttribute('aria-label')) ?? ''
    ).replace(/^Track 4: /, '');
    await selectRegion(window, 3);
    await expect(window.getByRole('img', { name: /^Piano Roll:/ })).toHaveAttribute(
      'aria-label',
      new RegExp(`^Piano Roll: ${Melody.length} notes in`)
    );

    // 6. Strip Silence on the phrase: one region per note.
    await importOnNewTrack(window, app, files.phrase, 5, 'Audio 4');
    await selectRegion(window, 4);
    await window.keyboard.press('Control+x');
    await expect(window.getByTestId('strip-silence-dialog')).toBeVisible();
    await window.getByTestId('strip-silence-threshold').fill('-40');
    await expect(window.getByTestId('strip-silence-count')).toContainText(`${PhraseNotes} regions`);
    await window.getByTestId('strip-silence-apply').click();
    // The drums, the humanised loop, the melody, its MIDI, and the phrase's notes, on five tracks.
    await expectRegions(window, 4 + PhraseNotes, 5);

    // 7. A tempo ramp with a curved shape; a beat mapped onto a transient of the loop in the Beat Mapping track.
    await window.getByTestId('global-tracks').click();
    await window.getByTestId('global-tempo').dblclick({ position: { x: 4 * Bar, y: 10 } });
    await window.getByTestId('tempo-point-0').click({ modifiers: ['Alt'] });
    const curve = window.getByTestId('tempo-curve-handle').first();
    await curve.focus();
    for (let i = 0; i < 6; i++) {
      await window.keyboard.press('ArrowUp');
    }
    await expect(curve).toHaveAttribute('aria-valuenow', '0.3');
    await window.getByRole('button', { name: 'Choose global tracks' }).click();
    await window.getByRole('menuitemcheckbox', { name: /Beat Mapping$/ }).click();
    await drums.click();
    const lane = window.getByTestId('beat-mapping-lane');
    const laneBox = await lane.boundingBox();
    if (!laneBox) {
      throw new Error('The Beat Mapping track is not on screen.');
    }
    // Map the beat at bar 3 beat 3 onto the loop's transient nearest it (where the flex marker left that kick).
    const beatX = laneBox.x + 2 * Bar + 2 * Quarter;
    const transientXs = await window.getByTestId('beat-mapping-transient').evaluateAll(lines =>
      lines.map(line => {
        const r = line.getBoundingClientRect();
        return r.x + r.width / 2;
      })
    );
    const transientX = transientXs.reduce((a, b) => (Math.abs(b - beatX) < Math.abs(a - beatX) ? b : a));
    expect(Math.abs(transientX - beatX)).toBeGreaterThan(2);
    const points = await window.locator('[data-testid^="tempo-point-"]').count();
    await window.mouse.move(beatX, laneBox.y + 6);
    await window.mouse.down();
    await window.mouse.move(transientX, laneBox.y + laneBox.height - 6, { steps: 8 });
    await window.mouse.up();
    await expect(window.locator('[data-testid^="tempo-point-"]')).not.toHaveCount(points);
    await window.getByTestId('global-tracks').click();

    // 8. Varispeed at 90 %: speed only, a bounce 1/0.9 as long at the same pitch (5 cents); speed and pitch, the pitch
    //    1.82 semitones down (5 cents).
    await soloOnly(window, tracks.slice(0, 3), 'Audio 3');
    const plain = await bounce(window, bounces, 'Plain', 3);
    await window.getByTestId('varispeed-toggle').click();
    await window.getByTestId('varispeed-mode').click();
    await window.getByRole('menuitemradio', { name: 'Speed Only' }).click();
    await window.getByTestId('varispeed-display').dblclick();
    await window.getByTestId('varispeed-speed').fill('-10');
    await window.keyboard.press('Enter');
    await expect(window.getByTestId('varispeed-display')).toHaveAttribute('aria-valuenow', '-10');
    const slower = await bounce(window, bounces, 'Speed Only', 3);
    const longer = measured('Varispeed speed only: length ratio', frames(slower) / frames(plain), '×');
    expect(Math.abs(longer - 1 / 0.9)).toBeLessThan(0.01);
    const note = (path: string, stretch: number) => pitchAt(path, (0.1 + 0.15) * stretch, (0.1 + 0.45) * stretch);
    const kept = measured('Varispeed speed only: pitch change', cents(note(slower, 1 / 0.9), note(plain, 1)), 'cents');
    expect(Math.abs(kept)).toBeLessThanOrEqual(5);
    await window.getByTestId('varispeed-mode').click();
    await window.getByRole('menuitemradio', { name: 'Varispeed (Speed and Pitch)' }).click();
    const tape = await bounce(window, bounces, 'Tape', 3);
    const fell = measured(
      'Varispeed speed and pitch: pitch change',
      cents(note(tape, 1 / 0.9), note(plain, 1)),
      'cents'
    );
    expect(Math.abs(fell - 12 * Math.log2(0.9) * 100)).toBeLessThanOrEqual(5);
    await window.getByTestId('varispeed-toggle').click();
    await soloOnly(window, tracks.slice(0, 3), '');

    // 9. Track alternatives: an alternative of the drum track; delete its region; back: it is there; again: it is not.
    await drums.getByTestId('track-alternatives').click();
    await window.getByRole('menuitem', { name: 'Duplicate Alternative' }).click();
    await expect(drums.getByTestId('track-alternatives')).toContainText('B');
    await selectRegion(window, 0);
    await window.keyboard.press('Delete');
    const regionsNow = 4 + PhraseNotes - 1;
    await expectRegions(window, regionsNow, 5);
    await drums.getByTestId('track-alternatives').click();
    await window.getByRole('menuitemradio', { name: 'A' }).click();
    await expectRegions(window, regionsNow + 1, 5);
    await drums.getByTestId('track-alternatives').click();
    await window.getByRole('menuitemradio', { name: 'B' }).click();
    await expectRegions(window, regionsNow, 5);
    await drums.getByTestId('track-alternatives').click();
    await window.getByRole('menuitemradio', { name: 'A' }).click();

    // 10. Bounce the melody track in place to a new track (effects included, source muted); then selection-based
    //     processing: Gain +6 dB and Channel EQ over the second half of the humanised loop's region, which becomes a
    //     new file and region.
    await timeline.click({ position: { x: 4 * Bar, y: Ruler + 2 * Lane + 6 } });
    await window.keyboard.press('Control+b');
    await expect(window.getByTestId('bounce-in-place-dialog')).toBeVisible();
    await window.getByTestId('bip-destination').getByRole('radio', { name: 'New Track' }).click();
    await window.getByTestId('bip-source').getByRole('radio', { name: 'Mute' }).click();
    await expect(window.getByTestId('bip-bypass-effects')).not.toBeChecked();
    await window.getByTestId('bip-apply').click();
    await expect(window.getByTestId('bounce-in-place-dialog')).toBeHidden({ timeout: 60_000 });
    await expect(window.getByRole('group', { name: /^Track \d+: .* Bounce$/ })).toBeVisible();
    await expect(window.getByRole('button', { name: 'Mute Audio 3', exact: true })).toHaveAttribute(
      'aria-pressed',
      'true'
    );

    await selectRegion(window, 1);
    await window.keyboard.press('Alt+x');
    await expect(window.getByTestId('processing-dialog')).toBeVisible();
    await window.getByTestId('processing-add-effect').click();
    await window.getByRole('menuitem', { name: 'Gain' }).click();
    await window.getByTestId('processing-param-anode.gain-gain').fill('6');
    await window.getByTestId('processing-param-anode.gain-gain').press('Tab');
    await window.getByTestId('processing-add-effect').click();
    await window.getByRole('menuitem', { name: 'Channel EQ' }).click();
    // The loop is 4 bars at 100 bpm; at 120 it follows into 4 bars: process bars 3–5.
    await window.getByTestId('processing-start').fill('3');
    await window.getByTestId('processing-start').press('Tab');
    await window.getByTestId('processing-apply').click();
    await expect(window.getByTestId('processing-dialog')).toBeHidden({ timeout: 60_000 });
    // The bounce's region, and the processed part split off the humanised loop's region.
    const afterProcessing = 4 + PhraseNotes + 2;
    await expectRegions(window, afterProcessing, 6);

    // 11. Save, close and reopen: everything is intact.
    const tempo = (await window.getByTestId('lcd-tempo').innerText()).trim();
    await window.keyboard.press('ControlOrMeta+s');
    await expect(window.getByTestId('unsaved')).toHaveCount(0);
    await window.getByRole('button', { name: 'Close project' }).click();
    await window.getByTestId('recent-Advanced').click();
    await expect(window.getByTestId('project-name')).toHaveText('Advanced');
    await expectRegions(window, afterProcessing, 6);
    await expect(window.getByTestId('lcd-tempo')).toHaveText(tempo);
    await window.getByTestId('flex-toggle').click();
    const reopened = window.getByRole('group', { name: 'Track 1: Audio 1' });
    await expect(reopened.getByTestId('track-flex')).toHaveAttribute('aria-pressed', 'true');
    await expect(reopened.getByTestId('track-flex-mode')).toContainText('Slicing');
    await expect(reopened.getByTestId('track-alternatives')).toContainText('A');
    await expect(window.getByTestId('flex-marker').first()).toBeVisible();
    // Bounce in place put its track under the melody's, so the MIDI track is a track further down now.
    await expect(window.getByRole('group', { name: `Track 5: ${midiTrack}` })).toBeVisible();
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
