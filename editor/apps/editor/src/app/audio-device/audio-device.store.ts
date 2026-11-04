import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { ConnectionService } from '../core/connection';
import type { AudioBridge, AudioDevice, AudioSettings } from '../core/desktop';
import { FAKE_SERVER } from '../core/fake/fake-token';
import { failure } from '../core/startup';
import { SystemStore } from '../state/system.store';
import { TransportStore } from '../state/transport.store';

/**
 * Who sets the audio device: the desktop app, which starts the engine host on it, or the fake Server. Null in a plain
 * browser, which can see the engine's stream but not the devices of the computer it runs on.
 */
export const AUDIO_BRIDGE = new InjectionToken<AudioBridge | null>('AudioBridge', {
  providedIn: 'root',
  factory: () =>
    inject(ConnectionService).desktop?.audio ?? inject(FAKE_SERVER, { optional: true })?.audioDevices ?? null
});

/** The rates and buffer sizes on offer, as the desktop app's main process accepts them (`validAudio`). */
export const SampleRates: readonly number[] = [44_100, 48_000, 88_200, 96_000, 176_400, 192_000];
export const BufferSizes: readonly number[] = [32, 64, 128, 256, 512, 1024];

/** A rate as it is said: 44.1 kHz. */
export function rateLabel(rate: number): string {
  return `${rate / 1000} kHz`;
}

/** Frames at `rate` in milliseconds, to a tenth. */
export function latencyMs(frames: number, rate: number): string {
  return `${((frames / rate) * 1000).toFixed(1)} ms`;
}

/**
 * Whether the backend can take its inputs from another device than its outputs (`<output uid>|<input uid>`): CoreAudio
 * and WASAPI can, ALSA and JACK cannot, and WASAPI needs the output named rather than the system's default.
 */
export function pairsDevices(backend: string, output: string | null): boolean {
  return backend === 'coreaudio' || (backend === 'wasapi' && !!output);
}

/** A device to choose: `missing` when the settings name one that is not connected now. */
export interface DeviceChoice {
  uid: string;
  name: string;
  missing: boolean;
}

function choices(devices: readonly AudioDevice[], chosen: string | null): DeviceChoice[] {
  const listed = devices.map(({ uid, name }) => ({ uid, name: name || uid, missing: false }));
  return chosen && !devices.some(device => device.uid === chosen)
    ? [...listed, { uid: chosen, name: `${chosen} (not connected)`, missing: true }]
    : listed;
}

/**
 * Settings ▸ Audio ▸ Devices: the devices the host can open, the settings being edited, and the switch. Choosing
 * changes only the draft; Apply restarts the engine host on it, which stops playback, and the desktop app puts the
 * previous settings back when that fails. What runs is `SystemStore`'s `engine.audio`, as the device reports it.
 */
@Injectable({ providedIn: 'root' })
export class AudioDeviceStore {
  private readonly bridge = inject(AUDIO_BRIDGE);
  private readonly connection = inject(ConnectionService);
  private readonly system = inject(SystemStore);
  private readonly transport = inject(TransportStore);

  readonly available = this.bridge !== null;
  private readonly saved = signal<AudioSettings | null>(null);
  readonly draft = signal<AudioSettings | null>(null);
  readonly backend = signal('none');
  readonly devices = signal<AudioDevice[]>([]);
  readonly applying = signal(false);
  readonly error = signal<string | null>(null);
  /** The desktop app is on a remote Server: its engine is that computer's, not this one's. */
  readonly remote = signal(false);

  readonly engine = computed(() => this.system.info()?.engine ?? null);
  readonly audio = computed(() => this.engine()?.audio ?? null);
  readonly recording = this.transport.recording;
  readonly changed = computed(() => {
    const [draft, saved] = [this.draft(), this.saved()];
    return !!draft && !!saved && JSON.stringify(draft) !== JSON.stringify(saved);
  });

  readonly canApply = computed(() => this.changed() && !this.applying() && !this.recording() && !this.remote());
  /** Whether an input device other than the output's own can be chosen. */
  readonly pairs = computed(() => pairsDevices(this.backend(), this.draft()?.device ?? null));
  readonly outputs = computed(() =>
    choices(
      this.devices().filter(device => device.outputChannels > 0),
      this.draft()?.device ?? null
    )
  );

  readonly inputs = computed(() =>
    this.pairs() || this.draft()?.inputDevice
      ? choices(
          this.devices().filter(device => device.inputChannels > 0),
          this.draft()?.inputDevice ?? null
        )
      : []
  );

  /** The device settled on other values than the saved ones (a buffer below its minimum, say). */
  readonly settled = computed(() => {
    const [audio, saved] = [this.audio(), this.saved()];
    return !!audio && !!saved && !this.applying() && audio.bufferFrames !== saved.bufferFrames;
  });

  /** Lists the devices and reads the settings; called when Settings opens, so a device plugged in since shows up. */
  async load(): Promise<void> {
    await this.system.refresh().catch(() => undefined);
    if (!this.bridge) {
      return;
    }

    try {
      const [listing, settings, target] = await Promise.all([
        this.bridge.devices(),
        this.bridge.settings(),
        this.connection.desktop?.target()
      ]);
      this.backend.set(listing.backend);
      this.devices.set(listing.devices);
      this.saved.set(settings);
      this.draft.set(settings);
      this.remote.set(target?.mode === 'remote');
    } catch (error) {
      this.error.set(failure(error));
    }
  }

  /** Chooses the output: a uid, '' for the system's default, or null for no device. */
  setOutput(device: string | null): void {
    this.edit(draft => {
      const inputDevice = device !== null && (draft.inputDevice === '' || pairsDevices(this.backend(), device));
      return { ...draft, device, inputDevice: inputDevice ? draft.inputDevice : null };
    });
  }

  /** Chooses the input: a uid, '' for none, or null for the output device's own. */
  setInput(inputDevice: string | null): void {
    this.edit(draft => ({ ...draft, inputDevice }));
  }

  setSampleRate(sampleRate: number): void {
    this.edit(draft => ({ ...draft, sampleRate }));
  }

  setBufferFrames(bufferFrames: number): void {
    this.edit(draft => ({ ...draft, bufferFrames }));
  }

  /** Back to the settings in use. */
  revert(): void {
    this.draft.set(this.saved());
    this.error.set(null);
  }

  /**
   * Switches to the draft. Stops the transport first, so the restart does not cut playback off behind the Server's
   * back; never while recording (the button is off then), which would lose the take. A failure keeps the draft on
   * screen with the reason beside it, and the engine on the previous settings.
   */
  async apply(): Promise<void> {
    const draft = this.draft();
    if (!draft || !this.bridge || !this.canApply()) {
      return;
    }

    if (this.transport.playing()) {
      this.transport.stop();
    }

    this.applying.set(true);
    this.error.set(null);
    try {
      const result = await this.bridge.apply(draft);
      if (result.applied) {
        this.saved.set(draft);
      } else {
        this.error.set(
          result.restored
            ? `${result.error} The engine is back on the previous settings.`
            : `${result.error} The previous settings would not start either, so the engine keeps trying them.`
        );
      }
    } catch (error) {
      this.error.set(failure(error));
    } finally {
      this.applying.set(false);
    }
  }

  private edit(change: (draft: AudioSettings) => AudioSettings): void {
    this.draft.update(draft => (draft ? change(draft) : draft));
    this.error.set(null);
  }
}
