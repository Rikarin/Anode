import { applyDragMode, crossfadeAt, type Span } from './drag-modes';

const span = (id: string, startTick: number, endTick: number, trackId = 't'): Span => ({
  id,
  trackId,
  startTick,
  endTick
});

describe('drag modes', () => {
  const others = [span('a', 0, 1000), span('c', 3000, 4000), span('x', 0, 5000, 'other')];

  it('leaves overlaps alone in Overlap mode', () => {
    expect(applyDragMode('overlap', [span('b', 800, 1800)], others)).toEqual([]);
  });

  it('shortens the earlier region in No Overlap mode', () => {
    expect(applyDragMode('noOverlap', [span('b', 800, 3200)], others)).toEqual([
      { kind: 'trimEnd', id: 'a', endTick: 800 },
      { kind: 'trimEnd', id: 'b', endTick: 3000 }
    ]);
    expect(applyDragMode('noOverlap', [span('b', 0, 500)], [span('a', 0, 1000)])).toEqual([
      { kind: 'delete', id: 'a' }
    ]);
  });

  it('turns overlaps into crossfades in X-Fade mode', () => {
    expect(applyDragMode('crossfade', [span('b', 800, 1800)], others)).toEqual([
      { kind: 'fade', id: 'a', fadeOutTicks: 200 },
      { kind: 'fade', id: 'b', fadeInTicks: 200 }
    ]);
    // MIDI does not crossfade; a region inside another is not a crossfade.
    expect(applyDragMode('crossfade', [{ ...span('b', 800, 1800), audio: false }], others)).toEqual([]);
    expect(applyDragMode('crossfade', [span('b', 100, 500)], others)).toEqual([]);
  });

  it('shuffles left onto the previous region and right against the next', () => {
    expect(applyDragMode('shuffleLeft', [span('b', 1500, 2000)], others)).toEqual([
      { kind: 'move', id: 'b', startTick: 1000 }
    ]);
    expect(applyDragMode('shuffleLeft', [span('b', 1500, 2000, 'empty')], others)).toEqual([
      { kind: 'move', id: 'b', startTick: 0 }
    ]);
    expect(applyDragMode('shuffleRight', [span('b', 1500, 2000)], others)).toEqual([
      { kind: 'move', id: 'b', startTick: 2500 }
    ]);
  });

  it('shuffles several moved regions one after another', () => {
    expect(
      applyDragMode('shuffleLeft', [span('b', 1500, 2000), span('d', 1800, 2100)], [span('a', 0, 1000)])
    ).toEqual([
      { kind: 'move', id: 'b', startTick: 1000 },
      { kind: 'move', id: 'd', startTick: 1500 }
    ]);
  });

  it('centres a crossfade on a boundary, limited by the spare audio', () => {
    expect(
      crossfadeAt({ endTick: 1000, spareAfterTicks: 500 }, { startTick: 1000, spareBeforeTicks: 50 }, 200)
    ).toEqual({ extendEarlier: 100, extendLater: 50, overlap: 150 });
    expect(
      crossfadeAt({ endTick: 1000, spareAfterTicks: 500 }, { startTick: 1200, spareBeforeTicks: 50 }, 200)
    ).toBeNull();
  });
});
