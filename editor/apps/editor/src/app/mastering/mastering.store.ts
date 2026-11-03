import { computed, DestroyRef, inject, Injectable, signal } from '@angular/core';
import type { MasteringCharacter, MasteringReading, MasteringSettings } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { Coalescer } from '../core/coalescer';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { isActive, JobsStore } from '../state/jobs.store';
import { ProjectStore } from '../state/project.store';
import {
  analysisRange,
  type AnalysisSpan,
  bypassGainDb,
  type MasteringAmount,
  masteringInsert,
  masteringSettingsOf
} from './mastering-math';

/** How long a live reading stays without a newer one (the meters stop when the transport does). */
const ReadingHoldMs = 600;

/**
 * The Mastering Assistant window's state (docs/12-phase-11.md): the master's assistant (its settings are its insert's
 * parameters, its analysis `Insert.mastering`), both read from the project as deltas update it; Analyse as a
 * `masteringAnalysis` job; settings as coalesced `setMastering` edits (a knob drag sends one at a time, the latest
 * shown meanwhile); the live loudness and gain reduction from the `mastering` event.
 */
@Injectable({ providedIn: 'root' })
export class MasteringStore {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly jobs = inject(JobsStore);
  private readonly notify = inject(Notify);

  /** The master's assistant, or null: Analyse adds one. */
  readonly insert = computed(() => masteringInsert(this.project.master()));
  /** Keyed `insertId/setting`. */
  private readonly coalescer = new Coalescer<number | boolean | MasteringCharacter>((key, value) => {
    const [insertId = '', setting = ''] = key.split('/');
    return this.send(insertId, setting as keyof MasteringSettings, value);
  });
  /** The settings, with what is sent but not confirmed yet. */
  readonly settings = computed<MasteringSettings | null>(() => {
    const insert = this.insert();
    if (!insert) {
      return null;
    }
    const stored = masteringSettingsOf(insert);
    const pending = (key: keyof MasteringSettings) => this.coalescer.pending(`${insert.id}/${key}`);
    return {
      character: (pending('character') as MasteringCharacter | undefined) ?? stored.character,
      targetLufs: (pending('targetLufs') as number | undefined) ?? stored.targetLufs,
      ceilingDbtp: (pending('ceilingDbtp') as number | undefined) ?? stored.ceilingDbtp,
      eq: (pending('eq') as number | undefined) ?? stored.eq,
      dynamics: (pending('dynamics') as number | undefined) ?? stored.dynamics,
      width: (pending('width') as number | undefined) ?? stored.width,
      matchedBypass: (pending('matchedBypass') as boolean | undefined) ?? stored.matchedBypass
    };
  });
  /** The last analysis (the Server leaves a null one out). */
  readonly analysis = computed(() => this.insert()?.mastering ?? null);
  /** The level-matched bypass' gain, from the analysis. */
  readonly bypassGain = computed(() => {
    const settings = this.settings();
    return settings ? bypassGainDb(this.analysis(), settings) : null;
  });

  private readonly jobId = signal<string | null>(null);
  readonly job = computed(() => {
    const id = this.jobId();
    return id ? (this.jobs.jobs().get(id) ?? null) : null;
  });
  readonly analysing = computed(() => isActive(this.job()));

  /** The live readings while playing; null when none came lately (or for another insert). */
  readonly reading = signal<MasteringReading | null>(null);
  private readingTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    const subscription = inject(EventStream).events$.subscribe(message => {
      if (message.type !== 'mastering') {
        return;
      }
      const { type: _type, ...reading } = message;
      this.reading.set(reading);
      if (this.readingTimer) {
        clearTimeout(this.readingTimer);
      }
      this.readingTimer = setTimeout(() => this.reading.set(null), ReadingHoldMs);
    });
    inject(DestroyRef).onDestroy(() => {
      subscription.unsubscribe();
      if (this.readingTimer) {
        clearTimeout(this.readingTimer);
      }
    });
  }

  /** The live reading for the current assistant. */
  readonly live = computed(() => {
    const reading = this.reading();
    return reading && reading.insertId === this.insert()?.id ? reading : null;
  });

  /** Analyse the master over the song or the cycle (adding an assistant last on the master when it has none). */
  async analyse(span: AnalysisSpan): Promise<void> {
    const job = await this.notify.run(
      () => this.api.analyseMastering({ add: true, ...analysisRange(span, this.project.settings()) }),
      'Could not analyse the master'
    );
    if (job) {
      this.jobId.set(job.id);
      this.jobs.update(job);
    }
  }

  /** A setting, coalesced per setting (a knob drag sends one value at a time). */
  set(key: MasteringAmount, value: number): Promise<void>;
  set(key: 'character', value: MasteringCharacter): Promise<void>;
  set(key: 'matchedBypass', value: boolean): Promise<void>;
  set(key: keyof MasteringSettings, value: number | boolean | MasteringCharacter): Promise<void> {
    const insert = this.insert();
    return insert ? this.coalescer.set(`${insert.id}/${key}`, value) : Promise.resolve();
  }

  private send(
    insertId: string,
    key: keyof MasteringSettings,
    value: number | boolean | MasteringCharacter
  ): Promise<unknown> {
    return this.project.execute([{ type: 'setMastering', insertId, [key]: value }], 'Mastering Assistant');
  }
}
