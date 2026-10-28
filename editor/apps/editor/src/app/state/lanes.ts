import type { Take, TakeFolder, Track } from '@anode/api-client';

/**
 * A row of the Tracks area: a track (indented when it is in a stack), a take of an open take folder in a row under
 * its track, or, while automation shows, one of the track's automation sub-lanes (a target). The timeline's lanes are
 * these rows, in order; the headers beside them follow the same list.
 */
export type LaneRow =
  | { kind: 'track'; id: string; track: Track; depth: number }
  | { kind: 'take'; id: string; track: Track; folder: TakeFolder; take: Take; index: number }
  | { kind: 'automation'; id: string; track: Track; target: string; depth: number };

/** An automation sub-lane's row id. */
export function automationRowId(trackId: string, target: string): string {
  return `${trackId}~${target}`;
}

/**
 * The rows for these tracks: members of a collapsed stack are hidden; an open take folder lists its takes under its
 * track, one row each; `automation` lists the targets each track shows as sub-lanes (null: automation hidden). With no
 * stacks collapsed, no folders open and automation hidden, rows are the tracks one to one.
 */
export function laneRows(
  tracks: readonly Track[],
  folders: readonly TakeFolder[],
  expanded: ReadonlySet<string>,
  automation: ReadonlyMap<string, readonly string[]> | null = null
): LaneRow[] {
  const collapsed = new Set(tracks.filter(t => t.kind === 'stack' && t.collapsed).map(t => t.id));
  const rows: LaneRow[] = [];
  for (const track of tracks) {
    if (track.parentId && collapsed.has(track.parentId)) {
      continue;
    }
    rows.push({ kind: 'track', id: track.id, track, depth: track.parentId ? 1 : 0 });
    const open = folders
      .filter(f => f.trackId === track.id && expanded.has(f.id))
      .sort((a, b) => a.startTick - b.startTick);
    for (const folder of open) {
      folder.takes.forEach((take, index) =>
        rows.push({ kind: 'take', id: `${folder.id}/${take.id}`, track, folder, take, index })
      );
    }
    for (const target of automation?.get(track.id) ?? []) {
      rows.push({
        kind: 'automation',
        id: automationRowId(track.id, target),
        track,
        target,
        depth: track.parentId ? 1 : 0
      });
    }
  }
  return rows;
}

/** The track a row edits (a take row's track is its folder's). */
export function rowTrackId(row: LaneRow | undefined): string | undefined {
  return row?.track.id;
}
