import type { ElectronApplication, Locator, Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { expect, test } from '../fixtures';

/*
 * Phase 9's exit criterion (docs/10-phase-9.md): MIDI reads and edits as a score that prints, and projects move to and
 * from other software through MusicXML, AAF and Standard MIDI Files, on the null device, against the real app (the
 * notation engine, the server domain, MusicXML, AAF and the Score Editor). The e2e global setup provides:
 *
 * - **pyaaf2** (MIT) as the independent AAF reader and fixture maker, behind one helper script, ANODE_AAF_HELPER
 *   (default `scripts/aaf-check.py`, run with ANODE_PYTHON, which the global setup points at the venv
 *   `scripts/setup-pyaaf2.py` makes in `engine/artifacts/pyaaf2`):
 *     `aaf-check.py read <file.aaf>` prints JSON `{ sampleRate, tracks: [{ name, clips: [{ name, position,
 *     sourceOffset, length, fadeIn, fadeOut, gainDb, media }] }], transitions: [{ track, position, length }] }` with
 *     positions, offsets and lengths in samples, `media` the essence's path or "embedded";
 *     `aaf-check.py make <out.aaf> <media folder>` writes the import fixture (two tracks, three clips, a crossfade),
 *     and prints the same JSON for what it wrote.
 * - **The MusicXML 4.0 schema** check, ANODE_MUSICXML_CHECK (default `scripts/musicxml-validate.py <file>`, exit 0 when
 *   the file validates against D's checked-in XSD, with xmllint), and D's string quartet fixture, ANODE_QUARTET_FIXTURE
 *   (default `engine/tests/Anode.Interchange.Tests/MusicXml/Fixtures/string-quartet.musicxml`).
 * - **A PDF text reader**, ANODE_PDF_TEXT (default `pdftotext`, poppler), to read the printed score's text.
 *
 * The piano passage is the notation engine's own exit-criterion case (the "exit-criterion" vector of
 * engine/tests/Anode.Notation.Tests/ScoreVectors.json). "Display quantise 1/8" is Logic's "8,12": the 1/8 grid with the
 * 1/8-triplet grid (480/320 ticks, the "exit-criterion-eighths" vector), which draws bar 3's sixteenths as E4/8 F4+G4/8;
 * with the 1/16-triplet grid kept (480/160) they would read as triplets. What was checked is recorded as `measured`
 * annotations.
 */

const Root = resolve(__dirname, '../../../../..');
const Python = process.env['ANODE_PYTHON'] ?? 'python3';
const AafHelper = process.env['ANODE_AAF_HELPER'] ?? join(Root, 'scripts/aaf-check.py');
const MusicXmlCheck = process.env['ANODE_MUSICXML_CHECK'] ?? join(Root, 'scripts/musicxml-validate.py');
const QuartetFixture =
  process.env['ANODE_QUARTET_FIXTURE'] ??
  join(Root, 'engine/tests/Anode.Interchange.Tests/MusicXml/Fixtures/string-quartet.musicxml');
const PdfText = process.env['ANODE_PDF_TEXT'] ?? 'pdftotext';

const Ppq = 960;
const Bar = 4 * Ppq;
const Rate = 48_000;

// ---- the Server's API, for setting up notes and checking the model (the UI is what is tested)

interface Connection {
  baseUrl: string;
  token: string | null;
}

async function connection(window: Page): Promise<Connection> {
  return window.evaluate(() =>
    (window as unknown as { anode: { connection(): Promise<Connection> } }).anode.connection()
  );
}

async function api<T>(window: Page, method: 'GET' | 'POST', path: string, body?: unknown): Promise<T> {
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

interface Note {
  id: number;
  pitch: number;
  startTick: number;
  lengthTicks: number;
}
interface ProjectShape {
  path: string;
  tracks: { id: string; name: string; kind: string; staffStyleId?: string | null }[];
  midiRegions: {
    id: string;
    trackId: string;
    startTick: number;
    notes: Note[];
    controls: { kind: string; number: number; value: number; tick: number }[];
    notation: { displayQuantizeTicks: number; tripletQuantizeTicks: number };
  }[];
  regions: {
    id: string;
    trackId: string;
    fileId: string;
    startTick: number;
    fileOffset: number;
    length: number;
    gainDb: number;
    fadeIn: number;
    fadeOut: number;
  }[];
  markers: { name: string; tick: number }[];
  settings: {
    tempoEvents: { tick: number; bpm: number }[];
    signatures: { tick: number; numerator: number; denominator: number }[];
  };
  audioFiles: { id: string; sampleRate: number; frames: number }[];
  score: {
    symbols: {
      id: string;
      kind: string;
      noteId?: number | null;
      tick?: number | null;
      endNoteId?: number | null;
      text?: string | null;
    }[];
    lyrics: { id: string; noteId: number; text: string; syllabic: string }[];
    chords: { id: string; tick: number; root: { step: string; alter: number }; quality: string }[];
    sets: { id: string; name: string; trackIds: string[] }[];
    layout: { title: string; transposingScore: boolean };
  };
}
interface ScoreEventShape {
  id: string;
  kind: 'note' | 'chord' | 'rest';
  tick: number;
  durationTicks: number;
  duration: { value: string; dots: number; tuplet: { group: number } | null };
  beam?: number | null;
  notes: {
    noteId: number;
    pitch: number;
    written: { step: string; alter: number; octave: number };
    tieStart: boolean;
    tieStop: boolean;
  }[];
}
interface ScoreDocumentShape {
  view: {
    transposing: boolean;
    parts: { trackId: string; measures: { staves: { voices: { number: number; events: ScoreEventShape[] }[] }[] }[] }[];
  };
  mei: string;
}

const project = (window: Page) => api<ProjectShape>(window, 'GET', '/project');
const commands = (window: Page, list: unknown[], label = 'Test') =>
  api(window, 'POST', '/project/commands', { commands: list, label });
const score = (window: Page, query: string) => api<ScoreDocumentShape>(window, 'GET', `/project/score?${query}`);

/** Every event of a part's staff and voice, in order. */
function eventsOf(document: ScoreDocumentShape, trackId: string, staff: number, voice: number): ScoreEventShape[] {
  const part = document.view.parts.find(p => p.trackId === trackId);
  return part?.measures.flatMap(m => m.staves[staff]?.voices.find(v => v.number === voice)?.events ?? []) ?? [];
}

/** Every note as the score draws it: [start of its first segment, length to the end of its last, concert pitch]. */
function drawnNotes(document: ScoreDocumentShape): number[][] {
  const spans = new Map<number, { start: number; end: number; pitch: number }>();
  for (const event of document.view.parts.flatMap(p =>
    p.measures.flatMap(m => m.staves.flatMap(s => s.voices.flatMap(v => v.events)))
  )) {
    for (const note of event.notes) {
      const span = spans.get(note.noteId);
      const end = event.tick + event.durationTicks;
      spans.set(
        note.noteId,
        span
          ? { start: Math.min(span.start, event.tick), end: Math.max(span.end, end), pitch: note.pitch }
          : { start: event.tick, end, pitch: note.pitch }
      );
    }
  }
  return [...spans.values()].map(s => [s.start, s.end - s.start, s.pitch]).sort(byStartThenPitch);
}

function byStartThenPitch(a: number[], b: number[]): number {
  return (a[0] ?? 0) - (b[0] ?? 0) || (a[2] ?? 0) - (b[2] ?? 0) || (a[1] ?? 0) - (b[1] ?? 0);
}

/** Records what the exit criterion checked as a test annotation. */
function measured(step: string, what: string): void {
  test.info().annotations.push({ type: 'measured', description: `${step}: ${what}` });
}

/** Lyrics in the order of their notes. */
function lyricsInOrder(p: ProjectShape): string[] {
  const starts = new Map(p.midiRegions.flatMap(r => r.notes.map(n => [n.id, r.startTick + n.startTick] as const)));
  return [...p.score.lyrics].sort((a, b) => (starts.get(a.noteId) ?? 0) - (starts.get(b.noteId) ?? 0)).map(l => l.text);
}

/** Chord symbols in time order, as text. */
function chordsInOrder(p: ProjectShape): string[] {
  return [...p.score.chords]
    .sort((a, b) => a.tick - b.tick)
    .map(ch => `${ch.root.step.toUpperCase()}${ch.root.alter > 0 ? '♯' : ch.root.alter < 0 ? '♭' : ''}${ch.quality}`);
}

// ---- driving the app

/** Chooses an application menu item by its id (apps/desktop/src/main.ts gives each `anode:<command>`). */
async function menu(app: ElectronApplication, command: string): Promise<void> {
  await app.evaluate(({ Menu }, id) => {
    const item = Menu.getApplicationMenu()?.getMenuItemById(id);
    if (!item) {
      throw new Error(`No menu item ${id}`);
    }
    (item.click as () => void)();
  }, `anode:${command}`);
}

/** The native open dialog answers `file` once; the save dialog answers `save`. */
async function answerDialogs(app: ElectronApplication, open: string | null, save: string | null = null): Promise<void> {
  await app.evaluate(
    ({ dialog }, [file, target]) => {
      if (file) {
        dialog.showOpenDialog = () => Promise.resolve({ canceled: false, filePaths: [file] });
      }
      if (target) {
        dialog.showSaveDialog = () => Promise.resolve({ canceled: false, filePath: target });
      }
    },
    [open, save] as const
  );
}

async function shortcut(window: Page, key: string): Promise<void> {
  await window.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await window.keyboard.press(key);
}

async function undoCount(window: Page): Promise<number> {
  const history = await api<{ undo: unknown[] }>(window, 'GET', '/project/history');
  return history.undo.length;
}

/** Saves the open project (it would otherwise ask to discard its changes) and closes it. */
async function saveAndClose(app: ElectronApplication, window: Page): Promise<void> {
  await shortcut(window, 'ControlOrMeta+s');
  await expect(window.getByRole('img', { name: 'Unsaved changes' })).toBeHidden();
  await menu(app, 'closeProject');
  await expect(window.getByTestId('new-project-name')).toBeVisible();
}

/** A new project, empty. */
async function newProject(window: Page, folder: string, name: string): Promise<void> {
  await window.getByTestId('new-project-name').fill(name);
  await window.getByTestId('new-project-folder').fill(folder);
  await window.getByTestId('create-project').click();
  await expect(window.getByTestId('project-name')).toHaveText(name);
}

/** The Score Editor in the Editors pane, on a track's regions (selected through the API's ids). */
async function openScore(window: Page): Promise<Locator> {
  await window.getByRole('tab', { name: 'Editors' }).click();
  await window.getByTestId('editor-view').getByRole('tab', { name: 'Score', exact: true }).click();
  const editor = window.getByTestId('score-editor');
  await expect(editor.locator('[data-page] svg').first()).toBeVisible({ timeout: 30_000 });
  return editor;
}

/** A note head of the engraving (its first segment). */
function head(editor: Locator, noteId: number): Locator {
  return editor.locator(`[data-page] g.note[id="n${noteId}"] .notehead use`).first();
}

async function clickNote(editor: Locator, noteId: number, modifiers: ('Shift' | 'Meta')[] = []): Promise<void> {
  await head(editor, noteId).click({ modifiers, force: true });
}

/** The centre of an element's first note head (an element id: `n{noteId}`, or `n{noteId}-2` for a tied segment). */
async function headCentre(editor: Locator, elementId: string): Promise<{ x: number; y: number }> {
  const box = await editor.locator(`[data-page] g.note[id="${elementId}"] .notehead use`).first().boundingBox();
  if (!box) {
    throw new Error(`No note head ${elementId}`);
  }
  return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}

/** The staff lines' y of a measure's first staff, top to bottom. */
async function staffLines(editor: Locator, measure: string): Promise<number[]> {
  const ys = await editor
    .locator(`[data-page] g.measure[id="${measure}"] g.staff`)
    .first()
    .locator('> path')
    .evaluateAll(paths => paths.map(p => p.getBoundingClientRect().y + p.getBoundingClientRect().height / 2));
  return ys.slice(0, 5).sort((a, b) => a - b);
}

/** Where a click inserts at a note's beat on the middle line of the measure's top staff (B4 on a treble staff). */
async function insertPoint(editor: Locator, measure: string, noteId: number): Promise<{ x: number; y: number }> {
  const { x } = await headCentre(editor, `n${noteId}`);
  const lines = await staffLines(editor, measure);
  return { x, y: lines[2] ?? 0 };
}

/** Where a click with the chord tool lands at an element's beat: above the measure's top staff. */
async function chordPoint(
  editor: Locator,
  measure: string,
  elementId: number | string
): Promise<{ x: number; y: number }> {
  const { x } = await headCentre(editor, typeof elementId === 'number' ? `n${elementId}` : elementId);
  const lines = await staffLines(editor, measure);
  const space = ((lines[4] ?? 0) - (lines[0] ?? 0)) / 4;
  return { x, y: (lines[0] ?? 0) - space };
}

/**
 * The piano passage of step 1, the notation engine's exit-criterion case (Anode.Notation.Tests' Cases.ExitCriterion):
 * chords, a triplet, a tie over the bar line, two voices, notes slightly off the grid, sixteenths in bar 3.
 */
const Passage = [
  // Bar 1: a C major chord slightly late, an eighth triplet slightly off, G4 tied over the bar line; C3 held.
  { pitch: 60, startTick: 12, lengthTicks: 940 },
  { pitch: 64, startTick: 12, lengthTicks: 940 },
  { pitch: 67, startTick: 12, lengthTicks: 940 },
  { pitch: 62, startTick: 965, lengthTicks: 300 },
  { pitch: 64, startTick: 1290, lengthTicks: 300 },
  { pitch: 65, startTick: 1595, lengthTicks: 300 },
  { pitch: 67, startTick: 1920, lengthTicks: 2880 },
  { pitch: 48, startTick: 0, lengthTicks: Bar },
  // Bar 2: E5 held over moving quarters (two voices); F2 and G2 slightly late.
  { pitch: 76, startTick: Bar + Ppq, lengthTicks: 3 * Ppq },
  { pitch: 72, startTick: Bar + Ppq, lengthTicks: Ppq },
  { pitch: 74, startTick: Bar + 2 * Ppq, lengthTicks: Ppq },
  { pitch: 71, startTick: Bar + 3 * Ppq, lengthTicks: Ppq },
  { pitch: 41, startTick: Bar + 10, lengthTicks: 1900 },
  { pitch: 43, startTick: Bar + 2 * Ppq + 10, lengthTicks: 1900 },
  // Bar 3: two sixteenths and an eighth, then a C major chord; C3 held.
  { pitch: 64, startTick: 2 * Bar, lengthTicks: 240 },
  { pitch: 65, startTick: 2 * Bar + 240, lengthTicks: 240 },
  { pitch: 67, startTick: 2 * Bar + 480, lengthTicks: 480 },
  { pitch: 60, startTick: 2 * Bar + Ppq, lengthTicks: 3 * Ppq },
  { pitch: 64, startTick: 2 * Bar + Ppq, lengthTicks: 3 * Ppq },
  { pitch: 67, startTick: 2 * Bar + Ppq, lengthTicks: 3 * Ppq },
  { pitch: 48, startTick: 2 * Bar, lengthTicks: Bar }
];

test.describe('Phase 9: notation and interchange', () => {
  test.setTimeout(600_000);

  test('reads, edits and prints MIDI as a score, and moves projects through MusicXML, AAF and MIDI files', async ({
    app,
    window
  }) => {
    const folder = mkdtempSync(join(tmpdir(), 'anode-phase9-'));
    try {
      await newProject(window, folder, 'Notation');

      // ---- 1. Score: the piano region with the piano style (split at C3) and display quantise 1/16.
      await commands(window, [
        { type: 'addTrack', kind: 'instrument', stereo: true, name: 'Piano' },
        { type: 'setKeys', events: [{ tick: 0, tonic: 0, mode: 'major' }] }
      ]);
      let state = await project(window);
      const notationPath = state.path;
      const piano = state.tracks.find(t => t.name === 'Piano')?.id ?? '';
      await commands(window, [
        { type: 'assignStaffStyle', trackIds: [piano], styleId: 'factory.piano' },
        { type: 'addMidiRegion', trackId: piano, startTick: 0, lengthTicks: 3 * Bar, name: 'Piano' }
      ]);
      state = await project(window);
      const regionId = state.midiRegions[0]?.id ?? '';
      await commands(window, [
        { type: 'addNotes', regionId, notes: Passage.map(n => ({ ...n, velocity: 90, channel: 1 })) },
        { type: 'setRegionNotation', regionIds: [regionId], displayQuantizeTicks: 240, tripletQuantizeTicks: 160 }
      ]);
      state = await project(window);
      const notes = state.midiRegions[0]?.notes ?? [];
      const id = (pitch: number, start: number) =>
        notes.find(n => n.pitch === pitch && Math.abs(n.startTick - start) < 20)?.id ?? -1;

      // Select the region (the inspector shows its score settings) and open the Score Editor.
      await window.locator('an-timeline').click({ position: { x: 40, y: 24 + 72 / 2 } });
      await expect(window.getByTestId('region-display-quantize')).toHaveValue('240');
      const editor = await openScore(window);

      const document = await score(window, `part=${piano}&view=linear`);
      const treble = eventsOf(document, piano, 0, 1);
      const voice2 = eventsOf(document, piano, 0, 2);
      const bass = eventsOf(document, piano, 1, 1);
      // The late chord on beat 1, the triplet drawn on its grid, the tie, the beams, the two voices, the bass staff.
      expect(treble[0]).toMatchObject({ kind: 'chord', tick: 0, duration: { value: 'quarter' } });
      expect(treble[0]?.notes.map(n => n.pitch)).toEqual([60, 64, 67]);
      const triplet = treble.filter(e => e.duration.tuplet !== null);
      expect(triplet.map(e => [e.tick, e.duration.value, e.notes[0]?.noteId])).toEqual([
        [960, 'eighth', id(62, 965)],
        [1280, 'eighth', id(64, 1290)],
        [1600, 'eighth', id(65, 1595)]
      ]);
      expect(new Set(triplet.map(e => e.duration.tuplet?.group)).size).toBe(1);
      const tie = treble.filter(e => e.notes.some(n => n.noteId === id(67, 1920)));
      expect(tie.map(e => [e.tick, e.notes[0]?.tieStart, e.notes[0]?.tieStop])).toEqual([
        [1920, true, false],
        [Bar, false, true]
      ]);
      const beamed = treble.filter(e => e.beam != null);
      expect(beamed.length).toBeGreaterThanOrEqual(5);
      expect(voice2.filter(e => e.kind !== 'rest').map(e => e.notes[0]?.pitch)).toEqual([72, 74, 71]);
      expect(bass.filter(e => e.kind !== 'rest').map(e => e.notes[0]?.pitch)).toEqual([48, 41, 43, 48]);
      expect(bass.find(e => e.notes[0]?.pitch === 41)?.tick).toBe(Bar);
      // Every note id has an element in the rendered SVG.
      const elements = await editor.locator('[data-page] g.note[id]').evaluateAll(list => list.map(e => e.id));
      for (const note of notes) {
        expect(elements).toContain(`n${note.id}`);
      }
      measured(
        '1 score',
        `${treble.length} treble v1 events, triplet at 960/1280/1600, tie 1920→${Bar}, ${beamed.length} beamed, ` +
          `voice 2 [72,74,71], bass [48,41,43,48], ${notes.length}/${notes.length} note ids in the SVG`
      );

      // Display quantise 1/8 (Logic's "8,12": the 1/8 grid and the 1/8-triplet grid) changes the score, not the MIDI.
      const midiBefore = JSON.stringify(notes);
      await window.getByTestId('region-display-quantize').selectOption('480');
      await window.getByTestId('region-triplet-quantize').selectOption('320');
      await expect.poll(async () => (await score(window, `part=${piano}&view=linear`)).mei).not.toBe(document.mei);
      const eighths = await score(window, `part=${piano}&view=linear`);
      const bar3 = eventsOf(eighths, piano, 0, 1).filter(e => e.tick >= 2 * Bar && e.tick < 2 * Bar + Ppq);
      expect(bar3.map(e => [e.tick, e.duration.value, e.notes.map(n => n.pitch)])).toEqual([
        [2 * Bar, 'eighth', [64]],
        [2 * Bar + 480, 'eighth', [65, 67]]
      ]);
      state = await project(window);
      expect(JSON.stringify(state.midiRegions[0]?.notes)).toBe(midiBefore);
      measured('1 display quantise', `480/320: bar 3 drawn E4/8 F4+G4/8; MIDI unchanged (${notes.length} notes)`);
      await window.getByTestId('region-display-quantize').selectOption('240');
      await window.getByTestId('region-triplet-quantize').selectOption('160');
      await expect.poll(async () => (await score(window, `part=${piano}&view=linear`)).mei).toBe(document.mei);

      // ---- 2. Editing in the score: each one undo step, and the Piano Roll shows the same notes.
      const steps = await undoCount(window);
      // Drag a note up one step: E5 → F5 in C major.
      const e5 = id(76, Bar + Ppq);
      await expect(head(editor, e5)).toBeVisible();
      await clickNote(editor, e5);
      const box = await head(editor, e5).boundingBox();
      const lines = await editor
        .locator('[data-page] g.measure[id="m1"] g.staff')
        .first()
        .locator('> path')
        .evaluateAll(paths => paths.map(p => p.getBoundingClientRect().y));
      const half = ((lines[4] ?? 0) - (lines[0] ?? 0)) / 8;
      if (box) {
        await window.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
        await window.mouse.down();
        // A step and a half less a little: past the editor's 4 px drag threshold, and rounding to one step.
        expect(half * 1.4).toBeGreaterThan(4);
        await window.mouse.move(box.x + box.width / 2, box.y + box.height / 2 - half * 1.4, { steps: 4 });
        await window.mouse.up();
      }
      await expect
        .poll(async () => (await project(window)).midiRegions[0]?.notes.find(n => n.id === e5)?.pitch)
        .toBe(77);
      expect(await undoCount(window)).toBe(steps + 1);

      // Insert a quarter note from the Part box at beat 3 of bar 2 (on the treble's middle line, B4).
      await editor.getByTestId('part-note-quarter').click();
      const existing = new Set((await project(window)).midiRegions[0]?.notes.map(n => n.id));
      const count = existing.size;
      const beat3 = await insertPoint(editor, 'm2', id(74, Bar + 2 * Ppq));
      await window.mouse.click(beat3.x, beat3.y);
      await expect.poll(async () => (await project(window)).midiRegions[0]?.notes.length).toBe(count + 1);
      const inserted = (await project(window)).midiRegions[0]?.notes.find(n => !existing.has(n.id));
      expect(inserted).toMatchObject({ startTick: Bar + 2 * Ppq, lengthTicks: Ppq, pitch: 71 });
      expect(await undoCount(window)).toBe(steps + 2);
      await editor.getByTestId('part-note-quarter').click(); // back to the pointer

      // Delete a note.
      const bassNote = id(48, 2 * Bar);
      await clickNote(editor, bassNote);
      await editor.getByTestId('score-scroller').press('Delete');
      await expect
        .poll(async () => (await project(window)).midiRegions[0]?.notes.some(n => n.id === bassNote))
        .toBe(false);
      expect(await undoCount(window)).toBe(steps + 3);

      // The Piano Roll shows the same notes.
      await window.getByTestId('editor-view').getByRole('tab', { name: 'Piano Roll', exact: true }).click();
      const rollNotes = await window.getByRole('img', { name: /^Piano Roll:/ }).getAttribute('aria-label');
      const edited = (await project(window)).midiRegions[0]?.notes.length ?? 0;
      expect(rollNotes).toContain(`${edited} notes`);
      await window.getByTestId('editor-view').getByRole('tab', { name: 'Score', exact: true }).click();
      await expect(editor.locator('[data-page] svg').first()).toBeVisible();
      measured(
        '2 editing',
        `E5→F5 (77), B4 quarter at ${Bar + 2 * Ppq}, a C3 deleted; undo steps +3; Piano Roll "${rollNotes}"`
      );

      // ---- 3. Symbols and text: render at their notes or positions, and survive save and reopen.
      const [c, d, eNote] = [id(62, 965), id(64, 1290), id(65, 1595)];
      await clickNote(editor, c);
      await editor.getByTestId('part-staccato').click();
      await clickNote(editor, d);
      await editor.getByTestId('part-accent').click();
      await clickNote(editor, c);
      await editor.getByTestId('part-dynamic-mf').click();
      await clickNote(editor, c);
      await clickNote(editor, eNote, ['Shift']);
      await editor.getByTestId('part-slur').click();
      await clickNote(editor, c);
      await clickNote(editor, eNote, ['Shift']);
      await editor.getByTestId('part-crescendo').click();
      // Lyrics: one syllable per note, a hyphenated word ("Hap-py day").
      await editor.getByTestId('part-lyric').click();
      await clickNote(editor, c);
      const lyric = editor.getByTestId('score-entry-lyric');
      await lyric.pressSequentially('Hap');
      await lyric.press('-');
      await lyric.pressSequentially('py');
      await lyric.press('Tab');
      await lyric.pressSequentially('day');
      await lyric.press('Enter');
      await editor.getByTestId('part-lyric').click();
      // Chord symbols at beats 1 of bars 1–3 and beat 3 of bar 1: C, Am7, Fmaj7, G7.
      await editor.getByTestId('part-chord').click();
      for (const [measure, noteId, text] of [
        ['m1', id(60, 12), 'C'],
        ['m1', id(67, 1920), 'Am7'],
        ['m2', `n${id(67, 1920)}-2`, 'Fmaj7'],
        ['m3', id(64, 2 * Bar), 'G7']
      ] as const) {
        const at = await chordPoint(editor, measure, noteId);
        await window.mouse.click(at.x, at.y);
        const field = editor.getByTestId('score-entry-chord');
        await field.fill(text);
        await field.press('Enter');
        await expect(field).toBeHidden();
      }
      await editor.getByTestId('part-chord').click();
      state = await project(window);
      expect(state.score.symbols.map(s => s.kind).sort()).toEqual([
        'accent',
        'crescendo',
        'dynamic',
        'slur',
        'staccato'
      ]);
      expect(state.score.symbols.find(s => s.kind === 'slur')).toMatchObject({ noteId: c, endNoteId: eNote });
      expect(state.score.lyrics.map(l => [l.text, l.syllabic]).sort()).toEqual([
        ['Hap', 'begin'],
        ['day', 'single'],
        ['py', 'end']
      ]);
      expect(lyricsInOrder(state)).toEqual(['Hap', 'py', 'day']);
      expect(chordsInOrder(state)).toEqual(['C', 'Am7', 'Fmaj7', 'G7']);
      expect([...state.score.chords].sort((a, b) => a.tick - b.tick).map(ch => ch.tick)).toEqual([
        0,
        1920,
        Bar,
        2 * Bar
      ]);
      await expect(editor.locator('[data-page] .syl').first()).toBeVisible();
      // The engraving follows the model a render later: wait for the last chord symbol, then check everything.
      const lastChord = [...state.score.chords].sort((a, b) => b.tick - a.tick)[0]?.id ?? '';
      await expect(editor.locator(`[data-page] [id="h${lastChord}"]`)).toBeAttached();
      const svg = (await editor.locator('[data-page]').first().innerHTML()).replace(/\s+/g, ' ');
      for (const text of ['Hap', 'py', 'day', 'Am7', 'Fmaj7', 'G7']) {
        expect(svg).toMatch(new RegExp(`>${text}<`));
      }
      for (const item of [...state.score.symbols.map(s => `s${s.id}`)]) {
        expect(svg).toContain(`id="${item}"`);
      }
      for (const syllable of state.score.lyrics) {
        expect(svg).toContain(`id="l${syllable.id}"`);
      }
      for (const chord of state.score.chords) {
        expect(svg).toContain(`id="h${chord.id}"`);
      }
      expect(svg).toMatch(/class="artic"/);
      expect(svg).toMatch(/class="slur"/);
      expect(svg).toMatch(/class="hairpin"/);
      expect(svg).toMatch(/class="dynam"/);
      measured('3 symbols', 'staccato, accent, mf, slur over 3 notes, crescendo, "Hap-py day", C Am7 Fmaj7 G7 drawn');

      // ---- 4. Staff styles and score sets: a B♭ clarinet part, concert in the full score, a set, an extracted part.
      await commands(window, [{ type: 'addTrack', kind: 'instrument', stereo: true, name: 'Clarinet' }]);
      state = await project(window);
      const clarinet = state.tracks.find(t => t.name === 'Clarinet')?.id ?? '';
      await commands(window, [
        { type: 'addMidiRegion', trackId: clarinet, startTick: 0, lengthTicks: Bar, name: 'Clarinet' }
      ]);
      state = await project(window);
      const clarinetRegion = state.midiRegions.find(r => r.trackId === clarinet)?.id ?? '';
      await commands(window, [
        {
          type: 'addNotes',
          regionId: clarinetRegion,
          notes: [{ pitch: 70, startTick: 0, lengthTicks: Bar, velocity: 90, channel: 1 }]
        }
      ]);
      await window.locator('an-track-header').last().click();
      // The inspector shows the clarinet's settings once the selection has reached it.
      await expect(window.getByRole('heading', { name: 'Track: Clarinet' })).toBeVisible();
      await window.getByTestId('track-staff-style').selectOption('factory.clarinetBb');
      await expect
        .poll(async () => (await project(window)).tracks.find(t => t.id === clarinet)?.staffStyleId)
        .toBe('factory.clarinetBb');
      const part = await score(window, `part=${clarinet}`);
      expect(part.view.transposing).toBe(true);
      expect(eventsOf(part, clarinet, 0, 1)[0]?.notes[0]?.written).toEqual({ step: 'c', alter: 0, octave: 5 });
      // Score Sets: a set of both tracks, opened, and the clarinet part extracted.
      await editor.getByTestId('score-open-sets').click();
      const sets = window.getByTestId('score-sets-window');
      await sets.getByTestId('score-set-new').click();
      await expect.poll(async () => (await project(window)).score.sets[0]?.trackIds).toEqual([piano, clarinet]);
      const setId = (await project(window)).score.sets[0]?.id ?? '';
      const full = await score(window, `set=${setId}`);
      expect(full.view.transposing).toBe(false);
      expect(eventsOf(full, clarinet, 0, 1)[0]?.notes[0]?.written).toEqual({ step: 'b', alter: -1, octave: 4 });
      await sets.getByTestId('score-set-open').click();
      await expect(window.getByTestId('score-window').locator('[data-page] svg').first()).toBeVisible();
      await sets.getByTestId('score-set-part').selectOption(clarinet);
      await sets.getByTestId('score-set-extract').click();
      await expect(window.getByTestId('score-window')).toContainText('Part: Clarinet');
      // Back to the full score: File ▸ Print Score prints the score window's set.
      await sets.getByTestId('score-set-open').click();
      await sets.getByTestId('score-sets-window-close').click();
      measured(
        '4 staff styles',
        'clarinet part written C5 (transposing), full score B♭4 (concert); set of 2; part extracted'
      );

      // ---- 5. Print the score set to PDF: pages, title, instrument names, lyrics and chord symbols as text.
      await commands(window, [
        {
          type: 'setScoreLayout',
          layout: { ...(await project(window)).score.layout, title: 'Golden Etude', transposingScore: false }
        }
      ]);
      const pdf = join(folder, 'Golden Etude.pdf');
      await answerDialogs(app, null, pdf);
      await menu(app, 'printScore');
      await expect.poll(() => existsSync(pdf), { timeout: 60_000 }).toBe(true);
      await expect.poll(() => readFileSync(pdf).subarray(-6).toString('latin1').includes('EOF')).toBe(true);
      const text = execFileSync(PdfText, ['-layout', pdf, '-'], { encoding: 'utf8' });
      const pages = text.split('\f').filter(page => page.trim().length > 0).length;
      expect(pages).toBe(1);
      for (const word of ['Golden Etude', 'Piano', 'Clarinet', 'Hap', 'py', 'day', 'Am7', 'Fmaj7', 'G7']) {
        expect(text).toContain(word);
      }
      measured('5 print', `${pages} page; title, Piano, Clarinet, Hap/py/day, Am7 Fmaj7 G7 as PDF text`);

      // ---- 6. MusicXML: export the set (validates), re-import into a new project, import the quartet fixture.
      const exports = join(folder, 'Exports');
      await menu(app, 'exportMusicXml');
      const dialog = window.getByTestId('interchange-dialog');
      await dialog.getByTestId('export-name').fill('Golden');
      await dialog.getByTestId('export-folder').fill(exports);
      await dialog.getByTestId('export-source').selectOption(`set:${setId}`);
      await window.getByTestId('interchange-start').click();
      await expect(dialog).toBeHidden({ timeout: 60_000 });
      const musicXml = join(exports, 'Golden.musicxml');
      expect(existsSync(musicXml)).toBe(true);
      execFileSync(Python, [MusicXmlCheck, musicXml], { stdio: 'inherit' });
      const original = await project(window);
      // What the file holds is the score: the notes where they are drawn (display quantise), at concert pitch.
      const drawn = drawnNotes(await score(window, `set=${setId}&view=page`));

      await saveAndClose(app, window);
      await newProject(window, mkdtempSync(join(folder, 'reimport-')), 'Reimport');
      await answerDialogs(app, musicXml);
      await menu(app, 'importMusicXml');
      await window.getByTestId('import-choose').click();
      await window.getByTestId('interchange-start').click();
      await expect(window.getByTestId('interchange-dialog')).toBeHidden({ timeout: 60_000 });
      const reimported = await project(window);
      expect(reimported.tracks.map(t => t.name)).toEqual(['Piano', 'Clarinet']);
      expect(reimported.tracks.map(t => t.staffStyleId)).toEqual(['factory.piano', 'factory.clarinetBb']);
      expect(pitchesOf(reimported)).toEqual(drawn);
      expect(reimported.score.symbols.map(s => s.kind).sort()).toEqual(original.score.symbols.map(s => s.kind).sort());
      expect(lyricsInOrder(reimported)).toEqual(['Hap', 'py', 'day']);
      expect(chordsInOrder(reimported)).toEqual(['C', 'Am7', 'Fmaj7', 'G7']);
      expect(reimported.score.layout.title).toBe('Golden Etude');
      const reDocument = await score(window, `part=${reimported.tracks[0]?.id ?? ''}`);
      const reTreble = eventsOf(reDocument, reimported.tracks[0]?.id ?? '', 0, 1);
      expect(reTreble.some(e => e.notes.some(n => n.tieStart))).toBe(true);
      expect(reTreble.filter(e => e.duration.tuplet !== null).map(e => e.tick)).toEqual([960, 1280, 1600]);
      measured(
        '6 MusicXML',
        `valid against the 4.0 XSD; ${drawn.length} notes back as drawn, ${reimported.score.symbols.length} symbols, ` +
          `3 syllables, 4 chord symbols, tie and triplet`
      );

      // The hand-written string quartet (key and meter changes): four tracks with their notes.
      await answerDialogs(app, QuartetFixture);
      await menu(app, 'importMusicXml');
      await window.getByTestId('import-choose').click();
      await window.getByTestId('interchange-start').click();
      await expect(window.getByTestId('interchange-dialog')).toBeHidden({ timeout: 60_000 });
      const quartet = await project(window);
      const strings = quartet.tracks.slice(-4);
      expect(strings).toHaveLength(4);
      const counts = strings.map(
        track => quartet.midiRegions.filter(r => r.trackId === track.id).flatMap(r => r.notes).length
      );
      for (const n of counts) {
        expect(n).toBeGreaterThan(0);
      }
      measured('6 quartet', `${strings.map((t, i) => `${t.name} ${counts[i]}`).join(', ')} notes`);

      // ---- 7. AAF: an arrangement of audio regions out (embedded and linked), checked by pyaaf2; a pyaaf2 fixture in.
      const aafFolder = mkdtempSync(join(folder, 'aaf-'));
      await saveAndClose(app, window);
      await newProject(window, aafFolder, 'Audio');
      const wav = join(aafFolder, 'tone.wav');
      writeWav(wav, Rate * 4);
      const file = await api<{ id: string }>(window, 'POST', '/project/files/import', { path: wav });
      await commands(window, [
        { type: 'addTrack', kind: 'audio', stereo: false, name: 'Audio A' },
        { type: 'addTrack', kind: 'audio', stereo: false, name: 'Audio B' }
      ]);
      state = await project(window);
      const [trackA, trackB] = state.tracks.filter(t => t.kind === 'audio').map(t => t.id);
      // Two overlapping regions on track A (a crossfade of 2400 samples: the first fades out over the overlap, the
      // second in) and one with an offset, gain and fades on track B.
      const Crossfade = 2400;
      await commands(window, [
        {
          type: 'addRegion',
          trackId: trackA,
          fileId: file.id,
          startTick: 0,
          fileOffset: 0,
          length: Rate * 2 + Crossfade
        },
        { type: 'addRegion', trackId: trackA, fileId: file.id, startTick: Bar, fileOffset: Rate, length: Rate * 2 },
        { type: 'addRegion', trackId: trackB, fileId: file.id, startTick: Bar / 2, fileOffset: 12_345, length: Rate }
      ]);
      state = await project(window);
      const regionOf = (track: string | undefined, tick: number) =>
        state.regions.find(r => r.trackId === track && r.startTick === tick)?.id;
      await commands(window, [
        { type: 'setRegion', regionId: regionOf(trackB, Bar / 2), gainDb: -6, fadeIn: 480, fadeOut: 4800 },
        { type: 'setRegion', regionId: regionOf(trackA, 0), fadeOut: Crossfade },
        { type: 'setRegion', regionId: regionOf(trackA, Bar), fadeIn: Crossfade }
      ]);
      state = await project(window);
      const samplesAt = (tick: number) => Math.round((tick / Ppq) * (60 / 120) * Rate);
      for (const embed of [true, false]) {
        await menu(app, 'exportAaf');
        const aafDialog = window.getByTestId('interchange-dialog');
        await aafDialog.getByTestId('export-name').fill(embed ? 'Embedded' : 'Linked');
        await aafDialog.getByTestId('export-folder').fill(aafFolder);
        if (!embed) {
          await aafDialog.getByTestId('export-media').getByRole('radio', { name: /Link/ }).click();
        }
        await window.getByTestId('interchange-start').click();
        await expect(aafDialog).toBeHidden({ timeout: 60_000 });
        const aaf = join(aafFolder, embed ? 'Embedded.aaf' : 'Linked.aaf');
        const read = JSON.parse(execFileSync(Python, [AafHelper, 'read', aaf], { encoding: 'utf8' })) as AafRead;
        expect(read.sampleRate).toBe(Rate);
        expect(read.tracks.map(t => t.name)).toEqual(['Audio A', 'Audio B']);
        const clips = read.tracks.flatMap(t => t.clips);
        expect(clips).toHaveLength(state.regions.length);
        for (const region of state.regions) {
          expect(
            clips.some(
              cl =>
                cl.position === samplesAt(region.startTick) &&
                cl.sourceOffset === region.fileOffset &&
                cl.length === region.length
            )
          ).toBe(true);
        }
        expect(read.transitions).toEqual([{ track: 'Audio A', position: samplesAt(Bar), length: Crossfade }]);
        expect(clips.find(cl => cl.sourceOffset === 12_345)).toMatchObject({ gainDb: -6, fadeIn: 480, fadeOut: 4800 });
        expect(clips.every(cl => (embed ? cl.media === 'embedded' : existsSync(cl.media)))).toBe(true);
        measured(
          `7 AAF ${embed ? 'embedded' : 'linked'}`,
          `pyaaf2: ${read.sampleRate} Hz, ${clips.length} clips at ${clips.map(cl => cl.position).join('/')} samples, ` +
            `offsets ${clips.map(cl => cl.sourceOffset).join('/')}, lengths ${clips.map(cl => cl.length).join('/')}, ` +
            `transition ${read.transitions[0]?.position}+${read.transitions[0]?.length}, gain −6 dB, fades 480/4800, ` +
            `media ${embed ? 'embedded' : 'linked files'}`
        );
      }
      // A pyaaf2 fixture in: tracks, regions and media, to the sample.
      const fixture = join(aafFolder, 'fixture.aaf');
      const made = JSON.parse(
        execFileSync(Python, [AafHelper, 'make', fixture, join(aafFolder, 'fixture-media')], { encoding: 'utf8' })
      ) as AafRead;
      await answerDialogs(app, fixture);
      await menu(app, 'importAaf');
      await window.getByTestId('import-choose').click();
      await window.getByTestId('interchange-start').click();
      await expect(window.getByTestId('interchange-dialog')).toBeHidden({ timeout: 60_000 });
      const withAaf = await project(window);
      for (const track of made.tracks) {
        const imported = withAaf.tracks.find(t => t.name === track.name);
        expect(imported).toBeDefined();
        const regions = withAaf.regions.filter(r => r.trackId === imported?.id);
        expect(regions.map(r => [samplesAt(r.startTick), r.fileOffset, r.length]).sort()).toEqual(
          track.clips.map(cl => [cl.position, cl.sourceOffset, cl.length]).sort()
        );
        for (const region of regions) {
          const clip = track.clips.find(cl => cl.position === samplesAt(region.startTick));
          const overlapIn = made.transitions.find(t => t.track === track.name && t.position === clip?.position);
          const overlapOut = made.transitions.find(
            t => t.track === track.name && clip && t.position + t.length === clip.position + clip.length
          );
          expect(region.fadeIn).toBe(overlapIn?.length ?? clip?.fadeIn);
          expect(region.fadeOut).toBe(overlapOut?.length ?? clip?.fadeOut);
          expect(region.gainDb).toBeCloseTo(clip?.gainDb ?? 0, 4);
        }
      }
      const fixtureFiles = new Set(
        withAaf.regions.filter(r => !state.regions.some(o => o.id === r.id)).map(r => r.fileId)
      );
      expect(fixtureFiles.size).toBe(2);
      measured(
        '7 AAF import',
        `pyaaf2 fixture: ${made.tracks.map(t => `${t.name} ${t.clips.length} clips`).join(', ')}; positions, offsets, ` +
          `lengths, fades and gain to the sample; ${fixtureFiles.size} media files copied`
      );

      // ---- 8. Standard MIDI Files: type 1 out (tempo map, signatures, markers, names, CCs), in again: the same.
      const midiFolder = mkdtempSync(join(folder, 'midi-'));
      await saveAndClose(app, window);
      await newProject(window, midiFolder, 'Midi');
      await commands(window, [
        {
          type: 'setTempoEvents',
          events: [
            { tick: 0, bpm: 100, curve: 'step', shape: 0 },
            { tick: 2 * Bar, bpm: 132, curve: 'step', shape: 0 }
          ]
        },
        {
          type: 'setSignatures',
          events: [
            { tick: 0, numerator: 4, denominator: 4 },
            { tick: 2 * Bar, numerator: 3, denominator: 4 }
          ]
        },
        { type: 'addMarker', name: 'Verse', tick: 0 },
        { type: 'addMarker', name: 'Chorus', tick: 2 * Bar },
        { type: 'addTrack', kind: 'instrument', stereo: true, name: 'Lead' }
      ]);
      state = await project(window);
      const lead = state.tracks.find(t => t.name === 'Lead')?.id ?? '';
      await commands(window, [
        { type: 'addMidiRegion', trackId: lead, startTick: 0, lengthTicks: 3 * Bar, name: 'Lead' }
      ]);
      state = await project(window);
      const leadRegion = state.midiRegions[0]?.id ?? '';
      await commands(window, [
        {
          type: 'addNotes',
          regionId: leadRegion,
          notes: [60, 64, 67, 72].map((pitch, i) => ({
            pitch,
            startTick: i * Ppq,
            lengthTicks: Ppq / 2,
            velocity: 100,
            channel: 1
          }))
        },
        {
          type: 'addControls',
          regionId: leadRegion,
          events: [
            { kind: 'controlChange', channel: 1, number: 1, value: 64, tick: 0 },
            { kind: 'controlChange', channel: 1, number: 7, value: 100, tick: Bar }
          ]
        }
      ]);
      const before = await project(window);
      await menu(app, 'exportMidi');
      const midiDialog = window.getByTestId('interchange-dialog');
      await midiDialog.getByTestId('export-name').fill('Song');
      await midiDialog.getByTestId('export-folder').fill(midiFolder);
      await window.getByTestId('interchange-start').click();
      await expect(midiDialog).toBeHidden({ timeout: 60_000 });
      const smf = join(midiFolder, 'Song.mid');
      const bytes = readFileSync(smf);
      expect(bytes.subarray(0, 4).toString('latin1')).toBe('MThd');
      expect(bytes.readUInt16BE(8)).toBe(1); // format 1
      expect(bytes.readUInt16BE(12)).toBe(960); // the project's resolution
      await saveAndClose(app, window);
      await newProject(window, mkdtempSync(join(folder, 'midi-in-')), 'MidiIn');
      await answerDialogs(app, smf);
      await menu(app, 'importMidiOptions');
      await window.getByTestId('import-choose').click();
      await window.getByTestId('interchange-start').click();
      await expect(window.getByTestId('interchange-dialog')).toBeHidden({ timeout: 60_000 });
      const after = await project(window);
      expect(after.settings.tempoEvents.map(t => [t.tick, t.bpm])).toEqual(
        before.settings.tempoEvents.map(t => [t.tick, t.bpm])
      );
      expect(after.settings.signatures).toEqual(before.settings.signatures);
      const markersOf = (p: ProjectShape) => [...p.markers].sort((a, b) => a.tick - b.tick).map(m => [m.name, m.tick]);
      expect(markersOf(after)).toEqual(markersOf(before));
      expect(markersOf(after)).toEqual([
        ['Verse', 0],
        ['Chorus', 2 * Bar]
      ]);
      expect(after.tracks.some(t => t.name === 'Lead')).toBe(true);
      expect(pitchesOf(after)).toEqual(pitchesOf(before));
      const controlsOf = (p: ProjectShape) =>
        p.midiRegions
          .flatMap(r => r.controls.map(cc => [r.startTick + cc.tick, cc.kind, cc.number, cc.value]))
          .sort((a, b) => Number(a[0]) - Number(b[0]));
      expect(controlsOf(after)).toEqual(controlsOf(before));
      measured(
        '8 SMF',
        `type 1 at 960 PPQ; tempos ${after.settings.tempoEvents.map(t => `${t.tick}:${t.bpm}`).join(' ')}, ` +
          `signatures 4/4 → 3/4 at ${2 * Bar}, markers Verse/Chorus, Lead's 4 notes and 2 CCs back`
      );

      // ---- 9. Save, close and reopen: everything is intact (the notation project of steps 1–6).
      await saveAndClose(app, window);
      await answerDialogs(app, notationPath);
      await menu(app, 'openProject');
      await expect(window.getByTestId('project-name')).toHaveText('Notation');
      const reopened = await project(window);
      expect(pitchesOf(reopened)).toEqual(pitchesOf(original));
      expect(reopened.score).toEqual(original.score);
      expect(reopened.tracks.find(t => t.name === 'Clarinet')?.staffStyleId).toBe('factory.clarinetBb');
      expect(reopened.midiRegions.find(r => r.trackId === piano)?.notation).toEqual(
        original.midiRegions.find(r => r.trackId === piano)?.notation
      );
      const reopenedEditor = await openScore(window);
      await expect(reopenedEditor.locator('[data-page] .syl').first()).toBeVisible();
      expect((await score(window, `set=${setId}&view=page`)).mei).toContain('Golden Etude');
      measured(
        '9 reopen',
        `${pitchesOf(reopened).length} notes, score (5 symbols, 3 syllables, 4 chords, set, layout) and styles intact`
      );
    } finally {
      rmSync(folder, { recursive: true, force: true });
    }
  });
});

interface AafRead {
  sampleRate: number;
  tracks: {
    name: string;
    clips: {
      name: string;
      position: number;
      sourceOffset: number;
      length: number;
      fadeIn: number;
      fadeOut: number;
      gainDb: number;
      media: string;
    }[];
  }[];
  transitions: { track: string; position: number; length: number }[];
}

function pitchesOf(p: ProjectShape): number[][] {
  return p.midiRegions
    .flatMap(r => r.notes.map(n => [r.startTick + n.startTick, n.lengthTicks, n.pitch]))
    .sort((a, b) => (a[0] ?? 0) - (b[0] ?? 0) || (a[2] ?? 0) - (b[2] ?? 0));
}

/** A mono 16-bit WAV of a 440 Hz tone. */
function writeWav(path: string, frames: number): void {
  const data = Buffer.alloc(frames * 2);
  for (let i = 0; i < frames; i++) {
    data.writeInt16LE(Math.round(Math.sin((2 * Math.PI * 440 * i) / Rate) * 12_000), i * 2);
  }
  const header = Buffer.alloc(44);
  header.write('RIFF', 0, 'latin1');
  header.writeUInt32LE(36 + data.length, 4);
  header.write('WAVEfmt ', 8, 'latin1');
  header.writeUInt32LE(16, 16);
  header.writeUInt16LE(1, 20);
  header.writeUInt16LE(1, 22);
  header.writeUInt32LE(Rate, 24);
  header.writeUInt32LE(Rate * 2, 28);
  header.writeUInt16LE(2, 32);
  header.writeUInt16LE(16, 34);
  header.write('data', 36, 'latin1');
  header.writeUInt32LE(data.length, 40);
  writeFileSync(path, Buffer.concat([header, data]));
}
