import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { InterfaceDevice, UnpluggedDevice } from '../core/fake/fake-audio-devices';
import type { FakeAnodeServer } from '../core/fake/fake-server';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { Startup } from '../core/startup';
import { SystemStore } from '../state/system.store';
import { TransportStore } from '../state/transport.store';
import { AudioDeviceSettings } from './audio-device-settings';
import { AUDIO_BRIDGE, AudioDeviceStore, latencyMs, pairsDevices, rateLabel } from './audio-device.store';

describe('audio device helpers', () => {
  it('says rates and latencies as the settings show them', () => {
    expect(rateLabel(44_100)).toBe('44.1 kHz');
    expect(rateLabel(48_000)).toBe('48 kHz');
    expect(latencyMs(192, 48_000)).toBe('4.0 ms');
  });

  it('knows which backends take inputs from another device', () => {
    expect(pairsDevices('coreaudio', '')).toBe(true);
    expect(pairsDevices('wasapi', 'output')).toBe(true);
    expect(pairsDevices('wasapi', '')).toBe(false);
    expect(pairsDevices('alsa', 'hw:0')).toBe(false);
    expect(pairsDevices('none', null)).toBe(false);
  });
});

describe('AudioDeviceStore', () => {
  let server: FakeAnodeServer;
  let store: AudioDeviceStore;

  beforeEach(async () => {
    server = setUpFakeServer();
    TestBed.inject(Startup); // what carries `engine` events to the SystemStore
    store = TestBed.inject(AudioDeviceStore);
    await store.load();
  });

  it('lists output and input devices apart', () => {
    expect(store.outputs().map(d => d.name)).toEqual(['Built-in Output', 'Fake Interface', 'Unplugged Interface']);
    expect(store.inputs().map(d => d.name)).toEqual(['Built-in Microphone', 'Fake Interface', 'Unplugged Interface']);
  });

  it('applies only on Apply, and then the engine reports the new stream', async () => {
    store.setOutput(InterfaceDevice);
    store.setSampleRate(44_100);
    store.setBufferFrames(256);
    expect(store.changed()).toBe(true);
    expect(store.audio()?.sampleRate).toBe(48_000);

    await store.apply();
    await settle();

    expect(store.error()).toBeNull();
    expect(store.changed()).toBe(false);
    expect(store.audio()).toEqual({
      sampleRate: 44_100,
      bufferFrames: 256,
      inputChannels: 8,
      outputChannels: 8,
      inputLatencyFrames: 300,
      outputLatencyFrames: 320
    });
    expect((await server.audioDevices.settings()).device).toBe(InterfaceDevice);
  });

  it('keeps the engine and the saved settings when a device cannot open', async () => {
    const before = store.audio();
    store.setOutput(UnpluggedDevice);

    await store.apply();
    await settle();

    expect(store.error()).toContain('audio device not found');
    expect(store.error()).toContain('back on the previous settings');
    expect(store.audio()).toEqual(before);
    expect(TestBed.inject(SystemStore).engineState()).toBe('inprocess');
    expect((await server.audioDevices.settings()).device).toBe('');
    // The draft stays, to change or revert.
    expect(store.changed()).toBe(true);
    store.revert();
    expect(store.changed()).toBe(false);
    expect(store.error()).toBeNull();
  });

  it('reports a rate the device refuses as it reports a device that cannot open', async () => {
    store.setOutput(InterfaceDevice);
    store.setSampleRate(192_000);

    await store.apply();

    expect(store.error()).toContain('could not set the sample rate');
    expect((await server.audioDevices.settings()).sampleRate).toBe(48_000);
  });

  it('says when the previous settings would not start either', async () => {
    vi.spyOn(server.audioDevices, 'apply').mockResolvedValue({ applied: false, error: 'Gone.', restored: false });
    store.setBufferFrames(64);

    await store.apply();

    expect(store.error()).toBe('Gone. The previous settings would not start either, so the engine keeps trying them.');
  });

  it('survives the bridge itself failing', async () => {
    vi.spyOn(server.audioDevices, 'apply').mockRejectedValue(
      new Error("Error invoking remote method 'anode:audio:apply': Error: Broken pipe")
    );
    store.setBufferFrames(64);

    await store.apply();

    expect(store.error()).toBe('Broken pipe');
    expect(store.applying()).toBe(false);
  });

  it('notices a device that settled on another buffer', async () => {
    store.setOutput(InterfaceDevice);
    store.setBufferFrames(32);

    await store.apply();
    await settle();

    expect(store.audio()?.bufferFrames).toBe(64);
    expect(store.settled()).toBe(true);
  });

  it('shows a saved device that is not connected instead of dropping it', () => {
    store.setOutput('gone-uid');
    expect(store.outputs().at(-1)).toEqual({ uid: 'gone-uid', name: 'gone-uid (not connected)', missing: true });
  });

  it('clears the input when no device is chosen, and keeps None', () => {
    store.setInput('fake:builtin-mic');
    store.setOutput(null);
    expect(store.draft()?.inputDevice).toBeNull();

    store.setInput('');
    store.setOutput(InterfaceDevice);
    expect(store.draft()?.inputDevice).toBe('');
  });

  it('never restarts the engine while recording', async () => {
    const apply = vi.spyOn(server.audioDevices, 'apply');
    const transport = TestBed.inject(TransportStore);
    transport.state.set('recording');
    store.setBufferFrames(64);

    expect(store.canApply()).toBe(false);
    await store.apply();

    expect(apply).not.toHaveBeenCalled();
  });

  it('stops playback before it restarts the engine', async () => {
    const transport = TestBed.inject(TransportStore);
    transport.state.set('playing');
    store.setBufferFrames(64);

    await store.apply();

    expect(transport.playing()).toBe(false);
  });
});

