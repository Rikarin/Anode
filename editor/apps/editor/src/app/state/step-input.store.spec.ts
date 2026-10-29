import { TestBed } from '@angular/core/testing';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { ProjectStore } from './project.store';
import { SelectionStore } from './selection.store';
import { StepInputStore } from './step-input.store';

describe('StepInputStore', () => {
  it('builds quick presses on each other: Chord on and off again ends off, and a key after Chord stacks', async () => {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Steps' }));
    await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: false }], 'Track');
    TestBed.inject(SelectionStore).selectTrack(project.trackIds()[0] ?? '');
    const steps = TestBed.inject(StepInputStore);
    await steps.start();
    await settle();

    // Two clicks before either answer: each reads the state the one before left.
    const first = steps.set({ chord: !steps.state().chord });
    expect(steps.state().chord).toBe(true);
    const second = steps.set({ chord: !steps.state().chord });
    expect(steps.state().chord).toBe(false);
    await Promise.all([first, second]);
    await settle();
    expect(steps.state().chord).toBe(false);
    expect((await server.stepInput()).chord).toBe(false);

    // Chord, then two keys at once: they stack at one position.
    void steps.set({ chord: true });
    void steps.step([60]);
    await steps.step([64]);
    await settle();
    const region = [...project.midiRegions().values()][0];
    expect(region?.notes.map(n => [n.pitch, n.startTick])).toEqual([
      [60, 0],
      [64, 0]
    ]);
  });
});
