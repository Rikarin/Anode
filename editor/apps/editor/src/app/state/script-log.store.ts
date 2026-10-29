import { Injectable, inject, signal } from '@angular/core';
import type { ParameterInfo } from '@anode/api-client';
import { EventStream } from '../core/events';

/** At most this many console lines are kept per Scripter slot. */
export const MaxScriptLines = 500;

/** A Scripter slot's key: track and slot. */
export function scriptKey(trackId: string, slotId: string): string {
  return `${trackId}/${slotId}`;
}

/** One console line: what the script traced, a dropped-lines note, or a problem the engine reported. */
export interface ScriptLine {
  text: string;
  kind: 'trace' | 'dropped' | 'problem';
}

/**
 * Scripter's console and run-time parameters (docs/08-phase-7.md, "Scripter"): `scriptLog` events carry a slot's
 * `Trace` lines (at most 50 a second; `dropped` counts the rest), `pluginProblem` a bypassed script's error, and
 * `parameters` the list a compile produced (the Server also stores it in `Insert.runtimeParameters`).
 */
@Injectable({ providedIn: 'root' })
export class ScriptLogStore {
  /** Console lines by {@link scriptKey}, oldest first. */
  readonly lines = signal<ReadonlyMap<string, readonly ScriptLine[]>>(new Map());
  /** The latest parameter list each slot reported, by {@link scriptKey}. */
  readonly parameters = signal<ReadonlyMap<string, readonly ParameterInfo[]>>(new Map());

  constructor() {
    inject(EventStream).events$.subscribe(message => {
      switch (message.type) {
        case 'scriptLog': {
          const added: ScriptLine[] = message.lines.map(text => ({ text, kind: 'trace' }));
          if (message.dropped > 0) {
            added.push({ text: `… ${message.dropped} more lines dropped`, kind: 'dropped' });
          }
          this.append(scriptKey(message.trackId, message.slotId), added);
          break;
        }
        case 'pluginProblem':
          if (message.trackId && message.insertId && message.pluginId === 'anode.scripter') {
            this.append(scriptKey(message.trackId, message.insertId), [{ text: message.message, kind: 'problem' }]);
          }
          break;
        case 'parameters': {
          const key = scriptKey(message.trackId, message.slotId);
          const list = message.parameters;
          this.parameters.update(map => new Map(map).set(key, list));
          break;
        }
        default:
          break;
      }
    });
  }

  linesOf(trackId: string, slotId: string): readonly ScriptLine[] {
    return this.lines().get(scriptKey(trackId, slotId)) ?? [];
  }

  clear(trackId: string, slotId: string): void {
    this.lines.update(map => {
      const next = new Map(map);
      next.delete(scriptKey(trackId, slotId));
      return next;
    });
  }

  private append(key: string, added: readonly ScriptLine[]): void {
    this.lines.update(map => new Map(map).set(key, [...(map.get(key) ?? []), ...added].slice(-MaxScriptLines)));
  }
}
