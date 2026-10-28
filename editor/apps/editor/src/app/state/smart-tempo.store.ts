import { Injectable, computed, inject, signal } from '@angular/core';
import type {
  BeatMapping,
  FileTempo,
  FlexFollow,
  ProjectTempoMode,
  TempoEvent,
  VarispeedMode
} from '@anode/api-client';
import { clampSpeed, effectiveSpeed, type VarispeedUnit } from '../music/varispeed';
import { ProjectStore } from './project.store';

/** The project tempo modes, as Logic's tempo display menu names them. */
export const TempoModes: readonly { value: ProjectTempoMode; label: string; detail: string }[] = [
  { value: 'keep', label: 'Keep Project Tempo', detail: 'Imports and recordings follow the project tempo.' },
  { value: 'adapt', label: 'Adapt Project Tempo', detail: 'The first import or recording sets the project tempo.' },
  { value: 'auto', label: 'Automatic', detail: 'Adapt while the project has no MIDI and one tempo; keep after.' }
];

/**
 * Smart Tempo, beat mapping, tempo curve shapes and Varispeed (docs/06-phase-5.md): the project tempo mode, a file's
 * tempo (the Smart Tempo Editor), adapting the project tempo to a region, mapping beats onto audio, and the speed the
 * project plays at. Each change is one undo step; the Varispeed display unit is this editor's own preference.
 */
@Injectable({ providedIn: 'root' })
export class SmartTempoStore {
  private readonly project = inject(ProjectStore);

  readonly settings = computed(() => this.project.settings().smartTempo);
  readonly mode = computed(() => this.settings().mode);
  readonly varispeed = computed(() => this.project.settings().varispeed);
  /** The speed the project plays at: 1 unless Varispeed is on. */
  readonly speed = computed(() => effectiveSpeed(this.varispeed()));
  /** How the Varispeed display shows the speed. */
  readonly varispeedUnit = signal<VarispeedUnit>('percent');
  /** The file the Smart Tempo Editor shows (its region's), or null: the selected audio region's. */
  readonly editorRegionId = signal<string | null>(null);

  setMode(mode: ProjectTempoMode): Promise<unknown> {
    const label = TempoModes.find(m => m.value === mode)?.label ?? mode;
    return this.project.execute([{ type: 'setSmartTempo', mode }], `Tempo Mode: ${label}`);
  }

  setDefaultFollow(defaultFollow: FlexFollow): Promise<unknown> {
    return this.project.execute([{ type: 'setSmartTempo', defaultFollow }], 'Set Default Flex & Follow');
  }

  setAnalyzeImports(analyzeImports: boolean): Promise<unknown> {
    return this.project.execute([{ type: 'setSmartTempo', analyzeImports }], 'Analyse Imported Audio');
  }

  /** The Smart Tempo Editor's edit of a file's beats and downbeats (or `null` to forget them). */
  setFileTempo(fileId: string, tempo: FileTempo | null, label = 'Edit File Tempo'): Promise<unknown> {
    return this.project.execute([{ type: 'setFileTempo', fileId, tempo }], label);
  }

  /** Sets the project tempo from a region's file tempo over its range (it then plays unwarped there). */
  adaptTempo(regionId: string): Promise<unknown> {
    return this.project.execute([{ type: 'adaptTempo', regionId }], 'Adapt Project Tempo');
  }

  /** Beat mapping: each tick lands on its sample. */
  mapBeats(mappings: readonly BeatMapping[]): Promise<unknown> {
    return this.project.execute([{ type: 'mapBeats', mappings: [...mappings] }], 'Beat Mapping');
  }

  /** A ramp's curve shape (−1…1), from the tempo point at `index` to the next. */
  setShape(index: number, shape: number): Promise<unknown> {
    const events: TempoEvent[] = this.project
      .settings()
      .tempoEvents.map((e, i) =>
        i === index ? { ...e, shape: Math.round(Math.min(1, Math.max(-1, shape)) * 100) / 100 } : e
      );
    return this.project.execute([{ type: 'setTempoEvents', events }], 'Tempo Curve');
  }

  setVarispeedEnabled(enabled: boolean): Promise<unknown> {
    return this.project.execute([{ type: 'setVarispeed', enabled }], enabled ? 'Varispeed On' : 'Varispeed Off');
  }

  setVarispeedMode(mode: VarispeedMode): Promise<unknown> {
    return this.project.execute([{ type: 'setVarispeed', mode }], 'Varispeed Mode');
  }

  setSpeed(speed: number): Promise<unknown> {
    return this.project.execute([{ type: 'setVarispeed', speed: clampSpeed(speed) }], 'Varispeed');
  }
}
