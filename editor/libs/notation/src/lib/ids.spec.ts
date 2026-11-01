import { chordSymbolIdOf, lyricIdOf, noteIdOf, ScoreIds, symbolIdOf } from './ids';
import { PendingNotationRenderer } from './renderer';

// The same cases as the engine's (engine/tests/Anode.Model.Tests/Phase9Tests.cs), so both sides agree.
describe('score element ids', () => {
  it('names elements as the notation engine does', () => {
    expect(ScoreIds.note(42)).toBe('n42');
    expect(ScoreIds.note(42, 3)).toBe('n42-3');
    expect(ScoreIds.chord(7, 2)).toBe('c7-2');
    expect(ScoreIds.symbol('sym_abc')).toBe('ssym_abc');
    expect(ScoreIds.rest('trk_1', 4, 0, 1, 2)).toBe('rtrk_1-4-0-1-2');
  });

  it('maps elements back to notes, symbols, lyrics and chord symbols', () => {
    expect(noteIdOf('n42')).toBe(42);
    expect(noteIdOf('n42-3')).toBe(42);
    expect(noteIdOf('c42')).toBeNull();
    expect(noteIdOf('n')).toBeNull();
    expect(noteIdOf('nx')).toBeNull();
    expect(noteIdOf('n042')).toBeNull();
    expect(noteIdOf('n42-')).toBeNull();
    expect(noteIdOf('n42-x')).toBeNull();
    expect(symbolIdOf('ssym_abc')).toBe('sym_abc');
    expect(symbolIdOf('s')).toBeNull();
    expect(symbolIdOf('n42')).toBeNull();
    expect(lyricIdOf('llyr_1')).toBe('lyr_1');
    expect(chordSymbolIdOf('hchd_1')).toBe('chd_1');
  });
});

describe('PendingNotationRenderer', () => {
  it('refuses to render until workstream C', async () => {
    const renderer = new PendingNotationRenderer();
    await expect(renderer.render('<mei/>', { view: 'linear', layout: {} as never })).rejects.toThrow(/workstream C/);
    renderer.dispose();
  });
});
