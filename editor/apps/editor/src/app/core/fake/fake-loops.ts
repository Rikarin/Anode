import {
  ApiError,
  type LoopInfo,
  type LoopKind,
  type LoopPage,
  type LoopScale,
  type Pack,
  type Pattern
} from '@anode/api-client';
import { defaultRow, defaultStep } from '../../music/pattern';
import type { LoopSearch } from '../api';

/** A MIDI loop's note, in ticks from the loop's start. */
export interface LoopNote {
  pitch: number;
  startTick: number;
  lengthTicks: number;
  velocity: number;
}

/** A loop of the fake library: its index entry and what adding it makes. */
export interface FakeLoop {
  info: LoopInfo;
  /** A MIDI loop's notes. */
  notes?: LoopNote[];
  /** A pattern loop's pattern (its sidecar's). */
  pattern?: Pattern;
}

/** An installed pack and its loops. */
export interface FakePack {
  pack: Pack;
  loops: FakeLoop[];
}

/** The fake Server's data directory, where packs install (`<DataDirectory>/Library/Packs/<id>/`). */
export const FakeDataDirectory = '/Users/anode/Library/Application Support/Anode';

const Ppq = 960;

function slug(text: string): string {
  return text
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '');
}

/** "TestPack" → "Test Pack"; "anode-essentials" → "Anode Essentials". */
function title(stem: string): string {
  return stem
    .replace(/[-_]+/g, ' ')
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/\b\w/g, c => c.toUpperCase())
    .trim();
}

/** A bass line: eighths on the root with a fifth and octave, in C (pitch class 0) from C2. */
function bassLine(beats: number): LoopNote[] {
  const pitches = [36, 36, 43, 36, 48, 36, 43, 46];
  return Array.from({ length: beats * 2 }, (_, i) => ({
    pitch: pitches[i % pitches.length] ?? 36,
    startTick: (i * Ppq) / 2,
    lengthTicks: Ppq / 2 - 40,
    velocity: i % 2 ? 90 : 110
  }));
}

/** Chords, a bar each: I V vi IV in G. */
function chords(beats: number): LoopNote[] {
  const progression = [
    [55, 59, 62],
    [62, 66, 69],
    [64, 67, 71],
    [60, 64, 67]
  ];
  const notes: LoopNote[] = [];
  for (let bar = 0; bar * 4 < beats; bar++) {
    for (const pitch of progression[bar % progression.length] ?? []) {
      notes.push({ pitch, startTick: bar * 4 * Ppq, lengthTicks: 4 * Ppq - 60, velocity: 92 });
    }
  }
  return notes;
}

/** A pattern loop's pattern: kick on the beats, snare on 2 and 4, hats on the eighths. */
function beatPattern(id: string): Pattern {
  const row = (n: string, kind: 'note', note: number, name: string, on: number[]) => {
    const r = defaultRow(`${id}-${n}`, kind, note, name);
    return { ...r, steps: r.steps.map((_, i) => defaultStep(on.includes(i))) };
  };
  return {
    rows: [
      row('kick', 'note', 36, 'Kick', [0, 4, 8, 12]),
      row('snare', 'note', 38, 'Snare', [4, 12]),
      row('hat', 'note', 42, 'Hat', [0, 2, 4, 6, 8, 10, 12, 14])
    ],
    swing: 0,
    seed: 1,
    stepTicks: 240
  };
}

interface Template {
  name: string;
  kind: LoopKind;
  tempo: number | null;
  beats: number | null;
  key: number | null;
  scale: LoopScale;
  tags: string[];
  file: string;
}

/** Every fake pack's loops: drums, bass, a pattern, keys, a one-shot; at several tempi and keys. */
const Templates: readonly Template[] = [
  {
    name: 'Drum Loop 100',
    kind: 'audio',
    tempo: 100,
    beats: 16,
    key: 0,
    scale: 'neither',
    tags: ['Drums', 'Electronic'],
    file: 'Drum Loop 100.wav'
  },
  {
    name: 'Bass Line',
    kind: 'midi',
    tempo: 110,
    beats: 8,
    key: 0,
    scale: 'minor',
    tags: ['Bass', 'Electronic'],
    file: 'Bass Line.mid'
  },
  {
    name: 'Beat Pattern',
    kind: 'pattern',
    tempo: 120,
    beats: 4,
    key: null,
    scale: 'neither',
    tags: ['Drums', 'Pattern'],
    file: 'Beat Pattern.anodeloop.json'
  },
  {
    name: 'Synth Chords',
    kind: 'midi',
    tempo: 120,
    beats: 16,
    key: 7,
    scale: 'major',
    tags: ['Synth', 'Keys'],
    file: 'Synth Chords.mid'
  },
  {
    name: 'Hat Groove',
    kind: 'audio',
    tempo: 120,
    beats: 8,
    key: null,
    scale: 'neither',
    tags: ['Drums', 'Percussion'],
    file: 'Hat Groove.wav'
  },
  {
    name: 'Piano Riff',
    kind: 'audio',
    tempo: 90,
    beats: 8,
    key: 2,
    scale: 'minor',
    tags: ['Keys', 'Piano'],
    file: 'Piano Riff.wav'
  },
  {
    name: 'Stab',
    kind: 'audio',
    tempo: null,
    beats: null,
    key: 9,
    scale: 'minor',
    tags: ['Synth', 'One-shot'],
    file: 'Stab.wav'
  }
];

