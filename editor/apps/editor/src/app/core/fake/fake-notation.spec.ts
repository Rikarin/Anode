import { StaffStyleIds } from '@anode/api-client';
import { VerovioEngine, type VerovioToolkitLike } from '@anode/notation';
import { VerovioToolkit } from 'verovio/esm';
import createVerovioModule from 'verovio/wasm';
import { noteElementIds } from './fake-notation';
import { FakeAnodeServer } from './fake-server';

// The fake Server's MEI engraved by the real Verovio: the editor works with `?fake` only if Verovio reads it, keeps
// every note's id and draws its text as text.
describe('the notation stand-in through Verovio', () => {
  let toolkit: VerovioToolkitLike;

  beforeAll(async () => {
    toolkit = new VerovioToolkit(await createVerovioModule());
  }, 30_000);

  it('engraves a piano and a clarinet part with symbols, lyrics and chord symbols', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Engraved' });
    await server.applyCommands({
      commands: [
        { type: 'addTrack', kind: 'instrument', stereo: false, name: 'Piano' },
        { type: 'addTrack', kind: 'instrument', stereo: false, name: 'Clarinet' }
      ]
    });
    const [piano, clarinet] = server.current?.tracks.map(t => t.id) ?? [];
    await server.applyCommands({
      commands: [
        { type: 'addMidiRegion', trackId: piano ?? '', startTick: 0, lengthTicks: 7680 },
        { type: 'addMidiRegion', trackId: clarinet ?? '', startTick: 0, lengthTicks: 7680 },
        { type: 'assignStaffStyle', trackIds: [clarinet ?? ''], styleId: StaffStyleIds.clarinetBb }
      ]
    });
    const [pianoRegion, clarinetRegion] = server.current?.midiRegions.map(r => r.id) ?? [];
    const note = (pitch: number, startTick: number, lengthTicks: number) => ({
      pitch,
      startTick,
      lengthTicks,
      velocity: 90,
      channel: 1
    });
    await server.applyCommands({
      commands: [
        {
          type: 'addNotes',
          regionId: pianoRegion ?? '',
          notes: [
            note(60, 0, 960),
            note(64, 0, 960),
            note(67, 0, 960),
            note(72, 960, 480),
            note(74, 1440, 480),
            note(76, 1920, 320),
            note(77, 2240, 320),
            note(79, 2560, 320),
            note(76, 2880, 1920),
            note(43, 0, 3840),
            note(48, 3840, 960)
          ]
        },
        { type: 'addNotes', regionId: clarinetRegion ?? '', notes: [note(70, 0, 1920), note(72, 1920, 1920)] }
      ]
    });
    const notes = server.current?.midiRegions[0]?.notes ?? [];
    const first = notes.find(n => n.pitch === 72)?.id ?? 0;
    const last = notes.find(n => n.pitch === 79)?.id ?? 0;
    await server.applyCommands({
      commands: [
        {
          type: 'addScoreSymbols',
          symbols: [
            { kind: 'accent', trackId: piano ?? '', noteId: first },
            { kind: 'dynamic', trackId: piano ?? '', tick: 0, text: 'mf' },
            { kind: 'slur', trackId: piano ?? '', noteId: first, endNoteId: last },
            { kind: 'crescendo', trackId: piano ?? '', tick: 1920, endTick: 3840 }
          ]
        },
        {
          type: 'setLyrics',
          verse: 1,
          syllables: [{ noteId: first, text: 'Sing', syllabic: 'single', extender: false }]
        },
        {
          type: 'addChordSymbols',
          chords: [
            {
              trackId: piano ?? '',
              tick: 0,
              root: { step: 'f', alter: 0 },
              quality: 'maj7',
              bass: null,
              showGrid: false
            }
          ]
        },
        { type: 'setScoreLayout', layout: { ...(server.current?.score.layout ?? ({} as never)), title: 'Etude' } }
      ]
    });

    for (const view of ['linear', 'page'] as const) {
      const document = await server.score({ view });
      const pages = new VerovioEngine(toolkit).render(document.mei, { view, layout: document.view.layout }).pages;
      const svg = pages.map(p => p.svg).join('');
      for (const elementId of noteElementIds(document.view).values()) {
        expect(svg).toContain(`id="${elementId}"`);
      }
      expect(svg).toMatch(/class="slur"/);
      expect(svg).toMatch(/class="hairpin"/);
      expect(svg).toMatch(/class="tuplet"/);
      expect(svg).toMatch(/>Sing</);
      expect(svg).toMatch(/>Fmaj7</);
      expect(svg).toMatch(/>Clarinet</);
      if (view === 'page') {
        expect(svg).toContain('Etude');
      }
    }
  });
});
