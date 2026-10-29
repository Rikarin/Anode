import type { ElectronApplication, Locator, Page } from '@playwright/test';
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test } from './fixtures';

const Rate = 48_000;
/** The project's tempo (a new project's): 120 bpm, so a beat is 0.5 s and a tick 1/1920 s. */
const Bpm = 120;
const Ppq = 960;
const TickSeconds = 60 / Bpm / Ppq;

// ---- fixtures written by the spec (docs/08-phase-7.md, the exit criterion)

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

/**
 * The articulation test instrument: an SFZ with two keyswitched groups over A2…A5 — a sine (keyswitch C0, 24, the
 * default) and a square (keyswitch C♯0, 25) — each a 1 s 220 Hz sample keyed at A2 (57).
 */
function writeKeyswitchSfz(folder: string): string {
  const frames = Rate;
  const sine = new Float32Array(frames);
  const square = new Float32Array(frames);
  for (let i = 0; i < frames; i++) {
    const phase = (220 * i) / Rate;
    sine[i] = 0.4 * Math.sin(2 * Math.PI * phase);
    square[i] = phase % 1 < 0.5 ? 0.3 : -0.3;
  }
  writeWav(join(folder, 'sine.wav'), sine);
  writeWav(join(folder, 'square.wav'), square);
  const sfz = join(folder, 'articulations.sfz');
  writeFileSync(
    sfz,
    [
      '<global> sw_lokey=24 sw_hikey=25 sw_default=24',
      '<group> sw_last=24 sw_label=Sine',
      '<region> sample=sine.wav pitch_keycenter=57 lokey=45 hikey=81',
      '<group> sw_last=25 sw_label=Square',
      '<region> sample=square.wav pitch_keycenter=57 lokey=45 hikey=81',
      ''
    ].join('\n')
  );
  return sfz;
}

/**
 * A swung drum loop at 120 bpm, two bars: a decaying 60 Hz kick on each beat and a noise hat on each off-beat eighth,
 * the off-beats delayed by a third of an eighth (160 ticks: a triplet swing). Returns the hits in ticks too.
 */
const SwingTicks = 160;
function swungLoop(): { samples: Float32Array; hits: number[] } {
  const samples = new Float32Array(4 * Rate);
  let x = 7;
  const noise = () => {
    x ^= x << 13;
    x ^= x >>> 17;
    x ^= x << 5;
    return ((x >>> 0) / 4_294_967_295) * 2 - 1;
  };
  const hits: number[] = [];
  for (let eighth = 0; eighth < 16; eighth++) {
    const tick = eighth * 480 + (eighth % 2 ? SwingTicks : 0);
    hits.push(tick);
    const from = Math.round(tick * TickSeconds * Rate);
    for (let i = 0; i < Rate / 8 && from + i < samples.length; i++) {
      const decay = Math.exp(-i / (eighth % 2 ? 600 : 3000));
      samples[from + i] =
        (samples[from + i] ?? 0) + (eighth % 2 ? 0.5 * noise() : 0.9 * Math.sin((2 * Math.PI * 60 * i) / Rate)) * decay;
    }
  }
  return { samples, hits };
}

/** The loop-search tone: 480 Hz at 48 kHz (a whole 100-frame period), 2 s at half scale. */
const TonePeriod = 100;
function tone(): Float32Array {
  return Float32Array.from({ length: 2 * Rate }, (_, i) => 0.5 * Math.sin((2 * Math.PI * i) / TonePeriod));
}

