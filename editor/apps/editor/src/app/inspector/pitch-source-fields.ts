import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { KeyMode, PitchSourceMode, RegionKey } from '@anode/api-client';
import { pitchFromKey, pitchKey, pitchNameText, PitchSpellings } from '@anode/audio-ui';
import { Field, selectValue } from '../control/styles';
import { ProjectStore } from '../state/project.store';

const Modes: readonly { mode: PitchSourceMode; label: string }[] = [
  { mode: 'off', label: 'Off' },
  { mode: 'root', label: 'Chord Track: Root' },
  { mode: 'chords', label: 'Chord Track: Chords' }
];

/** A region key as a select value (`f#:minor`), and back. */
function keyValue(key: RegionKey | null): string {
  return key ? `${pitchKey(key.tonic)}:${key.mode}` : '';
}

/**
 * A MIDI or audio region's Pitch Source and key (Phase 11), under its other Region inspector fields: Off, Root (moves
 * by each chord's root interval from the region's key) or, for MIDI, Chords (each note to the nearest chord tone); and
 * the region's own key (Chord ID detects it; none: the project's key). Each change is one undo step.
 */
@Component({
  selector: 'an-pitch-source-fields',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'contents' },
  template: `
    <span class="text-foreground-muted">Pitch Source</span>
    <select
      [class]="field"
      aria-label="Pitch Source"
      data-testid="region-pitch-source"
      [value]="mode()"
      (change)="setMode(selectValue($event))"
    >
      @for (option of modes; track option.mode) {
        <option [value]="option.mode" [disabled]="option.mode === 'chords' && audio()">{{ option.label }}</option>
      }
    </select>
    <span class="text-foreground-muted">Key</span>
    <select
      [class]="field"
      aria-label="Region key"
      data-testid="region-key"
      [value]="key()"
      (change)="setKey(selectValue($event))"
    >
      <option value="">Project Key</option>
      @for (option of keys; track option.value) {
        <option [value]="option.value">{{ option.label }}</option>
      }
    </select>
  `
})
export class PitchSourceFields {
  private readonly project = inject(ProjectStore);
  readonly regionId = input.required<string>();
  readonly mode = input.required<PitchSourceMode>();
  readonly regionKey = input.required<RegionKey | null>();
  /** An audio region: Chords is MIDI only. */
  readonly audio = input(false);

  protected readonly field = Field;
  protected readonly selectValue = selectValue;
  protected readonly modes = Modes;
  protected readonly keys = (['major', 'minor'] as const satisfies readonly KeyMode[]).flatMap(mode =>
    PitchSpellings.map(tonic => ({
      value: keyValue({ tonic, mode }),
      label: `${pitchNameText(tonic)} ${mode}`
    }))
  );
  protected readonly key = computed(() => keyValue(this.regionKey()));

  protected setMode(value: string): void {
    const mode = Modes.find(m => m.mode === value)?.mode;
    if (mode && mode !== this.mode()) {
      void this.project.execute([{ type: 'setPitchSource', regionIds: [this.regionId()], mode }], 'Pitch Source');
    }
  }

  protected setKey(value: string): void {
    const [tonic, mode] = value.split(':');
    const pitch = tonic ? pitchFromKey(tonic) : null;
    const key: RegionKey | null = pitch && (mode === 'major' || mode === 'minor') ? { tonic: pitch, mode } : null;
    if (keyValue(key) !== this.key()) {
      void this.project.execute([{ type: 'setRegionKey', regionIds: [this.regionId()], key }], 'Region Key');
    }
  }
}
