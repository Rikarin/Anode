import { type Type, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ParameterInfo, PluginInfo } from '@anode/api-client';
import { FakeMidiEffects, FakeT2Effects, FakeT2Instruments } from '../core/fake/fake-plugins-phase6';
import { DelayTapsPanel, tapSpan, tapsShown } from './delay-taps-panel';
import { DrawbarsPanel, drawbarSetting, drawbarValue } from './drawbars-panel';
import { FmAlgorithmPanel } from './fm-algorithm-panel';
import { MultipressorPanel, axisFrequency, axisPosition } from './multipressor-panel';
import { matchPanel } from './panel-patterns';
import { type PluginPanelHost, pluginPanel } from './plugin-panel';

const fake = (id: string): PluginInfo => {
  const plugin = [...FakeT2Instruments, ...FakeT2Effects].find(p => p.id === id);
  if (!plugin) {
    throw new Error(`no ${id}`);
  }
  return plugin;
};

const parameter = (id: string, min = 0, max = 1, extra: Partial<ParameterInfo> = {}): ParameterInfo => ({
  id,
  name: id,
  min,
  max,
  default: min,
  unit: '',
  steps: 0,
  valueNames: null,
  ...extra
});

const plugin = (id: string, parameters: ParameterInfo[]): PluginInfo => ({
  id,
  name: id,
  vendor: 'Test',
  version: '1',
  format: 'builtin',
  isInstrument: false,
  fileTypes: [],
  parameters,
  category: 'Test',
  hasSidechain: false,
  hasEditor: false
});

describe('Custom plugin panels: parameter-id patterns', () => {
  it('matches the fake catalogue’s four plugins, and nothing else', () => {
    expect(matchPanel(fake('anode.organ'))?.kind).toBe('drawbars');
    expect(matchPanel(fake('anode.fm'))?.kind).toBe('fmAlgorithm');
    expect(matchPanel(fake('anode.delay-designer'))?.kind).toBe('delayTaps');
    expect(matchPanel(fake('anode.multipressor'))?.kind).toBe('multipressorBands');
    const panelled = ['anode.organ', 'anode.fm', 'anode.delay-designer', 'anode.multipressor'];
    for (const other of [...FakeT2Instruments, ...FakeT2Effects, ...FakeMidiEffects]) {
      if (!panelled.includes(other.id)) {
        expect(pluginPanel(other)).toBeNull();
      }
    }
    expect(pluginPanel(null)).toBeNull();
    expect(pluginPanel(fake('anode.organ'))).toBe(DrawbarsPanel);
  });

  it('binds the roles: nine drawbars and the extras; four operators; four taps and a count; four bands, three crossovers', () => {
    const organ = matchPanel(fake('anode.organ'))?.binding;
    expect(organ?.series['drawbar']?.map(p => p.id)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9].map(n => `drawbar${n}`));
    expect(organ?.roles['rotarySpeed']?.id).toBe('rotarySpeed');
    expect(organ?.roles['scanner']?.id).toBe('vibratoChorus');
    const fm = matchPanel(fake('anode.fm'))?.binding;
    expect(fm?.roles['algorithm']?.id).toBe('algorithm');
    expect(fm?.series['ratio']?.map(p => p.id)).toEqual(['op1Ratio', 'op2Ratio', 'op3Ratio', 'op4Ratio']);
    const delay = matchPanel(fake('anode.delay-designer'))?.binding;
    expect(delay?.roles['count']?.id).toBe('taps');
    expect(delay?.series['pan']).toHaveLength(4);
    const bands = matchPanel(fake('anode.multipressor'))?.binding;
    expect(bands?.series['crossover']?.map(p => p.id)).toEqual(['cross1', 'cross2', 'cross3']);
    expect(bands?.series['enabled']).toHaveLength(4);
  });

  it('accepts reasonable alternative ids', () => {
    const bars = Array.from({ length: 9 }, (_, i) => parameter(`drawbar.${i + 1}`, 0, 8));
    expect(matchPanel(plugin('organ', bars))?.kind).toBe('drawbars');
    const ops = [1, 2].flatMap(n => [parameter(`operator${n}.ratio`, 0.5, 16), parameter(`operator${n}_level`)]);
    expect(matchPanel(plugin('fm', [parameter('fm.algo', 0, 7, { steps: 7 }), ...ops]))?.kind).toBe('fmAlgorithm');
    expect(
      matchPanel(plugin('delay', [parameter('tap_1_delay', 0, 2000, { unit: 'ms' }), parameter('tap_1_gain', -60, 0)]))
        ?.kind
    ).toBe('delayTaps');
    const bands = [1, 2].flatMap(n => [
      parameter(`band_${n}_thresh`, -60, 0),
      parameter(`band_${n}_ratio`, 1, 20),
      parameter(`band_${n}_makeup`, -24, 24)
    ]);
    expect(matchPanel(plugin('mb', [...bands, parameter('xover1', 20, 20_000)]))?.kind).toBe('multipressorBands');
  });

  it('rejects partial matches, so the generic editor shows', () => {
    const eight = Array.from({ length: 8 }, (_, i) => parameter(`drawbar${i + 1}`, 0, 8));
    expect(matchPanel(plugin('organ', eight))).toBeNull();
    // A gap: drawbar 5 missing.
    const gap = Array.from({ length: 9 }, (_, i) => parameter(`drawbar${i === 4 ? 10 : i + 1}`, 0, 8));
    expect(matchPanel(plugin('organ', gap))).toBeNull();
    // An algorithm with too few values, or one operator.
    const ops = [1, 2].flatMap(n => [parameter(`op${n}.ratio`), parameter(`op${n}.level`)]);
    expect(matchPanel(plugin('fm', [parameter('algorithm', 0, 3, { steps: 3 }), ...ops]))).toBeNull();
    expect(
      matchPanel(
        plugin('fm', [parameter('algorithm', 1, 8, { steps: 7 }), parameter('op1.ratio'), parameter('op1.level')])
      )
    ).toBeNull();
    // Taps without levels; bands without their crossovers.
    expect(matchPanel(plugin('delay', [parameter('tap1.time'), parameter('tap1.pan')]))).toBeNull();
    const bands = [1, 2, 3].flatMap(n => [
      parameter(`band${n}.threshold`),
      parameter(`band${n}.ratio`),
      parameter(`band${n}.gain`)
    ]);
    expect(matchPanel(plugin('mb', [...bands, parameter('crossover1')]))).toBeNull();
  });
});

