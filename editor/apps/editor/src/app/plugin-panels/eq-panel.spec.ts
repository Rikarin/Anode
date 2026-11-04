import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { PluginInfo } from '@anode/api-client';
import { eqGainY, spectrumX } from '@anode/audio-ui/lazy';
import { FakeEffects } from '../core/fake/fake-plugins';
import { FakeT2Effects } from '../core/fake/fake-plugins-phase6';
import { EqPanel, settleSpectrum } from './eq-panel';
import { matchPanel } from './panel-patterns';
import type { PluginPanelHost } from './plugin-panel';

const channelEq = FakeEffects.find(p => p.id === 'anode.channel-eq') as PluginInfo;
const linearEq = FakeT2Effects.find(p => p.id === 'anode.linear-eq') as PluginInfo;

async function render(plugin: PluginInfo) {
  const values = signal<Record<string, number>>(Object.fromEntries(plugin.parameters.map(p => [p.id, p.default])));
  const spectrum = signal<readonly number[] | null>(null);
  const sets: [string, number][] = [];
  const host: PluginPanelHost = {
    plugin,
    value: id => values()[id] ?? 0,
    set: (id, value) => {
      sets.push([id, value]);
      values.update(v => ({ ...v, [id]: value }));
    },
    readingValues: () => spectrum()
  };
  const fixture = TestBed.createComponent(EqPanel);
  fixture.componentRef.setInput('host', host);
  fixture.autoDetectChanges();
  await fixture.whenStable();
  const element = fixture.nativeElement as HTMLElement;
  const handle = (id: string) => element.querySelector<HTMLElement>(`[data-band="${id}"]`) as HTMLElement;
  const key = async (target: Element, key: string, shiftKey = false) => {
    target.dispatchEvent(new KeyboardEvent('keydown', { key, shiftKey, bubbles: true }));
    await fixture.whenStable();
  };
  return { element, values, spectrum, sets, handle, key, fixture };
}

