import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { XuiButtonImports } from '@xui/button';
import { Band, Field, selectValue } from '../control/styles';
import { AudioDeviceStore, BufferSizes, SampleRates, latencyMs, rateLabel } from './audio-device.store';

/** The output choice for no device, and the input choice for the output device's own inputs (neither is a uid). */
const NoDevice = 'anode:none';
const OwnInputs = 'anode:own';

/**
 * Settings ▸ Audio ▸ Devices: output and input device, sample rate and buffer size, applied together, and the stream
 * that runs with the latency its device reports. Without the desktop app (a browser) it shows only what runs.
 */
@Component({
  selector: 'an-audio-device-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports],
  template: `
    <h3 [class]="band">Audio ▸ Devices</h3>
    <div class="flex flex-col gap-3 p-3 text-xs" data-testid="audio-devices">
      @if (store.draft(); as draft) {
        <div class="grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-1.5">
          <label class="text-foreground-muted" for="an-audio-output">Output</label>
          <select
            id="an-audio-output"
            [class]="field"
            data-testid="audio-output"
            [disabled]="locked()"
            (change)="setOutput(value($event))"
          >
            <option [value]="noDevice" [selected]="draft.device === null">No device</option>
            <option value="" [selected]="draft.device === ''">System default</option>
            @for (device of store.outputs(); track device.uid) {
              <option [value]="device.uid" [selected]="draft.device === device.uid">{{ device.name }}</option>
            }
          </select>

          <label class="text-foreground-muted" for="an-audio-input">Input</label>
          <select
            id="an-audio-input"
            [class]="field"
            data-testid="audio-input"
            [disabled]="locked() || draft.device === null"
            (change)="setInput(value($event))"
          >
            <option [value]="ownInputs" [selected]="draft.inputDevice === null">Same as output</option>
            <option value="" [selected]="draft.inputDevice === ''">None</option>
            @for (device of store.inputs(); track device.uid) {
              <option [value]="device.uid" [selected]="draft.inputDevice === device.uid">{{ device.name }}</option>
            }
          </select>

          <label class="text-foreground-muted" for="an-audio-rate">Sample rate</label>
          <select
            id="an-audio-rate"
            [class]="field"
            data-testid="audio-rate"
            [disabled]="locked()"
            (change)="store.setSampleRate(+value($event))"
          >
            @for (rate of rates; track rate) {
              <option [value]="rate" [selected]="draft.sampleRate === rate">{{ rateLabel(rate) }}</option>
            }
          </select>

          <label class="text-foreground-muted" for="an-audio-buffer">Buffer size</label>
          <select
            id="an-audio-buffer"
            [class]="field"
            data-testid="audio-buffer"
            [disabled]="locked()"
            (change)="store.setBufferFrames(+value($event))"
          >
            @for (size of buffers; track size) {
              <option [value]="size" [selected]="draft.bufferFrames === size">{{ size }} samples</option>
            }
          </select>
        </div>

        @if (store.remote()) {
          <p class="text-foreground-muted" data-testid="audio-remote">
            This window uses a remote Server. Its audio device is set on that computer.
          </p>
        } @else {
          <div class="flex items-center gap-2">
            <button
              xuiButton
              type="button"
              size="sm"
              data-testid="audio-apply"
              [disabled]="!store.canApply()"
              (click)="store.apply()"
            >
              {{ store.applying() ? 'Applying…' : 'Apply' }}
            </button>
            @if (store.changed() && !store.applying()) {
              <button
                xuiButton
                type="button"
                size="sm"
                variant="outline"
                data-testid="audio-revert"
                (click)="store.revert()"
              >
                Revert
              </button>
            }
            <span class="text-foreground-muted" data-testid="audio-apply-note">{{ applyNote() }}</span>
          </div>
        }
      } @else if (!store.available) {
        <p class="text-foreground-muted" data-testid="audio-no-bridge">
          The audio device is chosen in the desktop app, on the computer that runs the engine.
        </p>
      }

      @if (store.error(); as error) {
        <p class="text-error" role="status" data-testid="audio-error">{{ error }}</p>
      }

      @if (store.audio(); as audio) {
        <dl class="grid grid-cols-[5.5rem_1fr] gap-x-2 gap-y-0.5" data-testid="audio-running">
          <dt class="text-foreground-muted">Running</dt>
          <dd class="readout text-foreground" data-testid="audio-stream">
            {{ rateLabel(audio.sampleRate) }}, {{ audio.bufferFrames }} samples, {{ audio.inputChannels }} in /
            {{ audio.outputChannels }} out
          </dd>
          @if (measured()) {
            <dt class="text-foreground-muted">Latency</dt>
            <dd class="readout text-foreground" data-testid="audio-latency">
              @if (audio.inputChannels > 0) {
                {{ ms(audio.inputLatencyFrames) }} in, {{ ms(audio.outputLatencyFrames) }} out,
                {{ ms(audio.inputLatencyFrames + audio.outputLatencyFrames) }} round trip
              } @else {
                {{ ms(audio.outputLatencyFrames) }} out
              }
            </dd>
          }
        </dl>
        @if (!measured()) {
          <p class="text-foreground-muted" data-testid="audio-no-latency">
            No device is running, so there is no latency to measure.
          </p>
        }
        @if (store.settled()) {
          <p class="text-foreground-muted" data-testid="audio-settled">
            The device settled on {{ audio.bufferFrames }} samples.
          </p>
        }
        @if (store.engine()?.message; as message) {
          <p class="text-foreground-muted" data-testid="audio-engine-message">{{ message }}</p>
        }
      } @else {
        <p class="text-foreground-muted" data-testid="audio-stopped">
          {{ store.applying() ? 'Restarting the engine…' : 'The engine is not running.' }}
        </p>
      }
    </div>
  `
})
export class AudioDeviceSettings {
  protected readonly store = inject(AudioDeviceStore);
  protected readonly band = Band;
  protected readonly field = Field;
  protected readonly value = selectValue;
  protected readonly rates = SampleRates;
  protected readonly buffers = BufferSizes;
  protected readonly rateLabel = rateLabel;
  protected readonly noDevice = NoDevice;
  protected readonly ownInputs = OwnInputs;

  protected readonly locked = computed(() => this.store.applying() || this.store.remote());
  /** A device reports latency; the null backend has none (nor has any stream with both at 0). */
  protected readonly measured = computed(() => {
    const audio = this.store.audio();
    return !!audio && audio.inputLatencyFrames + audio.outputLatencyFrames > 0;
  });

  protected readonly applyNote = computed(() =>
    this.store.recording()
      ? 'Stop recording to apply.'
      : this.store.applying()
        ? 'Restarting the engine…'
        : this.store.changed()
          ? 'Applying restarts the engine and stops playback.'
          : ''
  );

  constructor() {
    void this.store.load();
  }

  protected ms(frames: number): string {
    const audio = this.store.audio();
    return audio ? latencyMs(frames, audio.sampleRate) : '';
  }

  protected setOutput(value: string): void {
    this.store.setOutput(value === NoDevice ? null : value);
  }

  protected setInput(value: string): void {
    this.store.setInput(value === OwnInputs ? null : value);
  }
}
