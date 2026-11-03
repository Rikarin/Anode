import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { type ChannelFormat, SpeakerLayouts } from '@anode/api-client';
import { XuiButtonImports } from '@xui/button';
import { Band, Field, selectValue } from '../control/styles';
import { FloatingWindow } from '../shell/floating-window';
import { UiStore } from '../state/ui.store';
import { DeviceOutputs, type MonitoringMode, MonitoringStore } from './monitoring.store';

/**
 * Settings… (the app menu's ⌘,; docs/11-phase-10.md): the user's settings, Phase 10's Audio ▸ Monitoring among them —
 * how the master reaches the device: its speakers as they are, a down-mix (ITU-R BS.775) to a smaller layout, or
 * binaural for headphones (to outputs 1–2); and the speaker map, each channel of what is sent to a device output (or
 * not sent). Monitoring never changes a bounce. Each change is saved at once (`PUT /settings/monitoring`).
 * Lazy-loaded.
 */
@Component({
  selector: 'an-settings-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow, XuiButtonImports],
  template: `
    <an-floating-window
      title="Settings"
      testId="settings"
      width="28rem"
      [origin]="{ x: 260, y: 96 }"
      (closed)="ui.settingsOpen.set(false)"
    >
      <h3 [class]="band">Audio ▸ Monitoring</h3>
      <div class="flex flex-col gap-3 p-3 text-xs">
        <div
          class="bezel-group text-foreground h-6.5 self-start text-[11px] font-semibold"
          role="radiogroup"
          aria-label="Monitoring"
        >
          @for (option of modes; track option.value) {
            <button
              type="button"
              role="radio"
              class="px-3"
              [attr.data-testid]="'monitoring-' + option.value"
              [attr.aria-checked]="monitoring.mode() === option.value"
              [class.text-lit]="monitoring.mode() === option.value"
              [attr.disabled]="option.value === 'downmix' && !monitoring.targets().length ? '' : null"
              (click)="setMode(option.value)"
            >
              {{ option.label }}
            </button>
          }
        </div>
        <p class="text-foreground-muted" data-testid="monitoring-note">{{ note() }}</p>
        @if (monitoring.mode() === 'downmix') {
          <div class="grid grid-cols-[5.5rem_1fr] items-center gap-x-2">
            <label class="text-foreground-muted" for="an-downmix">Down-mix to</label>
            <select id="an-downmix" [class]="field" data-testid="downmix-target" (change)="setDownmix(value($event))">
              @for (target of monitoring.targets(); track target.format) {
                <option [value]="target.format" [selected]="monitoring.settings().downmixTo === target.format">
                  {{ target.name }}
                </option>
              }
            </select>
          </div>
        }
      </div>
      @if (monitoring.rows().length) {
        <h3 [class]="band">Speaker Map</h3>
        <div class="flex flex-col gap-1 p-3 text-xs">
          <table class="w-full border-separate border-spacing-y-1" aria-label="Speaker map" data-testid="speaker-map">
            <thead>
              <tr class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">
                <th class="w-16 text-start font-semibold">Channel</th>
                <th class="text-start font-semibold">Speaker</th>
                <th class="w-32 text-start font-semibold">Device output</th>
              </tr>
            </thead>
            <tbody>
              @for (row of monitoring.rows(); track row.index) {
                <tr [attr.data-channel]="row.short">
                  <td class="text-foreground readout">{{ row.index + 1 }}</td>
                  <td class="text-foreground font-semibold">
                    {{ row.short }} <span class="text-foreground-muted readout font-normal">{{ row.label }}</span>
                  </td>
                  <td>
                    <select
                      [class]="field"
                      [attr.aria-label]="'Device output of ' + row.short"
                      (change)="monitoring.setSpeakerOutput(row.index, +value($event))"
                    >
                      <option value="0" [selected]="row.output === 0">Not sent</option>
                      @for (output of outputs; track output) {
                        <option [value]="output" [selected]="row.output === output">Output {{ output }}</option>
                      }
                    </select>
                  </td>
                </tr>
              }
            </tbody>
          </table>
          <button
            xuiButton
            type="button"
            size="sm"
            variant="outline"
            class="self-start"
            data-testid="speaker-map-reset"
            (click)="monitoring.resetSpeakerMap()"
          >
            Channel n to Output n
          </button>
        </div>
      }
    </an-floating-window>
  `
})
export class SettingsWindow {
  protected readonly ui = inject(UiStore);
  protected readonly monitoring = inject(MonitoringStore);
  protected readonly band = Band;
  protected readonly field = Field;
  protected readonly value = selectValue;
  protected readonly outputs = Array.from({ length: DeviceOutputs }, (_, i) => i + 1);
  protected readonly modes: readonly { value: MonitoringMode; label: string }[] = [
    { value: 'speakers', label: 'Speakers' },
    { value: 'downmix', label: 'Down-mix' },
    { value: 'binaural', label: 'Binaural' }
  ];

  constructor() {
    void this.monitoring.load();
  }

  protected note(): string {
    const project = SpeakerLayouts[this.monitoring.projectFormat()].name;
    switch (this.monitoring.mode()) {
      case 'binaural':
        return `The ${project} master rendered for headphones (SADIE II HRTFs), to outputs 1 and 2.`;
      case 'downmix':
        return `The ${project} master down-mixed (ITU-R BS.775) for a smaller speaker set.`;
      default:
        return `The ${project} master's channels to the device's outputs, as the map says.`;
    }
  }

  protected setMode(mode: MonitoringMode): void {
    if (mode !== this.monitoring.mode()) {
      void this.monitoring.setMode(mode);
    }
  }

  protected setDownmix(format: string): void {
    void this.monitoring.setDownmix(format as ChannelFormat);
  }
}