// ---- reading bounces and files (as in advanced-audio-golden-path.spec.ts)

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
 * Onsets in seconds: where the 1 ms energy jumps eightfold over the 20 ms before it (at most one per 60 ms), each at
 * the first sample within 10 dB of the hit's peak.
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
  for (let i = 0; i < energy.length; i++) {
    let before = 0;
    for (let k = Math.max(0, i - 20); k < i; k++) {
      before += energy[k] ?? 0;
    }
    before /= 20;
    const now = energy[i] ?? 0;
    if (now > 1e-4 && now > 8 * before && (found.length === 0 || i / 1000 - (found.at(-1) ?? 0) > 0.06)) {
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

/** The magnitude of frequency `hz` between two times (Goertzel over a Hann window). */
function magnitudeAt(samples: Float32Array, sampleRate: number, from: number, to: number, hz: number): number {
  const a = Math.round(from * sampleRate);
  const n = Math.round((to - from) * sampleRate);
  const w = (2 * Math.PI * hz) / sampleRate;
  let s1 = 0;
  let s2 = 0;
  for (let i = 0; i < n; i++) {
    const hann = 0.5 - 0.5 * Math.cos((2 * Math.PI * i) / (n - 1));
    const s = (samples[a + i] ?? 0) * hann + 2 * Math.cos(w) * s1 - s2;
    s2 = s1;
    s1 = s;
  }
  return Math.sqrt(s1 * s1 + s2 * s2 - 2 * Math.cos(w) * s1 * s2) / n;
}

/** The third harmonic's level relative to the fundamental's (a sine ≈ 0, a square ≈ 1/3). */
function thirdHarmonic(path: string, from: number, to: number, hz: number): number {
  const { sampleRate, samples } = readWav(path);
  return magnitudeAt(samples, sampleRate, from, to, 3 * hz) / magnitudeAt(samples, sampleRate, from, to, hz);
}

/** The dominant frequency between two times, by autocorrelation over 40…2 000 Hz. */
function pitchAt(path: string, from: number, to: number): number {
  const { sampleRate, samples } = readWav(path);
  const a = Math.round(from * sampleRate);
  const n = Math.round((to - from) * sampleRate);
  let best = 0;
  let bestLag = 0;
  for (let lag = Math.floor(sampleRate / 2000); lag <= Math.ceil(sampleRate / 40); lag++) {
    let sum = 0;
    for (let i = 0; i < n - lag; i++) {
      sum += (samples[a + i] ?? 0) * (samples[a + i + lag] ?? 0);
    }
    if (sum > best) {
      best = sum;
      bestLag = lag;
    }
  }
  return sampleRate / bestLag;
}

/** Records what the exit criterion measured as a test annotation, and returns it. */
function measured(what: string, value: number, unit: string): number {
  test.info().annotations.push({ type: 'measured', description: `${what}: ${value.toFixed(3)} ${unit}` });
  return value;
}

/** Every file under `folder` named `name` (the Audio File Editor writes `<name>.v<N>.wav` beside the original). */
function findFiles(folder: string, pattern: RegExp): string[] {
  const found: string[] = [];
  for (const entry of readdirSync(folder)) {
    const path = join(folder, entry);
    if (statSync(path).isDirectory()) {
      found.push(...findFiles(path, pattern));
    } else if (pattern.test(entry)) {
      found.push(path);
    }
  }
  return found;
}

// ---- driving the app

// Lane geometry at the default view: the ruler is 24 px, lanes 72 px.
const Ruler = 24;
const Lane = 72;
const laneY = (lane: number) => Ruler + lane * Lane + Lane / 2;

async function regionCount(window: Page): Promise<number> {
  const label = await window.getByRole('img', { name: /^Tracks area:/ }).getAttribute('aria-label');
  return Number(/(\d+) regions?/.exec(label ?? '')?.[1] ?? 0);
}

/** A new software instrument track (selected once added); `lane` is its 0-based lane. */
async function addInstrumentTrack(window: Page): Promise<void> {
  await window.getByTestId('add-track-menu').click();
  await window.getByTestId('add-instrument-track').click();
}

/** A one-bar MIDI region at bar 1 of lane `lane` (a double-click on an instrument lane), selected. */
async function addMidiRegion(window: Page, lane: number): Promise<void> {
  const before = await regionCount(window);
  await window.locator('an-timeline').dblclick({ position: { x: 4, y: laneY(lane) } });
  await expect.poll(() => regionCount(window)).toBe(before + 1);
  await window.locator('an-timeline').click({ position: { x: 8, y: Ruler + lane * Lane + 6 } });
}

/** Selects the region at the start of lane `lane`. */
async function selectRegion(window: Page, lane: number): Promise<void> {
  await window.locator('an-timeline').click({ position: { x: 8, y: Ruler + lane * Lane + 6 } });
}

/** Imports a WAV onto a new audio track (the desktop app's open dialog answers with `path`). */
async function importOnNewTrack(window: Page, app: ElectronApplication, path: string): Promise<void> {
  await window.getByTestId('add-track').click();
  await app.evaluate(({ dialog }, file) => {
    dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [file] });
  }, path);
  const regions = await regionCount(window);
  await window.getByRole('button', { name: 'Import audio' }).click();
  await expect.poll(() => regionCount(window), { timeout: 30_000 }).toBe(regions + 1);
}

/** Shows an editor of the Editors pane by its tab. */
async function showEditor(window: Page, name: string): Promise<void> {
  await window.getByRole('tab', { name: 'Editors' }).click();
  await window.getByTestId('editor-view').getByRole('tab', { name, exact: true }).click();
}

