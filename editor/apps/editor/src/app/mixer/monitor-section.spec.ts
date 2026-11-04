import { TestBed } from '@angular/core/testing';
import type { Track } from '@anode/api-client';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { MixerStore } from '../state/mixer.store';
import { ProjectStore } from '../state/project.store';
import { MonitoringStore } from '../surround/monitoring.store';
import { MonitorSectionBar } from './monitor-section';
import { stripSummary } from './strip-settings';

describe('solo safe, the solo mode and the monitor section', () => {
  it('keeps solo safe on a strip and shows it', async () => {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Solo' }));
    await project.execute([{ type: 'addAux', name: 'Verb' }], 'Aux');
    const verb = () => project.trackList()[0] as Track;
    await TestBed.inject(MixerStore).setStripInput(verb().id, { soloSafe: true });
    expect(verb().strip.soloSafe).toBe(true);
    expect(stripSummary(verb().strip)).toBe('Safe');
    await expect(
      server.applyCommands({ commands: [{ type: 'setStrip', soloSafe: true }], label: 'x' })
    ).rejects.toThrow('solo safe');
  });

  it('keeps the solo mode as a project setting', async () => {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Solo' }));
    await TestBed.inject(MixerStore).setMixerSettings({ soloMode: 'afl' });
    expect(project.settings().mixer.soloMode).toBe('afl');
    expect(server.current?.settings.mixer.soloMode).toBe('afl');
  });

  it('presses Dim and shows the level the speakers are at', async () => {
    const server = setUpFakeServer();
    const fixture = TestBed.createComponent(MonitorSectionBar);
    await fixture.whenStable();
    await settle();
    const element = fixture.nativeElement as HTMLElement;
    const level = () => element.querySelector('[data-testid="monitor-level"]')?.textContent.trim();
    expect(level()).toBe('0 dB');

    element.querySelector<HTMLElement>('[data-testid="monitor-dim"]')?.click();
    await settle();
    await fixture.whenStable();
    expect(level()).toBe('-20 dB');
    expect((await server.monitoring()).section?.dim).toBe(true);

    await TestBed.inject(MonitoringStore).setSection({ reference: true, dim: false, referenceDb: -6 });
    await fixture.whenStable();
    expect(level()).toBe('-6 dB');
  });

  it('keeps the section when the monitoring mode changes, and refuses levels out of range', async () => {
    const server = setUpFakeServer();
    const monitoring = TestBed.inject(MonitoringStore);
    await monitoring.setSection({ mono: true });
    await monitoring.setMode('speakers');
    expect(monitoring.section().mono).toBe(true);
    await expect(
      server.setMonitoring({
        binaural: false,
        downmixTo: null,
        speakerMap: [],
        section: { ...monitoring.section(), dimDb: 3 }
      })
    ).rejects.toThrow('dim');
  });
});
