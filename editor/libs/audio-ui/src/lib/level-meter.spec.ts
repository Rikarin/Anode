import { TestBed } from '@angular/core/testing';
import { AnLevelMeter, meterFraction } from './level-meter';

describe('meterFraction', () => {
  it('maps the floor to 0, 0 dBFS to 1 and clamps beyond', () => {
    expect(meterFraction(-60)).toBe(0);
    expect(meterFraction(-30)).toBe(0.5);
    expect(meterFraction(0)).toBe(1);
    expect(meterFraction(6)).toBe(1);
    expect(meterFraction(-Infinity)).toBe(0);
  });
});

describe('AnLevelMeter', () => {
  it('exposes the level as an ARIA meter', async () => {
    const fixture = TestBed.createComponent(AnLevelMeter);
    fixture.componentRef.setInput('db', -12.4);
    fixture.componentRef.setInput('label', 'Audio 1');
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.getAttribute('role')).toBe('meter');
    expect(host.getAttribute('aria-label')).toBe('Audio 1');
    expect(host.getAttribute('aria-valuenow')).toBe('-12');
  });
});
