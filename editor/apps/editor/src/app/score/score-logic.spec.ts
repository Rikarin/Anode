import type { LyricSyllable, ScoreView } from '@anode/api-client';
import { chordText, parseChord } from './chords';
import { continuesWord, lyricKey, lyricNotes } from './lyrics';
import {
  dragAccidental,
  fifthsOfKey,
  gridsOf,
  keyName,
  logicPitchName,
  pitchAt,
  placementOf,
  snapToGrids,
  spell,
  stepPitch,
  writtenFifths
} from './notation-math';
import {
  deleteSelection,
  type IndexedNote,
  indexView,
  insertNote,
  moveNotes,
  snapDisplayTick,
  symbolAt,
  symbolsForNotes,
  valueTicks,
  writtenPosition
} from './score-edit';
import {
  duplicateStyle,
  instrumentNameOf,
  newScoreId,
  normaliseStaves,
  presetOf,
  withInstrumentName,
  withPreset
} from './score-model';

const Treble = {
  id: 'factory.treble',
  name: 'Treble',
  staves: [{ clef: 'treble' as const, transpose: 0, voices: 2 }],
  displayQuantizeTicks: 240,
  tripletQuantizeTicks: 160,
  brace: false
};
const Measure = {
  number: 2,
  startTick: 3840,
  lengthTicks: 3840,
  time: { numerator: 4, denominator: 4 },
  key: { fifths: 0, mode: 'major' as const },
  showTime: false,
  showKey: false,
  tempos: [],
  rehearsals: [],
  barline: 'single' as const
};

describe('chord symbol entry', () => {
  it('reads roots, qualities and basses', () => {
    expect(parseChord('C')).toEqual({ root: { step: 'c', alter: 0 }, quality: '', bass: null });
    expect(parseChord('Am7')).toEqual({ root: { step: 'a', alter: 0 }, quality: 'm7', bass: null });
    expect(parseChord('Fmaj7/C')).toEqual({
      root: { step: 'f', alter: 0 },
      quality: 'maj7',
      bass: { step: 'c', alter: 0 }
    });
    expect(parseChord(' G7 ')).toEqual({ root: { step: 'g', alter: 0 }, quality: '7', bass: null });
    expect(parseChord('Bb7')).toMatchObject({ root: { step: 'b', alter: -1 }, quality: '7' });
    expect(parseChord('F#m7b5')).toMatchObject({ root: { step: 'f', alter: 1 }, quality: 'm7b5' });
    expect(parseChord('E♭maj7')).toMatchObject({ root: { step: 'e', alter: -1 }, quality: 'maj7' });
    expect(parseChord('C6/9')).toMatchObject({ quality: '69', bass: null });
    expect(parseChord('C/E')).toMatchObject({ quality: '', bass: { step: 'e', alter: 0 } });
  });

  it('normalises spellings of the same quality', () => {
    expect(parseChord('Cmin7')?.quality).toBe('m7');
    expect(parseChord('C-7')?.quality).toBe('m7');
    expect(parseChord('CM7')?.quality).toBe('maj7');
    expect(parseChord('CΔ')?.quality).toBe('maj7');
    expect(parseChord('C°')?.quality).toBe('dim');
    expect(parseChord('Cø')?.quality).toBe('m7b5');
    expect(parseChord('C+')?.quality).toBe('aug');
  });

  it('refuses what is not a chord symbol', () => {
    expect(parseChord('')).toBeNull();
    expect(parseChord('Xyz')).toBeNull();
    expect(parseChord('H7')).toBeNull();
    expect(parseChord('Cfoo')).toBeNull();
    expect(parseChord('C/X')).toBeNull();
  });

  it('writes chord symbols back with accidental signs', () => {
    expect(chordText({ root: { step: 'f', alter: 1 }, quality: 'm7', bass: null })).toBe('F♯m7');
    expect(chordText({ root: { step: 'f', alter: 0 }, quality: 'maj7', bass: { step: 'c', alter: 0 } })).toBe(
      'Fmaj7/C'
    );
  });
});

