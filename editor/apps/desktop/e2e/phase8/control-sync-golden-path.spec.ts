import type { ElectronApplication, Locator, Page } from '@playwright/test';
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { expect, test } from '../fixtures';
import { MidiProbe, songPosition } from './midiprobe';
import { OscClient } from './osc';

/*
 * Phase 8's exit criterion (docs/09-phase-8.md): Anode played and mixed from hardware, following and leading other gear
 * in time, and splitting a recording into stems, on the null device, with `anode-midiprobe` (workstream A, its README
 * is the protocol) standing in for hardware on Anode's virtual MIDI ports ("Anode In" `anode:in`, "Anode Out"
 * `anode:out`, which the host publishes with ANODE_VIRTUAL_MIDI=1 on macOS) and in the Link session. The e2e global
 * setup builds the probe and the Stem Splitter's test mix and points the Server at the locally exported models.
 * Tolerances are the brief's; what was measured is recorded as `measured` annotations.
 */

const Bpm = 120;
// Lane geometry at the default view: the ruler is 24 px, lanes 72 px, a bar 192 px (as in the earlier golden paths).
const Ruler = 24;
const Lane = 72;
const Bar = 192;

// ---- driving the app

/** Chooses an application menu item by its id (apps/desktop/src/main.ts gives each `anode:<command>`). */
async function menu(app: ElectronApplication, command: string): Promise<void> {
  await app.evaluate(({ Menu }, id) => {
    const item = Menu.getApplicationMenu()?.getMenuItemById(id);
    if (!item) {
      throw new Error(`No menu item ${id}`);
    }
    // Electron types `click` as Function; a menu item's handler takes the event, window and contents, all optional.
    (item.click as () => void)();
  }, `anode:${command}`);
}

/** A key command for the window, pressed with nothing focused. */
async function shortcut(window: Page, key: string): Promise<void> {
  await window.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await window.keyboard.press(key);
}

/** A strip's fader (the Mixer shows; `name` is the track's). */
function fader(window: Page, name: string): Locator {
  return window.getByRole('group', { name: `${name} channel strip` }).getByRole('slider', { name: `${name} volume` });
}

/** A fader's value in dB, as it shows it. */
async function volumeOf(window: Page, name: string): Promise<number> {
  return Number(await fader(window, name).getAttribute('aria-valuenow'));
}

/** The playhead's position in seconds, from the LCD's clock time (`00:00:12.345`). */
async function playheadSeconds(window: Page): Promise<number> {
  const text = (await window.getByTestId('lcd-time').textContent()) ?? '';
  const [h = 0, m = 0, s = 0] = text.trim().split(':').map(Number);
  return h * 3600 + m * 60 + s;
}

async function tempoOf(window: Page): Promise<number> {
  return Number((await window.getByTestId('lcd-tempo').textContent())?.trim());
}

/** Records what the exit criterion measured as a test annotation, and returns it. */
function measured(what: string, value: number, unit: string): number {
  test.info().annotations.push({ type: 'measured', description: `${what}: ${value.toFixed(3)} ${unit}` });
  return value;
}

/** A new project with `count` audio tracks after an instrument track at 2 (track 2's instrument hears CCs). */
async function newProject(window: Page, folder: string, count: number): Promise<void> {
  await window.getByTestId('new-project-name').fill('Control');
  await window.getByTestId('new-project-folder').fill(folder);
  // 44.1 kHz: the Stem Splitter's test mix is (and the model runs) at that rate, and files import at the project's.
  await window.getByRole('radio').filter({ hasText: '44.1 kHz' }).click();
  await window.getByTestId('create-project').click();
  await expect(window.getByTestId('project-name')).toHaveText('Control');
  const headers = window.locator('an-track-header');
  await window.getByTestId('add-track').click();
  await window.getByTestId('add-track-menu').click();
  await window.getByTestId('add-instrument-track').click();
  for (let n = 3; n <= count; n++) {
    await headers.last().click();
    await window.getByTestId('add-track').click();
    await expect(headers).toHaveCount(n);
  }
}

