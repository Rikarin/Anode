import { TestBed } from '@angular/core/testing';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { LiveLoopsStore } from './live-loops.store';

/** A project with an audio and an instrument track, two scenes and the test pack, at a clock the test moves. */
async function grid() {
  const clock = { time: 0 };
  const server = setUpFakeServer({ now: () => clock.time });
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Loops' }));
  await project.execute(
    [
      { type: 'addTrack', kind: 'audio', stereo: true },
      { type: 'addTrack', kind: 'instrument', stereo: true }
    ],
    'Tracks'
  );
  const [audio = '', synth = ''] = project.trackIds();
  const live = TestBed.inject(LiveLoopsStore);
  await live.addScene();
  await live.addScene();
  const [s1 = '', s2 = ''] = project.liveLoops().scenes.map(s => s.id);
  await server.installPack('/x/TestPack.anodepack');
  return { server, project, live, clock, audio, synth, s1, s2 };
}

describe('LiveLoopsStore', () => {
  it('follows the launcher: queued, then playing with its start sample, stopping keeps it, stopped forgets', async () => {
    const { server, project, live, clock, synth, s1, s2 } = await grid();
    await live.dropLoop('test-pack:bass-line', synth, s1);
    await live.dropLoop('test-pack:synth-chords', synth, s2);
    const [c1, c2] = project.liveLoops().cells;
    if (!c1 || !c2) {
      throw new Error('no cells');
    }
    expect(c1).toMatchObject({ trackId: synth, sceneId: s1, kind: 'midi' });

    await live.trigger(c1);
    await settle();
    // Launching activates the track for Live Loops (one undo step) and queues the cell for bar 2 (2 s at 120 bpm).
    expect(project.track(synth)?.liveLoopsActive).toBe(true);
    expect(live.status(c1.id)).toEqual({ state: 'queued', sample: 96_000 });
    expect(live.selected()).toEqual(new Set([c1.id]));
    expect(live.current()?.id).toBe(c1.id);

    clock.time = 2_100;
    server.advance();
    await settle();
    expect(live.status(c1.id)).toEqual({ state: 'playing', sample: 96_000, started: 96_000 });

    await live.trigger(c2);
    await settle();
    expect(live.status(c1.id)).toEqual({ state: 'stopping', sample: 192_000, started: 96_000 });
    expect(live.state(c2.id)).toBe('queued');

    clock.time = 4_100;
    server.advance();
    await settle();
    expect(live.state(c1.id)).toBe('stopped');
    expect(live.states().has(c1.id)).toBe(false);
    expect(live.playingOn(synth)?.id).toBe(c2.id);

    // A click on a playing cell stops its track at the grid's boundary.
    await live.trigger(c2);
    await settle();
    expect(live.state(c2.id)).toBe('stopping');
  });

  it('adds pattern and MIDI cells, and sets play modes, quantise, names and lengths', async () => {
    const { project, live, synth, s1, s2 } = await grid();
    const track = project.track(synth);
    if (!track) {
      throw new Error('no track');
    }
    await live.addPattern(track, s1);
    await live.addMidi(track, s2);
    const [pattern, midi] = project.liveLoops().cells;
    if (!pattern || !midi) {
      throw new Error('no cells');
    }
    expect(pattern).toMatchObject({ kind: 'pattern', lengthTicks: 3840, sceneId: s1 });
    expect(pattern.content.pattern?.pattern.rows.map(r => r.note)).toEqual([60]);
    expect(midi).toMatchObject({ kind: 'midi', lengthTicks: 3840, sceneId: s2 });

    await live.setPlayMode([pattern, midi], 'oneShot');
    expect(project.liveLoops().cells.map(c => c.playMode)).toEqual(['oneShot', 'oneShot']);
    await live.setCellQuantize([pattern], 'quarter');
    expect(project.liveLoops().cells[0]?.quantize).toBe('quarter');
    await live.setCellQuantize([pattern], null);
    expect(project.liveLoops().cells[0]?.quantize).toBeNull();
    await live.renameCell(pattern, 'Groove');
    await live.setCellLength(pattern, 7680);
    expect(project.liveLoops().cells[0]).toMatchObject({ name: 'Groove', lengthTicks: 7680 });
  });

  it('copies, moves and deletes cells; edits scenes; drops loops', async () => {
    const { server, project, live, audio, synth, s1, s2 } = await grid();
    await live.dropLoop('test-pack:drum-loop-100', audio, s1);
    const drums = project.liveLoops().cells[0];
    if (!drums) {
      throw new Error('no cell');
    }
    expect(drums).toMatchObject({ kind: 'audio', trackId: audio });
    await live.moveCell(drums, audio, s2, true);
    expect(
      project
        .liveLoops()
        .cells.map(c => c.sceneId)
        .sort()
    ).toEqual([s1, s2].sort());
    // A dropped cell on its own slot is not a move.
    const before = (await server.history()).undo.length;
    await live.moveCell(drums, audio, s1, false);
    expect((await server.history()).undo.length).toBe(before);
    await live.deleteCells(project.liveLoops().cells.map(c => c.id));
    expect(project.liveLoops().cells).toEqual([]);

    await live.renameScene(s1, 'Verse');
    await live.addScene(0);
    expect(project.liveLoops().scenes.map(s => s.name)).toEqual(['Scene 3', 'Verse', 'Scene 2']);
    await live.removeScene(s2);
    expect(project.liveLoops().scenes).toHaveLength(2);
    expect(synth).not.toBe('');
  });

  it('extends and clears the selection', async () => {
    const { project, live, synth, s1, s2 } = await grid();
    const track = project.track(synth);
    if (!track) {
      throw new Error('no track');
    }
    await live.addPattern(track, s1);
    await live.addPattern(track, s2);
    const [a = '', b = ''] = project.liveLoops().cells.map(c => c.id);
    live.select(a, false);
    live.select(b, true);
    expect(live.selected()).toEqual(new Set([a, b]));
    expect(live.currentId()).toBe(b);
    live.select(b, true);
    expect(live.selected()).toEqual(new Set([a]));
    expect(live.currentId()).toBe(a);
    live.clearSelection();
    expect(live.current()).toBeUndefined();
  });

  it('records a performance and into a cell', async () => {
    const { server, live, synth, s1 } = await grid();
    await live.recordPerformance(true);
    expect(live.recordingPerformance()).toBe(true);
    expect(server.liveRecording).toBe('performance');
    await live.recordPerformance(false);
    expect(live.recordingPerformance()).toBe(false);
    expect(server.liveRecording).toBeNull();
    await live.recordCell(synth, s1, true);
    expect(live.recordingCell()).toBe(`${synth}/${s1}`);
    await live.recordCell(synth, s1, false);
    expect(live.recordingCell()).toBeNull();
  });
});