describe('AudioDeviceSettings', () => {
  async function open(): Promise<ComponentFixture<AudioDeviceSettings>> {
    TestBed.inject(Startup); // what carries `engine` events to the SystemStore
    const fixture = TestBed.createComponent(AudioDeviceSettings);
    await settle();
    await fixture.whenStable();
    return fixture;
  }

  const element = (fixture: ComponentFixture<AudioDeviceSettings>) => fixture.nativeElement as HTMLElement;
  const testId = (fixture: ComponentFixture<AudioDeviceSettings>, id: string) =>
    element(fixture).querySelector<HTMLElement>(`[data-testid="${id}"]`);

  async function choose(fixture: ComponentFixture<AudioDeviceSettings>, id: string, value: string): Promise<void> {
    const select = testId(fixture, id) as HTMLSelectElement;
    select.value = value;
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();
  }

  it('shows the running stream and its latency', async () => {
    setUpFakeServer();
    const fixture = await open();

    expect(testId(fixture, 'audio-stream')?.textContent).toContain('48 kHz, 128 samples, 2 in / 2 out');
    // 172 and 192 frames at 48 kHz.
    expect(testId(fixture, 'audio-latency')?.textContent).toContain('3.6 ms in, 4.0 ms out, 7.6 ms round trip');
  });

  it('applies a device from the lists and shows the failure when it cannot open', async () => {
    setUpFakeServer();
    const fixture = await open();
    const apply = () => testId(fixture, 'audio-apply') as HTMLButtonElement;
    expect(apply().disabled).toBe(true);

    await choose(fixture, 'audio-output', UnpluggedDevice);
    expect(apply().disabled).toBe(false);
    apply().click();
    await settle();
    await fixture.whenStable();

    expect(testId(fixture, 'audio-error')?.textContent).toContain('audio device not found');
    expect(testId(fixture, 'audio-stream')?.textContent).toContain('48 kHz');

    await choose(fixture, 'audio-output', InterfaceDevice);
    expect(testId(fixture, 'audio-error')).toBeNull();
    apply().click();
    await settle();
    await fixture.whenStable();

    expect(testId(fixture, 'audio-error')).toBeNull();
    expect(testId(fixture, 'audio-stream')?.textContent).toContain('8 in / 8 out');
  });

  it('says there is nothing to measure without a device', async () => {
    setUpFakeServer();
    const fixture = await open();
    await choose(fixture, 'audio-output', 'anode:none');
    (testId(fixture, 'audio-apply') as HTMLButtonElement).click();
    await settle();
    await fixture.whenStable();

    expect(testId(fixture, 'audio-latency')).toBeNull();
    expect(testId(fixture, 'audio-no-latency')).not.toBeNull();
    expect((testId(fixture, 'audio-input') as HTMLSelectElement).disabled).toBe(true);
  });

  it('only shows what runs when there is no bridge, as in a browser', async () => {
    TestBed.overrideProvider(AUDIO_BRIDGE, { useValue: null });
    setUpFakeServer();
    const fixture = await open();

    expect(testId(fixture, 'audio-no-bridge')).not.toBeNull();
    expect(testId(fixture, 'audio-output')).toBeNull();
    expect(testId(fixture, 'audio-stream')).not.toBeNull();
  });

  it('is read-only on a remote Server', async () => {
    setUpFakeServer();
    const store = TestBed.inject(AudioDeviceStore);
    const fixture = await open();
    store.remote.set(true);
    await fixture.whenStable();

    expect(testId(fixture, 'audio-remote')).not.toBeNull();
    expect(testId(fixture, 'audio-apply')).toBeNull();
    expect((testId(fixture, 'audio-output') as HTMLSelectElement).disabled).toBe(true);
  });
});
