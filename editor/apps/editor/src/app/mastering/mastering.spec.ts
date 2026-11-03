import { TestBed } from '@angular/core/testing';
import { DefaultMasteringSettings, MasteringParameters, type ProjectSettings } from '@anode/api-client';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { PluginWindows } from '../mixer/plugin-windows';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import {
  analysisRange,
  analysisRows,
  bypassGainDb,
  masteringSettingsOf,
  readingText,
  signedDb
} from './mastering-math';
import { MasteringWindow } from './mastering-window';
import { MasteringStore } from './mastering.store';

describe('Mastering Assistant math', () => {
  it('reads the settings from the insert parameters, defaults for missing ones', () => {
    const insert = {
      id: 'ins_1',
      pluginId: MasteringParameters.pluginId,
      name: 'Mastering Assistant',
      bypassed: false,
      parameters: { character: 2, targetLufs: -9, matchedBypass: 1 },
      mastering: null
    };
    expect(masteringSettingsOf(insert)).toEqual({
      ...DefaultMasteringSettings,
      character: 'punch',
      targetLufs: -9,
      matchedBypass: true
    });
  });

  it('matches the bypass to the target, and formats signed dB and readings', () => {
    expect(bypassGainDb(null, DefaultMasteringSettings)).toBeNull();
    const analysis = { integratedLufs: -20.25 } as Parameters<typeof bypassGainDb>[0];
    expect(bypassGainDb(analysis, DefaultMasteringSettings)).toBe(6.3);
    expect(signedDb(6.25)).toBe('+6.3 dB');
    expect(signedDb(-1.5)).toBe('−1.5 dB');
    expect(signedDb(0)).toBe('0.0 dB');
    expect(signedDb(null)).toBe('—');
    expect(readingText(-14, 'LUFS')).toBe('−14.0 LUFS');
    expect(readingText(-0.01, 'dB')).toBe('0.0 dB');
    expect(analysisRows(null).every(r => r.value === '—')).toBe(true);
  });

  it('analyses the whole song or the cycle area', () => {
    const settings = { endTick: 30_720, cycle: { enabled: true, startTick: 3840, endTick: 7680 } } as ProjectSettings;
    expect(analysisRange('cycle', settings)).toEqual({ startTick: 3840, endTick: 7680 });
    expect(analysisRange('song', settings)).toEqual({ startTick: 0, endTick: 30_720 });
    expect(analysisRange('cycle', { ...settings, cycle: { ...settings.cycle, enabled: false } })).toEqual({
      startTick: 0,
      endTick: 30_720
    });
  });
});

describe('the Mastering Assistant window', () => {
  async function open() {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Master' }));
    await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: true }], 'Track');
    const fixture = TestBed.createComponent(MasteringWindow);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const refresh = async () => {
      await settle();
      await fixture.whenStable();
    };
    return { server, project, element, refresh, store: TestBed.inject(MasteringStore) };
  }

  it('analyses (adding the assistant), shows the analysis, and sets the character and knobs', async () => {
    const { server, element, refresh, store } = await open();
    expect(element.querySelector('[data-testid="mastering-empty"]')).not.toBeNull();
    element.querySelector<HTMLButtonElement>('[data-testid="mastering-analyse"]')?.click();
    await refresh();
    expect(server.current?.master.inserts.at(-1)?.pluginId).toBe(MasteringParameters.pluginId);
    expect(element.querySelector('[role="progressbar"]')).not.toBeNull();
    server.runJobs();
    await refresh();
    expect(store.analysis()?.revision).toBe(1);
    expect(element.querySelector('an-spectrum-graph svg')?.getAttribute('aria-label')).toContain('Clean curve');
    expect(element.querySelector('[data-testid="mastering-analysis"]')?.textContent).toContain('LUFS');
    expect(element.querySelector('[data-testid="mastering-bypass-gain"]')?.textContent).toContain('Bypass at');

    element.querySelector<HTMLButtonElement>('[data-testid="mastering-character-valve"]')?.click();
    await refresh();
    expect(server.current?.master.inserts.at(-1)?.parameters['character']).toBe(1);
    const valve = element.querySelector<HTMLElement>('[data-testid="mastering-character-valve"]');
    expect(valve?.getAttribute('aria-checked')).toBe('true');
    valve?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    await refresh();
    expect(store.settings()?.character).toBe('punch');

    const target = element.querySelector('[data-parameter="targetLufs"] [role="slider"]');
    target?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true }));
    await refresh();
    expect(store.settings()?.targetLufs).toBeGreaterThan(-14);

    element.querySelector<HTMLButtonElement>('[data-testid="mastering-bypass"]')?.click();
    await refresh();
    expect(server.current?.master.inserts.at(-1)?.parameters['matchedBypass']).toBe(1);
  });

  it('shows the live loudness and gain reduction from the mastering event', async () => {
    const { server, element, refresh } = await open();
    await server.analyseMastering({ add: true });
    server.runJobs();
    await refresh();
    await server.controlTransport({ command: 'play' });
    server.advance();
    await refresh();
    const meter = element.querySelector('[data-testid="mastering-meters"] [aria-label="Integrated"]');
    expect(meter?.getAttribute('aria-valuetext')).toBe('−14.0 LUFS');
    await server.controlTransport({ command: 'stop' });
  });

  it('opens from the master’s assistant slot in place of the generic editor', async () => {
    const { server, project, refresh } = await open();
    await project.execute([{ type: 'addInsert', pluginId: MasteringParameters.pluginId }], 'Insert');
    await refresh();
    const insert = server.current?.master.inserts.at(-1);
    const ui = TestBed.inject(UiStore);
    if (insert) {
      await TestBed.inject(PluginWindows).open(null, insert);
    }
    expect(ui.masteringOpen()).toBe(true);
    expect(ui.pluginWindows()).toEqual([]);
  });
});
