import { Injectable, computed, inject, signal } from '@angular/core';
import {
  type ChannelFormat,
  DefaultMonitorSection,
  DefaultMonitoringSettings,
  type MonitorSection,
  type MonitoringSettings,
  SpeakerLayouts
} from '@anode/api-client';
import { shortSpeakerLabel } from '@anode/audio-ui/lazy';
import { ANODE_API } from '../core/api';
import { Notify } from '../core/notify';
import { ProjectStore } from '../state/project.store';

/** How the master reaches the device: its own speakers, a down-mix, or binaural for headphones. */
export type MonitoringMode = 'speakers' | 'downmix' | 'binaural';

/** One row of the speaker map: a channel of what is sent and the device output (1-based; 0 not sent) it goes to. */
export interface SpeakerRow {
  index: number;
  label: string;
  short: string;
  output: number;
}

/** The device outputs the speaker map offers (1…64, as the Server accepts). */
export const DeviceOutputs = 64;

export function monitoringMode(settings: MonitoringSettings): MonitoringMode {
  return settings.binaural ? 'binaural' : settings.downmixTo ? 'downmix' : 'speakers';
}

/** The formats a project in `format` can be down-mixed to: every narrower layout (stereo first). */
export function downmixTargets(format: ChannelFormat): { format: ChannelFormat; name: string }[] {
  const channels = SpeakerLayouts[format].speakers.length;
  return Object.values(SpeakerLayouts)
    .filter(l => l.speakers.length < channels && l.format !== 'mono')
    .sort((a, b) => (a.format === 'stereo' ? -1 : b.format === 'stereo' ? 1 : 0))
    .map(l => ({ format: l.format, name: l.name }));
}

/** What reaches the device: binaural's two ears, the down-mix's layout, else the project's. */
export function sentFormat(settings: MonitoringSettings, project: ChannelFormat): ChannelFormat {
  return settings.binaural ? 'stereo' : (settings.downmixTo ?? project);
}

/**
 * The speaker map as rows (`MonitoringSettings.speakerMap`, 1-based; empty or short: channel i to output i + 1).
 * Binaural always goes to outputs 1–2, so it has no rows.
 */
export function speakerRows(settings: MonitoringSettings, project: ChannelFormat): SpeakerRow[] {
  if (settings.binaural) {
    return [];
  }
  return SpeakerLayouts[sentFormat(settings, project)].speakers.map((s, index) => ({
    index,
    label: s.label,
    short: shortSpeakerLabel(s.label),
    output: settings.speakerMap[index] ?? index + 1
  }));
}

/** The map with channel `index` sent to `output` (the rows' whole map, so the defaults become explicit). */
export function withSpeakerOutput(rows: readonly SpeakerRow[], index: number, output: number): number[] {
  return rows.map(row => (row.index === index ? output : row.output));
}

/**
 * The user's monitoring settings (docs/11-phase-10.md, `MonitoringSettings`; `GET/PUT /settings/monitoring`): binaural
 * on or off, the down-mix target, and the speaker map to the device's outputs. Not the project's: they never change a
 * bounce. Loaded when the Settings window opens; each change is saved at once.
 */
@Injectable({ providedIn: 'root' })
export class MonitoringStore {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);
  private readonly project = inject(ProjectStore);

  readonly settings = signal<MonitoringSettings>(DefaultMonitoringSettings);
  readonly loaded = signal(false);
  readonly mode = computed(() => monitoringMode(this.settings()));
  readonly projectFormat = computed(() => this.project.settings().surroundFormat);
  readonly targets = computed(() => downmixTargets(this.projectFormat()));
  readonly rows = computed(() => speakerRows(this.settings(), this.projectFormat()));
  /** The monitor section: what the speakers get after the master. */
  readonly section = computed(() => this.settings().section ?? DefaultMonitorSection);

  async load(): Promise<void> {
    const settings = await this.notify.run(() => this.api.monitoring(), 'Could not load the monitoring settings');
    if (settings) {
      this.settings.set(settings);
      this.loaded.set(true);
    }
  }

  /** Speakers, a down-mix (to stereo unless another target is chosen), or binaural. */
  setMode(mode: MonitoringMode): Promise<void> {
    const current = this.settings();
    const target = current.downmixTo ?? this.targets()[0]?.format ?? null;
    switch (mode) {
      case 'binaural':
        return this.save({ ...current, binaural: true, downmixTo: null });
      case 'downmix':
        return this.save({ ...current, binaural: false, downmixTo: target, speakerMap: [] });
      default:
        return this.save({ ...current, binaural: false, downmixTo: null, speakerMap: [] });
    }
  }

  setDownmix(format: ChannelFormat): Promise<void> {
    return this.save({ ...this.settings(), binaural: false, downmixTo: format, speakerMap: [] });
  }

  /** Changes the monitor section (Dim, Mono, Ref, the levels), which the host follows at once. */
  setSection(change: Partial<MonitorSection>): Promise<void> {
    return this.save({ ...this.settings(), section: { ...this.section(), ...change } });
  }

  setSpeakerOutput(index: number, output: number): Promise<void> {
    const speakerMap = withSpeakerOutput(this.rows(), index, output);
    return this.save({ ...this.settings(), speakerMap });
  }

  /** Every channel to output i + 1 again. */
  resetSpeakerMap(): Promise<void> {
    return this.save({ ...this.settings(), speakerMap: [] });
  }

  private async save(settings: MonitoringSettings): Promise<void> {
    const previous = this.settings();
    this.settings.set(settings);
    const saved = await this.notify.run(() => this.api.setMonitoring(settings), 'Could not save the monitoring');
    this.settings.set(saved ?? previous);
  }
}
