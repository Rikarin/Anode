import { TestBed } from '@angular/core/testing';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { SmartControlsPane } from './smart-controls-pane';

describe('Smart Controls pane', () => {
  async function open() {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    await server.createProject({ name: 'Smart' });
    await settle();
    await TestBed.inject(PluginsStore).refetchPlugins();
    await project.execute([{ type: 'addTrack', stereo: true, kind: 'instrument' }], 'New Track');
    const synth = project.trackIds()[0] ?? '';
    const fixture = TestBed.createComponent(SmartControlsPane);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const query = (selector: string) => element.querySelector<HTMLElement>(selector);
    const button = (selector: string) => element.querySelector<HTMLButtonElement>(selector);
    const stable = async () => {
      await settle();
      await fixture.whenStable();
    };
    return { server, project, synth, fixture, element, query, button, stable };
  }

  it('asks for a track, then shows its controls; Auto Map names the eight knobs', async () => {
    const { synth, element, query, button, stable } = await open();
    expect(element.textContent).toContain('Select a track');
    TestBed.inject(SelectionStore).track.set(synth);
    await stable();
    expect(query('[data-testid="smart-track-name"]')?.textContent.trim()).toBe('Inst 1');
    const knobs = () => [...element.querySelectorAll<HTMLElement>('[data-testid="smart-knob"]')];
    expect(knobs()).toHaveLength(8);
    expect(element.querySelectorAll('[data-testid="smart-switch"]')).toHaveLength(2);
    expect(query('[data-testid="smart-pad"]')).not.toBeNull();
    expect(button('[data-testid="smart-compare"]')?.disabled).toBe(true);
    button('[data-testid="smart-auto-map"]')?.click();
    await stable();
    expect(knobs().map(k => k.getAttribute('data-control'))).toEqual([
      'knob1',
      'knob2',
      'knob3',
      'knob4',
      'knob5',
      'knob6',
      'knob7',
      'knob8'
    ]);
    const names = knobs().map(k => k.querySelector('[id^="an-knob-"]')?.textContent.trim() ?? '');
    expect(names).toEqual(['Cutoff', 'Resonance', 'Filter Env', 'Attack', 'Release', 'LFO Rate', 'LFO Depth', 'Glide']);
  });

  it('selects a control for the inspector, renames it, and inverts a mapping', async () => {
    const { project, synth, element, query, button, stable } = await open();
    TestBed.inject(SelectionStore).track.set(synth);
    await stable();
    button('[data-testid="smart-auto-map"]')?.click();
    await stable();
    const toggle = button('[data-testid="smart-inspector-toggle"]');
    toggle?.click();
    await stable();
    expect(toggle?.getAttribute('aria-pressed')).toBe('true');
    expect(query('[data-testid="smart-inspector"]')).not.toBeNull();
    const knob2 = query('[data-testid="smart-knob"][data-control="knob2"]');
    knob2?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    await stable();
    expect(knob2?.getAttribute('aria-selected')).toBe('true');
    const name = element.querySelector<HTMLInputElement>('[data-testid="smart-control-name"]');
    expect(name?.value).toBe(project.track(synth)?.smartControls?.controls[1]?.name);
    if (name) {
      name.value = 'Bite';
      name.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    }
    await stable();
    expect(project.track(synth)?.smartControls?.controls[1]?.name).toBe('Bite');
    expect(element.querySelectorAll('[data-testid="smart-mapping"]')).toHaveLength(1);
    button('[data-testid="smart-mapping-invert"]')?.click();
    await stable();
    expect(project.track(synth)?.smartControls?.controls[1]?.mappings[0]?.invert).toBe(true);
    expect(query('[data-testid="smart-mapping-invert"]')?.getAttribute('aria-pressed')).toBe('true');
    // A graph point's key press sends the edited graph.
    const point = query('[data-testid="mapping-graph"] [role="slider"]');
    point?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await stable();
    expect(project.track(synth)?.smartControls?.controls[1]?.mappings[0]?.graph).toHaveLength(3);
  });

  it('turns a knob and compares with the saved value', async () => {
    const { project, synth, query, button, stable } = await open();
    TestBed.inject(SelectionStore).track.set(synth);
    await stable();
    button('[data-testid="smart-auto-map"]')?.click();
    await stable();
    const saved = project.track(synth)?.smartControls?.controls[0]?.value ?? 0;
    const slider = query('[data-testid="smart-knob"][data-control="knob1"] [role="slider"]');
    slider?.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
    await stable();
    expect(project.track(synth)?.smartControls?.controls[0]?.value).toBe(1);
    const compare = button('[data-testid="smart-compare"]');
    expect(compare?.disabled).toBe(false);
    compare?.click();
    await stable();
    expect(compare?.getAttribute('aria-pressed')).toBe('true');
    expect(slider?.getAttribute('aria-valuenow')).toBe(String(Math.round(saved * 1000) / 1000));
  });
});