/** Solos exactly one track (by header number) for a bounce: every other solo key off. */
async function soloOnly(window: Page, track: number): Promise<void> {
  const headers = window.locator('an-track-header');
  const count = await headers.count();
  for (let i = 0; i < count; i++) {
    const header = headers.nth(i);
    const key = header.getByRole('button', { name: /^Solo / });
    const on = (await key.getAttribute('aria-pressed')) === 'true';
    const label = (await header.getAttribute('aria-label')) ?? '';
    if (on !== label.startsWith(`Track ${track}: `)) {
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

// The Event List (event-list.ts): rows `event-row-N` with cells by `data-column`.

/** The Event List's selected row. */
function selectedRow(window: Page): Locator {
  return window.getByTestId('event-list').locator('[aria-selected="true"]').first();
}

/** Edits one cell of a row in place: click, fill, Return. */
async function editCell(window: Page, row: Locator, column: string, text: string): Promise<void> {
  await row.locator(`[data-column="${column}"]`).click();
  const input = window.getByTestId(`event-edit-${column}`);
  await input.fill(text);
  await input.press('Enter');
  await expect(window.getByTestId(`event-edit-${column}`)).toBeHidden();
}

/** Inserts a note with the Event List and places it: position (bar beat division tick) and pitch. */
async function insertNote(window: Page, position: string, pitch: number): Promise<void> {
  await window.getByTestId('event-insert').click();
  await window.getByTestId('event-insert-note').click();
  await editCell(window, selectedRow(window), 'position', position);
  await editCell(window, selectedRow(window), 'num', String(pitch));
}

/** The Event List's rows as text, by column. */
async function eventRows(window: Page): Promise<Record<string, string>[]> {
  return window
    .getByTestId('event-list')
    .locator('an-event-row')
    .evaluateAll(rows =>
      rows.map(row => {
        const cells: Record<string, string> = {};
        for (const cell of Array.from(row.querySelectorAll('[data-column]'))) {
          cells[cell.getAttribute('data-column') ?? ''] = cell.textContent.trim();
        }
        return cells;
      })
    );
}

/** A `bar beat division tick` position in 4/4 (1/16 divisions) as project ticks. */
function ticksOf(position: string): number {
  const [bar = 1, beat = 1, division = 1, tick = 1] = position.split(/\s+/).map(Number);
  return (bar - 1) * 4 * Ppq + (beat - 1) * Ppq + (division - 1) * 240 + (tick - 1);
}

/** Drags across a Step Editor lane's body from one point to another (body-local pixels). */
async function drawLine(window: Page, lane: Locator, from: [number, number], to: [number, number]): Promise<void> {
  const box = await lane.boundingBox();
  if (!box) {
    throw new Error('The lane is not on screen.');
  }
  await window.mouse.move(box.x + from[0], box.y + from[1]);
  await window.mouse.down();
  await window.mouse.move(box.x + to[0], box.y + to[1], { steps: 12 });
  await window.mouse.up();
}

/** Chooses an option of an xUI select by its text. */
async function choose(window: Page, select: Locator, option: string | RegExp): Promise<void> {
  await select.click();
  await window.getByRole('option', { name: option }).first().click();
}

/** Sets the Audio File Editor's selection to exact frames. */
async function selectFrames(window: Page, start: number, end: number): Promise<void> {
  for (const [id, value] of [
    ['afe-start', start],
    ['afe-end', end]
  ] as const) {
    const field = window.getByTestId(id);
    await field.fill(String(value));
    await field.dispatchEvent('change');
  }
  await expect(window.getByTestId('afe-selection-readout')).toContainText(`length ${end - start}`);
}

/** Runs an Audio File Editor operation and waits for the file's next version. */
async function fileOperation(window: Page, op: string, value?: string): Promise<void> {
  const waveform = window.getByTestId('afe-waveform');
  const version = Number((await waveform.getAttribute('data-version')) ?? 0);
  await window.getByTestId('afe-functions').click();
  await window.getByTestId(`afe-op-${op}`).click();
  if (await window.getByTestId('afe-op-panel').isVisible()) {
    if (value !== undefined) {
      await window.getByTestId('afe-op-value').fill(value);
    }
    await window.getByTestId('afe-op-apply').click();
  }
  await expect(waveform).toHaveAttribute('data-version', String(version + 1), { timeout: 30_000 });
}

// The Phase 7 exit criterion (docs/08-phase-7.md): MIDI and audio edited at the event and sample level, and grooves,
// articulations and scripts shaping a performance, on the real app with the null device. It needs every workstream:
// A (Scripter in the chain, step-input forwarding, range preview), B (the Server's commands, jobs and package), C (this
// editor), D (Scripter) and E (the algorithms and SFZ keyswitches).
//
// Gap (reported to the integration): the host's virtual MIDI input never sends (VirtualMidiBackend.Read returns 0), so
// step 6's "step-enter from the virtual MIDI input" plays the on-screen keyboard (`/midi/live`) instead; the Server must
// forward live MIDI to step input while MIDI step input is on, or the host needs a way for tests to feed its input.
test.fixme('edits MIDI and audio at the event and sample level: Event List, Step Editor, MIDI Transform, grooves, articulations, step input, Audio File Editor, Scripter, reopening', async ({
  app,
  window
}) => {
  const scratch = mkdtempSync(join(tmpdir(), 'anode-midi-editing-'));
  const bounces = join(scratch, 'Bounces');
  const sfz = writeKeyswitchSfz(scratch);
  const loop = swungLoop();
  const loopPath = join(scratch, 'swing loop.wav');
  writeWav(loopPath, loop.samples);
  const tonePath = join(scratch, 'tone.wav');
  writeWav(tonePath, tone());
  try {
    await window.getByTestId('new-project-name').fill('Editing');
    await window.getByTestId('new-project-folder').fill(scratch);
    await window.getByTestId('create-project').click();
    await expect(window.getByTestId('project-name')).toHaveText('Editing');

    // 1. Event List: notes entered and edited numerically, a program change and a CC 1 inserted, a note deleted.
    await addInstrumentTrack(window); // track 1, lane 0: the synth
    await addMidiRegion(window, 0);
    await showEditor(window, 'Event List');
    await expect(window.getByTestId('event-list-view')).toBeVisible();
    await insertNote(window, '1 1 1 1', 60);
    await insertNote(window, '1 2 1 1', 62);
    await insertNote(window, '1 3 1 1', 79);
    await insertNote(window, '1 4 1 1', 84);
    await window.getByTestId('event-filter-notes').click();
    await expect(window.getByTestId('event-filter-notes')).toHaveAttribute('aria-pressed', 'true');
    // The second note: to 1 2 3 1 (an eighth late), an eighth long, velocity 90, and D4 (74).
    const second = window.getByTestId('event-row-1');
    await editCell(window, second, 'position', '1 2 3 1');
    await editCell(window, window.getByTestId('event-row-1'), 'length', '0 0 2 0');
    await editCell(window, window.getByTestId('event-row-1'), 'val', '90');
    await editCell(window, window.getByTestId('event-row-1'), 'num', '74');
    await window.getByTestId('event-filter-notes').click();
    // A program change (program 5) and a CC 1 (64) at the first note.
    await window.getByTestId('event-row-0').locator('[data-column="status"]').click();
    for (const [kind, value] of [
      ['programChange', '5'],
      ['controlChange', '64']
    ] as const) {
      await window.getByTestId('event-insert').click();
      await window.getByTestId(`event-insert-${kind}`).click();
      await editCell(window, selectedRow(window), 'val', value);
    }
    // Delete the last note.
    const rowsBefore = await eventRows(window);
    const last = rowsBefore.findIndex(r => r['position'] === '1 4 1 1');
    await window.getByTestId(`event-row-${last}`).locator('[data-column="status"]').click();
    await window.getByTestId('event-delete').click();
    const edited = await eventRows(window);
    expect(edited.map(r => [r['position'], r['status'], r['num'], r['val'], r['length']])).toEqual([
      ['1 1 1 1', 'Program', '', '5', ''],
      ['1 1 1 1', 'Control', '1', '64', ''],
      ['1 1 1 1', 'Note', 'C3', '100', '0 0 1 0'],
      ['1 2 3 1', 'Note', 'D4', '90', '0 0 2 0'],
      ['1 3 1 1', 'Note', 'G4', '100', '0 0 1 0']
    ]);
    // The region plays the edits: three notes, the second an eighth late.
    await soloOnly(window, 1);
    const eventBounce = await bounce(window, bounces, 'Event List', 2);
    const eventOnsets = onsets(eventBounce).filter(t => t < 2);
    expect(eventOnsets).toHaveLength(3);
    [0, 0.75, 1].forEach((t, i) =>
      expect(
        measured(`Event List note ${i + 1} off its time`, Math.abs((eventOnsets[i] ?? 0) - t) * 1000, 'ms')
      ).toBeLessThanOrEqual(5)
    );

    // 2. Step Editor: a velocity ramp and a CC 74 lane over the bar with the line tool (pixel maths: step-editor.ts —
    //    a bar is 384 px at the default zoom, a lane's values run from y 60 (minimum) to y 4 (maximum)).
    await showEditor(window, 'Step Editor');
    await window.getByTestId('add-lane-number').fill('74');
    await window.getByTestId('add-lane').click();
    await window.getByTestId('add-lane-controlChange').click();
    await window.getByTestId('step-tool-line').click();
    await expect(window.getByTestId('step-tool-line')).toHaveAttribute('aria-checked', 'true');
    await drawLine(window, window.getByTestId('step-lane-velocity'), [0, 60], [384, 4]);
    await drawLine(window, window.getByTestId('step-lane-controlChange-74'), [0, 60], [384, 4]);
    await showEditor(window, 'Event List');
    const ramped = await eventRows(window);
    // Velocity 1 + 126·tick/3840 at each note's start.
    expect(ramped.filter(r => r['status'] === 'Note').map(r => [r['position'], r['val']])).toEqual([
      ['1 1 1 1', '1'],
      ['1 2 3 1', '48'],
      ['1 3 1 1', '64']
    ]);
    const cutoff = ramped.filter(r => r['status'] === 'Control' && r['num'] === '74');
    expect(cutoff.map(r => [ticksOf(r['position'] ?? ''), Number(r['val'])])).toEqual(
      Array.from({ length: 16 }, (_, i) => [i * 240, Math.round((127 * i) / 16)])
    );

    // 3. MIDI Transform: Humanize, seeded (the same seed twice gives the same events), then Select and Operate on the
    //    notes above C4 (Logic's C4 is 72): velocity 100 and a 1/16 length.
    await window.keyboard.press('ControlOrMeta+9');
    const transform = window.getByTestId('transform-window');
    await expect(transform).toBeVisible();
    const notesOf = async () => (await eventRows(window)).filter(r => r['status'] === 'Note');
    const beforeHumanize = await notesOf();
    await choose(window, window.getByTestId('transform-preset'), 'Humanize');
    await window.getByTestId('transform-seed').fill('1234');
    await window.getByTestId('transform-seed').press('Enter');
    await window.getByTestId('transform-apply').click();
    await expect(window.getByTestId('transform-status')).toContainText('Transformed');
    const humanized = await notesOf();
    humanized.forEach((n, i) => {
      const was = beforeHumanize[i] ?? {};
      expect(Math.abs(ticksOf(n['position'] ?? '') - ticksOf(was['position'] ?? ''))).toBeLessThanOrEqual(10);
      expect(Math.abs(Number(n['val']) - Number(was['val']))).toBeLessThanOrEqual(10);
    });
    await window.keyboard.press('ControlOrMeta+z');
    await expect.poll(notesOf).toEqual(beforeHumanize);
    await choose(window, window.getByTestId('transform-preset'), 'Humanize');
    await window.getByTestId('transform-seed').fill('1234');
    await window.getByTestId('transform-seed').press('Enter');
    await window.getByTestId('transform-apply').click();
    await expect.poll(notesOf).toEqual(humanized);
    await window.keyboard.press('ControlOrMeta+z');
    await expect.poll(notesOf).toEqual(beforeHumanize);

    await window.getByTestId('transform-new').click();
    await window.getByTestId('transform-condition-status').selectOption('equal');
    await window.getByTestId('transform-condition-status-a').selectOption('144');
    await window.getByTestId('transform-condition-pitch').selectOption('greaterEqual');
    await window.getByTestId('transform-condition-pitch-a').fill('73');
    await window.getByTestId('transform-condition-pitch-a').press('Enter');
    await window.getByTestId('transform-operation-velocity').selectOption('fix');
    await window.getByTestId('transform-operation-velocity-a').fill('100');
    await window.getByTestId('transform-operation-velocity-a').press('Enter');
    await window.getByTestId('transform-operation-length').selectOption('fix');
    await window.getByTestId('transform-operation-length-a').fill('0 0 1 0');
    await window.getByTestId('transform-operation-length-a').press('Enter');
    await window.getByTestId('transform-mode').getByRole('radio', { name: 'Select and Operate' }).click();
    await window.getByTestId('transform-apply').click();
    await expect
      .poll(async () => (await notesOf()).map(n => [n['num'], n['val'], n['length']]))
      .toEqual([
        ['C3', beforeHumanize[0]?.['val'], '0 0 1 0'],
        ['D4', '100', '0 0 1 0'],
        ['G4', '100', '0 0 1 0']
      ]);
    await window.getByTestId('transform-window-close').click();

    // 4. Grooves: a template from the swung drum loop's transients and one from a MIDI region; a straight region
    //    quantised to the audio groove lands within 2 ms of the loop's hits; the drum track as the groove track, and
    //    a track that matches it quantises to it.
    await importOnNewTrack(window, app, loopPath); // track 2, lane 1
    await selectRegion(window, 1);
    await window.locator('an-timeline').click({ button: 'right', position: { x: 8, y: Ruler + Lane + 6 } });
    await window.getByTestId('make-groove-template').click();
    await expect(window.getByTestId('groove-window')).toBeVisible();
    await window.getByTestId('groove-name').fill('Swing Loop');
    await window.getByTestId('groove-bars').selectOption('1');
    await window.getByTestId('groove-grid').selectOption('480');
    await window.getByTestId('groove-make').click();
    await expect(window.getByTestId('project-groove')).toHaveCount(1);
    await selectRegion(window, 0);
    await window.getByTestId('groove-name').fill('From MIDI');
    await window.getByTestId('groove-make').click();
    await expect(window.getByTestId('project-groove')).toHaveCount(2);
    await window.getByTestId('groove-window-close').click();

    // A straight eighths region on track 3.
    const straight = Array.from({ length: 8 }, (_, i) => `1 ${Math.floor(i / 2) + 1} ${i % 2 ? 3 : 1} 1`);
    const enterStraight = async (lane: number) => {
      await addInstrumentTrack(window);
      await addMidiRegion(window, lane);
      await showEditor(window, 'Event List');
      for (const position of straight) {
        await insertNote(window, position, 60);
      }
    };
    await enterStraight(2);
    await showEditor(window, 'Piano Roll');
    await choose(window, window.getByTestId('piano-roll-quantize-value'), 'Groove: Swing Loop');
    await window.getByTestId('quantize').click();
    await showEditor(window, 'Event List');
    const swung = (await notesOf()).map(n => ticksOf(n['position'] ?? ''));
    expect(swung).toHaveLength(8);
    swung.forEach((tick, i) =>
      expect(
        measured(
          `Groove quantise: note ${i + 1} off the loop's hit`,
          Math.abs(tick - (loop.hits[i] ?? 0)) * TickSeconds * 1000,
          'ms'
        )
      ).toBeLessThanOrEqual(2)
    );

    // The groove track: the drum track; track 4 matches it, and a plain 1/8 quantise follows the groove.
    await window
      .getByRole('group', { name: /^Track 2: / })
      .getByTestId('groove-track')
      .click();
    await expect(window.getByRole('group', { name: /^Track 2: / }).getByTestId('groove-track')).toHaveAttribute(
      'aria-pressed',
      'true'
    );
    await enterStraight(3);
    await window
      .getByRole('group', { name: /^Track 4: / })
      .getByTestId('match-groove')
      .check();
    await showEditor(window, 'Piano Roll');
    await choose(window, window.getByTestId('piano-roll-quantize-value'), '1/8');
    await window.getByTestId('quantize').click();
    await showEditor(window, 'Event List');
    const matched = (await notesOf()).map(n => ticksOf(n['position'] ?? ''));
    matched.forEach((tick, i) =>
      expect(Math.abs(tick - (loop.hits[i] ?? 0)) * TickSeconds * 1000).toBeLessThanOrEqual(2)
    );

    // 5. Articulations: a set with keyswitch outputs (C0 sine, C♯0 square) on a Sampler track playing the test SFZ;
    //    two A2 notes given an articulation each in the Piano Roll; the bounce plays the first as a sine and the
    //    second as a square (its third harmonic).
    await addInstrumentTrack(window); // track 5, lane 4
    await window.getByTestId('instrument-picker').click();
    await window.getByRole('option', { name: 'Anode Sampler' }).click();
    await app.evaluate(({ dialog }, path) => {
      dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [path] });
    }, sfz);
    await window.getByRole('button', { name: 'Load file…' }).click();
    await expect(window.getByTestId('instrument-file')).toContainText('articulations.sfz');
    await window.getByTestId('articulation-set-new').click();
    const setEditor = window.getByTestId('articulation-set-editor');
    await expect(setEditor).toBeVisible();
    await window.getByTestId('articulation-set-name').fill('Sine and Square');
    await window.getByTestId('articulation-add').click();
    await window.getByTestId('articulation-add').click();
    await window.getByTestId('articulation-name').nth(0).fill('Sine');
    await window.getByTestId('articulation-name').nth(1).fill('Square');
    await setEditor.getByRole('tab', { name: 'Outputs' }).click();
    for (const [i, key] of ['C0', 'C#0'].entries()) {
      await window.getByTestId('output-add').click();
      await window
        .getByTestId('output-articulation')
        .nth(i)
        .selectOption(String(i + 1));
      await window.getByTestId('output-kind').nth(i).selectOption('note');
      await window.getByTestId('output-number').nth(i).fill(key);
      await window.getByTestId('output-value').nth(i).fill('100');
    }
    await window.getByTestId('articulation-set-save').click();
    await expect(window.getByTestId('articulation-set-problems')).toBeHidden();
    await window.getByTestId('articulation-set-editor-close').click();
    await expect(window.getByTestId('articulation-set-select')).not.toHaveValue('');
    await addMidiRegion(window, 4);
    await showEditor(window, 'Event List');
    await insertNote(window, '1 1 1 1', 57);
    await insertNote(window, '1 3 1 1', 57);
    await showEditor(window, 'Piano Roll');
    for (const [tick, articulation] of [
      [0, '1'],
      [1920, '2']
    ] as const) {
      await window
        .getByTestId('articulation-chip')
        .and(window.locator(`[data-tick="${tick}"]`))
        .click();
      await window
        .getByTestId('articulation-choice')
        .and(window.locator(`[data-articulation="${articulation}"]`))
        .click();
    }
    await soloOnly(window, 5);
    const articulated = await bounce(window, bounces, 'Articulations', 2);
    expect(measured('Sine articulation: third harmonic', thirdHarmonic(articulated, 0.1, 0.6, 220), '×')).toBeLessThan(
      0.05
    );
    expect(
      measured('Square articulation: third harmonic', thirdHarmonic(articulated, 1.1, 1.6, 220), '×')
    ).toBeGreaterThan(0.2);

    // 6. Step input: with the Step Input Keyboard, a note, a chord and a rest (eighths); then MIDI step input.
    await addInstrumentTrack(window); // track 6, lane 5: steps make a new region on it
    await window.keyboard.press('Enter'); // to the start
    await window.keyboard.press('ControlOrMeta+Alt+k');
    const keyboard = window.getByTestId('step-input-keyboard');
    await expect(keyboard).toBeVisible();
    await window.getByTestId('step-value-480').click();
    const key = (name: string) => window.getByTestId('step-keys').getByRole('button', { name, exact: true });
    await key('C3').click();
    await window.getByTestId('step-chord').click();
    for (const name of ['C3', 'E3', 'G3']) {
      await key(name).click();
    }
    await window.getByTestId('step-chord').click();
    await window.getByTestId('step-rest').click();
    await expect(window.getByTestId('step-position')).toContainText('1 2 3 1');
    // MIDI step input: the keyboard's notes (see the gap above), D3 then an F3 + A3 chord.
    await window.getByTestId('midi-step-input').click();
    await expect(window.getByTestId('midi-step-input')).toHaveAttribute('aria-pressed', 'true');
    await window.getByTestId('step-input-keyboard-close').click();
    await window.getByRole('tab', { name: 'Keyboard' }).click();
    const live = (name: string) => window.getByRole('button', { name, exact: true }).last();
    await live('D3').dispatchEvent('pointerdown', { button: 0, pointerId: 1 });
    await live('D3').dispatchEvent('pointerup', { button: 0, pointerId: 1 });
    await live('F3').dispatchEvent('pointerdown', { button: 0, pointerId: 1 });
    await live('A3').dispatchEvent('pointerdown', { button: 0, pointerId: 2 });
    await live('F3').dispatchEvent('pointerup', { button: 0, pointerId: 1 });
    await live('A3').dispatchEvent('pointerup', { button: 0, pointerId: 2 });
    await window.getByTestId('midi-step-input').click();
    await selectRegion(window, 5);
    await showEditor(window, 'Event List');
    await expect
      .poll(async () => (await notesOf()).map(n => [n['position'], n['num'], n['length']]))
      .toEqual([
        ['1 1 1 1', 'C3', '0 0 2 0'],
        ['1 1 3 1', 'C3', '0 0 2 0'],
        ['1 1 3 1', 'E3', '0 0 2 0'],
        ['1 1 3 1', 'G3', '0 0 2 0'],
        ['1 2 3 1', 'D3', '0 0 2 0'],
        ['1 3 1 1', 'F3', '0 0 2 0'],
        ['1 3 1 1', 'A3', '0 0 2 0']
      ]);

    // 7. Audio File Editor on a periodic tone's file: normalise, fade in, reverse, silence, undo the silence, loop
    //    search. The file's versions on disk reflect each operation.
    await importOnNewTrack(window, app, tonePath); // track 7, lane 6
    await selectRegion(window, 6);
    await showEditor(window, 'Audio File');
    await expect(window.getByTestId('audio-file-editor')).toBeVisible();
    const version = (n: number) => {
      const [path] = findFiles(scratch, new RegExp(`^tone\\.v${n}\\.wav$`));
      expect(path, `tone.v${n}.wav`).toBeDefined();
      return readWav(path ?? '').samples;
    };
    const original = readWav(tonePath).samples;
    await selectFrames(window, 0, 24_000);
    await fileOperation(window, 'normalize', '-1');
    const normalised = version(1);
    const peak = Math.max(...Array.from(normalised.subarray(0, 24_000), Math.abs));
    expect(measured('Normalised peak', 20 * Math.log10(peak), 'dBFS')).toBeCloseTo(-1, 1);
    expect(normalised[30_000]).toBeCloseTo(original[30_000] ?? 0, 3);
    await selectFrames(window, 0, 4_800);
    await fileOperation(window, 'fadeIn');
    const faded = version(2);
    expect(Math.abs(faded[1] ?? 1)).toBeLessThan(0.01);
    expect(Math.abs(faded[4_825] ?? 0)).toBeCloseTo(Math.abs(normalised[4_825] ?? 0), 3);
    await selectFrames(window, 24_000, 48_000);
    await fileOperation(window, 'reverse');
    const reversed = version(3);
    for (const i of [24_000, 30_017, 47_999]) {
      expect(reversed[i]).toBeCloseTo(faded[24_000 + 47_999 - i] ?? 0, 3);
    }
    await selectFrames(window, 48_000, 60_000);
    await fileOperation(window, 'silence');
    const silenced = version(4);
    expect(Math.max(...Array.from(silenced.subarray(48_000, 60_000), Math.abs))).toBe(0);
    await window.getByTestId('afe-undo').click();
    await expect(window.getByTestId('afe-waveform')).toHaveAttribute('data-version', '3');
    await selectFrames(window, 60_000, 96_000);
    await window.getByTestId('afe-loop-search').click();
    await expect(window.getByTestId('afe-loop-result').first()).toBeVisible({ timeout: 30_000 });
    const marker = window.getByTestId('afe-loop-marker').first();
    const loopEnd = Number(await marker.getAttribute('data-end-frame'));
    const loopStart = Number(await marker.getAttribute('data-start-frame'));
    expect(
      measured('Loop search: end off the selection end', Math.abs(loopEnd - 96_000), 'frames')
    ).toBeLessThanOrEqual(TonePeriod);
    expect((loopEnd - loopStart) % TonePeriod).toBe(0);

    // 8. Scripter on a synth track: a factory preset, then a script that sends each note and an octave above it 100 ms
    //    later (a PluginParameters slider for the interval); a runaway script is bypassed and playback goes on.
    await addInstrumentTrack(window); // track 8, lane 7
    await addMidiRegion(window, 7);
    await showEditor(window, 'Event List');
    await insertNote(window, '1 1 1 1', 60);
    await window.keyboard.press('x');
    const fx = window.getByRole('group', { name: /^MIDI FX of / }).last();
    await fx.getByTestId('midi-fx-empty').click();
    await window.getByRole('menuitem', { name: 'Scripter' }).click();
    await fx.getByRole('button', { name: /MIDI FX 1 on .*: Scripter/ }).click();
    const editor = window.getByTestId('script-editor');
    await expect(editor).toBeVisible();
    await window.getByTestId('script-editor-presets').click();
    await window.getByTestId('script-preset-transposer').click();
    await expect(window.getByTestId('script-editor-parameters')).toBeVisible();
    const script = [
      "var PluginParameters = [{ name: 'Interval', type: 'lin', minValue: 0, maxValue: 24, numberOfSteps: 24, defaultValue: 12 }];",
      "Trace('Octave echo ready');",
      'function HandleMIDI(event) {',
      '  event.send();',
      '  if (event instanceof NoteOn || event instanceof NoteOff) {',
      '    var echo = event instanceof NoteOn ? new NoteOn(event) : new NoteOff(event);',
      "    echo.pitch = MIDI.normalizeData(event.pitch + GetParameter('Interval'));",
      '    echo.sendAfterMilliseconds(100);',
      '  }',
      '}'
    ].join('\n');
    await window.getByTestId('script-editor-code').click();
    await window.keyboard.press('ControlOrMeta+a');
    await window.keyboard.insertText(script);
    await window.keyboard.press('ControlOrMeta+Enter');
    await expect(window.getByTestId('script-parameter-p0').getByRole('slider', { name: 'Interval' })).toBeVisible();
    await expect(window.getByTestId('script-editor-console')).toContainText('Octave echo ready');
    await window.keyboard.press('x');
    await soloOnly(window, 8);
    const echoed = await bounce(window, bounces, 'Scripter', 2);
    const [note, echo] = onsets(echoed);
    expect(measured('Scripter: echo after the note', ((echo ?? 0) - (note ?? 0)) * 1000, 'ms')).toBeGreaterThanOrEqual(
      99
    );
    expect(((echo ?? 0) - (note ?? 0)) * 1000).toBeLessThanOrEqual(101);
    const ratio =
      pitchAt(echoed, (echo ?? 0) + 0.02, (echo ?? 0) + 0.08) / pitchAt(echoed, (note ?? 0) + 0.02, (note ?? 0) + 0.08);
    expect(measured('Scripter: echo pitch ratio', ratio, '×')).toBeCloseTo(2, 1);
    // A runaway script: bypassed with its problem in the console, and playback continues.
    await window.getByTestId('script-editor-code').click();
    await window.keyboard.press('ControlOrMeta+a');
    await window.keyboard.insertText('function HandleMIDI(event) {\n  while (true) {}\n}');
    await window.keyboard.press('ControlOrMeta+Enter');
    await window.getByTestId('play').click();
    await expect(window.getByTestId('script-editor-console').locator('[data-kind="problem"]')).toBeVisible({
      timeout: 10_000
    });
    await expect(window.getByTestId('lcd-bars')).not.toHaveText('1 1 1   1', { timeout: 5_000 });
    await window.getByRole('button', { name: 'Stop' }).click();
    // Back to the echo for the reopening check.
    await window.getByTestId('script-editor-code').click();
    await window.keyboard.press('ControlOrMeta+a');
    await window.keyboard.insertText(script);
    await window.keyboard.press('ControlOrMeta+Enter');
    await window.getByTestId('script-editor-close').click();

    // 9. Save, close and reopen: everything is intact.
    await window.keyboard.press('ControlOrMeta+s');
    await expect(window.getByTestId('unsaved')).toBeHidden();
    await window.getByRole('button', { name: 'Close project' }).click();
    await window.getByTestId('recent-Editing').click();
    await expect(window.getByTestId('project-name')).toHaveText('Editing');
    await selectRegion(window, 0);
    await showEditor(window, 'Event List');
    await expect.poll(async () => (await eventRows(window)).length).toBe(edited.length + 16);
    await showEditor(window, 'Step Editor');
    await expect(window.getByTestId('step-lane-controlChange-74')).toBeVisible();
    await selectRegion(window, 4);
    await expect(window.getByTestId('articulation-set-select')).not.toHaveValue('');
    await showEditor(window, 'Piano Roll');
    await expect(window.getByTestId('articulation-chip').and(window.locator('[data-tick="1920"]'))).toContainText(
      /Square|S/
    );
    await selectRegion(window, 1);
    await window.locator('an-timeline').click({ button: 'right', position: { x: 8, y: Ruler + Lane + 6 } });
    await window.getByTestId('make-groove-template').click();
    await expect(window.getByTestId('project-groove')).toHaveCount(2);
    await window.getByTestId('groove-window-close').click();
    await expect(window.getByRole('group', { name: /^Track 2: / }).getByTestId('groove-track')).toHaveAttribute(
      'aria-pressed',
      'true'
    );
    await selectRegion(window, 6);
    await showEditor(window, 'Audio File');
    await expect(window.getByTestId('afe-waveform')).toHaveAttribute('data-version', '3');
    await window.keyboard.press('x');
    await window
      .getByRole('group', { name: /^MIDI FX of / })
      .last()
      .getByRole('button', { name: /: Scripter/ })
      .click();
    await expect(window.getByTestId('script-editor-code')).toContainText('sendAfterMilliseconds(100)');
    await expect(window.getByTestId('script-parameter-p0')).toBeVisible();
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