describe('EQ panel', () => {
  it('binds both EQs, whose ids are the same', () => {
    expect(matchPanel(channelEq)?.kind).toBe('eqCurve');
    expect(matchPanel(linearEq)?.kind).toBe('eqCurve');
    expect(matchPanel({ ...channelEq, parameters: channelEq.parameters.slice(1) })?.kind).not.toBe('eqCurve');
  });

  it('draws a handle per band where the band is', async () => {
    const { element, handle, values, fixture } = await render(channelEq);
    expect(element.querySelectorAll('[data-testid="eq-handle"]')).toHaveLength(8);
    values.update(v => ({ ...v, peak2Freq: 1_000, peak2Gain: 12 }));
    await fixture.whenStable();
    const peak = handle('peak2');
    expect(Number.parseFloat(peak.style.left)).toBeCloseTo(spectrumX(1_000) * 100, 3);
    expect(Number.parseFloat(peak.style.top)).toBeCloseTo(eqGainY(12) * 100, 3);
    expect(peak.getAttribute('aria-valuetext')).toBe('1.00 kHz, +12.0 dB, Q 1.00');
    expect(element.querySelector('[data-testid="eq-curve"]')?.getAttribute('points')?.split(' ')).toHaveLength(240);
  });

  it('moves a band with the keyboard: frequency, gain, Q', async () => {
    const { handle, key, sets, values } = await render(channelEq);
    const peak = handle('peak2');
    await key(peak, 'ArrowUp');
    expect(sets).toEqual([['peak2Gain', 0.5]]);
    await key(peak, 'ArrowDown', true);
    expect(values()['peak2Gain']).toBeCloseTo(0.4);
    await key(peak, 'ArrowRight');
    expect(sets.at(-1)?.[0]).toBe('peak2Freq');
    expect(values()['peak2Freq']).toBeGreaterThan(600);
    await key(peak, 'PageUp');
    expect(sets.at(-1)).toEqual(['peak2Q', 1.19]);
  });

  it('keeps a value within its parameter, and a cut’s up and down are its slope', async () => {
    const { handle, key, sets, values } = await render(channelEq);
    const shelf = handle('lowShelf');
    for (let i = 0; i < 80; i++) {
      await key(shelf, 'PageUp');
    }
    expect(values()['lowShelfQ']).toBe(2);
    const cut = handle('hpf');
    await key(cut, 'ArrowUp');
    expect(sets.at(-1)).toEqual(['hpfSlope', 2]);
    await key(cut, 'PageDown');
    expect(sets.at(-1)).toEqual(['hpfSlope', 1]);
    expect(cut.getAttribute('aria-valuetext')).toContain('24 dB per octave');
  });

  it('drags a band: across is frequency, down is gain; a cut only moves across', async () => {
    const { handle, sets, element, fixture } = await render(channelEq);
    const graph = element.querySelector('[data-testid="eq-graph"]') as HTMLElement;
    graph.getBoundingClientRect = () => new DOMRect(0, 0, 300, 100);
    const drag = async (target: HTMLElement, x: number, y: number) => {
      target.setPointerCapture = () => undefined;
      target.dispatchEvent(new PointerEvent('pointerdown', { button: 0, pointerId: 1, bubbles: true }));
      target.dispatchEvent(new PointerEvent('pointermove', { clientX: x, clientY: y, pointerId: 1, bubbles: true }));
      target.dispatchEvent(new PointerEvent('pointerup', { pointerId: 1, bubbles: true }));
      await fixture.whenStable();
    };
    await drag(handle('peak3'), 150, 25);
    expect(sets).toEqual([
      ['peak3Freq', 632],
      ['peak3Gain', 12]
    ]);
    sets.length = 0;
    await drag(handle('lpf'), 200, 90);
    expect(sets.map(s => s[0])).toEqual(['lpfFreq']);
    // Not grabbed: moving over a handle does nothing.
    sets.length = 0;
    handle('peak1').dispatchEvent(new PointerEvent('pointermove', { clientX: 10, pointerId: 1, bubbles: true }));
    expect(sets).toEqual([]);
  });

  it('switches bands, and bends the curve with them', async () => {
    const { element, values, sets, fixture } = await render(channelEq);
    values.update(v => ({ ...v, peak2Gain: 9 }));
    await fixture.whenStable();
    const line = () => element.querySelector('[data-testid="eq-curve"]')?.getAttribute('points');
    const before = line();
    const button = element.querySelector<HTMLButtonElement>('[aria-label="Peak 2"][aria-pressed]');
    expect(button?.getAttribute('aria-pressed')).toBe('true');
    button?.click();
    await fixture.whenStable();
    expect(sets).toEqual([['peak2On', 0]]);
    expect(button?.getAttribute('aria-pressed')).toBe('false');
    expect(line()).not.toBe(before);
  });

  it('shows the live spectrum once the engine sends one', async () => {
    const { element, spectrum, fixture } = await render(linearEq);
    expect(element.querySelector('[data-testid="eq-analyser"]')).toBeNull();
    spectrum.set(Array.from({ length: 64 }, () => -30));
    await fixture.whenStable();
    expect(element.querySelector('[data-testid="eq-analyser"]')?.getAttribute('d')).toMatch(/^M/);
    spectrum.set(null);
    await fixture.whenStable();
    expect(element.querySelector('[data-testid="eq-analyser"]')).toBeNull();
  });

  it('reads the Channel EQ’s dynamic gains before its spectrum', async () => {
    const { element, values, spectrum, handle, fixture } = await render(channelEq);
    values.update(v => ({ ...v, peak2Dyn: 1 }));
    spectrum.set([0, 0, -4.5, 0, 0, 0, ...Array.from({ length: 64 }, () => -30)]);
    await fixture.whenStable();
    expect(handle('peak2').getAttribute('aria-valuetext')).toContain('dynamic −4.5 dB');
    expect(handle('peak2').classList).toContain('ring-2');
    expect(element.querySelector('[data-testid="eq-analyser"]')?.getAttribute('d')).toContain('L');
  });

  it('shows the focused band’s placement, and its dynamics once it is dynamic', async () => {
    const { element, values, handle, fixture } = await render(channelEq);
    const settings = () => element.querySelector('[data-testid="eq-band-settings"]');
    const parameters = () => [...(settings()?.querySelectorAll('an-panel-parameter') ?? [])].length;
    expect(settings()?.textContent).toContain('Peak 2');
    expect(parameters()).toBe(2); // placement, dynamic
    handle('peak3').dispatchEvent(new FocusEvent('focus'));
    await fixture.whenStable();
    expect(settings()?.textContent).toContain('Peak 3');
    values.update(v => ({ ...v, peak3Dyn: 1 }));
    await fixture.whenStable();
    expect(parameters()).toBe(7);
    handle('hpf').dispatchEvent(new FocusEvent('focus'));
    await fixture.whenStable();
    expect(parameters()).toBe(1); // a cut has a placement alone
  });

  it('splits the curve once a band is placed, and says so', async () => {
    const { element, values, handle, fixture } = await render(channelEq);
    expect(element.querySelector('[data-testid="eq-curve-side"]')).toBeNull();
    values.update(v => ({ ...v, peak2Gain: 9, peak2Place: 2 }));
    await fixture.whenStable();
    const main = element.querySelector('[data-testid="eq-curve"]')?.getAttribute('points');
    const side = element.querySelector('[data-testid="eq-curve-side"]')?.getAttribute('points');
    expect(side).toBeTruthy();
    expect(side).not.toBe(main);
    expect(handle('peak2').getAttribute('aria-valuetext')).toContain(', side');
  });

  it('has no settings row on the Linear Phase EQ', async () => {
    const { element } = await render(linearEq);
    expect(element.querySelector('[data-testid="eq-band-settings"]')).toBeNull();
    expect(element.querySelectorAll('[data-testid="eq-handle"]')).toHaveLength(8);
  });

  it('settles the analyser: up at once, down gently', () => {
    expect(settleSpectrum([-40, -40], [-20, -60])).toEqual([-20, -47]);
    expect(settleSpectrum([], [-20])).toEqual([-20]);
  });
});