describe('lyrics entry', () => {
  const existing = (noteId: number, text: string, syllabic: LyricSyllable['syllabic']): LyricSyllable => ({
    id: `lyr_${noteId}`,
    noteId,
    verse: 1,
    text,
    syllabic,
    extender: false
  });

  it('enters a syllable and moves on with Space and Tab, back with ⇧Tab', () => {
    expect(lyricKey({ key: ' ', shiftKey: false }, 1, 'la', false, null)).toEqual({
      change: { noteId: 1, text: 'la', syllabic: 'single', extender: false },
      move: 1,
      done: false
    });
    expect(lyricKey({ key: 'Tab', shiftKey: true }, 1, 'la', false, null)?.move).toBe(-1);
    expect(lyricKey({ key: 'a', shiftKey: false }, 1, 'la', false, null)).toBeNull();
  });

  it('joins syllables of a word with a hyphen', () => {
    expect(lyricKey({ key: '-', shiftKey: false }, 1, 'Hap', false, null)?.change?.syllabic).toBe('begin');
    expect(lyricKey({ key: '-', shiftKey: false }, 2, 'pi', true, null)?.change?.syllabic).toBe('middle');
    expect(lyricKey({ key: ' ', shiftKey: false }, 3, 'ness', true, null)?.change?.syllabic).toBe('end');
    expect(lyricKey({ key: '_', shiftKey: false }, 3, 'oh', false, null)?.change).toMatchObject({ extender: true });
  });

  it('removes a syllable whose field is emptied, and changes nothing when it is the same', () => {
    expect(lyricKey({ key: 'Tab', shiftKey: false }, 1, '', false, existing(1, 'la', 'single'))?.change).toEqual({
      noteId: 1,
      text: '',
      syllabic: 'single',
      extender: false
    });
    expect(lyricKey({ key: 'Tab', shiftKey: false }, 1, '', false, null)?.change).toBeNull();
    expect(lyricKey({ key: 'Tab', shiftKey: false }, 1, 'la', false, existing(1, 'la', 'single'))?.change).toBeNull();
    expect(lyricKey({ key: 'Escape', shiftKey: false }, 1, 'la', false, null)).toEqual({
      change: null,
      move: 0,
      done: true
    });
    expect(lyricKey({ key: 'Enter', shiftKey: false }, 1, 'la', false, null)).toMatchObject({ move: 0, done: true });
  });

  it('knows whether the previous syllable continues its word', () => {
    const lyrics = [existing(1, 'Hap', 'begin'), existing(2, 'py', 'end')];
    expect(continuesWord(lyrics, [1, 2, 3], 2, 1)).toBe(true);
    expect(continuesWord(lyrics, [1, 2, 3], 3, 1)).toBe(false);
    expect(continuesWord(lyrics, [1, 2, 3], 1, 1)).toBe(false);
  });

  it('orders the notes of a part: first staff, first voice, a chord by its top note, tied segments once', () => {
    const event = (
      id: string,
      tick: number,
      pitches: [number, number][],
      kind: 'note' | 'chord' | 'rest' = 'note'
    ) => ({
      id,
      kind,
      tick,
      durationTicks: 960,
      duration: { value: 'quarter' as const, dots: 0, tuplet: null },
      notes: pitches.map(([noteId, pitch]) => ({
        id: `n${noteId}`,
        noteId,
        pitch,
        written: { step: 'c' as const, alter: 0, octave: 4 },
        staffPosition: 0,
        tieStart: false,
        tieStop: false,
        notehead: 'normal' as const
      })),
      stem: 'up' as const,
      symbolIds: [],
      lyricIds: [],
      chordIds: []
    });
    const view = {
      parts: [
        {
          trackId: 't',
          measures: [
            {
              staves: [
                {
                  voices: [
                    {
                      number: 1,
                      events: [
                        event(
                          'c1',
                          0,
                          [
                            [1, 60],
                            [2, 64]
                          ],
                          'chord'
                        ),
                        event('r1', 960, [], 'rest'),
                        event('n3', 1920, [[3, 67]]),
                        event('n3-2', 2880, [[3, 67]])
                      ]
                    },
                    { number: 2, events: [event('n9', 0, [[9, 48]])] }
                  ]
                }
              ]
            }
          ]
        }
      ]
    } as unknown as ScoreView;
    expect(lyricNotes(view, 't')).toEqual([2, 3]);
  });
});

