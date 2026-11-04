import { TestBed } from '@angular/core/testing';
import {
  type ChannelStrip,
  DefaultMonitoringSettings,
  DefaultPanner,
  StripDefaults,
  type Track
} from '@anode/api-client';
import { pannerTargets, targetsOf } from '../automation/targets';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { MixerStrip } from '../mixer/mixer-strip';
import { objectChange, surroundChange } from '../mixer/strip-panner';
import { mappingTargets } from '../smart-controls/mapping-targets';
import { MeterBus } from '../state/meters';
import { MixerStore, withPannerChange } from '../state/mixer.store';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import {
  MonitoringStore,
  downmixTargets,
  monitoringMode,
  sentFormat,
  speakerRows,
  withSpeakerOutput
} from './monitoring.store';
import { SettingsWindow } from './settings-window';
import { SurroundSettingsWindow } from './surround-settings-window';

const strip = (extra: Partial<ChannelStrip> = {}): ChannelStrip => ({
  volumeDb: 0,
  pan: 0,
  mute: false,
  solo: false,
  inserts: [],
  sends: [],
  output: 'master',
  groups: [],
  vcaId: null,
  midiEffects: [],
  inputFormat: 'mono',
  outputFormat: 'surround51',
  panner: { ...DefaultPanner, mode: 'surround' },
  ...StripDefaults,
  ...extra
});

async function voiceStrip() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Surround' }));
  await project.execute([{ type: 'addTrack', kind: 'audio', stereo: false, name: 'Voice' }], 'Track');
  const track = () => project.trackList()[0] as Track;
  const fixture = TestBed.createComponent(MixerStrip);
  fixture.componentRef.setInput('track', track());
  await fixture.whenStable();
  const refresh = async () => {
    await settle();
    fixture.componentRef.setInput('track', track());
    await fixture.whenStable();
  };
  return { server, project, track, fixture, element: fixture.nativeElement as HTMLElement, refresh };
}

describe('panner targets (automation and Smart Controls)', () => {
  it('offers the Surround Panner on a surround output and the 3D Object Panner on an object', () => {
    expect(pannerTargets(strip({ outputFormat: 'stereo', panner: DefaultPanner }))).toEqual([]);
    const surround = pannerTargets(strip());
    expect(surround.map(t => t.target)).toEqual([
      'surround:angle',
      'surround:diversity',
      'surround:elevation',
      'surround:centre',
      'surround:lfe',
      'surround:spread'
    ]);
    const angle = surround[0];
    expect(angle?.name).toBe('Surround Angle');
    expect(angle?.format(110)).toBe('110° L');
    expect(angle?.toNormal(0)).toBe(0.5);
    expect(angle?.fromNormal(1)).toBe(180);
    expect(surround[4]?.format(-144)).toBe('−∞ dB');
    const object = pannerTargets(strip({ panner: { ...DefaultPanner, mode: 'object' } }));
    expect(object.map(t => t.target)).toEqual(['object:azimuth', 'object:elevation', 'object:distance', 'object:size']);
    expect(object[1]?.fromNormal(0)).toBe(-90);
  });

  it('lists them under Panner for tracks, and maps them as Smart Control targets', () => {
    const track = { kind: 'audio', strip: strip(), instrument: null } as const;
    expect(targetsOf(track, () => undefined).filter(t => t.group === 'Panner')).toHaveLength(6);
    // The master has no panner.
    expect(targetsOf({ strip: strip() }, () => undefined).some(t => t.group === 'Panner')).toBe(false);
    const groups = mappingTargets(
      { ...track, midiEffects: [], smartControls: null, strip: strip() } as unknown as Track,
      () => undefined
    );
    expect(groups.at(-1)?.group).toBe('Panner');
  });

  it('turns the panners’ fields into setPanner’s flat ones', () => {
    expect(surroundChange({ angle: 110, elevation: 30 })).toEqual({ angle: 110, surroundElevation: 30 });
    expect(objectChange({ elevation: 45, size: 0.2 })).toEqual({ objectElevation: 45, size: 0.2 });
    expect(withPannerChange(DefaultPanner, { angle: 90, objectElevation: 10 })).toEqual({
      ...DefaultPanner,
      surround: { ...DefaultPanner.surround, angle: 90 },
      object: { ...DefaultPanner.object, elevation: 10 }
    });
  });
});

