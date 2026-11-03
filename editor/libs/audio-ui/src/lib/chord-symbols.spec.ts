import {
  chordAt,
  chordBlock,
  chordLabel,
  chordSpans,
  chordSymbol,
  parseChordSymbol,
  pitchFromKey,
  pitchKey,
  PitchSpellings,
  scaleText
} from './chord-symbols';

describe('chord symbols', () => {
  it('writes chords as symbols and names', () => {
    expect(chordSymbol({ root: { step: 'a', alter: 0 }, quality: 'm7', bass: null })).toBe('Am7');
    expect(chordSymbol({ root: { step: 'g', alter: 0 }, quality: '', bass: { step: 'b', alter: 0 } })).toBe('G/B');
    expect(chordSymbol({ root: { step: 'f', alter: 1 }, quality: 'm7b5', bass: null })).toBe('F♯m7♭5');
    expect(chordSymbol({ root: { step: 'b', alter: -1 }, quality: 'maj7', bass: null })).toBe('B♭maj7');
    expect(chordLabel({ root: { step: 'g', alter: 0 }, quality: '', bass: { step: 'b', alter: 0 } })).toBe(
      'G major over B'
    );
  });

  it('parses typed symbols, with the usual aliases, and refuses the rest', () => {
    expect(parseChordSymbol('Am7')).toEqual({ root: { step: 'a', alter: 0 }, quality: 'm7', bass: null });
    expect(parseChordSymbol('Fmaj7')?.quality).toBe('maj7');
    expect(parseChordSymbol('FΔ7')?.quality).toBe('maj7');
    expect(parseChordSymbol('Bbm7b5')).toEqual({ root: { step: 'b', alter: -1 }, quality: 'm7b5', bass: null });
    expect(parseChordSymbol('F#-7')).toEqual({ root: { step: 'f', alter: 1 }, quality: 'm7', bass: null });
    expect(parseChordSymbol(' G / B ')).toEqual({
      root: { step: 'g', alter: 0 },
      quality: '',
      bass: { step: 'b', alter: 0 }
    });
    expect(parseChordSymbol('Csus')?.quality).toBe('sus4');
    expect(parseChordSymbol('C°')?.quality).toBe('dim');
    expect(parseChordSymbol('C/C')?.bass).toBeNull();
    for (const bad of ['', 'H7', 'Cmaj13', 'C###', 'C/X']) {
      expect(parseChordSymbol(bad)).toBeNull();
    }
    for (const pitch of PitchSpellings) {
      expect(pitchFromKey(pitchKey(pitch))).toEqual(pitch);
    }
  });

  it('spans each chord to the next and finds the chord at a tick', () => {
    const chords = [
      { id: 'b', tick: 3840 },
      { id: 'a', tick: 0 },
      { id: 'c', tick: 5760 }
    ];
    expect(chordSpans(chords, 3840).map(s => [s.chord.id, s.startTick, s.endTick])).toEqual([
      ['a', 0, 3840],
      ['b', 3840, 5760],
      ['c', 5760, 9600]
    ]);
    expect(chordAt(chords, 4000)?.id).toBe('b');
    expect(chordAt(chords, -1)).toBeNull();
    const x = (tick: number) => tick / 10;
    expect(chordBlock({ chord: null, startTick: 0, endTick: 3840 }, x)).toEqual({ x: 0, width: 383 });
    expect(chordBlock({ chord: null, startTick: 0, endTick: 10 }, x)).toEqual({ x: 0, width: 6 });
  });

  it('names key and scale changes', () => {
    expect(scaleText({ tonic: { step: 'c', alter: 0 }, scale: 'major' })).toBe('C major');
    expect(scaleText({ tonic: { step: 'd', alter: 0 }, scale: 'dorian' })).toBe('D Dorian');
  });
});