describe('notation math', () => {
  it('reads key signatures', () => {
    expect(fifthsOfKey({ tonic: 0, mode: 'major' })).toBe(0);
    expect(fifthsOfKey({ tonic: 7, mode: 'major' })).toBe(1);
    expect(fifthsOfKey({ tonic: 5, mode: 'major' })).toBe(-1);
    expect(fifthsOfKey({ tonic: 9, mode: 'minor' })).toBe(0);
    expect(fifthsOfKey({ tonic: 2, mode: 'minor' })).toBe(-1);
    expect(fifthsOfKey(null)).toBe(0);
    expect(writtenFifths(-2, 2)).toBe(0);
    expect(writtenFifths(0, 7)).toBe(1);
    expect(keyName(-3, 'major')).toBe('E♭ major');
  });

  it('spells pitches in the key', () => {
    expect(spell(61, 2)).toEqual({ step: 'c', alter: 1, octave: 4 });
    expect(spell(61, -3)).toEqual({ step: 'd', alter: -1, octave: 4 });
    expect(spell(66, 0)).toEqual({ step: 'f', alter: 1, octave: 4 });
    expect(spell(71, -6)).toEqual({ step: 'c', alter: -1, octave: 5 });
  });

  it('places pitches on staves and back, with clefs and transposition', () => {
    // E4 on the treble's bottom line, G2 on the bass's, middle C on the alto's middle line.
    expect(placementOf('treble', 0, 0, 64).staffPosition).toBe(0);
    expect(placementOf('bass', 0, 0, 43).staffPosition).toBe(0);
    expect(placementOf('alto', 0, 0, 60).staffPosition).toBe(4);
    // A guitar's E3 sits on the treble 8vb's bottom line, written E4.
    expect(placementOf('treble8vb', 0, 0, 52)).toEqual({
      staffPosition: 0,
      written: { step: 'e', alter: 0, octave: 4 }
    });
    // A B♭ clarinet's concert B♭3 is written C4 (one ledger line below the treble).
    expect(placementOf('treble', 2, -2, 58)).toEqual({
      staffPosition: -2,
      written: { step: 'c', alter: 0, octave: 4 }
    });
    expect(pitchAt('treble', 0, 0, 4)).toBe(71);
    expect(pitchAt('treble', 0, 1, 1)).toBe(66);
    expect(pitchAt('treble', 0, 0, 1, 'sharp')).toBe(66);
    expect(pitchAt('treble', 2, -2, -2)).toBe(58);
    expect(pitchAt('bass', 0, 0, 0)).toBe(43);
  });

  it('steps pitches through the key, as a drag does', () => {
    expect(stepPitch(64, 1, 0, 0)).toBe(65);
    expect(stepPitch(64, 1, 0, 1)).toBe(66);
    expect(stepPitch(60, -1, 0, 0)).toBe(59);
    expect(stepPitch(64, 1, 0, 0, 'sharp')).toBe(66);
    expect(stepPitch(64, 0, 0, 0, 'flat')).toBe(63);
    expect(dragAccidental({ altKey: true, shiftKey: false })).toBe('sharp');
    expect(dragAccidental({ altKey: false, shiftKey: true })).toBe('flat');
    expect(dragAccidental({ altKey: true, shiftKey: true })).toBe('natural');
    expect(dragAccidental({ altKey: false, shiftKey: false })).toBeNull();
    expect(logicPitchName(60)).toBe('C3');
  });

  it('takes the region grids or the style grids, and snaps from bar lines', () => {
    const notation = {
      displayQuantizeTicks: 0,
      tripletQuantizeTicks: 0,
      interpretation: true,
      noOverlap: true,
      syncopation: false,
      maxDots: 1
    };
    expect(gridsOf(notation, Treble)).toEqual({ straight: 240, triplet: 160 });
    expect(gridsOf({ ...notation, displayQuantizeTicks: 480 }, Treble)).toEqual({ straight: 480, triplet: 0 });
    expect(snapToGrids(3850, 3840, 240, 0)).toBe(3840);
    expect(snapToGrids(4150, 3840, 240, 160)).toBe(4160);
    expect(snapToGrids(100, 0, 0, 0)).toBe(100);
  });
});

