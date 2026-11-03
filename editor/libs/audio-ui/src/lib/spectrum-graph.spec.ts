import { TestBed } from '@angular/core/testing';
import { AnSpectrumGraph, areaPath, spectrumPoints, spectrumRange, spectrumX } from './spectrum-graph';

describe('spectrum graph math', () => {
  it('places frequencies on a log axis from 20 Hz to 20 kHz', () => {
    expect(spectrumX(20)).toBe(0);
    expect(spectrumX(20_000)).toBe(1);
    expect(spectrumX(632.46)).toBeCloseTo(0.5, 3);
    expect(spectrumX(5)).toBe(0);
  });

  it('spans both series with room, in 6 dB steps, at least 12 dB', () => {
    expect(spectrumRange([-4, 2], [0])).toEqual({ min: -12, max: 6 });
    expect(spectrumRange([0, 0])).toEqual({ min: -6, max: 6 });
    expect(spectrumRange([])).toEqual({ min: -12, max: 12 });
  });

  it('draws points top-down and closes the area at the bottom', () => {
    const points = spectrumPoints([20, 20_000], [6, -6], { min: -6, max: 6 }, 100, 50);
    expect(points).toEqual([
      { x: 0, y: 0 },
      { x: 100, y: 50 }
    ]);
    expect(areaPath(points, 50)).toBe('M0,50 L0,0 L100,50 L100,50 Z');
    expect(areaPath([], 50)).toBe('');
  });
});

describe('AnSpectrumGraph', () => {
  it('is an image named by its label, with the spectrum and the curve', async () => {
    const fixture = TestBed.createComponent(AnSpectrumGraph);
    fixture.componentRef.setInput('bands', [100, 1000, 10_000]);
    fixture.componentRef.setInput('spectrum', [3, 0, -3]);
    fixture.componentRef.setInput('curve', [1, 0, -1]);
    fixture.componentRef.setInput('label', 'Spectrum against the Valve curve');
    await fixture.whenStable();
    const svg = (fixture.nativeElement as HTMLElement).querySelector('svg');
    expect(svg?.getAttribute('role')).toBe('img');
    expect(svg?.getAttribute('aria-label')).toBe('Spectrum against the Valve curve');
    expect(svg?.querySelectorAll('polyline')).toHaveLength(2);
  });
});
