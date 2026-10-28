import { ApiError, type DirectoryListing, type FileEntry, type FileKind } from '@anode/api-client';
import { hash } from './fake-audio';

/** The fake Server's home folder (`/files` starts here). */
export const FakeHome = '/Users/anode';

/** A folder of the fake disk: subfolders are objects, files are their sizes in bytes. */
interface Folder {
  [name: string]: Folder | number;
}

/** What the All Files browser shows in fake mode: a home with music, samples, loops and MIDI files. */
const Disk: Folder = {
  Desktop: { 'Session notes.txt': 2_048, 'Rough mix.wav': 9_400_000 },
  Documents: { 'Lyrics.txt': 3_100 },
  Downloads: { 'Groove 96 bpm.mid': 1_840, 'Piano ballad.mid': 3_920, 'Vocal chop.wav': 1_250_000 },
  Music: {
    Anode: { 'Demo.anode': { 'project.json': 18_000 } },
    Loops: {
      'Drum Loop 120.wav': 1_840_000,
      'Bass Loop 120.wav': 1_160_000,
      'Pad Swell.wav': 2_880_000,
      'Shaker.wav': 460_000
    },
    MIDI: { 'Chord progression.mid': 920, 'Funk bass.mid': 1_220, 'Drum fill.midi': 410 },
    Samples: {
      Drums: { 'Kick.wav': 96_000, 'Snare.wav': 88_000, 'Hat closed.wav': 24_000 },
      'Piano.sfz': 2_400
    }
  }
};

/** A file's kind from its name, as the Server's probing says (format for audio). */
export function fileKind(name: string): FileKind {
  const lower = name.toLowerCase();
  if (/\.(wav|wave|aif|aiff|flac|mp3|caf)$/.test(lower)) {
    return 'audio';
  }
  if (/\.(mid|midi|smf)$/.test(lower)) {
    return 'midi';
  }
  if (lower.endsWith('.anode')) {
    return 'project';
  }
  if (/\.(sfz|clap)$/.test(lower)) {
    return 'instrument';
  }
  return 'other';
}

/** The folder at a path, or null. */
function folderAt(path: string): Folder | null {
  const relative = path === FakeHome ? '' : path.startsWith(`${FakeHome}/`) ? path.slice(FakeHome.length + 1) : null;
  if (relative === null) {
    return null;
  }
  let folder: Folder = Disk;
  for (const part of relative.split('/').filter(Boolean)) {
    const next = folder[part];
    if (typeof next !== 'object') {
      return null;
    }
    folder = next;
  }
  return folder;
}

/** Whether a file exists on the fake disk. */
export function fakeFileExists(path: string): boolean {
  const slash = path.lastIndexOf('/');
  const folder = folderAt(path.slice(0, slash));
  return typeof folder?.[path.slice(slash + 1)] === 'number';
}

/** `/files` on the fake disk: folders first, then files, by name; audio with a probed format. */
export function listFakeFolder(
  path: string | null | undefined,
  sampleRate: number,
  modified: string
): DirectoryListing {
  const at = (path ?? FakeHome).replace(/\/+$/, '') || FakeHome;
  const folder = folderAt(at);
  if (!folder) {
    throw new ApiError(404, `No folder ${at}.`);
  }
  const entries: FileEntry[] = Object.entries(folder).map(([name, value]) => {
    const full = `${at}/${name}`;
    const isFolder = typeof value === 'object' && !name.endsWith('.anode');
    const kind: FileKind = isFolder ? 'folder' : fileKind(name);
    const audio = kind === 'audio';
    const channels = audio ? (hash(full) % 2) + 1 : null;
    return {
      name,
      path: full,
      kind,
      size: typeof value === 'number' ? value : 0,
      modified,
      sampleRate: audio ? sampleRate : null,
      channels,
      frames: audio && typeof value === 'number' && channels ? Math.floor(value / (3 * channels)) : null
    };
  });
  entries.sort((a, b) => Number(b.kind === 'folder') - Number(a.kind === 'folder') || a.name.localeCompare(b.name));
  return { path: at, parent: at === FakeHome ? null : at.slice(0, at.lastIndexOf('/')) || null, entries };
}
