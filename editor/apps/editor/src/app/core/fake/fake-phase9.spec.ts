import {
  ApiError,
  DefaultRegionNotation,
  type EditCommand,
  EmptyProjectScore,
  type ScoreEvent,
  type ScoreView,
  StaffStyleIds
} from '@anode/api-client';
import { noteIdOf } from '@anode/notation';
import { durationsOf, noteElementIds } from './fake-notation';
import { FakeAnodeServer } from './fake-server';

async function status(run: () => Promise<unknown>): Promise<number> {
  try {
    await run();
    return 200;
  } catch (error) {
    return error instanceof ApiError ? error.status : 500;
  }
}

const Bar = 3840;

/** A project with a piano track and one 2-bar region of the given notes (pitch, start, length). */
async function setUp(notes: [number, number, number][] = []) {
  const server = new FakeAnodeServer();
  await server.createProject({ name: 'Score' });
  await server.applyCommands({ commands: [{ type: 'addTrack', kind: 'instrument', stereo: false, name: 'Piano' }] });
  const trackId = server.current?.tracks[0]?.id ?? '';
  await server.applyCommands({ commands: [{ type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: 2 * Bar }] });
  const regionId = server.current?.midiRegions[0]?.id ?? '';
  if (notes.length) {
    await server.applyCommands({
      commands: [
        {
          type: 'addNotes',
          regionId,
          notes: notes.map(([pitch, startTick, lengthTicks]) => ({
            pitch,
            startTick,
            lengthTicks,
            velocity: 90,
            channel: 1
          }))
        }
      ]
    });
  }
  const noteIds = server.current?.midiRegions[0]?.notes.map(n => n.id) ?? [];
  return { server, trackId, regionId, noteIds };
}

function events(view: ScoreView, staff = 0, voice = 1): ScoreEvent[] {
  return (
    view.parts[0]?.measures.flatMap(m => m.staves[staff]?.voices.find(v => v.number === voice)?.events ?? []) ?? []
  );
}