describe('the channel strip in surround', () => {
  it('swaps the pan knob for the Surround Panner, then the 3D Object Panner, with the bed/object switch', async () => {
    const { project, track, element, refresh } = await voiceStrip();
    expect(element.querySelector('an-pan-knob')).not.toBeNull();
    expect(element.querySelector('an-strip-panner')).toBeNull();

    await project.execute([{ type: 'setSurroundFormat', format: 'surround714' }], 'Format');
    await refresh();
    expect(element.querySelector('an-pan-knob')).toBeNull();
    expect(element.querySelector('[data-testid="strip-surround-panner"]')).not.toBeNull();
    expect(element.querySelectorAll('[data-testid="multichannel-meter"] [data-channel]')).toHaveLength(12);
    expect(element.querySelector('[data-testid="input-format"]')?.textContent).toMatch(/2\.0\s*→\s*7\.1\.4/);

    // A puck drag goes out as flat setPanner fields, coalesced into one undo step.
    const mixer = TestBed.inject(MixerStore);
    void mixer.setPanner(track().id, { angle: 100 });
    await mixer.setPanner(track().id, { angle: 110, diversity: 0.9 });
    expect(mixer.panner(track().id)?.surround.angle).toBe(110);
    await refresh();
    mixer.releasePanner(track().id);
    expect(track().strip.panner.surround).toMatchObject({ angle: 110, diversity: 0.9 });

    (element.querySelector('[data-testid="mode-object"]') as HTMLButtonElement).click();
    await refresh();
    expect(track().strip.panner.mode).toBe('object');
    expect(element.querySelector('[data-testid="strip-object-panner"]')).not.toBeNull();

    // Routed away from the master, it cannot be an object: the switch is disabled and the strip pans in stereo.
    await project.execute([{ type: 'setOutput', trackId: track().id, output: 'output:1' }], 'Output');
    await refresh();
    expect(element.querySelector('an-pan-knob')).not.toBeNull();
  });

  it('opens the full panner in a window and disables the object switch where objects cannot go', async () => {
    const { project, track, element, fixture, refresh } = await voiceStrip();
    await project.execute([{ type: 'setSurroundFormat', format: 'surround51' }], 'Format');
    await project.execute([{ type: 'addAux', bus: 2 }], 'Aux');
    const aux = project.trackList().find(t => t.kind === 'aux') as Track;
    await project.execute([{ type: 'setStripFormat', trackId: aux.id, inputFormat: 'surround51' }], 'Aux format');
    await project.execute([{ type: 'setOutput', trackId: track().id, output: 'bus:2' }], 'Output');
    await refresh();
    const object = element.querySelector('[data-testid="mode-object"]') as HTMLButtonElement;
    expect(object.disabled).toBe(true);
    (element.querySelector('[aria-label^="Open Voice Surround Panner"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(element.querySelector('[data-testid="panner-window"]')).not.toBeNull();
    expect(element.querySelectorAll('[data-testid="panner-window"] an-knob').length).toBeGreaterThanOrEqual(2);
  });

  it('meters every speaker from the Server’s peaks, falling back to left and right', () => {
    setUpFakeServer();
    const meters = TestBed.inject(MeterBus);
    meters.apply([{ trackId: 't1', peakLeft: 0.5, peakRight: 0.4, peaks: [0.5, 0.4, 0.3, 0, 0.2, 0.1] }], {
      trackId: '',
      peakLeft: 0.2,
      peakRight: 0.1
    });
    expect(meters.channelLevels('t1', 6)).toEqual([0.5, 0.4, 0.3, 0, 0.2, 0.1]);
    expect(meters.channelLevels('master', 4)).toEqual([0.2, 0.1, 0, 0]);
  });
});

describe('monitoring', () => {
  it('works out modes, down-mix targets and the speaker map rows', () => {
    expect(monitoringMode(DefaultMonitoringSettings)).toBe('speakers');
    expect(monitoringMode({ binaural: false, downmixTo: 'stereo', speakerMap: [] })).toBe('downmix');
    expect(downmixTargets('surround51').map(t => t.format)).toEqual(['stereo', 'lcr', 'quad', 'surround50']);
    expect(downmixTargets('stereo')).toEqual([]);
    expect(sentFormat({ binaural: true, downmixTo: null, speakerMap: [] }, 'surround714')).toBe('stereo');
    const rows = speakerRows({ binaural: false, downmixTo: null, speakerMap: [3, 4] }, 'surround51');
    expect(rows.map(r => [r.short, r.output])).toEqual([
      ['L', 3],
      ['R', 4],
      ['C', 3],
      ['LFE', 4],
      ['Ls', 5],
      ['Rs', 6]
    ]);
    expect(withSpeakerOutput(rows, 3, 0)).toEqual([3, 4, 3, 0, 5, 6]);
    expect(speakerRows({ binaural: true, downmixTo: null, speakerMap: [] }, 'surround51')).toEqual([]);
  });

  it('loads and saves the user’s monitoring through the Server', async () => {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Mon' }));
    await project.execute([{ type: 'setSurroundFormat', format: 'surround51' }], 'Format');
    await settle();
    const store = TestBed.inject(MonitoringStore);
    await store.load();
    await store.setMode('downmix');
    expect(await server.monitoring()).toEqual({ binaural: false, downmixTo: 'stereo', speakerMap: [] });
    await store.setSpeakerOutput(1, 0);
    expect((await server.monitoring()).speakerMap).toEqual([1, 0]);
    await store.setMode('binaural');
    expect((await server.monitoring()).binaural).toBe(true);
  });
});

describe('the settings windows', () => {
  it('sets the project format from Project Settings ▸ Surround', async () => {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Fmt' }));
    TestBed.inject(UiStore).surroundSettingsOpen.set(true);
    const fixture = TestBed.createComponent(SurroundSettingsWindow);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-format="stereo"]')?.getAttribute('aria-checked')).toBe('true');
    (element.querySelector('[data-format="surround714"]') as HTMLButtonElement).click();
    await settle();
    await fixture.whenStable();
    expect(project.settings().surroundFormat).toBe('surround714');
    expect(element.querySelector('[data-format="surround714"]')?.getAttribute('aria-checked')).toBe('true');
  });

  it('shows monitoring with the speaker map in Settings', async () => {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Set' }));
    await project.execute([{ type: 'setSurroundFormat', format: 'surround51' }], 'Format');
    const fixture = TestBed.createComponent(SettingsWindow);
    await settle();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelectorAll('[data-testid="speaker-map"] tbody tr')).toHaveLength(6);
    (element.querySelector('[data-testid="monitoring-binaural"]') as HTMLButtonElement).click();
    await settle();
    await fixture.whenStable();
    expect(element.querySelector('[data-testid="speaker-map"]')).toBeNull();
    expect(element.querySelector('[data-testid="monitoring-note"]')?.textContent).toContain('headphones');
  });
});
