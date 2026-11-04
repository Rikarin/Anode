import { signal, type Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { PluginInfo } from '@anode/api-client';
import { FakeParityEffects } from '../core/fake/fake-plugins-effects';
import { readingKind } from '../mixer/plugin-window';
import { goniometerPoints, noteName, PhasePanel, ScopePanel, tracePoints, TunerPanel } from './meter-panels';
import { matchPanel } from './panel-patterns';
import { deferredPanel, type PluginPanelHost } from './plugin-panel';
import { expanderGain, ExpanderPanel, OscillatorPanel, spreadBands, SpreadPanel, waveCycle } from './utility-panels';

const fake = (id: string): PluginInfo => FakeParityEffects.find(p => p.id === id) as PluginInfo;

async function render<T>(panel: Type<T>, info: PluginInfo, readings: readonly number[] | null = null) {
  const values = signal<Record<string, number>>(Object.fromEntries(info.parameters.map(p => [p.id, p.default])));
  const live = signal(readings);
  const sets: [string, number][] = [];
  const host: PluginPanelHost = {
    plugin: info,
    value: id => values()[id] ?? 0,
    set: (id, value) => {
      sets.push([id, value]);
      values.update(v => ({ ...v, [id]: value }));
    },
    readingValues: () => live()
  };
  const fixture = TestBed.createComponent(panel);
  fixture.componentRef.setInput('host', host);
  fixture.autoDetectChanges();
  await fixture.whenStable();
  return { element: fixture.nativeElement as HTMLElement, live, values, sets, fixture };
}

describe('The utility panels', () => {
  it('are found by plugin id and load deferred', () => {
    const kinds: [string, string][] = [
      ['anode.tuner', 'tuner'],
      ['anode.scope', 'scope'],
      ['anode.phase-meter', 'phaseMeter'],
      ['anode.expander', 'expander'],
      ['anode.test-oscillator', 'testOscillator'],
      ['anode.frequency-spread', 'frequencySpread']
    ];
    for (const [id, kind] of kinds) {
      expect(matchPanel(fake(id))?.kind).toBe(kind);
      expect(deferredPanel(fake(id))).toBe(kind);
    }
  });

  it('reads each meter on its own scale', () => {
    expect(readingKind(fake('anode.phase-meter'))).toMatchObject({ min: -1, max: 1, label: 'Correlation', unit: '' });
    expect(readingKind(fake('anode.tuner'))).toMatchObject({ min: -50, max: 50, unit: 'ct' });
    expect(readingKind(fake('anode.scope'))).toMatchObject({ label: 'Level', unit: 'dB' });
    expect(readingKind(fake('anode.expander'))).toMatchObject({ kind: 'reduction' });
  });

  it('Tuner: note, needle and offset', async () => {
    expect(noteName(69)).toBe('A4');
    expect(noteName(61)).toBe('C♯4');
    expect(noteName(21)).toBe('A0');
    const { element, live, fixture } = await render(TunerPanel, fake('anode.tuner'));
    const text = (id: string) => element.querySelector(`[data-testid="${id}"]`)?.textContent.trim();
    expect(text('tuner-note')).toBe('—');
    expect(text('tuner-offset')).toBe('No pitch');
    expect(element.querySelector('[data-testid="tuner-needle"]')).toBeNull();
    live.set([442.5, 69, 9.8]);
    await fixture.whenStable();
    expect(text('tuner-note')).toBe('A4');
    expect(text('tuner-offset')).toBe('+9.8 ct · 442.5 Hz');
    const needle = element.querySelector<HTMLElement>('[data-testid="tuner-needle"]');
    expect(needle?.style.left).toBe('59.8%');
    expect(needle?.classList).toContain('bg-meter-mid');
    live.set([440, 69, -1]);
    await fixture.whenStable();
    expect(element.getAttribute('aria-valuetext')).toBe('A4, −1.0 ct · 440.0 Hz');
    expect(needle?.classList).toContain('bg-meter-low');
  });

  it('Scope: a trace per channel', async () => {
    expect(tracePoints([1, 0, -1], 100, 40)).toBe('0,0 50,20 100,40');
    expect(tracePoints([2], 100, 40)).toBe('0,0');
    const { element, live, fixture } = await render(ScopePanel, fake('anode.scope'));
    expect(element.querySelector('[data-testid="scope-left"]')).toBeNull();
    live.set([0, 1, 0, -1]);
    await fixture.whenStable();
    expect(element.querySelector('[data-testid="scope-left"]')?.getAttribute('points')).toBe('0,120 600,0');
    expect(element.querySelector('[data-testid="scope-right"]')?.getAttribute('points')).toBe('0,120 600,240');
  });

  it('Phase Meter: mono stands up, the correlation and the balance read out', async () => {
    expect(goniometerPoints([1, 0, 0, Math.SQRT2], 200)).toMatchObject([{ x: 100 }]);
    expect(goniometerPoints([1, 0, Math.SQRT2, 0], 200)).toEqual([{ x: 200, y: 100 }]);
    const { element, live, fixture } = await render(PhasePanel, fake('anode.phase-meter'), [-0.5, 3, 0, 0, 0.1, 0.1]);
    expect(element.querySelectorAll('[data-testid="goniometer-point"]')).toHaveLength(2);
    expect(element.querySelector('an-reading-meter')?.getAttribute('aria-valuetext')).toBe('−0.5');
    expect(element.querySelector('[data-testid="phase-balance"]')?.textContent).toContain('3.0 dB right');
    live.set([1, 0]);
    await fixture.whenStable();
    expect(element.querySelector('[data-testid="phase-balance"]')?.textContent).toContain('centred');
  });

  it('Expander: the engine’s curve, within its range', async () => {
    expect(expanderGain(-10, -20, 3, 0, -60)).toBe(0);
    expect(expanderGain(-30, -20, 3, 0, -60)).toBe(-20);
    expect(expanderGain(-34, -20, 3, 6, -60)).toBe(-28);
    expect(expanderGain(-20, -20, 3, 6, -60)).toBe(-1.5);
    expect(expanderGain(-90, -20, 3, 0, -40)).toBe(-40);
    const { element, values, fixture } = await render(ExpanderPanel, fake('anode.expander'));
    const curve = () => element.querySelector('[data-testid="expander-curve"]')?.getAttribute('points');
    const before = curve();
    expect(before?.split(' ')).toHaveLength(97);
    values.update(v => ({ ...v, threshold: -20 }));
    await fixture.whenStable();
    expect(curve()).not.toBe(before);
    expect(element.querySelector('svg')?.getAttribute('aria-label')).toContain('-20');
  });

  it('Test Oscillator: the wave drawn, and a press sets a reference frequency', async () => {
    expect(waveCycle(2)[0]).toBe(1);
    expect(waveCycle(2)[95]).toBe(-1);
    expect(waveCycle(0)[24]).toBeCloseTo(1);
    const { element, sets, values, fixture } = await render(OscillatorPanel, fake('anode.test-oscillator'));
    const press = element.querySelector<HTMLButtonElement>('[aria-label="Frequency"] button');
    press?.click();
    await fixture.whenStable();
    expect(sets).toEqual([['frequency', 100]]);
    expect(press?.getAttribute('aria-pressed')).toBe('true');
    expect(element.querySelector('svg')?.getAttribute('aria-label')).toBe('Waveform: Sine');
    values.update(v => ({ ...v, waveform: 3 }));
    await fixture.whenStable();
    expect(element.querySelector('svg')?.getAttribute('aria-label')).toBe('Waveform: Sawtooth');
  });

  it('Frequency Spread: bands left and right in turn over their span', async () => {
    const bands = spreadBands(4, 100, 1_600);
    expect(bands.map(b => b.side)).toEqual(['L', 'R', 'L', 'R']);
    expect(bands[0]?.to).toBeCloseTo(bands[1]?.from ?? 0);
    const { element, values, fixture } = await render(SpreadPanel, fake('anode.frequency-spread'));
    expect(element.querySelectorAll('[data-testid="spread-band"]')).toHaveLength(8);
    values.update(v => ({ ...v, bands: 3 }));
    await fixture.whenStable();
    expect(element.querySelectorAll('[data-side="R"]')).toHaveLength(1);
  });
});