/**
 * A track name as a Mackie Control scribble strip shows it (docs/control/README.md): at most 6 characters, spaces going
 * first, then lowercase vowels from the end, keeping a trailing number.
 */
function abbreviate(name: string): string {
  let text = name;
  if (text.length > 6) {
    text = text.replace(/ /g, '');
  }
  const number = /\d+$/.exec(text)?.[0] ?? '';
  let head = text.slice(0, text.length - number.length);
  for (let i = head.length - 1; i > 0 && head.length + number.length > 6; i--) {
    if ('aeiou'.includes(head[i] ?? '')) {
      head = head.slice(0, i) + head.slice(i + 1);
    }
  }
  return (head + number).slice(0, 6);
}

// ---- reading audio (for the Stem Splitter's SDR)

function readWav(path: string): Float32Array {
  const bytes = readFileSync(path);
  let offset = 12;
  let channels = 1;
  let bits = 16;
  let format = 1;
  let data: Buffer | null = null;
  while (offset + 8 <= bytes.length) {
    const id = bytes.toString('ascii', offset, offset + 4);
    const size = bytes.readUInt32LE(offset + 4);
    if (id === 'fmt ') {
      format = bytes.readUInt16LE(offset + 8);
      channels = bytes.readUInt16LE(offset + 10);
      bits = bytes.readUInt16LE(offset + 22);
    } else if (id === 'data') {
      data = bytes.subarray(offset + 8, offset + 8 + size);
    }
    offset += 8 + size + (size % 2);
  }
  if (!data) {
    throw new Error(`${path} has no data`);
  }
  const step = bits / 8;
  const frames = Math.floor(data.length / (step * channels));
  const out = new Float32Array(frames);
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
    out[f] = sum / channels;
  }
  return out;
}

/** Signal-to-distortion ratio of an estimate against the reference, in dB. */
function sdr(reference: Float32Array, estimate: Float32Array): number {
  let signal = 0;
  let error = 0;
  const n = Math.min(reference.length, estimate.length);
  for (let i = 0; i < n; i++) {
    const r = reference[i] ?? 0;
    signal += r * r;
    error += (r - (estimate[i] ?? 0)) ** 2;
  }
  return 10 * Math.log10(signal / Math.max(error, 1e-12));
}

/** Where scripts/build-content.mjs writes the test mix and its stems. */
const StemMix = process.env['ANODE_STEM_MIX'] ?? resolve(__dirname, '../../../../../engine/artifacts/content/stem-mix');

/** A Mackie Control unit's Host Connection Query (serial ABCDEFG, challenge 01 02 03 04), as McuVectors.json has it. */
const McuHostQuery = [0xf0, 0x00, 0x00, 0x66, 0x14, 0x01, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 1, 2, 3, 4, 0xf7];

// The device runs at the project's rate (44.1 kHz, the test mix's).
test.use({ desktopSettings: { audio: { sampleRate: 44_100 } } });

