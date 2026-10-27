import { Injectable, inject } from '@angular/core';
import type { ChannelStrip, EditCommand } from '@anode/api-client';
import { Coalescer } from '../core/coalescer';
import { ProjectStore } from './project.store';

/** A channel strip's continuous controls. */
export type StripControl = 'volumeDb' | 'pan';

const Labels: Record<StripControl, string> = { volumeDb: 'Volume', pan: 'Pan' };

/** The strip key of the master in {@link MixerStore}'s methods. */
export const Master = null;

/**
 * Channel strip edits for the track headers and the Mixer. Fader and pan drags are optimistic and coalesced: each
 * control has at most one `setStrip` in flight and shows the value being sent until the Server's delta confirms it
 * (docs/01-implementation-plan.md §4.4); the Server folds a drag's batches into one undo step. Mute and solo are
 * single edits.
 */
@Injectable({ providedIn: 'root' })
export class MixerStore {
  private readonly project = inject(ProjectStore);
  private readonly coalescer = new Coalescer<number>((key, value) => {
    const [owner = '', control = 'volumeDb'] = key.split('|');
    const field = control as StripControl;
    return this.project.execute([stripCommand(owner || null, { [field]: value })], Labels[field]);
  });

  /** A strip as stored: the track's, or the master's for {@link Master}. */
  strip(trackId: string | null): ChannelStrip | undefined {
    return trackId === null ? this.project.master() : this.project.track(trackId)?.strip;
  }

  /** The value to show for a control: the one being sent, else the confirmed one. Reactive. */
  value(trackId: string | null, control: StripControl): number {
    return this.coalescer.pending(key(trackId, control)) ?? this.strip(trackId)?.[control] ?? 0;
  }

  /** Sets a control during a drag: shown at once, sent at most one request at a time. */
  set(trackId: string | null, control: StripControl, value: number): Promise<void> {
    return this.coalescer.set(key(trackId, control), value);
  }

  toggleMute(trackId: string | null): void {
    const strip = this.strip(trackId);
    if (strip) {
      void this.project.execute([stripCommand(trackId, { mute: !strip.mute })], strip.mute ? 'Unmute' : 'Mute');
    }
  }

  toggleSolo(trackId: string): void {
    const strip = this.strip(trackId);
    if (strip) {
      void this.project.execute([stripCommand(trackId, { solo: !strip.solo })], strip.solo ? 'Unsolo' : 'Solo');
    }
  }
}

function key(trackId: string | null, control: StripControl): string {
  return `${trackId ?? ''}|${control}`;
}

/** A `setStrip` command for a track's strip, or the master's when `trackId` is null. */
export function stripCommand(
  trackId: string | null,
  change: Partial<Pick<ChannelStrip, 'volumeDb' | 'pan' | 'mute' | 'solo'>>
): EditCommand {
  return { type: 'setStrip', ...(trackId === null ? {} : { trackId }), ...change };
}
