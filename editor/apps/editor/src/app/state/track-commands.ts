import type { EditCommand, TrackInput } from '@anode/api-client';

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