describe('FakeAnodeServer, Phase 9 (docs/10-phase-9.md)', () => {
  it('starts projects with an empty score and regions with the default notation', async () => {
    const { server } = await setUp();
    expect(server.current?.score).toEqual(EmptyProjectScore);
    expect(server.current?.midiRegions[0]?.notation).toEqual(DefaultRegionNotation);
    expect(server.current?.tracks[0]?.staffStyleId ?? null).toBeNull();
  });

  it('lists the factory staff styles in the library order', async () => {
    const server = new FakeAnodeServer();
    const styles = await server.staffStyles();
    expect(styles.map(s => s.id)).toEqual(
      Object.values(StaffStyleIds).filter((id, i, all) => id !== 'factory.' && all.indexOf(id) === i)
    );
    expect(styles.find(s => s.id === StaffStyleIds.piano)).toMatchObject({
      brace: true,
      staves: [{ splitPitch: 60 }, {}]
    });
    expect(styles.find(s => s.id === StaffStyleIds.clarinetBb)?.staves[0]?.transpose).toBe(2);
  });

  it('renders the score of a region: staves by split point, chords, ties across bar lines, rests, beams and ids', async () => {
    // C major chord (bar 1 beat 1), a tie from beat 4 of bar 1 into bar 2, eighths, a bass note.
    const { server, trackId } = await setUp([
      [60, 0, 960],
      [64, 0, 960],
      [67, 0, 960],
      [72, 960, 480],
      [74, 1440, 480],
      [76, 2880, 1920],
      [43, 0, 1920]
    ]);
    const { view, mei } = await server.score({ part: trackId });
    expect(view.parts[0]?.staves.map(s => s.clef)).toEqual(['treble', 'bass']);
    const treble = events(view);
    expect(treble[0]).toMatchObject({ kind: 'chord', tick: 0, duration: { value: 'quarter' } });
    expect(treble[0]?.notes.map(n => n.pitch)).toEqual([60, 64, 67]);
    expect(treble[1]?.beam).not.toBeNull();
    expect(treble[1]?.beam).toBe(treble[2]?.beam);
    const tiedId = server.current?.midiRegions[0]?.notes.find(n => n.pitch === 76)?.id;
    const tied = treble.filter(e => e.notes.some(n => n.pitch === 76));
    expect(tied.map(e => [e.tick, e.notes[0]?.tieStart, e.notes[0]?.tieStop, e.id])).toEqual([
      [2880, true, false, `n${tiedId}`],
      [3840, false, true, `n${tiedId}-2`]
    ]);
    expect(events(view, 1).find(e => e.kind !== 'rest')?.notes[0]).toMatchObject({ pitch: 43, staffPosition: 0 });
    expect(treble.some(e => e.kind === 'rest' && e.id.startsWith(`r${trackId}-`))).toBe(true);
    // Every note has its element in the MEI.
    for (const [noteId, elementId] of noteElementIds(view)) {
      expect(noteIdOf(elementId)).toBe(noteId);
      expect(mei).toContain(`xml:id="${elementId}"`);
    }
    expect(mei).toContain('meiversion="5.1"');
    expect(mei).toContain('symbol="brace"');
  });

  it('draws notes by the display quantise, never moving them, and writes triplets on the triplet grid', async () => {
    const { server, trackId, regionId } = await setUp([
      [72, 10, 470],
      [74, 495, 470],
      [72, 1920, 320],
      [74, 2240, 320],
      [76, 2560, 320]
    ]);
    let view = (await server.score({ part: trackId })).view;
    expect(
      events(view)
        .filter(e => e.kind !== 'rest')
        .map(e => e.tick)
    ).toEqual([0, 480, 1920, 2240, 2560]);
    const triplets = events(view).filter(e => e.duration.tuplet);
    expect(triplets.map(e => e.duration.value)).toEqual(['eighth', 'eighth', 'eighth']);
    expect(new Set(triplets.map(e => e.duration.tuplet?.group)).size).toBe(1);
    await server.applyCommands({
      commands: [
        { type: 'setRegionNotation', regionIds: [regionId], displayQuantizeTicks: 960, tripletQuantizeTicks: 0 }
      ]
    });
    view = (await server.score({ part: trackId })).view;
    expect(
      events(view)
        .filter(e => e.kind !== 'rest')
        .map(e => e.tick)
    ).toEqual([0, 960, 1920, 2880]);
    expect(server.current?.midiRegions[0]?.notes.map(n => n.startTick)).toEqual([10, 495, 1920, 2240, 2560]);
  });

  it('splits voices by overlap and transposes a clarinet part, concert in the full score', async () => {
    const { server, trackId } = await setUp([
      [72, 0, 1920],
      [76, 0, 960],
      [77, 960, 960]
    ]);
    await server.applyCommands({
      commands: [{ type: 'assignStaffStyle', trackIds: [trackId], styleId: StaffStyleIds.treble }]
    });
    let view = (await server.score({ part: trackId })).view;
    expect(
      events(view, 0, 1)
        .filter(e => e.kind !== 'rest')
        .map(e => e.notes.map(n => n.pitch))
    ).toEqual([[76], [77]]);
    expect(
      events(view, 0, 2)
        .filter(e => e.kind !== 'rest')
        .map(e => e.notes.map(n => n.pitch))
    ).toEqual([[72]]);
    await server.applyCommands({
      commands: [{ type: 'assignStaffStyle', trackIds: [trackId], styleId: StaffStyleIds.clarinetBb }]
    });
    view = (await server.score({ part: trackId })).view;
    expect(view.transposing).toBe(true);
    // Concert F5 (77) is written G5 for a B♭ clarinet.
    const g5 = events(view)
      .flatMap(e => e.notes)
      .find(n => n.pitch === 77);
    expect(g5?.written).toEqual({ step: 'g', alter: 0, octave: 5 });
    const full = (await server.score({})).view;
    expect(full.transposing).toBe(false);
    expect(
      events(full)
        .flatMap(e => e.notes)
        .find(n => n.pitch === 77)?.written
    ).toEqual({ step: 'f', alter: 0, octave: 5 });
  });

  it('moves notes by key steps and inserts notes by staff position, each one undo step', async () => {
    const { server, trackId, regionId, noteIds } = await setUp([[64, 0, 960]]);
    // E → F (a step of C major), then with ⌥ (a sharp) F♯.
    await server.applyCommands({ commands: [{ type: 'moveScoreNotes', regionId, noteIds, steps: 1, ticks: 0 }] });
    expect(server.current?.midiRegions[0]?.notes[0]?.pitch).toBe(65);
    await server.applyCommands({
      commands: [{ type: 'moveScoreNotes', regionId, noteIds, steps: 0, ticks: 480, accidental: 'sharp' }]
    });
    expect(server.current?.midiRegions[0]?.notes[0]).toMatchObject({ pitch: 66, startTick: 480 });
    await server.undo();
    expect(server.current?.midiRegions[0]?.notes[0]).toMatchObject({ pitch: 65, startTick: 0 });
    // A quarter at beat 3 of bar 2 on the treble's middle line (B4).
    await server.applyCommands({
      commands: [{ type: 'addScoreNote', trackId, tick: Bar + 1920, lengthTicks: 960, staffPosition: 4, staff: 0 }]
    });
    expect(server.current?.midiRegions[0]?.notes.at(-1)).toMatchObject({
      pitch: 71,
      startTick: Bar + 1920,
      velocity: 80
    });
    // On the bass staff, beyond the region: a new region of whole bars.
    await server.applyCommands({
      commands: [{ type: 'addScoreNote', trackId, tick: 5 * Bar + 960, lengthTicks: 960, staffPosition: 0, staff: 1 }]
    });
    const added = server.current?.midiRegions.at(-1);
    expect(added).toMatchObject({ startTick: 5 * Bar, lengthTicks: Bar });
    expect(added?.notes[0]).toMatchObject({ pitch: 43, startTick: 960 });
  });

  it('keeps symbols, lyrics and chord symbols, refusing bad anchors, and drops note-attached ones with their notes', async () => {
    const { server, trackId, regionId, noteIds } = await setUp([
      [60, 0, 960],
      [62, 960, 960],
      [64, 1920, 960]
    ]);
    const [a, b, c] = noteIds as [number, number, number];
    await server.applyCommands({
      commands: [
        {
          type: 'addScoreSymbols',
          symbols: [
            { kind: 'staccato', trackId, noteId: a },
            { kind: 'dynamic', trackId, tick: 0, text: 'mf' },
            { kind: 'slur', trackId, noteId: a, endNoteId: c },
            { kind: 'crescendo', trackId, tick: 0, endTick: 1920 }
          ]
        },
        {
          type: 'setLyrics',
          verse: 1,
          syllables: [
            { noteId: a, text: 'Hel', syllabic: 'begin', extender: false },
            { noteId: b, text: 'lo', syllabic: 'end', extender: false }
          ]
        },
        {
          type: 'addChordSymbols',
          chords: [{ trackId, tick: 0, root: { step: 'a', alter: 0 }, quality: 'm7', bass: null, showGrid: false }]
        }
      ]
    });
    const score = server.current?.score;
    expect(score?.symbols.map(s => s.id.startsWith('sym_'))).toEqual([true, true, true, true]);
    expect(score?.lyrics.map(l => l.text)).toEqual(['Hel', 'lo']);
    const { view, mei } = await server.score({ part: trackId, view: 'page' });
    expect(events(view).find(e => e.notes.some(n => n.noteId === a))?.symbolIds).toHaveLength(2);
    expect(mei).toMatch(/<artic xml:id="ssym_\d+" artic="stacc"\/>/);
    expect(mei).toMatch(/<dynam xml:id="ssym_\d+" staff="1" tstamp="1">mf<\/dynam>/);
    expect(mei).toMatch(/<slur xml:id="ssym_\d+" staff="1" startid="#n\d+" endid="#n\d+"\/>/);
    expect(mei).toMatch(/<hairpin [^>]*form="cres"/);
    expect(mei).toMatch(/<syl xml:id="llyr_\d+" wordpos="i" con="d">Hel<\/syl>/);
    expect(mei).toMatch(/<harm xml:id="hchd_\d+" staff="1" tstamp="1">Am7<\/harm>/);
    expect(mei).toContain('<pgHead>');

    // A lyric's empty text removes it; the syllable of a note keeps its id.
    const id = score?.lyrics[0]?.id;
    await server.applyCommands({
      commands: [
        { type: 'setLyrics', verse: 1, syllables: [{ noteId: a, text: 'Hal', syllabic: 'begin', extender: false }] }
      ]
    });
    expect(server.current?.score.lyrics[0]).toMatchObject({ id, text: 'Hal' });

    const refused: EditCommand[] = [
      { type: 'addScoreSymbols', symbols: [{ kind: 'staccato', trackId, tick: 0 }] },
      { type: 'addScoreSymbols', symbols: [{ kind: 'dynamic', trackId, tick: 0, text: 'loud' }] },
      { type: 'addScoreSymbols', symbols: [{ kind: 'slur', trackId, noteId: a, endTick: 960 }] },
      { type: 'addScoreSymbols', symbols: [{ kind: 'text', trackId, tick: 0, text: 'x'.repeat(201) }] },
      { type: 'setLyrics', verse: 1, syllables: [{ noteId: 999_999, text: 'x', syllabic: 'single', extender: false }] },
      {
        type: 'setStaffStyle',
        style: {
          id: 'factory.mine',
          name: 'Mine',
          staves: [{ clef: 'treble', transpose: 0, voices: 1 }],
          displayQuantizeTicks: 240,
          tripletQuantizeTicks: 0,
          brace: false
        }
      }
    ];
    for (const command of refused) {
      expect(await status(() => server.applyCommands({ commands: [command] }))).toBe(422);
    }

    // Deleting a note takes its symbols and syllables with it; undo brings them back.
    await server.applyCommands({ commands: [{ type: 'deleteNotes', regionId, noteIds: [a] }] });
    expect(server.current?.score.symbols.map(s => s.kind).sort()).toEqual(['crescendo', 'dynamic']);
    expect(server.current?.score.lyrics.map(l => l.text)).toEqual(['lo']);
    await server.undo();
    expect(server.current?.score.symbols).toHaveLength(4);
  });

  it('keeps staff styles, score sets and the layout, with their deltas', async () => {
    const { server, trackId } = await setUp([[60, 0, 960]]);
    const deltas: string[] = [];
    server.subscribe(message => {
      if (message.type === 'projectChanged') {
        deltas.push(...message.delta.ops.map(o => o.op));
      }
    });
    await server.applyCommands({
      commands: [
        {
          type: 'setStaffStyle',
          style: {
            id: 'sts_lead',
            name: 'Lead',
            staves: [{ clef: 'treble8vb', transpose: 0, voices: 1 }],
            displayQuantizeTicks: 480,
            tripletQuantizeTicks: 0,
            brace: false
          }
        },
        { type: 'assignStaffStyle', trackIds: [trackId], styleId: 'sts_lead' },
        { type: 'setScoreSet', set: { id: 'scs_1', name: 'Full', trackIds: [trackId] } },
        { type: 'setScoreLayout', layout: { ...EmptyProjectScore.layout, title: 'Song', composer: 'Me' } }
      ]
    });
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(deltas).toEqual(
      expect.arrayContaining(['staffStyleUpserted', 'trackUpserted', 'scoreSetUpserted', 'scoreLayoutChanged'])
    );
    const { view } = await server.score({ set: 'scs_1' });
    expect(view.parts[0]?.staves[0]?.clef).toBe('treble8vb');
    expect(view.layout.title).toBe('Song');
    await server.applyCommands({ commands: [{ type: 'deleteStaffStyle', styleId: 'sts_lead' }] });
    expect(server.current?.tracks[0]?.staffStyleId ?? null).toBeNull();
    expect(await status(() => server.score({ set: 'scs_missing' }))).toBe(404);
    expect(
      await status(() =>
        server.applyCommands({
          commands: [{ type: 'setScoreLayout', layout: { ...EmptyProjectScore.layout, staffHeightMm: 40 } }]
        })
      )
    ).toBe(422);
  });

  it('exports MusicXML, AAF and MIDI files as jobs, as the bounce does', async () => {
    const { server } = await setUp([[60, 0, 960]]);
    const run = async (start: () => Promise<{ id: string }>) => {
      const job = await start();
      server.runJobs();
      return server.job(job.id);
    };
    const folder = server.current?.path.replace(/\/+$/, '');
    expect((await run(() => server.exportMusicXml({ fileName: 'Song', compressed: false }))).files).toEqual([
      expect.stringMatching(/\/Exports\/Song\.musicxml$/)
    ]);
    expect(
      (await run(() => server.exportMusicXml({ fileName: 'Song.mxl', compressed: true, directory: '/tmp/out' }))).files
    ).toEqual(['/tmp/out/Song.mxl']);
    expect((await run(() => server.exportMidi({ fileName: 'Song', format: 1 }))).files[0]).toMatch(/Song\.mid$/);
    expect((await run(() => server.exportAaf({ fileName: 'Song', embedMedia: true }))).files[0]).toMatch(/Song\.aaf$/);
    expect(folder).toBeTruthy();
    expect(await status(() => server.exportMidi({ fileName: 'Song', format: 2 }))).toBe(422);
    expect(await status(() => server.exportMusicXml({ fileName: '', compressed: false }))).toBe(422);
  });

  it('imports MusicXML, AAF and MIDI files as jobs, each one undo step with its score content', async () => {
    const { server } = await setUp();
    const run = async (start: () => Promise<{ id: string }>) => {
      const job = await start();
      server.runJobs();
      return server.job(job.id);
    };
    const tracksBefore = server.current?.tracks.length ?? 0;
    expect((await run(() => server.importMusicXml({ path: '/Users/me/String Quartet.musicxml' }))).state).toBe('done');
    const quartet = server.current?.tracks.slice(tracksBefore) ?? [];
    expect(quartet.map(t => [t.name, t.staffStyleId])).toEqual([
      ['Violin I', 'factory.treble'],
      ['Violin II', 'factory.treble'],
      ['Viola', 'factory.viola'],
      ['Cello', 'factory.cello']
    ]);
    expect(server.current?.settings.signatures.map(s => s.numerator)).toEqual([4, 3]);
    expect(server.current?.score.layout.title).toBe('String Quartet');
    await server.undo();
    expect(server.current?.tracks).toHaveLength(tracksBefore);

    await run(() => server.uploadMusicXml(new File(['<score-partwise/>'], 'Song.musicxml')));
    const score = server.current?.score;
    expect(score?.symbols.map(s => s.kind)).toEqual(['dynamic', 'slur', 'staccato']);
    expect(score?.lyrics.map(l => l.text)).toEqual(['Hap', 'py', 'day']);
    const notes = new Set(server.current?.midiRegions.flatMap(r => r.notes.map(n => n.id)));
    expect(score?.lyrics.every(l => notes.has(l.noteId))).toBe(true);

    await run(() => server.importAaf({ path: '/Users/me/Session.aaf' }));
    const audio = server.current?.tracks.filter(t => t.kind === 'audio') ?? [];
    expect(audio.map(t => t.name)).toEqual(['Drums', 'Bass']);
    expect(server.current?.regions.filter(r => audio.some(t => t.id === r.trackId))).toHaveLength(3);

    const before = server.current?.tracks.length ?? 0;
    await run(() => server.uploadMidi(new File(['MThd'], 'Groove.mid'), { merge: true }));
    expect(server.current?.tracks.length).toBe(before + 1);
    expect(
      await status(() => server.importMidi({ path: '/x.wav', importTempo: true, importMarkers: true, merge: false }))
    ).toBe(422);
    expect(await status(() => server.importAaf({ path: '/x.mid' }))).toBe(422);
  });

  it('decomposes durations into dotted and triplet values', () => {
    expect(durationsOf(1440, 1).map(d => [d.duration.value, d.duration.dots])).toEqual([['quarter', 1]]);
    expect(durationsOf(1440, 0).map(d => d.duration.value)).toEqual(['quarter', 'eighth']);
    expect(durationsOf(320, 1).map(d => [d.duration.value, d.duration.tuplet?.actual])).toEqual([['eighth', 3]]);
    expect(durationsOf(1120, 1).map(d => d.ticks)).toEqual([960, 160]);
  });
});