test('plays and mixes from hardware, syncs with other gear and splits stems: assignments, Smart Controls, Mackie Control, HUI, OSC, clock and MTC out and in, Link, the Stem Splitter, reopening', async ({
  app,
  window
}) => {
  test.setTimeout(600_000);
  const scratch = mkdtempSync(join(tmpdir(), 'anode-control-golden-'));
  const mix = join(StemMix, 'mix.wav');
  expect(existsSync(mix), `the test mix (${mix}) is built`).toBe(true);
  const probe = await MidiProbe.start();
  let osc: OscClient | null = null;
  try {
    await newProject(window, scratch, 16);
    // The test mix on track 1: signal for the meters, and the region the Stem Splitter splits.
    const headers = window.locator('an-track-header');
    await headers.first().click();
    await app.evaluate(({ dialog }, path) => {
      dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [path] });
    }, mix);
    await window.getByRole('button', { name: 'Import audio' }).click();
    await expect(window.getByRole('img', { name: /^Tracks area:/ })).toHaveAttribute('aria-label', /1 region/, {
      timeout: 30_000
    });
    await shortcut(window, 'x'); // the Mixer

    await test.step('1. Controller assignments: learn a knob and a button; the knob moves only the volume, and writes automation in Latch', async () => {
      await menu(app, 'controllerAssignments');
      const assignments = window.getByTestId('controller-assignments');
      await expect(assignments).toBeVisible();
      await assignments.getByTestId('controller-learn').click();
      await expect(assignments.getByTestId('learn-banner')).toBeVisible();
      // Select track 2's volume: its fader, clicked while learning.
      await fader(window, 'Inst 1').click();
      await expect(assignments.getByTestId('learn-target')).toContainText('Inst 1: Volume');
      await probe.cc(21, 64);
      await expect(assignments.getByTestId('learn-last')).toContainText('CC 21 = 64');
      await expect(assignments.getByTestId('assignment-row')).toHaveCount(1);
      await expect(assignments.getByTestId('assignment-row')).toContainText('CC 21');
      // A button (note 36) toggles the selected track's mute.
      await assignments.getByTestId('learn-target-menu').click();
      await window.getByTestId('learn-target-mute').click();
      await probe.press(36);
      await expect(assignments.getByTestId('assignment-row')).toHaveCount(2);
      await expect(assignments.getByTestId('assignment-row').nth(1)).toContainText('Selected Track: Mute');
      await assignments.getByTestId('learn-done').click();
      await assignments.getByTestId('controller-assignments-close').click();

      // The knob moves track 2's volume, and only that: the instrument hears no MIDI.
      const before = await volumeOf(window, 'Inst 1');
      await probe.sweep(21, 64, 120);
      await expect.poll(() => volumeOf(window, 'Inst 1')).toBeGreaterThan(before);
      const inst = window.getByRole('group', { name: /^Track \d+: Inst 1$/ });
      await expect(inst.getByTestId('midi-activity')).not.toHaveAttribute('data-active', 'true');
      // The button mutes the selected track (track 2).
      await window.getByRole('group', { name: /^Track \d+: Inst 1$/ }).click();
      await probe.press(36);
      await expect(window.getByRole('button', { name: 'Mute Inst 1' }).first()).toHaveAttribute('aria-pressed', 'true');
      await probe.press(36);
      await expect(window.getByRole('button', { name: 'Mute Inst 1' }).first()).toHaveAttribute(
        'aria-pressed',
        'false'
      );

      // Latch: a pass with the knob writes it as volume automation.
      await shortcut(window, 'a');
      await inst.getByTestId('automation-mode').click();
      await window.getByRole('menuitemradio', { name: 'Latch' }).click();
      await window.getByTestId('play').click();
      await window.waitForTimeout(300);
      await probe.sweep(21, 120, 40, 30);
      await window.getByRole('button', { name: 'Stop' }).click();
      await expect(window.getByRole('slider', { name: /Volume automation point/ }).first()).toBeVisible();
      expect(await window.getByRole('slider', { name: /Volume automation point/ }).count()).toBeGreaterThan(2);
      await shortcut(window, 'a');
    });

    await test.step('2. Smart Controls from hardware: knob 1 learned to CC 22 moves its parameter', async () => {
      await window.getByRole('group', { name: /^Track \d+: Inst 1$/ }).click();
      await shortcut(window, 'b');
      await window.getByTestId('smart-auto-map').click();
      const knob1 = window.locator('[data-testid="smart-knob"][data-control="knob1"]');
      await knob1.click({ button: 'right' });
      await window.getByTestId('learn-assignment').click();
      const assignments = window.getByTestId('controller-assignments');
      await expect(assignments.getByTestId('learn-target')).toContainText('Smart Control');
      await probe.cc(22, 10);
      await expect(assignments.getByTestId('assignment-row')).toHaveCount(3);
      await assignments.getByTestId('learn-done').click();
      const value = async () => Number(await knob1.getByRole('slider').getAttribute('aria-valuenow'));
      const before = await value();
      await probe.sweep(22, 10, 110);
      await expect.poll(value).not.toBe(before);
      await assignments.getByTestId('controller-assignments-close').click();
    });

    await test.step('3. Mackie Control: faders, scribble strips, touch, transport, banking, meters, timecode', async () => {
      await shortcut(window, 'x'); // the Mixer again
      await expect(fader(window, 'Audio 1')).toBeVisible();
      await menu(app, 'controlSurfaces');
      const setup = window.getByTestId('control-surfaces');
      await setup.getByTestId('surface-kind').selectOption('mackieControl');
      await setup.getByTestId('surface-input').selectOption('anode:in');
      await setup.getByTestId('surface-output').selectOption('anode:out');
      await probe.record();
      await setup.getByTestId('surface-add').click();
      await expect(setup.getByTestId('surface-row')).toHaveCount(1);
      // The handshake: Anode queries the device; the unit's host connection query is answered and connects it.
      await probe.wait('F00000661400F7');
      await probe.send(McuHostQuery);
      await probe.wait('F0000066140241424344454647');
      await expect(setup.locator('[data-connected="true"]')).toHaveCount(1);
      // The first eight faders as 14-bit pitch bends, and the names on the strips (abbreviated to 6 characters).
      const names = await headers.evaluateAll(es =>
        es.map(e => (e.getAttribute('aria-label') ?? '').replace(/^Track \d+: /, ''))
      );
      await expect
        .poll(async () => (await probe.mcuState()).strips.map(s => s.top.trim()))
        .toEqual(names.slice(0, 8).map(abbreviate));
      const state = await probe.mcuState();
      expect(state.faders).toHaveLength(8);
      // The audio tracks sit at 0 dB (about 0.78 on the fader law); the knob turned Inst 1 down in step 1.
      const inst = names.indexOf('Inst 1');
      const zeroDb = state.faders[inst === 0 ? 1 : 0] ?? 0;
      expect(zeroDb).toBeGreaterThan(0.7);
      expect(state.faders.filter((_, i) => i !== inst).every(f => Math.abs(f - zeroDb) < 0.002)).toBe(true);
      expect(state.faders[inst] ?? 1).toBeLessThan(zeroDb);
      // Fader 1 with touch moves track 1's volume.
      const before = await volumeOf(window, 'Audio 1');
      await probe.mcuFader(0, 0.5);
      await expect.poll(() => volumeOf(window, 'Audio 1')).toBeLessThan(before);
      // Play, Stop and Record.
      await probe.mcuButton('play');
      await expect(window.getByTestId('play')).toHaveAttribute('aria-pressed', 'true');
      // Meters arrive while playing (channel pressure), and the timecode display runs with the playhead.
      await expect.poll(async () => Math.max(...(await probe.mcuState()).meters)).toBeGreaterThan(0);
      const first = (await probe.mcuState()).timecode;
      await expect.poll(async () => (await probe.mcuState()).timecode).not.toBe(first);
      await probe.mcuButton('stop');
      await expect(window.getByTestId('play')).toHaveAttribute('aria-pressed', 'false');
      // Arm Audio 2 from its strip's record button, then Record.
      const audio2 = names.indexOf('Audio 2');
      const arm = window.getByRole('button', { name: 'Record enable Audio 2', exact: true });
      await probe.mcuButton('recArm', audio2);
      await expect(arm).toHaveAttribute('aria-pressed', 'true');
      await probe.mcuButton('record');
      await expect(window.getByTestId('record')).toHaveAttribute('aria-pressed', 'true');
      await probe.mcuButton('stop');
      await expect(window.getByTestId('record')).toHaveAttribute('aria-pressed', 'false');
      await probe.mcuButton('recArm', audio2);
      await expect(arm).toHaveAttribute('aria-pressed', 'false');
      // Bank right: tracks 9–16 on the strips.
      await probe.mcuButton('bankRight');
      await expect(setup.getByTestId('surface-bank')).toHaveText('Tracks 9–16');
      await expect
        .poll(async () => (await probe.mcuState()).strips.map(s => s.top.trim()))
        .toEqual(names.slice(8, 16).map(abbreviate));
    });

    await test.step('4. HUI: the ping is answered, and fader 1 moves track 1’s volume', async () => {
      const setup = window.getByTestId('control-surfaces');
      await setup.getByTestId('surface-remove').click();
      await expect(setup.getByTestId('surface-row')).toHaveCount(0);
      await setup.getByTestId('surface-kind').selectOption('hui');
      await setup.getByTestId('surface-input').selectOption('anode:in');
      await setup.getByTestId('surface-output').selectOption('anode:out');
      await probe.huiPing(true);
      await setup.getByTestId('surface-add').click();
      await expect(setup.locator('[data-connected="true"]')).toHaveCount(1);
      // A unit's own ping is answered at once.
      const since = await probe.now();
      await probe.send([0x90, 0x00, 0x00]);
      await probe.wait('90007F', 1, 5_000, since);
      const before = await volumeOf(window, 'Audio 1');
      await probe.huiFader(0, 0.7);
      await expect.poll(() => volumeOf(window, 'Audio 1')).not.toBe(before);
    });

    await test.step('5. OSC: play and set a volume over UDP; a subscriber hears an editor change', async () => {
      const setup = window.getByTestId('control-surfaces');
      await setup.getByTestId('osc-port').fill('9123');
      await setup.getByTestId('osc-port').press('Enter');
      await setup.getByTestId('osc-enabled').check();
      await expect(setup.getByTestId('osc-status')).toHaveText('Listening');
      osc = await OscClient.open(9123);
      await osc.send('/anode/transport/play');
      await expect(window.getByTestId('play')).toHaveAttribute('aria-pressed', 'true');
      await window.getByRole('button', { name: 'Stop' }).click();
      const before = await volumeOf(window, 'Audio 1');
      await osc.send('/anode/track/1/volume', [0.5]);
      await expect.poll(() => volumeOf(window, 'Audio 1')).not.toBe(before);
      await osc.subscribe();
      await expect(setup.getByTestId('osc-subscribed')).toContainText(`${osc.port}`);
      const client = osc;
      client.received.length = 0;
      await fader(window, 'Audio 1').focus();
      await window.keyboard.press('ArrowUp');
      await expect.poll(() => client.received.some(m => m.address === '/anode/track/1/volume')).toBe(true);
      await setup.getByTestId('control-surfaces-close').click();
    });

    await test.step('6. MIDI Clock and MTC out at 120 bpm: tick spacing, Start/Stop/SPP, timecode', async () => {
      await menu(app, 'syncSettings');
      const sync = window.getByTestId('sync-settings');
      await sync.getByTestId('sync-add-destination').selectOption('anode:out');
      const destination = sync.getByTestId('sync-destination');
      await destination.getByTestId('sync-destination-mtc').check();
      await expect(destination.getByTestId('sync-destination-clock')).toBeChecked();
      await window.getByRole('button', { name: 'Go to start' }).click();
      await probe.record();
      await window.getByTestId('play').click();
      await window.waitForTimeout(3_000);
      await window.getByRole('button', { name: 'Stop' }).click();
      await probe.wait('FC');
      const clock = await probe.analyzeClock();
      expect(measured('Clock tick interval (median)', clock.medianMs, 'ms')).toBeCloseTo(60_000 / Bpm / 24, 1);
      expect(measured('Clock jitter (p99 deviation)', clock.p99DeviationMs, 'ms')).toBeLessThanOrEqual(0.5);
      // A rare stall of the JIT host's null device is one long interval, not jitter.
      expect(measured('Clock outliers', clock.outliers, 'intervals')).toBeLessThanOrEqual(2);
      expect(clock.transport.map(t => t.kind)).toEqual(expect.arrayContaining(['start', 'stop']));
      expect(clock.transport.findIndex(t => t.kind === 'start')).toBeLessThan(
        clock.transport.findIndex(t => t.kind === 'stop')
      );
      // A locate sends Song Position Pointer: bar 3 is sixteenth 32.
      await probe.record();
      await window.locator('an-timeline').click({ position: { x: 2 * Bar + 1, y: Ruler / 2 } });
      await expect.poll(async () => (await probe.recorded()).map(songPosition).find(p => p !== null)).toBe(32);
      // MTC decodes to the playhead within a frame: played from bar 3 (4 s at 120 bpm), its timecode starts there and
      // keeps to the playhead's time.
      await probe.record();
      const from = await playheadSeconds(window);
      await window.getByTestId('play').click();
      await window.waitForTimeout(1_500);
      await window.getByRole('button', { name: 'Stop' }).click();
      const mtc = await probe.analyzeMtc('fps25');
      expect(mtc.positions.length).toBeGreaterThan(4);
      // A set of eight quarter frames reads as its frame at the time of its last piece, seven quarter frames after the
      // first: the first set's first piece went out as playing started, at the playhead.
      const first = mtc.positions.find(p => !p.fullFrame);
      const startedAt = (first?.seconds ?? 0) - 7 / (25 * 4);
      expect(measured('MTC start against the playhead', Math.abs(startedAt - from), 's')).toBeLessThanOrEqual(1 / 25);
      expect(measured('MTC drift', mtc.maxDriftFrames, 'frames')).toBeLessThanOrEqual(1);
    });

    await test.step('7. MIDI Clock and MTC in: follow 100 bpm with Start/Stop; chase MTC from 00:00:10:00', async () => {
      const sync = window.getByTestId('sync-settings');
      await sync.getByTestId('sync-source-midiClock').click();
      await sync.getByTestId('sync-clock-in').selectOption('anode:in');
      await probe.clockStart(100);
      await expect(window.getByTestId('play')).toHaveAttribute('aria-pressed', 'true');
      await expect(window.getByTestId('sync-indicator')).toHaveAttribute('data-locked', 'true');
      await expect
        .poll(async () => Math.abs(Number(await window.getByTestId('sync-tempo').textContent()) - 100))
        .toBeLessThanOrEqual(0.5);
      measured('Clock in tempo', Number(await window.getByTestId('sync-tempo').textContent()), 'bpm');
      await probe.clockStop();
      await expect(window.getByTestId('play')).toHaveAttribute('aria-pressed', 'false');

      await sync.getByTestId('sync-source-mtc').click();
      await sync.getByTestId('sync-mtc-in').selectOption('anode:in');
      const start = await probe.mtcStart('00:00:10:00', 'fps25');
      await expect(window.getByTestId('play')).toHaveAttribute('aria-pressed', 'true');
      await window.waitForTimeout(1_000);
      const t0 = await probe.now();
      const at = await playheadSeconds(window);
      const t1 = await probe.now();
      const timecode = 10 + ((t0 + t1) / 2 - start) / 1e9;
      expect(measured('MTC chase', Math.abs(at - timecode), 's')).toBeLessThanOrEqual(1 / 25 + (t1 - t0) / 2e9 + 0.01);
      await probe.mtcStop();
      await expect(window.getByTestId('play')).toHaveAttribute('aria-pressed', 'false');
      await sync.getByTestId('sync-source-internal').click();
      await sync.getByTestId('sync-settings-close').click();
    });

    await test.step('8. Ableton Link: tempo both ways, beat phase within 1 ms', async () => {
      await window.getByTestId('link-toggle').click();
      await expect(window.getByTestId('link-toggle')).toHaveAttribute('aria-pressed', 'true');
      await probe.linkJoin(128);
      await expect(window.getByTestId('link-peers')).toHaveText('1');
      await expect.poll(() => tempoOf(window)).toBe(128);
      // A tempo change in Anode reaches the probe.
      await window.getByTestId('lcd-tempo').click();
      await window.getByRole('textbox', { name: 'Tempo in bpm' }).fill('131');
      await window.getByRole('textbox', { name: 'Tempo in bpm' }).press('Enter');
      await expect.poll(async () => Math.round((await probe.linkState()).tempo * 100) / 100).toBe(131);
      // Playing, Anode's beat (the MIDI Clock it sends, following Link) is the session's within 1 ms.
      // (The probe counts each clock's beat from the Start before it, so it records from before the start.)
      await probe.record();
      await window.getByTestId('play').click();
      await window.waitForTimeout(3_000);
      const error = await probe.linkClockError();
      expect(error.clocks).toBeGreaterThan(24);
      expect(measured('Link phase difference (max)', error.maxAbsMs, 'ms')).toBeLessThanOrEqual(1);
      await window.getByRole('button', { name: 'Stop' }).click();
      await window.getByTestId('link-toggle').click();
      await expect(window.getByTestId('link-toggle')).toHaveAttribute('aria-pressed', 'false');
      await probe.linkLeave();
    });

    await test.step('9. Stem Splitter: four tracks under the region’s track, each stem near its source', async () => {
      const timeline = window.locator('an-timeline');
      await timeline.click({ position: { x: 40, y: Ruler + Lane / 2 } });
      await menu(app, 'stemSplitter');
      const dialog = window.getByTestId('stem-splitter');
      await dialog.getByTestId('stem-set-four').check();
      const count = await headers.count();
      await window.getByTestId('stem-split').click();
      await expect(window.getByTestId('stem-stage')).toBeVisible();
      await expect(headers).toHaveCount(count + 4, { timeout: 300_000 });
      for (const [i, stem] of ['Vocals', 'Drums', 'Bass', 'Other'].entries()) {
        await expect(headers.nth(1 + i)).toHaveAttribute('aria-label', new RegExp(`mix – ${stem}`));
      }
      // Each stem against its source: the files the job wrote into the project's Audio Files ("mix Vocals.wav").
      const audio = readdirSync(scratch, { recursive: true, withFileTypes: true }).find(
        e => e.isDirectory() && e.name === 'Audio Files'
      );
      expect(audio).toBeTruthy();
      const folder = join(audio?.parentPath ?? scratch, 'Audio Files');
      const minimum: Record<string, number> = { vocals: 3, drums: 6, bass: 6, other: 3 };
      for (const [stem, floor] of Object.entries(minimum)) {
        const written = join(folder, `mix ${stem[0]?.toUpperCase()}${stem.slice(1)}.wav`);
        const value = sdr(readWav(join(StemMix, `${stem}.wav`)), readWav(written));
        expect(measured(`SDR ${stem}`, value, 'dB')).toBeGreaterThanOrEqual(floor);
      }
    });

    await test.step('10. Save, close and reopen: assignments, surfaces and sync settings are intact', async () => {
      await menu(app, 'syncSettings');
      await window.getByTestId('sync-settings').getByTestId('sync-frame-rate').selectOption('fps30');
      await window.getByTestId('sync-settings-close').click();
      await shortcut(window, 'ControlOrMeta+s');
      await expect(window.getByTestId('unsaved')).toHaveCount(0);
      await window.getByRole('button', { name: 'Close project' }).click();
      await window.getByTestId('recent-Control').click();
      await expect(window.getByTestId('project-name')).toHaveText('Control');
      await menu(app, 'controllerAssignments');
      await expect(window.getByTestId('controller-assignments').getByTestId('assignment-row')).toHaveCount(3);
      await window.getByTestId('controller-assignments-close').click();
      await menu(app, 'controlSurfaces');
      await expect(window.getByTestId('control-surfaces').getByTestId('surface-row')).toHaveCount(1);
      await expect(window.getByTestId('control-surfaces').getByTestId('osc-enabled')).toBeChecked();
      await window.getByTestId('control-surfaces-close').click();
      await menu(app, 'syncSettings');
      const sync = window.getByTestId('sync-settings');
      await expect(sync.getByTestId('sync-destination')).toHaveCount(1);
      await expect(sync.getByTestId('sync-frame-rate')).toHaveValue('fps30');
    });
  } finally {
    (osc as OscClient | null)?.close();
    await probe.stop();
    rmSync(scratch, { recursive: true, force: true });
  }
});