/**
 * A `.anodepack` on the fake Server's disk: any path ending in `.anodepack` is a pack named after its file
 * (`TestPack.anodepack` → "Test Pack", id `test-pack`) holding {@link Templates}' loops; other paths are refused.
 */
export function readPack(path: string): FakePack {
  if (!/\.anodepack$/i.test(path)) {
    throw new ApiError(422, `${path} is not an Anode content pack (.anodepack).`);
  }
  const stem = (path.split(/[\\/]/).pop() ?? path).replace(/\.anodepack$/i, '');
  const name = title(stem);
  const id = slug(name);
  const folder = `${FakeDataDirectory}/Library/Packs/${id}`;
  const loops: FakeLoop[] = Templates.map(t => {
    const loopId = `${id}:${slug(t.name)}`;
    const loop: FakeLoop = {
      info: {
        id: loopId,
        name: t.name,
        kind: t.kind,
        packId: id,
        path: `${folder}/${t.file}`,
        tempo: t.tempo,
        beats: t.beats,
        key: t.key,
        scale: t.scale,
        tags: [...t.tags],
        oneShot: t.tempo === null,
        favourite: false
      }
    };
    if (t.kind === 'midi') {
      loop.notes = t.tags.includes('Bass') ? bassLine(t.beats ?? 8) : chords(t.beats ?? 16);
    }
    if (t.kind === 'pattern') {
      loop.pattern = beatPattern(loopId);
    }
    return loop;
  });
  return {
    pack: {
      id,
      name,
      version: '1.0.0',
      author: 'Anode',
      loopCount: loops.length,
      size: 1_048_576 * (2 + loops.length),
      installed: true
    },
    loops
  };
}

/** The fake Server's loop index: the installed packs' loops, the user's, and the favourites. */
export class FakeLoopLibrary {
  private readonly installed = new Map<string, FakePack>();
  private readonly user: FakeLoop[] = [];
  private readonly favourites = new Set<string>();

  packs(): Pack[] {
    return [...this.installed.values()].map(p => ({ ...p.pack })).sort((a, b) => a.name.localeCompare(b.name));
  }

  install(path: string): Pack {
    const pack = readPack(path);
    this.installed.set(pack.pack.id, pack);
    return { ...pack.pack };
  }

  remove(id: string): void {
    const pack = this.installed.get(id);
    if (!pack) {
      throw new ApiError(404, `No pack ${id} is installed.`);
    }
    this.installed.delete(id);
    for (const loop of pack.loops) {
      this.favourites.delete(loop.info.id);
    }
  }

  /** Every loop of the index. */
  all(): FakeLoop[] {
    return [...[...this.installed.values()].flatMap(p => p.loops), ...this.user].map(loop => ({
      ...loop,
      info: { ...loop.info, favourite: this.favourites.has(loop.info.id) }
    }));
  }

  get(id: string): FakeLoop {
    const loop = this.all().find(l => l.info.id === id);
    if (!loop) {
      throw new ApiError(404, `No loop ${id}.`);
    }
    return loop;
  }

  setFavourite(id: string, favourite: boolean): LoopInfo {
    this.get(id);
    if (favourite) {
      this.favourites.add(id);
    } else {
      this.favourites.delete(id);
    }
    return this.get(id).info;
  }

  /** Saves a user loop ("Add to Loop Library"). */
  add(loop: FakeLoop): LoopInfo {
    this.user.push(loop);
    return this.get(loop.info.id).info;
  }

  /** A search, as `GET /library/loops` answers it: filtered, sorted by name, paged. */
  query(search: LoopSearch): LoopPage {
    const text = search.text?.trim().toLowerCase() ?? '';
    const tags = (search.tags ?? []).map(t => t.toLowerCase());
    const matches = this.all()
      .map(l => l.info)
      .filter(info => {
        const lowerTags = info.tags.map(t => t.toLowerCase());
        if (text && !info.name.toLowerCase().includes(text) && !lowerTags.some(t => t.includes(text))) {
          return false;
        }
        if (!tags.every(t => lowerTags.includes(t))) {
          return false;
        }
        if (search.kind && info.kind !== search.kind) {
          return false;
        }
        if (search.key !== null && search.key !== undefined && info.key !== search.key) {
          return false;
        }
        if (search.scale) {
          const ok =
            search.scale === 'major' || search.scale === 'minor'
              ? info.scale === search.scale || info.scale === 'both'
              : info.scale === search.scale;
          if (!ok) {
            return false;
          }
        }
        return !(search.favouritesOnly && !info.favourite);
      })
      .sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
    const offset = Math.max(0, search.offset ?? 0);
    const limit = Math.min(500, Math.max(1, search.limit ?? 100));
    return { items: matches.slice(offset, offset + limit), total: matches.length };
  }
}

/** The interval from a loop's key to the project's, −6…+5 semitones (0 without a key). */
export function keyInterval(loopKey: number | null | undefined, projectTonic: number): number {
  if (loopKey === null || loopKey === undefined) {
    return 0;
  }
  const up = (((projectTonic - loopKey) % 12) + 12) % 12;
  return up > 5 ? up - 12 : up;
}
