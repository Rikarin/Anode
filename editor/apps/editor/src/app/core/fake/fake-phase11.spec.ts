import {
  ApiError,
  ChordQualities,
  DrumKitNotes,
  type EditCommand,
  EmptyChordTrack,
  EmptyEnvironment,
  EnvironmentObjectKinds,
  MasteringBands,
  type NewChord,
  type Note,
  pitchClass,
  type PitchName,
  rootInterval,
  sectionKind
} from '@anode/api-client';
import { sessionPlayerEdit } from '../../state/session-players.store';
import { FakeAnodeServer } from './fake-server';

/** A value the test needs to be there. */
function must<T>(value: T | null | undefined): T {
  if (value === null || value === undefined) {
    throw new Error('Expected a value.');
  }
  return value;
}

const Ppq = 960;
const Bar = 4 * Ppq;

async function status(run: () => Promise<unknown>): Promise<number> {
  try {
    await run();
    return 200;
  } catch (error) {
    return error instanceof ApiError ? error.status : 500;
  }
}

const name = (step: PitchName['step'], alter = 0): PitchName => ({ step, alter });

/** The golden path's eight chords, a bar each: C, Am7, Fmaj7, G7, Em, A7, Dm7, G/B. */
const Progression: { chord: NewChord; pitches: number[] }[] = [
  { chord: { tick: 0, root: name('c'), quality: '', bass: null }, pitches: [48, 60, 64, 67] },
  { chord: { tick: Bar, root: name('a'), quality: 'm7', bass: null }, pitches: [45, 57, 60, 64, 67] },
  { chord: { tick: 2 * Bar, root: name('f'), quality: 'maj7', bass: null }, pitches: [41, 57, 60, 64, 65] },
  { chord: { tick: 3 * Bar, root: name('g'), quality: '7', bass: null }, pitches: [43, 59, 62, 65, 67] },
  { chord: { tick: 4 * Bar, root: name('e'), quality: 'm', bass: null }, pitches: [40, 59, 64, 67] },
  { chord: { tick: 5 * Bar, root: name('a'), quality: '7', bass: null }, pitches: [45, 61, 64, 67, 69] },
  { chord: { tick: 6 * Bar, root: name('d'), quality: 'm7', bass: null }, pitches: [38, 57, 60, 62, 65] },
  { chord: { tick: 7 * Bar, root: name('g'), quality: '', bass: name('b') }, pitches: [47, 55, 62, 67] }
];

async function song(): Promise<FakeAnodeServer> {
  const server = new FakeAnodeServer();
  await server.createProject({ name: 'Song' });
  return server;
}

async function run(server: FakeAnodeServer, ...commands: EditCommand[]): Promise<void> {
  await server.applyCommands({ commands, label: 'Test' });
}

function project(server: FakeAnodeServer) {
  const current = server.current;
  if (!current) {
    throw new Error('No project.');
  }
  return current;
}

/** A Session Player track with one 8-bar region from the start. */
async function player(server: FakeAnodeServer, kind: 'drummer' | 'bass' | 'keyboard') {
  await run(server, { type: 'addSessionPlayerTrack', player: kind, regions: true });
  const current = project(server);
  const track = current.tracks.find(
    t => t.parentId === null && current.sessionPlayerRegions.some(r => r.trackId === t.id)
  );
  const region = current.sessionPlayerRegions.at(-1);
  if (!track || !region) {
    throw new Error('No Session Player track.');
  }
  return { track, region };
}

async function notesOf(server: FakeAnodeServer, regionId: string): Promise<Note[]> {
  return (await server.sessionPlayerNotes(regionId)).notes;
}

function perBar(notes: readonly Note[], bars: number): number[] {
  return Array.from(
    { length: bars },
    (_, b) => notes.filter(n => n.startTick >= b * Bar && n.startTick < (b + 1) * Bar).length
  );
}

