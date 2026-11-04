import type { EngineStatus } from '@anode/api-client';
import type { AudioApplied, AudioBridge, AudioDevice, AudioSettings } from '../desktop';

/** A device the fake lists that cannot be opened, as one unplugged after it was listed. */
export const UnpluggedDevice = 'fake:unplugged';
/** A device with a limited range: at most 96 kHz, and it settles on 64 samples below that. */
export const InterfaceDevice = 'fake:interface';
const InterfaceMaxRate = 96_000;
const InterfaceMinBuffer = 64;

const Devices: readonly AudioDevice[] = [
  { uid: 'fake:builtin-out', name: 'Built-in Output', inputChannels: 0, outputChannels: 2, sampleRate: 48_000 },
  { uid: 'fake:builtin-mic', name: 'Built-in Microphone', inputChannels: 1, outputChannels: 0, sampleRate: 48_000 },
  { uid: InterfaceDevice, name: 'Fake Interface', inputChannels: 8, outputChannels: 8, sampleRate: 48_000 },
  { uid: UnpluggedDevice, name: 'Unplugged Interface', inputChannels: 2, outputChannels: 2, sampleRate: 48_000 }
];

const Version = '0.1.0-fake';

/**
 * The desktop app's audio bridge and the engine host behind it, for `?fake` and unit tests: switching drops the link
 * to the host as the real restart does, and a device that cannot open leaves it on the previous settings, unsaved.
 * Latencies are made up: the buffer plus a converter's worth of frames, as a driver reports them.
 */
export class FakeAudioDevices implements AudioBridge {
  private current: AudioSettings = { device: '', inputDevice: null, sampleRate: 48_000, bufferFrames: 128 };

  constructor(private readonly publish: (engine: EngineStatus) => void) {}

  /** The engine's status on the settings in use. */
  get engine(): EngineStatus {
    return this.running(this.current);
  }

  devices(): Promise<{ backend: string; devices: AudioDevice[] }> {
    return Promise.resolve({ backend: 'coreaudio', devices: structuredClone([...Devices]) });
  }

  settings(): Promise<AudioSettings> {
    return Promise.resolve({ ...this.current });
  }

  async apply(settings: AudioSettings): Promise<AudioApplied> {
    this.publish({ state: 'disconnected', hostVersion: null, backend: null, message: 'Restarting.', audio: null });
    await Promise.resolve();
    const error = this.refusal(settings);
    if (!error) {
      this.current = { ...settings };
    }

    this.publish(this.engine);
    return error ? { applied: false, error, restored: true } : { applied: true };
  }

  /** What the host would say when it cannot open `settings`, as `NativeAudioSession.Open` does. */
  private refusal({ device, inputDevice, sampleRate }: AudioSettings): string | null {
    if (device === UnpluggedDevice || inputDevice === UnpluggedDevice) {
      return 'Could not open the audio device: audio device not found (OSStatus 0).';
    }

    return device === InterfaceDevice && sampleRate > InterfaceMaxRate
      ? 'Could not open the audio device: could not set the sample rate (OSStatus 560226676).'
      : null;
  }

  private running({ device, inputDevice, sampleRate, bufferFrames }: AudioSettings): EngineStatus {
    if (device === null) {
      return this.status('null', {
        sampleRate,
        bufferFrames,
        inputChannels: 16,
        outputChannels: 16,
        inputLatencyFrames: 0,
        outputLatencyFrames: 0
      });
    }

    const frames = device === InterfaceDevice ? Math.max(bufferFrames, InterfaceMinBuffer) : bufferFrames;
    const inputChannels = inputDevice === '' ? 0 : device === InterfaceDevice ? 8 : 2;
    return this.status('coreaudio', {
      sampleRate,
      bufferFrames: frames,
      inputChannels,
      outputChannels: device === InterfaceDevice ? 8 : 2,
      inputLatencyFrames: inputChannels > 0 ? frames + 44 : 0,
      outputLatencyFrames: frames + 64
    });
  }

  private status(backend: string, audio: EngineStatus['audio']): EngineStatus {
    return { state: 'inprocess', backend, hostVersion: Version, message: null, audio };
  }
}
