import type { ElectronApplication, Locator, Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { expect, test } from '../fixtures';

/*
 * Phase 9's exit criterion (docs/10-phase-9.md): MIDI reads and edits as a score that prints, and projects move to and
 * from other software through MusicXML, AAF and Standard MIDI Files, on the null device. Written by the editor
 * workstream (C) against the real app; `test.fixme` until the five workstreams merge (the notation engine, the server
 * domain, MusicXML, AAF). The integrator provides:
 *
 * - **pyaaf2** (MIT) as the independent AAF reader and fixture maker, behind one helper script, ANODE_AAF_HELPER
 *   (default `scripts/aaf-check.py`, run with ANODE_PYTHON, default `python3`, in the venv E's setup script makes):
 *     `aaf-check.py read <file.aaf>` prints JSON `{ sampleRate, tracks: [{ name, clips: [{ name, position,
 *     sourceOffset, length, fadeIn, fadeOut, gainDb, media }] }], transitions: [{ track, position, length }] }` with
 *     positions, offsets and lengths in samples, `media` the essence's path or "embedded";
 *     `aaf-check.py make <out.aaf> <media folder>` writes the import fixture (two tracks, three clips, a crossfade),
 *     and prints the same JSON for what it wrote.
 * - **The MusicXML 4.0 schema** check, ANODE_MUSICXML_CHECK (default `scripts/musicxml-validate.py <file>`, exit 0 when
 *   the file validates against D's checked-in XSD), and D's string quartet fixture, ANODE_QUARTET_FIXTURE (default
 *   `engine/tests/Anode.Interchange.Tests/Fixtures/StringQuartet.musicxml`).
 * - **A PDF text reader**, ANODE_PDF_TEXT (default `pdftotext`, poppler), to read the printed score's text.
 */

const Root = resolve(__dirname, '../../../../..');
const Python = process.env['ANODE_PYTHON'] ?? 'python3';
const AafHelper = process.env['ANODE_AAF_HELPER'] ?? join(Root, 'scripts/aaf-check.py');
const MusicXmlCheck = process.env['ANODE_MUSICXML_CHECK'] ?? join(Root, 'scripts/musicxml-validate.py');
const QuartetFixture =
  process.env['ANODE_QUARTET_FIXTURE'] ??
  join(Root, 'engine/tests/Anode.Interchange.Tests/Fixtures/StringQuartet.musicxml');
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
  tracks: { id: string; name: string; kind: string; staffStyleId?: string | null }[];
  midiRegions: {
    id: string;
    trackId: string;
    startTick: number;
    notes: Note[];
    notation: { displayQuantizeTicks: number };
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
  score: {
    symbols: {
      kind: string;
      noteId?: number | null;
      tick?: number | null;
      endNoteId?: number | null;
      text?: string | null;
    }[];
    lyrics: { noteId: number; text: string; syllabic: string }[];
    chords: { tick: number; root: { step: string; alter: number }; quality: string }[];
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

/** The piano passage of step 1: chords, a tie over the bar line, a triplet, two voices, notes slightly off the grid. */
const Passage = [
  // Bar 1: a C major chord, a tie from beat 4 into bar 2 (G4).
  { pitch: 60, startTick: 0, lengthTicks: 960 },
  { pitch: 64, startTick: 0, lengthTicks: 960 },
  { pitch: 67, startTick: 0, lengthTicks: 960 },
  { pitch: 72, startTick: 960, lengthTicks: 480 },
  { pitch: 74, startTick: 1440 + 12, lengthTicks: 470 }, // slightly late
  { pitch: 76, startTick: 1920 - 9, lengthTicks: 960 }, // slightly early
  { pitch: 67, startTick: 2880, lengthTicks: 1920 }, // tied across the bar line
  // Bar 2: a triplet of eighths on beat 3, then two voices: a held E5 over moving notes.
  { pitch: 72, startTick: Bar + 1920, lengthTicks: 320 },
  { pitch: 74, startTick: Bar + 2240, lengthTicks: 320 },
  { pitch: 76, startTick: Bar + 2560, lengthTicks: 320 },
  { pitch: 79, startTick: 2 * Bar, lengthTicks: 1920 },
  { pitch: 72, startTick: 2 * Bar, lengthTicks: 960 },
  { pitch: 74, startTick: 2 * Bar + 960, lengthTicks: 960 },
  // Bass staff (below C3 = 60): a whole note per bar.
  { pitch: 48, startTick: 0, lengthTicks: Bar },
  { pitch: 43, startTick: Bar, lengthTicks: Bar },
  { pitch: 36, startTick: 2 * Bar, lengthTicks: Bar }
];

test.describe('Phase 9: notation and interchange', () => {
  test.setTimeout(600_000);

  test.fixme('reads, edits and prints MIDI as a score, and moves projects through MusicXML, AAF and MIDI files', async ({
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
      const bass = eventsOf(document, piano, 1, 1);
      // The chord, the eighths on the 1/16 grid (the late and early ones drawn on it), the tie, the triplet, the voices.
      expect(treble[0]).toMatchObject({ kind: 'chord', tick: 0, duration: { value: 'quarter' } });
      expect(treble.find(e => e.notes.some(n => n.noteId === id(74, 1452)))?.tick).toBe(1440);
      expect(treble.find(e => e.notes.some(n => n.noteId === id(76, 1911)))?.tick).toBe(1920);
      const tie = treble.filter(e => e.notes.some(n => n.noteId === id(67, 2880)));
      expect(tie.map(e => [e.tick, e.notes[0]?.tieStart, e.notes[0]?.tieStop])).toEqual([
        [2880, true, false],
        [Bar, false, true]
      ]);
      const triplet = treble.filter(e => e.duration.tuplet !== null);
      expect(triplet.map(e => e.duration.value)).toEqual(['eighth', 'eighth', 'eighth']);
      expect(new Set(triplet.map(e => e.duration.tuplet?.group)).size).toBe(1);
      expect(treble.filter(e => e.beam != null).length).toBeGreaterThanOrEqual(2);
      expect(eventsOf(document, piano, 0, 2).some(e => e.notes.some(n => n.pitch === 72 || n.pitch === 74))).toBe(true);
      expect(bass.filter(e => e.kind !== 'rest').map(e => e.notes[0]?.pitch)).toEqual([48, 43, 36]);
      // Every note id has an element in the rendered SVG.
      const elements = await editor.locator('[data-page] g.note[id]').evaluateAll(list => list.map(e => e.id));
      for (const note of notes) {
        expect(elements).toContain(`n${note.id}`);
      }

      // Display quantise 1/8 changes the score and leaves the MIDI unchanged.
      const midiBefore = JSON.stringify(notes);
      await window.getByTestId('region-display-quantize').selectOption('480');
      await expect.poll(async () => (await score(window, `part=${piano}`)).mei).not.toBe(document.mei);
      state = await project(window);
      expect(JSON.stringify(state.midiRegions[0]?.notes)).toBe(midiBefore);
      await window.getByTestId('region-display-quantize').selectOption('240');

      // ---- 2. Editing in the score: each one undo step, and the Piano Roll shows the same notes.
      const steps = await undoCount(window);
      // Drag a note up one step: E5 → F5 in C major.
      const e5 = id(76, 1911);
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
        await window.mouse.move(box.x + box.width / 2, box.y + box.height / 2 - half * 1.2, { steps: 4 });
        await window.mouse.up();
      }
      await expect
        .poll(async () => (await project(window)).midiRegions[0]?.notes.find(n => n.id === e5)?.pitch)
        .toBe(77);
      expect(await undoCount(window)).toBe(steps + 1);

      // Insert a quarter note from the Part box at beat 3 of bar 2 (on the treble's middle line, B4).
      await editor.getByTestId('part-note-quarter').click();
      const m2 = editor.locator('[data-page] g.measure[id="m2"] g.staff').first();
      const middle = await m2.locator('> path').nth(2).boundingBox();
      const count = (await project(window)).midiRegions[0]?.notes.length ?? 0;
      if (middle) {
        await window.mouse.click(middle.x + middle.width * 0.55, middle.y);
      }
      await expect.poll(async () => (await project(window)).midiRegions[0]?.notes.length).toBe(count + 1);
      const inserted = (await project(window)).midiRegions[0]?.notes.find(
        n => n.startTick === Bar + 1920 && n.pitch === 71
      );
      expect(inserted?.lengthTicks).toBe(960);
      expect(await undoCount(window)).toBe(steps + 2);
      await editor.getByTestId('part-note-quarter').click(); // back to the pointer

      // Delete a note.
      const bassNote = id(36, 2 * Bar);
      await clickNote(editor, bassNote);
      await editor.getByTestId('score-scroller').press('Delete');
      await expect
        .poll(async () => (await project(window)).midiRegions[0]?.notes.some(n => n.id === bassNote))
        .toBe(false);
      expect(await undoCount(window)).toBe(steps + 3);

      // The Piano Roll shows the same notes.
      await window.getByTestId('editor-view').getByRole('tab', { name: 'Piano Roll', exact: true }).click();
      const rollNotes = await window.getByRole('img', { name: /^Piano Roll:/ }).getAttribute('aria-label');
      expect(rollNotes).toContain(`${(await project(window)).midiRegions[0]?.notes.length} notes`);
      await window.getByTestId('editor-view').getByRole('tab', { name: 'Score', exact: true }).click();

      // ---- 3. Symbols and text: render at their notes or positions, and survive save and reopen.
      const [c, d, eNote] = [id(72, 960), id(74, 1452), id(77, 1911)];
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
      // Chord symbols at beats 1 of bars 1–3 and beat 3 of bar 1: C, Am7, Fmaj7, G7.
      await editor.getByTestId('part-chord').click();
      for (const [measure, x, text] of [
        ['m1', 0.08, 'C'],
        ['m1', 0.55, 'Am7'],
        ['m2', 0.08, 'Fmaj7'],
        ['m3', 0.08, 'G7']
      ] as const) {
        const top = await editor
          .locator(`[data-page] g.measure[id="${measure}"] g.staff`)
          .first()
          .locator('> path')
          .first()
          .boundingBox();
        if (top) {
          await window.mouse.click(top.x + top.width * x, top.y + 2);
        }
        const field = editor.getByTestId('score-entry-chord');
        await field.fill(text);
        await field.press('Enter');
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
      expect(state.score.lyrics.map(l => [l.text, l.syllabic])).toEqual([
        ['Hap', 'begin'],
        ['py', 'end'],
        ['day', 'single']
      ]);
      expect(state.score.chords.map(ch => `${ch.root.step.toUpperCase()}${ch.quality}`)).toEqual([
        'C',
        'Am7',
        'Fmaj7',
        'G7'
      ]);
      const svg = (await editor.locator('[data-page]').first().innerHTML()).replace(/\s+/g, ' ');
      for (const text of ['Hap', 'py', 'day', 'Am7', 'Fmaj7', 'G7']) {
        expect(svg).toContain(`>${text}<`);
      }
      expect(svg).toMatch(/class="artic"/);
      expect(svg).toMatch(/class="slur"/);
      expect(svg).toMatch(/class="hairpin"/);
      expect(svg).toMatch(/class="dynam"/);

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
      await window.getByTestId('track-staff-style').selectOption('factory.clarinetBb');
      await expect
        .poll(async () => (await project(window)).tracks.find(t => t.id === clarinet)?.staffStyleId)
        .toBe('factory.clarinetBb');
      const part = await score(window, `part=${clarinet}`);
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
      const text = execFileSync(PdfText, ['-layout', pdf, '-'], { encoding: 'utf8' });
      const pages = (text.match(/\f/g) ?? []).length || 1;
      expect(pages).toBe(1);
      for (const word of ['Golden Etude', 'Piano', 'Clarinet', 'Hap', 'day', 'Am7', 'Fmaj7', 'G7']) {
        expect(text).toContain(word);
      }

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

      await window.getByTestId('project-menu').click();
      await window.getByRole('menuitem', { name: 'Save as Alternative…' }).waitFor();
      await window.keyboard.press('Escape');
      await shortcut(window, 'ControlOrMeta+s');
      await menu(app, 'closeProject');
      await newProject(window, mkdtempSync(join(folder, 'reimport-')), 'Reimport');
      await answerDialogs(app, musicXml);
      await menu(app, 'importMusicXml');
      await window.getByTestId('import-choose').click();
      await window.getByTestId('interchange-start').click();
      await expect(window.getByTestId('interchange-dialog')).toBeHidden({ timeout: 60_000 });
      const reimported = await project(window);
      expect(reimported.tracks.map(t => t.name)).toEqual(['Piano', 'Clarinet']);
      const pitches = (p: ProjectShape) =>
        p.midiRegions.flatMap(r => r.notes.map(n => [r.startTick + n.startTick, n.lengthTicks, n.pitch])).sort();
      expect(pitches(reimported)).toEqual(pitches(original));
      expect(reimported.score.symbols.map(s => s.kind).sort()).toEqual(original.score.symbols.map(s => s.kind).sort());
      expect(reimported.score.lyrics.map(l => l.text)).toEqual(['Hap', 'py', 'day']);
      expect(reimported.score.chords.map(ch => ch.quality)).toEqual(['', 'm7', 'maj7', '7']);
      const reDocument = await score(window, `part=${reimported.tracks[0]?.id ?? ''}`);
      const reTreble = eventsOf(reDocument, reimported.tracks[0]?.id ?? '', 0, 1);
      expect(reTreble.some(e => e.notes.some(n => n.tieStart))).toBe(true);
      expect(reTreble.some(e => e.duration.tuplet !== null)).toBe(true);

      // The hand-written string quartet (key and meter changes): four tracks with their notes.
      await answerDialogs(app, QuartetFixture);
      await menu(app, 'importMusicXml');
      await window.getByTestId('import-choose').click();
      await window.getByTestId('interchange-start').click();
      await expect(window.getByTestId('interchange-dialog')).toBeHidden({ timeout: 60_000 });
      const quartet = await project(window);
      const strings = quartet.tracks.slice(-4);
      expect(strings).toHaveLength(4);
      for (const track of strings) {
        expect(quartet.midiRegions.filter(r => r.trackId === track.id).flatMap(r => r.notes).length).toBeGreaterThan(0);
      }

      // ---- 7. AAF: an arrangement of audio regions out (embedded and linked), checked by pyaaf2; a pyaaf2 fixture in.
      const aafFolder = mkdtempSync(join(folder, 'aaf-'));
      await menu(app, 'closeProject');
      await newProject(window, aafFolder, 'Audio');
      const wav = join(aafFolder, 'tone.wav');
      writeWav(wav, Rate * 4);
      await api(window, 'POST', '/project/files/import', { path: wav });
      state = await project(window);
      await commands(window, [
        { type: 'addTrack', kind: 'audio', stereo: false, name: 'Audio A' },
        { type: 'addTrack', kind: 'audio', stereo: false, name: 'Audio B' }
      ]);
      state = await project(window);
      const [trackA, trackB] = state.tracks.filter(t => t.kind === 'audio').map(t => t.id);
      const fileId = (await api<{ audioFiles: { id: string }[] }>(window, 'GET', '/project')).audioFiles[0]?.id ?? '';
      // Two overlapping clips on track A (a crossfade) and one with an offset, gain and fades on track B.
      await commands(window, [
        { type: 'addRegion', trackId: trackA, fileId, startTick: 0, fileOffset: 0, length: Rate * 2 },
        { type: 'addRegion', trackId: trackA, fileId, startTick: Bar, fileOffset: Rate, length: Rate * 2 },
        { type: 'addRegion', trackId: trackB, fileId, startTick: Bar / 2, fileOffset: 12_345, length: Rate }
      ]);
      state = await project(window);
      const clipB = state.regions.find(r => r.trackId === trackB);
      await commands(window, [
        { type: 'setRegion', regionId: clipB?.id, gainDb: -6, fadeIn: 480, fadeOut: 4800 },
        { type: 'setCrossfade', trackId: trackA, length: 2400 }
      ]);
      state = await project(window);
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
        const file = join(aafFolder, embed ? 'Embedded.aaf' : 'Linked.aaf');
        const read = JSON.parse(execFileSync(Python, [AafHelper, 'read', file], { encoding: 'utf8' })) as AafRead;
        expect(read.sampleRate).toBe(Rate);
        const clips = read.tracks.flatMap(t => t.clips);
        for (const region of state.regions) {
          const startSample = Math.round((region.startTick / Ppq) * (60 / 120) * Rate);
          expect(
            clips.some(
              cl => cl.position === startSample && cl.sourceOffset === region.fileOffset && cl.length === region.length
            )
          ).toBe(true);
        }
        expect(read.transitions.length).toBe(1);
        expect(clips.find(cl => cl.sourceOffset === 12_345)).toMatchObject({ gainDb: -6, fadeIn: 480, fadeOut: 4800 });
        expect(clips.every(cl => (embed ? cl.media === 'embedded' : existsSync(cl.media)))).toBe(true);
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
        expect(regions.map(r => [r.fileOffset, r.length]).sort()).toEqual(
          track.clips.map(cl => [cl.sourceOffset, cl.length]).sort()
        );
      }

      // ---- 8. Standard MIDI Files: type 1 out (tempo map, signatures, markers, names, CCs), in again: the same.
      const midiFolder = mkdtempSync(join(folder, 'midi-'));
      await menu(app, 'closeProject');
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
          type: 'setControls',
          regionId: leadRegion,
          add: [{ kind: 'controlChange', number: 1, tick: 0, value: 64, channel: 1 }]
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
      expect(readFileSync(smf).subarray(0, 4).toString('latin1')).toBe('MThd');
      await menu(app, 'closeProject');
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
      expect(after.markers.map(m => [m.name, m.tick])).toEqual(before.markers.map(m => [m.name, m.tick]));
      expect(after.tracks.some(t => t.name === 'Lead')).toBe(true);
      expect(pitchesOf(after)).toEqual(pitchesOf(before));

      // ---- 9. Save, close and reopen: everything is intact (the notation project of steps 1–6).
      await shortcut(window, 'ControlOrMeta+s');
      await menu(app, 'closeProject');
      await answerDialogs(app, folder);
      await menu(app, 'openProject');
      await expect(window.getByTestId('project-name')).toHaveText('Notation');
      const reopened = await project(window);
      expect(pitchesOf(reopened)).toEqual(pitchesOf(original));
      expect(reopened.score).toEqual(original.score);
      expect(reopened.tracks.find(t => t.name === 'Clarinet')?.staffStyleId).toBe('factory.clarinetBb');
      const reopenedEditor = await openScore(window);
      await expect(reopenedEditor.locator('[data-page] .syl').first()).toBeVisible();
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
