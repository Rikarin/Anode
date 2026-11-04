import { type Type, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { PluginInfo } from '@anode/api-client';
import { FakeT3Effects, FakeT3Instruments } from '../core/fake/fake-plugins-phase11';
import { FakeMidiEffects, FakeT2Effects, FakeT2Instruments } from '../core/fake/fake-plugins-phase6';
import { matchPanel } from './panel-patterns';
import { PhysicalModelPanel, placeAt, placePercent, stepPosition } from './physical-model-panel';
import { type PluginPanelHost, deferredPanel, pluginPanel } from './plugin-panel';
import { grainWindow, partialBars, SampleAlchemyPanel, sourceOverview } from './sample-alchemy-panel';

const t3 = (id: string): PluginInfo => {
  const found = [...FakeT3Instruments, ...FakeT3Effects].find(p => p.id === id);
  if (!found) {
    throw new Error(`no ${id}`);
  }
  return found;
};

/** A host over plain values, recording what a panel sets. */
async function render<T>(panel: Type<T>, info: PluginInfo) {
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
  const fixture = TestBed.createComponent(panel);
  fixture.componentRef.setInput('host', host);
  fixture.autoDetectChanges();
  await fixture.whenStable();
  const element = fixture.nativeElement as HTMLElement;
  const press = async (target: Element | null | undefined, key: string) => {
    target?.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
    await fixture.whenStable();
  };
  return { element, sets, values, press };
}

describe('Custom plugin panels: the T3 instruments (Phase 11)', () => {
  it('matches the physical model and Sample Alchemy as deferred panels; the T3 effects get the generic editor', () => {
    expect(deferredPanel(t3('anode.physical-model'))).toBe('physicalModel');
    expect(deferredPanel(t3('anode.sample-alchemy'))).toBe('sampleAlchemy');
    expect(pluginPanel(t3('anode.physical-model'))).toBeNull();
    for (const id of ['anode.tape-replay', 'anode.saturation', 'anode.down-mixer', 'anode.binaural']) {
      expect(matchPanel(t3(id))).toBeNull();
    }
    for (const other of [...FakeT2Instruments, ...FakeT2Effects, ...FakeMidiEffects]) {
      expect(deferredPanel(other)).toBe(other.id === 'anode.linear-eq' ? 'eqCurve' : null);
    }
    const bound = matchPanel(t3('anode.physical-model'))?.binding;
    expect(bound?.series['exciterPosition']).toHaveLength(3);
    expect(bound?.series['pickupPosition']?.map(p => p.id)).toEqual(['pickup1Position', 'pickup2Position']);
  });

  it('places markers along the object and steps them from the keyboard', () => {
    expect(placeAt(55, { left: 0, width: 100 })).toBe(0.556);
    expect(placeAt(0, { left: 0, width: 100 })).toBe(0);
    expect(placePercent(1)).toBe(95);
    expect(stepPosition(0.5, 'ArrowRight', false)).toBe(0.51);
    expect(stepPosition(0.5, 'ArrowLeft', true)).toBe(0.499);
    expect(stepPosition(0.95, 'PageUp', false)).toBe(1);
    expect(stepPosition(0.5, 'Home', false)).toBe(0);
    expect(stepPosition(0.5, 'x', false)).toBeNull();
  });

  it('physical model: three exciters and two pickups as sliders; an exciter that is off is dimmed', async () => {
    const { element, sets, press, values } = await render(PhysicalModelPanel, t3('anode.physical-model'));
    expect(element.querySelectorAll('[data-testid="exciter"]')).toHaveLength(3);
    const pickups = element.querySelectorAll('[data-testid="pickup"]');
    expect(pickups).toHaveLength(2);
    await press(pickups[1], 'ArrowLeft');
    expect(sets.at(-1)).toEqual(['pickup2Position', 69]);
    const exciters = element.querySelectorAll('[data-testid="exciter"]');
    expect(exciters[0]?.getAttribute('aria-valuetext')).toBe('15 %, Pick');
    expect(exciters[1]?.classList).toContain('opacity-35');
    values.update(v => ({ ...v, object: 3 }));
    await press(null, '');
    expect(element.querySelector('[data-testid="physical-object"] path')).not.toBeNull();
  });

  it('Sample Alchemy: the grain window in granular mode, partials in additive', async () => {
    expect(sourceOverview()).toHaveLength(96);
    expect(sourceOverview()).toEqual(sourceOverview());
    expect(grainWindow(0, 0.4)).toEqual({ start: 5, width: 9 });
    expect(partialBars(4, 0).map(b => b.height)).toEqual([100, 50, 33.3, 25]);
    const { element, sets, press } = await render(SampleAlchemyPanel, t3('anode.sample-alchemy'));
    // The instrument's modes are Additive, Granular, Spectral, in its parameter's order.
    expect(element.querySelector('[data-testid="alchemy-position"]')).toBeNull();
    expect(element.querySelector('[data-parameter="partials"]')).not.toBeNull();
    element.querySelector<HTMLButtonElement>('[data-testid="alchemy-mode-1"]')?.click();
    expect(sets.at(-1)).toEqual(['mode', 1]);
    await press(null, '');
    await press(element.querySelector('[data-testid="alchemy-position"]'), 'ArrowRight');
    expect(sets.at(-1)).toEqual(['position', 1]);
    expect(element.querySelector('[data-parameter="grainSize"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="alchemy-mode-1"]')?.getAttribute('aria-checked')).toBe('true');
  });
});