/** A host over plain values, recording what a panel sets. */
function testHost(info: PluginInfo) {
  const values = signal<Record<string, number>>(Object.fromEntries(info.parameters.map(p => [p.id, p.default])));
  const sets: [string, number][] = [];
  const host: PluginPanelHost = {
    plugin: info,
    value: id => values()[id] ?? 0,
    set: (id, value) => {
      sets.push([id, value]);
      values.update(v => ({ ...v, [id]: value }));
    }
  };
  return { host, sets, values };
}

async function render<T>(panel: Type<T>, info: PluginInfo) {
  const { host, sets, values } = testHost(info);
  const fixture = TestBed.createComponent(panel);
  fixture.componentRef.setInput('host', host);
  fixture.autoDetectChanges();
  await fixture.whenStable();
  const element = fixture.nativeElement as HTMLElement;
  const press = async (target: Element | null | undefined, key: string) => {
    target?.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
    await fixture.whenStable();
  };
  return { element, sets, values, press, fixture };
}

describe('Custom plugin panels: value plumbing', () => {
  it('converts drawbar settings for 0…8 and 0…1 ranges', () => {
    expect(drawbarSetting(parameter('a', 0, 8), 6)).toBe(6);
    expect(drawbarSetting(parameter('a', 0, 1), 0.5)).toBe(4);
    expect(drawbarValue(parameter('a', 0, 1), 2)).toBe(0.25);
    expect(drawbarValue(parameter('a', 0, 8), 9)).toBe(8);
  });

  it('drawbars: nine sliders by footage, set through the host', async () => {
    const { element, sets, press } = await render(DrawbarsPanel, fake('anode.organ'));
    const bars = element.querySelectorAll('an-drawbar [role="slider"]');
    expect(bars).toHaveLength(9);
    expect(element.querySelector(`#${bars[1]?.getAttribute('aria-labelledby') ?? ''}`)?.textContent).toBe('5⅓′');
    await press(bars[3], 'ArrowUp');
    expect(sets).toEqual([['drawbar4', 1]]);
    expect(bars[3]?.getAttribute('aria-valuenow')).toBe('1');
    expect(element.querySelector('[data-parameter="rotarySpeed"]')).not.toBeNull();
  });

  it('FM: picks an algorithm from the radio group, and shows a row per operator', async () => {
    const { element, sets, press } = await render(FmAlgorithmPanel, fake('anode.fm'));
    const radios = element.querySelectorAll<HTMLButtonElement>('[role="radio"]');
    expect(radios).toHaveLength(8);
    expect(radios[0]?.getAttribute('aria-checked')).toBe('true');
    radios[4]?.click();
    expect(sets).toEqual([['algorithm', 4]]);
    await press(radios[4], 'ArrowRight');
    expect(sets.at(-1)).toEqual(['algorithm', 5]);
    expect(element.querySelector('[aria-label="Algorithm 6"]')?.getAttribute('aria-checked')).toBe('true');
    expect(element.querySelectorAll('[role="listitem"]')).toHaveLength(4);
    expect(element.querySelector('[data-parameter="op3Level"]')).not.toBeNull();
  });

  it('Delay Designer: taps as sliders (level ↑, time →), add and remove taps by the count', async () => {
    expect(tapSpan(0.5)).toBe(1);
    expect(tapSpan(0)).toBe(0.5);
    expect(tapsShown(undefined, 0, 4)).toBe(4);
    expect(tapsShown(parameter('taps', 1, 26), 9, 4)).toBe(4);
    const { element, sets, press, values } = await render(DelayTapsPanel, fake('anode.delay-designer'));
    const taps = () => element.querySelectorAll('[data-testid="delay-tap"]');
    expect(taps()).toHaveLength(4);
    await press(taps()[0], 'ArrowUp');
    expect(sets.at(-1)?.[0]).toBe('tapALevel');
    await press(taps()[1], 'ArrowRight');
    expect(sets.at(-1)).toEqual(['tapBTime', 0.26]);
    element.querySelector<HTMLButtonElement>('[data-testid="delay-remove-tap"]')?.click();
    expect(values()['taps']).toBe(3);
    await press(null, '');
    expect(taps()).toHaveLength(3);
    // Four tap parameter sets: no more than four taps.
    element.querySelector<HTMLButtonElement>('[data-testid="delay-add-tap"]')?.click();
    await press(null, '');
    expect(element.querySelector<HTMLButtonElement>('[data-testid="delay-add-tap"]')?.disabled).toBe(true);
  });

  it('Multipressor: band columns with bypass, crossovers kept in order on a log axis', async () => {
    expect(axisPosition(20)).toBe(0);
    expect(axisFrequency(1)).toBeCloseTo(20_000);
    expect(axisFrequency(axisPosition(632))).toBeCloseTo(632);
    const { element, sets, press, values } = await render(MultipressorPanel, fake('anode.multipressor'));
    expect(element.querySelectorAll('[data-testid="multipressor-band"]')).toHaveLength(4);
    const bypass = element.querySelector<HTMLButtonElement>('[aria-label="Bypass band 2"]');
    bypass?.click();
    expect(sets).toEqual([['band2On', 0]]);
    await press(null, '');
    expect(bypass?.getAttribute('aria-pressed')).toBe('true');
    const crossovers = element.querySelectorAll('[data-testid="crossover"]');
    expect(crossovers).toHaveLength(3);
    await press(crossovers[0], 'ArrowRight');
    expect(sets.at(-1)?.[0]).toBe('cross1');
    expect(values()['cross1']).toBeGreaterThan(150);
    // Page up many times: it stops below the next crossover.
    for (let i = 0; i < 20; i++) {
      await press(crossovers[0], 'PageUp');
    }
    expect(values()['cross1']).toBeLessThan(values()['cross2'] ?? 0);
  });
});
