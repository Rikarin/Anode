import { TestBed } from '@angular/core/testing';
import type { PerformanceStatus } from '@anode/api-client';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { OverloadHoldMs, PerformanceStore } from '../state/performance.store';
import { loadClass, percent, PerformanceMeter } from './performance-meter';

const status = (change: Partial<PerformanceStatus> = {}): PerformanceStatus => ({
  audioLoad: 0.25,
  audioPeakLoad: 0.5,
  coreLoads: [0.1, 0.8, 0.95],
  diskLoad: 0.2,
  overloads: 0,
  underruns: 0,
  ...change
});

describe('the load meter', () => {
  it('reads loads as percentages and colours them towards the limit', () => {
    expect(percent(0.256)).toBe('26 %');
    expect([loadClass(0.1), loadClass(0.75), loadClass(0.95)]).toEqual([
      'bg-meter-low',
      'bg-meter-mid',
      'bg-meter-high'
    ]);
  });

  it('counts from when it connected, lights on an overload, goes out, and resets', () => {
    setUpFakeServer();
    const store = TestBed.inject(PerformanceStore);
    store.apply(status({ overloads: 7, underruns: 2 }), 0);
    expect([store.overloads(), store.underruns(), store.overloaded()]).toEqual([0, 0, false]);
    store.apply(status({ overloads: 9, underruns: 2 }), 1_000);
    expect([store.overloads(), store.overloaded()]).toEqual([2, true]);
    store.apply(status({ overloads: 9, underruns: 3 }), 2_000);
    expect([store.underruns(), store.overloaded()]).toEqual([1, true]);
    store.apply(status({ overloads: 9, underruns: 3 }), 2_000 + OverloadHoldMs);
    expect(store.overloaded()).toBe(false);
    store.reset();
    expect([store.overloads(), store.underruns()]).toEqual([0, 0]);
    store.apply(status({ overloads: 1 }), 9_000); // the host started again
    expect(store.overloads()).toBe(1);
  });

  it('draws a bar per core, the disk and the light', async () => {
    setUpFakeServer();
    const fixture = TestBed.createComponent(PerformanceMeter);
    TestBed.inject(PerformanceStore).apply(status(), 0);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelectorAll('[data-testid="core-bars"] > span')).toHaveLength(3);
    expect(element.querySelector('[data-testid="performance-meter"]')?.getAttribute('aria-label')).toBe(
      'Audio 25 %, disk 20 %, 0 overloads'
    );
    TestBed.inject(PerformanceStore).apply(status({ coreLoads: [] }), 1);
    await fixture.whenStable();
    expect(element.querySelectorAll('[data-testid="core-bars"] > span')).toHaveLength(1);
  });

  it('follows the fake Server, which can overload on demand', async () => {
    const server = setUpFakeServer();
    const store = TestBed.inject(PerformanceStore);
    server.emitPerformance();
    await settle();
    expect(store.status()?.coreLoads.length).toBeGreaterThan(0);
    server.simulateOverload();
    await settle();
    expect(store.overloads()).toBe(1);
    expect(store.overloaded()).toBe(true);
  });
});