describe('score edits', () => {
  const note = (noteId: number, tick: number, pitch = 60, trackId = 't'): IndexedNote => ({
    noteId,
    trackId,
    pitch,
    elementId: `n${noteId}`,
    tick,
    staff: 0
  });

  it('measures values with dots and triplets', () => {
    expect(valueTicks('quarter')).toBe(960);
    expect(valueTicks('quarter', 1)).toBe(1440);
    expect(valueTicks('eighth', 0, true)).toBe(320);
    expect(valueTicks('half', 2)).toBe(3360);
  });

  it('inserts a note at the snapped tick and staff position', () => {
    // Beat 3 of bar 2, clicked a little late: the 1/16 grid takes it back.
    const tick = snapDisplayTick(3840 + 1920 + 70, Measure, null, Treble);
    expect(tick).toBe(3840 + 1920);
    expect(snapDisplayTick(3840 + 1930, Measure, null, Treble, 320)).toBe(3840 + 1920);
    expect(snapDisplayTick(3840 + 2200, Measure, null, Treble, 320)).toBe(3840 + 2240);
    expect(snapDisplayTick(3840 * 3, Measure, null, Treble)).toBe(3840 * 2 - 1);
    expect(
      insertNote({
        tool: { kind: 'note', value: 'quarter', dots: 0, triplet: false },
        trackId: 't',
        tick,
        staffPosition: 4,
        staff: 0
      })
    ).toEqual({
      type: 'addScoreNote',
      trackId: 't',
      tick: 5760,
      lengthTicks: 960,
      staffPosition: 4,
      staff: 0,
      accidental: null,
      regionId: null
    });
  });

  it('converts a concert-pitch click on a transposing staff to its written position', () => {
    // Concert C4 (one ledger line below the treble) is a B♭ clarinet's D4.
    expect(writtenPosition(-2, 'treble', 0, 2, 0)).toBe(-1);
    expect(writtenPosition(3, 'treble', 2, 2, 0)).toBe(3);
  });

  it('moves notes per region in one batch, and nothing for no move', () => {
    expect(
      moveNotes(
        [
          { noteId: 3, regionId: 'a' },
          { noteId: 1, regionId: 'a' },
          { noteId: 7, regionId: 'b' }
        ],
        1,
        240,
        null
      )
    ).toEqual([
      { type: 'moveScoreNotes', regionId: 'a', noteIds: [1, 3], steps: 1, ticks: 240, accidental: null },
      { type: 'moveScoreNotes', regionId: 'b', noteIds: [7], steps: 1, ticks: 240, accidental: null }
    ]);
    expect(moveNotes([{ noteId: 1, regionId: 'a' }], 0, 0, null)).toEqual([]);
    expect(moveNotes([{ noteId: 1, regionId: 'a' }], 0, 0, 'flat')).toHaveLength(1);
  });

  it('deletes notes, symbols, syllables and chord symbols in one batch', () => {
    expect(
      deleteSelection({
        notes: [{ noteId: 1, regionId: 'a' }],
        symbols: ['sym_1'],
        lyrics: [{ noteId: 4, verse: 1 }],
        chords: ['chd_1']
      })
    ).toEqual([
      { type: 'deleteNotes', regionId: 'a', noteIds: [1] },
      { type: 'deleteScoreSymbols', symbolIds: ['sym_1'] },
      { type: 'setLyrics', verse: 1, syllables: [{ noteId: 4, text: '', syllabic: 'single', extender: false }] },
      { type: 'deleteChordSymbols', chordIds: ['chd_1'] }
    ]);
    expect(deleteSelection({ notes: [], symbols: [], lyrics: [], chords: [] })).toEqual([]);
  });

  it('puts Part box symbols on selected notes', () => {
    const notes = [note(3, 960, 64), note(1, 0, 60), note(2, 480, 62)];
    expect(symbolsForNotes('staccato', notes)).toEqual([
      { kind: 'staccato', trackId: 't', noteId: 1 },
      { kind: 'staccato', trackId: 't', noteId: 2 },
      { kind: 'staccato', trackId: 't', noteId: 3 }
    ]);
    expect(symbolsForNotes('slur', notes)).toEqual([{ kind: 'slur', trackId: 't', noteId: 1, endNoteId: 3 }]);
    expect(symbolsForNotes('slur', [note(1, 0)])).toBeNull();
    expect(symbolsForNotes('slur', [note(1, 0), note(2, 960, 60, 'u')])).toBeNull();
    expect(symbolsForNotes('dynamic', notes, 'mf')).toEqual([{ kind: 'dynamic', trackId: 't', noteId: 1, text: 'mf' }]);
    expect(symbolsForNotes('staccato', [])).toBeNull();
    expect(symbolAt('crescendo', 't', 0, 0, null, 1920)).toEqual({
      kind: 'crescendo',
      trackId: 't',
      tick: 0,
      staff: null,
      text: null,
      endTick: 1920
    });
    expect(symbolAt('pedalDown', 't', 960, 1)).toMatchObject({ staff: 1, text: null, endTick: null });
  });

  it('indexes a view: staves in order, notes by id, element ticks', () => {
    const view = {
      measures: [Measure],
      parts: [
        {
          trackId: 't',
          staffStyleId: 'factory.piano',
          transpose: 0,
          staves: [
            { clef: 'treble', transpose: 0, voices: 2 },
            { clef: 'bass', transpose: 0, voices: 2 }
          ],
          measures: [
            {
              staves: [
                {
                  voices: [
                    {
                      number: 1,
                      events: [{ id: 'n5', kind: 'note', tick: 3840, notes: [{ id: 'n5', noteId: 5, pitch: 64 }] }]
                    }
                  ]
                },
                { voices: [{ number: 1, events: [{ id: 'r', kind: 'rest', tick: 3840, notes: [] }] }] }
              ]
            }
          ]
        }
      ]
    } as unknown as ScoreView;
    const index = indexView(view);
    expect(index.staves.map(s => [s.trackId, s.staff, s.clef])).toEqual([
      ['t', 0, 'treble'],
      ['t', 1, 'bass']
    ]);
    expect(index.notes.get(5)).toMatchObject({ tick: 3840, staff: 0, elementId: 'n5' });
    expect(index.ticks.get('r')).toBe(3840);
  });
});

