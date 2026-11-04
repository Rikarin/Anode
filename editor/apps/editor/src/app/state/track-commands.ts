import type { EditCommand, Track, TrackInput } from '@anode/api-client';

export interface TrackChange {
  name?: string;
  color?: string;
  input?: TrackInput;
  recordArmed?: boolean;
  inputMonitoring?: boolean;
}

/** A `setTrack` command changing only what `change` names (the generated type wants `input`, null for unchanged). */
export function setTrack(trackId: string, change: TrackChange): EditCommand {
  return { type: 'setTrack', trackId, input: null, ...change };
}

/** A label for the undo history ("Rename Track", "Record Enable"). */
export function setTrackLabel(change: TrackChange): string {
  if (change.name !== undefined) {
    return 'Rename Track';
  }
  if (change.color !== undefined) {
    return 'Track Color';
  }
  if (change.recordArmed !== undefined) {
    return change.recordArmed ? 'Record Enable' : 'Record Disable';
  }
  if (change.inputMonitoring !== undefined) {
    return change.inputMonitoring ? 'Input Monitoring On' : 'Input Monitoring Off';
  }
  return 'Track Input';
}

/**
 * The `moveTrack` index that moves a track one place up (-1) or down (1), or null when it is at that end. A stack's
 * members follow it, so a member moves among its stack's members, and any other track past the next track or stack with
 * its members.
 */
export function moveIndex(
  tracks: readonly Pick<Track, 'id' | 'parentId'>[],
  trackId: string,
  offset: -1 | 1
): number | null {
  const parentId = tracks.find(t => t.id === trackId)?.parentId ?? null;
  const siblings = tracks.flatMap((t, index) => ((t.parentId ?? null) === parentId ? [{ id: t.id, index }] : []));
  const at = siblings.findIndex(t => t.id === trackId);
  return at < 0 ? null : (siblings[at + offset]?.index ?? null);
}