describe('FakeAnodeServer, Phase 11', () => {
  it('starts projects without chords, Session Player regions or an Environment', async () => {
    const server = await song();
    expect(server.current?.chordTrack).toEqual(EmptyChordTrack);
    expect(server.current?.sessionPlayerRegions).toEqual([]);
    expect(server.current?.environment).toEqual(EmptyEnvironment);
    await run(server, { type: 'addTrack', kind: 'instrument', stereo: true });
    const trackId = server.current?.tracks[0]?.id ?? '';
    await run(server, { type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: Bar });
    const region = server.current?.midiRegions[0];
    expect([region?.pitchSource, region?.key]).toEqual(['off', null]);
  });

  describe('the chord track', () => {
    it('adds, edits and deletes chords, each one undo step, a chord at a taken tick replacing it', async () => {
      const server = await song();
      await run(server, { type: 'addChords', chords: Progression.slice(0, 3).map(p => p.chord) });
      expect(project(server).chordTrack.chords.map(c => c.quality)).toEqual(['', 'm7', 'maj7']);
      await run(server, { type: 'addChords', chords: [{ tick: Bar, root: name('a'), quality: 'm', bass: null }] });
      expect(project(server).chordTrack.chords.map(c => c.quality)).toEqual(['', 'm', 'maj7']);
      const first = must(project(server).chordTrack.chords[0]);
      await run(server, { type: 'editChords', chords: [{ ...first, quality: '7', bass: name('e') }] });
      expect(project(server).chordTrack.chords[0]).toMatchObject({ quality: '7', bass: name('e') });
      await server.undo();
      expect(project(server).chordTrack.chords[0]?.quality).toBe('');
      await run(server, { type: 'deleteChords', chordIds: [first.id] });
      expect(project(server).chordTrack.chords).toHaveLength(2);
    });

    it('refuses unknown qualities, alterations past ±2 and negative ticks', async () => {
      const server = await song();
      const bad: NewChord[] = [
        { tick: 0, root: name('c'), quality: 'maj13', bass: null },
        { tick: 0, root: name('c', 3), quality: '', bass: null },
        { tick: -1, root: name('c'), quality: '', bass: null }
      ];
      for (const chord of bad) {
        expect(await status(() => run(server, { type: 'addChords', chords: [chord] }))).toBe(422);
      }
      expect(await status(() => run(server, { type: 'deleteChords', chordIds: ['chd_404'] }))).toBe(422);
    });

    it('keeps key and scale changes in tick order, one per tick', async () => {
      const server = await song();
      await run(server, {
        type: 'setScales',
        scales: [
          { tick: 4 * Bar, tonic: name('a'), scale: 'minor' },
          { tick: 0, tonic: name('c'), scale: 'dorian' }
        ]
      });
      expect(project(server).chordTrack.scales.map(s => s.scale)).toEqual(['dorian', 'minor']);
      const twice = [0, 0].map(tick => ({ tick, tonic: name('c'), scale: 'major' as const }));
      expect(await status(() => run(server, { type: 'setScales', scales: twice }))).toBe(422);
    });
  });

  describe('Chord ID', () => {
    async function progressionRegion(server: FakeAnodeServer) {
      await run(server, { type: 'addTrack', kind: 'instrument', stereo: true, name: 'Keys' });
      const trackId = must(project(server).tracks[0]).id;
      await run(server, { type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: 8 * Bar });
      const regionId = must(project(server).midiRegions[0]).id;
      await run(server, {
        type: 'addNotes',
        regionId,
        notes: Progression.flatMap(({ chord, pitches }) =>
          pitches.map(pitch => ({ pitch, velocity: 90, channel: 1, startTick: chord.tick, lengthTicks: Bar - 60 }))
        )
      });
      return regionId;
    }

    it('writes the eight known chords of a MIDI region to the chord track, in one undo step', async () => {
      const server = await song();
      const regionId = await progressionRegion(server);
      const job = await server.detectChords({ regionIds: [regionId], write: 'replace', detectKeys: true });
      expect(job.kind).toBe('chordId');
      server.runJobs();
      const chords = project(server).chordTrack.chords;
      const right = Progression.filter(({ chord }) =>
        chords.some(
          c =>
            Math.abs(c.tick - chord.tick) <= Ppq / 2 &&
            pitchClass(c.root) === pitchClass(chord.root) &&
            c.quality === chord.quality &&
            (c.bass ? pitchClass(c.bass) : null) === (chord.bass ? pitchClass(chord.bass) : null)
        )
      );
      expect(right.length).toBeGreaterThanOrEqual(7);
      expect(project(server).midiRegions[0]?.key).toEqual({ tonic: name('c'), mode: 'major' });
      await server.undo();
      expect(project(server).chordTrack.chords).toEqual([]);
    });

    it('merges only where the chord track has no chord within a beat', async () => {
      const server = await song();
      const regionId = await progressionRegion(server);
      await run(server, { type: 'addChords', chords: [{ tick: 0, root: name('d'), quality: 'sus4', bass: null }] });
      await server.detectChords({ regionIds: [regionId], write: 'merge', detectKeys: false });
      server.runJobs();
      const chords = project(server).chordTrack.chords;
      expect(chords[0]?.quality).toBe('sus4');
      expect(chords.length).toBeGreaterThanOrEqual(8);
    });

    it('refuses regions that do not exist', async () => {
      const server = await song();
      expect(
        await status(() => server.detectChords({ regionIds: ['mid_9'], write: 'replace', detectKeys: true }))
      ).toBe(422);
    });
  });

  describe('Pitch Source and region keys', () => {
    it('sets a MIDI region to follow, refuses Chords on audio, and sets and clears keys', async () => {
      const server = await song();
      await run(server, { type: 'addTrack', kind: 'instrument', stereo: true });
      const trackId = must(project(server).tracks[0]).id;
      await run(server, { type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: Bar });
      const regionId = must(project(server).midiRegions[0]).id;
      await run(server, { type: 'setPitchSource', regionIds: [regionId], mode: 'chords' });
      expect(project(server).midiRegions[0]?.pitchSource).toBe('chords');
      await run(server, { type: 'setRegionKey', regionIds: [regionId], key: { tonic: name('e', -1), mode: 'minor' } });
      expect(project(server).midiRegions[0]?.key).toEqual({ tonic: name('e', -1), mode: 'minor' });
      await run(server, { type: 'setRegionKey', regionIds: [regionId], key: null });
      expect(project(server).midiRegions[0]?.key).toBeNull();
      await run(server, { type: 'addTrack', kind: 'audio', stereo: true });
      const audioTrack = must(project(server).tracks.find(t => t.kind === 'audio'));
      const file = await server.importAudioFile('/Users/me/Music/bass.wav');
      await run(server, { type: 'addRegion', trackId: audioTrack.id, fileId: file.id, startTick: 0 });
      const audioRegion = must(project(server).regions[0]);
      await run(server, { type: 'setPitchSource', regionIds: [audioRegion.id], mode: 'root' });
      expect(project(server).regions[0]?.pitchSource).toBe('root');
      expect(
        await status(() => run(server, { type: 'setPitchSource', regionIds: [audioRegion.id], mode: 'chords' }))
      ).toBe(422);
    });
  });

  describe('Session Players', () => {
    it('serves a style library of four drum, three bass and three keys styles with presets and section defaults', async () => {
      const server = await song();
      const catalog = await server.sessionPlayerCatalog();
      const count = (player: string) => catalog.styles.filter(s => s.player === player).length;
      expect([count('drummer'), count('bass'), count('keyboard')]).toEqual([4, 3, 3]);
      expect(catalog.styles.some(s => s.synthPlayer)).toBe(true);
      for (const style of catalog.styles) {
        expect(style.presets.length).toBeGreaterThan(0);
        expect(style.sections.map(s => s.kind).sort()).toEqual(['bridge', 'chorus', 'intro', 'outro', 'verse']);
      }
    });

    it('makes a Drummer a multi-output kit with a region per arrangement section, by its kind', async () => {
      const server = await song();
      await run(
        server,
        { type: 'addSection', name: 'Verse', startTick: 0, lengthTicks: 4 * Bar },
        { type: 'addSection', name: 'Chorus', startTick: 4 * Bar, lengthTicks: 4 * Bar }
      );
      await run(server, { type: 'addSessionPlayerTrack', player: 'drummer', styleId: 'drums.rock', regions: true });
      const current = project(server);
      const drummer = current.tracks.find(t => t.kind === 'drummer');
      expect(drummer?.drumMachine?.pads.length).toBeGreaterThan(1);
      expect(current.tracks.filter(t => t.parentId === drummer?.id)).toHaveLength(
        drummer?.drumMachine?.pads.length ?? 0
      );
      const regions = current.sessionPlayerRegions;
      expect(regions.map(r => [r.name, r.startTick, r.lengthTicks])).toEqual([
        ['Verse', 0, 4 * Bar],
        ['Chorus', 4 * Bar, 4 * Bar]
      ]);
      expect(must(regions[1]).parameters.pad.intensity).toBeGreaterThan(must(regions[0]).parameters.pad.intensity);
      expect(new Set(regions.map(r => r.seed)).size).toBe(2);
      // An 8-bar region over two sections holds a fill at the first section's last bar.
      await run(server, { type: 'deleteRegions', regionIds: regions.map(r => r.id) });
      await run(server, {
        type: 'addSessionPlayerRegion',
        trackId: must(drummer).id,
        startTick: 0,
        lengthTicks: 8 * Bar,
        parameters: null
      });
      const region = must(project(server).sessionPlayerRegions[0]);
      const counts = perBar(await notesOf(server, region.id), 8);
      expect(counts[3]).toBeGreaterThan(Math.max(must(counts[0]), must(counts[1]), must(counts[2])));
      const fill = (await notesOf(server, region.id)).filter(n => n.startTick >= 3.5 * Bar && n.startTick < 4 * Bar);
      expect(fill.some(n => DrumKitNotes.toms.includes(n.pitch))).toBe(true);
    });

    it('plays more hits with complexity and louder with intensity; the same seed the same notes, Regenerate others', async () => {
      const server = await song();
      const { region } = await player(server, 'drummer');
      const hits: number[] = [];
      for (const complexity of [0.1, 0.5, 0.9]) {
        await run(server, {
          type: 'setSessionPlayerRegion',
          regionId: region.id,
          complexity,
          drums: null,
          bass: null,
          keys: null
        });
        hits.push(must(perBar(await notesOf(server, region.id), 8)[0]));
      }
      expect(hits[0]).toBeLessThan(must(hits[1]));
      expect(hits[1]).toBeLessThan(must(hits[2]));
      const loudness: number[] = [];
      for (const intensity of [0.1, 0.5, 0.9]) {
        await run(server, {
          type: 'setSessionPlayerRegion',
          regionId: region.id,
          intensity,
          drums: null,
          bass: null,
          keys: null
        });
        const notes = await notesOf(server, region.id);
        loudness.push(notes.reduce((sum, n) => sum + n.velocity, 0) / notes.length);
      }
      expect(loudness[0]).toBeLessThan(must(loudness[1]));
      expect(loudness[1]).toBeLessThan(must(loudness[2]));
      const before = await server.sessionPlayerNotes(region.id);
      expect(await server.sessionPlayerNotes(region.id)).toEqual(before);
      await server.regenerateSessionPlayer(region.id, {});
      const after = await server.sessionPlayerNotes(region.id);
      expect(after.hash).not.toBe(before.hash);
      expect(after.notes).not.toEqual(before.notes);
      await server.undo();
      expect(await server.sessionPlayerNotes(region.id)).toEqual(before);
    });

    it('applies a preset first and clears it on any other change; refuses another player’s switches', async () => {
      const server = await song();
      const { region } = await player(server, 'bass');
      const preset = must(
        must((await server.sessionPlayerCatalog()).styles.find(s => s.id === region.parameters.styleId)).presets[2]
      );
      await run(server, {
        type: 'setSessionPlayerRegion',
        regionId: region.id,
        presetId: preset.id,
        drums: null,
        bass: null,
        keys: null
      });
      expect(project(server).sessionPlayerRegions[0]?.parameters).toMatchObject({
        presetId: preset.id,
        pad: preset.parameters.pad
      });
      await run(server, {
        type: 'setSessionPlayerRegion',
        regionId: region.id,
        swing: 0.4,
        drums: null,
        bass: null,
        keys: null
      });
      expect(project(server).sessionPlayerRegions[0]?.parameters).toMatchObject({ presetId: null, swing: 0.4 });
      expect(project(server).sessionPlayerRegions[0]?.seed).toBe(region.seed);
      const drums = { pieces: [], rhythmSourceTrackId: null };
      expect(
        await status(() =>
          run(server, { type: 'setSessionPlayerRegion', regionId: region.id, drums, bass: null, keys: null })
        )
      ).toBe(422);
      expect(
        await status(() =>
          run(server, {
            type: 'setSessionPlayerRegion',
            regionId: region.id,
            drums: null,
            bass: { lowNote: 40, highNote: 45, slides: false, deadNotes: false },
            keys: null
          })
        )
      ).toBe(422);
    });

    it('follows the chord track: bass roots (or slash basses) on chord changes, keys only chord tones', async () => {
      const server = await song();
      await run(server, { type: 'addChords', chords: Progression.map(p => p.chord) });
      const bass = await player(server, 'bass');
      const notes = await notesOf(server, bass.region.id);
      for (const { chord } of Progression) {
        const down = notes.find(n => Math.abs(n.startTick - chord.tick) <= Ppq / 8);
        expect(down && down.pitch % 12).toBe(pitchClass(chord.bass ?? chord.root));
      }
      const range = must(bass.region.parameters.bass);
      expect(notes.every(n => n.pitch >= range.lowNote && n.pitch <= range.highNote)).toBe(true);
      const keys = await player(server, 'keyboard');
      const voiced = await notesOf(server, keys.region.id);
      for (const { chord } of Progression) {
        const tones = new Set(
          must(ChordQualities.find(q => q.quality === chord.quality)).intervals.map(
            i => (pitchClass(chord.root) + i) % 12
          )
        );
        const at = voiced.filter(n => n.startTick >= chord.tick && n.startTick < chord.tick + Bar);
        expect(at.length).toBeGreaterThan(0);
        expect(at.every(n => tones.has(n.pitch % 12))).toBe(true);
      }
    });

    it('converts to a MIDI region and to a pattern region with the generated notes', async () => {
      const server = await song();
      const { region } = await player(server, 'drummer');
      const notes = await notesOf(server, region.id);
      await server.convertSessionPlayer(region.id, { target: 'midi' });
      server.runJobs();
      const midi = project(server).midiRegions[0];
      expect(project(server).sessionPlayerRegions).toEqual([]);
      expect(midi?.notes.map(n => [n.pitch, n.startTick, n.velocity])).toEqual(
        notes.map(n => [n.pitch, n.startTick, n.velocity])
      );
      await server.undo();
      await server.convertSessionPlayer(region.id, { target: 'pattern' });
      server.runJobs();
      const pattern = project(server).patternRegions[0];
      expect(pattern?.pattern.rows.map(r => r.note).sort()).toEqual([...new Set(notes.map(n => n.pitch))].sort());
    });

    it('splits a Drummer region longer than a pattern row into pattern regions of 64 steps, as the Server does', async () => {
      const server = await song();
      const { region } = await player(server, 'drummer');
      await server.applyCommands({
        commands: [{ ...sessionPlayerEdit(region.id, { lengthTicks: 8 * 3840 }) }],
        label: 'Long'
      });
      await server.convertSessionPlayer(region.id, { target: 'pattern' });
      server.runJobs();
      const parts = [...project(server).patternRegions].sort((a, b) => a.startTick - b.startTick);
      expect(parts.map(p => [p.startTick - region.startTick, p.lengthTicks])).toEqual([
        [0, 4 * 3840],
        [4 * 3840, 4 * 3840]
      ]);
      expect(parts[0]?.pattern.rows.every(r => r.length === 64)).toBe(true);
    });

    it('moves, copies and deletes Session Player regions; removing the track removes them', async () => {
      const server = await song();
      const { track, region } = await player(server, 'keyboard');
      await run(server, { type: 'moveRegion', regionId: region.id, startTick: Bar });
      expect(project(server).sessionPlayerRegions[0]?.startTick).toBe(Bar);
      await run(server, { type: 'copyRegions', regionIds: [region.id], offsetTicks: 8 * Bar });
      const [a, b] = project(server).sessionPlayerRegions;
      expect([b?.startTick, b?.seed]).toEqual([9 * Bar, a?.seed]);
      await run(server, { type: 'deleteRegions', regionIds: [region.id] });
      expect(project(server).sessionPlayerRegions).toHaveLength(1);
      await run(server, { type: 'removeTrack', trackId: track.id });
      expect(project(server).sessionPlayerRegions).toEqual([]);
    });
  });

  it('mirrors the contract arithmetic and data', () => {
    expect(rootInterval(0, 7)).toBe(-5);
    expect(rootInterval(0, 5)).toBe(5);
    expect(rootInterval(0, 6)).toBe(-6);
    expect(pitchClass({ step: 'c', alter: -1 })).toBe(11);
    expect(pitchClass({ step: 'b', alter: 1 })).toBe(0);
    expect(sectionKind('Chorus 2')).toBe('chorus');
    expect(sectionKind('Solo')).toBe('verse');
    expect(ChordQualities.map(q => q.quality)).toContain('m7b5');
    expect(MasteringBands).toHaveLength(30);
    expect(EnvironmentObjectKinds.channelSplitter.outlets).toHaveLength(17);
    expect(DrumKitNotes.kick[0]).toBe(36);
  });
});