describe('score windows model', () => {
  it('makes ids with the model prefixes', () => {
    expect(newScoreId('sts')).toMatch(/^sts_[0-9a-f]{12}$/);
    expect(newScoreId('scs')).toMatch(/^scs_/);
  });

  it('keeps split points on every staff but the last', () => {
    expect(
      normaliseStaves([
        { clef: 'treble', transpose: 0, splitPitch: null, voices: 1 },
        { clef: 'bass', transpose: 0, splitPitch: 48, voices: 1 }
      ])
    ).toEqual([
      { clef: 'treble', transpose: 0, splitPitch: 60, voices: 1 },
      { clef: 'bass', transpose: 0, splitPitch: null, voices: 1 }
    ]);
    expect(duplicateStyle(Treble, 'sts_x')).toMatchObject({ id: 'sts_x', name: 'Treble Copy' });
  });

  it('maps page presets both ways', () => {
    const layout = {
      pageWidthMm: 210,
      pageHeightMm: 297,
      margins: { top: 15, bottom: 15, left: 15, right: 15 },
      staffHeightMm: 7,
      title: '',
      subtitle: '',
      composer: '',
      header: '',
      footer: '',
      transposingScore: false,
      instrumentNames: []
    };
    expect(presetOf(layout)).toEqual({ name: 'A4', landscape: false });
    expect(presetOf({ pageWidthMm: 279.4, pageHeightMm: 215.9 })).toEqual({ name: 'Letter', landscape: true });
    expect(presetOf({ pageWidthMm: 100, pageHeightMm: 100 })).toEqual({ name: 'Custom', landscape: false });
    expect(withPreset(layout, 'A3', true)).toMatchObject({ pageWidthMm: 420, pageHeightMm: 297 });
    // Instrument names list only those that differ from the track's.
    const named = withInstrumentName(layout, 't1', 'Clarinet', { full: 'Clarinet in B♭', short: 'Cl.' });
    expect(named.instrumentNames).toEqual([{ trackId: 't1', full: 'Clarinet in B♭', short: 'Cl.' }]);
    expect(instrumentNameOf(named, 't1', 'Clarinet')).toEqual({ full: 'Clarinet in B♭', short: 'Cl.' });
    expect(withInstrumentName(named, 't1', 'Clarinet', { full: 'Clarinet', short: 'Clari' }).instrumentNames).toEqual(
      []
    );
  });
});
